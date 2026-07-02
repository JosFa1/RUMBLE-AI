using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Train;

internal sealed class TrainingBridgeServer : IDisposable
{
    private const int DefaultPort = 8765;
    private const string DefaultHost = "127.0.0.1";
    private const int RequestTimeoutMilliseconds = 5000;
    private const int MaxRequestCharacters = 16384;

    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly Action<string> _logError;
    private readonly TrainingEnvironmentManager _manager;
    private readonly ObservationBuilder _observationBuilder;
    private readonly ActionExecutor _actionExecutor;
    private readonly TrainingExplorationService _explorationService;
    private readonly ConcurrentQueue<PendingObservationRequest> _observationRequests = new();
    private readonly ConcurrentQueue<PendingStepRequest> _stepRequests = new();
    private readonly ConcurrentQueue<PendingResetRequest> _resetRequests = new();
    private readonly ConcurrentQueue<PendingDebugProbeRequest> _debugProbeRequests = new();
    private readonly object _gate = new();
    private readonly object _debugGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    private const double DebugSummaryIntervalSeconds = 5.0;

    private TcpListener _listener;
    private CancellationTokenSource _cts;
    private bool _started;
    private bool _disposed;
    private PendingStepRequest _activeStepRequest;
    private string _lastRequestType;
    private string _lastErrorCode;
    private float? _lastReward;
    private DateTime _nextDebugSummaryLogUtc = DateTime.MinValue;

    public TrainingBridgeServer(
        TrainingEnvironmentManager manager,
        ObservationBuilder observationBuilder,
        ActionExecutor actionExecutor,
        TrainingExplorationService explorationService,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string> logError)
    {
        _manager = manager;
        _observationBuilder = observationBuilder;
        _actionExecutor = actionExecutor;
        _explorationService = explorationService;
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
        _logError = logError ?? (_ => { });
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _started && _listener != null;
            }
        }
    }

    public void StartIfNeeded()
    {
        if (_disposed || _manager == null || !_manager.IsReady)
        {
            return;
        }

        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            var host = ResolveConfiguredHost();
            var port = ResolveConfiguredPort();

            if (!IPAddress.TryParse(host, out var ipAddress))
            {
                _logWarn($"TrainingBridgeServer host '{host}' is not a literal IP, falling back to {DefaultHost}.");
                ipAddress = IPAddress.Loopback;
                host = DefaultHost;
            }

            if (!IPAddress.IsLoopback(ipAddress))
            {
                _logWarn($"TrainingBridgeServer start skipped: host '{host}' is not loopback.");
                return;
            }

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(ipAddress, port);
            _listener.Start();
            _started = true;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

            _logInfo($"TrainingBridgeServer listening on {host}:{port}.");
            MaybeLogDebugSummary("start", force: true);
        }
    }

    public void Pump()
    {
        if (_disposed)
        {
            return;
        }

        while (_resetRequests.TryDequeue(out var pendingReset))
        {
            ProcessPendingReset(pendingReset);
        }

        ProcessPendingStep();

        while (_observationRequests.TryDequeue(out var pendingObservation))
        {
            ProcessPendingObservation(pendingObservation);
        }

        while (_debugProbeRequests.TryDequeue(out var pendingDebugProbe))
        {
            ProcessPendingDebugProbe(pendingDebugProbe);
        }

        MaybeLogDebugSummary("pump");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // best effort
            }

            try
            {
                _listener?.Stop();
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string ResolveConfiguredHost()
    {
        var host = Environment.GetEnvironmentVariable("AI_TRAIN_BRIDGE_HOST");
        if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultHost;
        }

        return host.Trim();
    }

    private static int ResolveConfiguredPort()
    {
        var portText = Environment.GetEnvironmentVariable("AI_TRAIN_BRIDGE_PORT");
        if (!string.IsNullOrWhiteSpace(portText) &&
            int.TryParse(portText, out var parsedPort) &&
            parsedPort > 0 &&
            parsedPort < 65536)
        {
            return parsedPort;
        }

        return DefaultPort;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _logWarn($"TrainingBridgeServer accept failed: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                _logError($"TrainingBridgeServer accept loop error: {ex.Message}");
                continue;
            }

            if (client != null)
            {
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = RequestTimeoutMilliseconds;
            client.SendTimeout = RequestTimeoutMilliseconds;

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };

                string requestType = null;
                var readTask = Task.Run(() => ReadRequest(reader), cancellationToken);
                _ = readTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                var readTimeoutTask = Task.Delay(RequestTimeoutMilliseconds, cancellationToken);
                var completedRead = await Task.WhenAny(readTask, readTimeoutTask).ConfigureAwait(false);
                if (completedRead != readTask)
                {
                    _logWarn("TrainingBridgeServer rejected timed out request.");
                    RecordRequestOutcome("unknown", "request_timeout", null);
                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                        // best effort
                    }

                    await writer.WriteLineAsync(CreateErrorResponse("unknown", "request_timeout", "Request read timed out.")).ConfigureAwait(false);
                    return;
                }

                var readResult = await readTask.ConfigureAwait(false);
                if (readResult.Status == RequestReadStatus.Timeout)
                {
                    _logWarn("TrainingBridgeServer rejected timed out request.");
                    RecordRequestOutcome("unknown", "request_timeout", null);
                    await writer.WriteLineAsync(CreateErrorResponse("unknown", "request_timeout", "Request read timed out.")).ConfigureAwait(false);
                    return;
                }

                if (readResult.Status == RequestReadStatus.TooLarge)
                {
                    _logWarn("TrainingBridgeServer rejected oversized request.");
                    RecordRequestOutcome("unknown", "request_too_large", null);
                    await writer.WriteLineAsync(CreateErrorResponse("unknown", "request_too_large", "Request body exceeded the maximum allowed size.")).ConfigureAwait(false);
                    return;
                }

                var requestText = readResult.Content;
                if (string.IsNullOrWhiteSpace(requestText))
                {
                    _logWarn("TrainingBridgeServer rejected empty request.");
                    RecordRequestOutcome("unknown", "empty_request", null);
                    await writer.WriteLineAsync(CreateErrorResponse("unknown", "empty_request", "Request body was empty.")).ConfigureAwait(false);
                    return;
                }

                if (!TryParseRequestType(requestText, out requestType, out var parseError))
                {
                    _logWarn($"TrainingBridgeServer rejected malformed request: {parseError}");
                    RecordRequestOutcome(requestType ?? "unknown", "malformed_request", null);
                    await writer.WriteLineAsync(CreateErrorResponse(requestType ?? "unknown", "malformed_request", "Request JSON was malformed or missing a type.", new Dictionary<string, object>
                    {
                        { "parseError", parseError }
                    })).ConfigureAwait(false);
                    return;
                }

                _logInfo($"TrainingBridgeServer received request type '{requestType}'.");

                if (string.Equals(requestType, "status", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = _manager?.GetBridgeStatus() ?? new TrainingBridgeStatus
                    {
                        type = "status_result",
                        protocolVersion = TrainingProtocol.Version,
                        requestType = "status",
                        sceneReady = false,
                        trainingSceneName = null,
                        playerRootFound = false,
                        episodeId = 0,
                        episodeStep = 0,
                        tick = 0,
                        timeSeconds = 0,
                        lastError = "manager_unavailable",
                        error = null
                    };

                    var response = JsonSerializer.Serialize(payload, _jsonOptions);
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                    RecordRequestOutcome("status", null, null);
                    MaybeLogDebugSummary("status");
                    _logInfo("TrainingBridgeServer served status request.");
                    return;
                }

                if (string.Equals(requestType, "get_observation", StringComparison.OrdinalIgnoreCase))
                {
                    var responseJson = await RequestObservationAsync(cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
                    MaybeLogDebugSummary("get_observation");
                    return;
                }

                if (string.Equals(requestType, "reset_episode", StringComparison.OrdinalIgnoreCase))
                {
                    var responseJson = await RequestResetAsync(cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
                    MaybeLogDebugSummary("reset_episode");
                    return;
                }

                if (string.Equals(requestType, "step", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseStepAction(requestText, out var stepAction, out var stepError))
                    {
                        _logWarn($"TrainingBridgeServer rejected malformed step request: {stepError}");
                        RecordRequestOutcome("step", "malformed_step_request", null);
                        await writer.WriteLineAsync(CreateErrorResponse("step", "malformed_step_request", "Step request is missing a valid action.", new Dictionary<string, object>
                        {
                            { "parseError", stepError }
                        })).ConfigureAwait(false);
                        return;
                    }

                    var responseJson = await RequestStepAsync(stepAction, cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
                    MaybeLogDebugSummary("step");
                    return;
                }

                if (string.Equals(requestType, "debug_probe", StringComparison.OrdinalIgnoreCase))
                {
                    var responseJson = await RequestDebugProbeAsync(cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
                    MaybeLogDebugSummary("debug_probe");
                    return;
                }

                _logWarn($"TrainingBridgeServer rejected unsupported request type '{requestType}'.");
                RecordRequestOutcome(requestType ?? "unknown", "unknown_request_type", null);
                await writer.WriteLineAsync(CreateErrorResponse(requestType ?? "unknown", "unknown_request_type", "Unknown request type.", new Dictionary<string, object>
                {
                    { "requestType", requestType }
                })).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logError($"TrainingBridgeServer client error: {ex.Message}");
                try
                {
                    using var errorWriter = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 4096, leaveOpen: true)
                    {
                        AutoFlush = true
                    };
                    RecordRequestOutcome("unknown", "internal_error", null);
                    await errorWriter.WriteLineAsync(CreateErrorResponse("unknown", "internal_error", "An unexpected bridge error occurred.", new Dictionary<string, object>
                    {
                        { "exception", ex.Message }
                    })).ConfigureAwait(false);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private async Task<string> RequestObservationAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            _logWarn("TrainingBridgeServer rejected observation request: bridge disposed.");
            RecordRequestOutcome("get_observation", "bridge_disposed", null);
            return CreateObservationErrorResponse("get_observation", "bridge_disposed", "Bridge is shutting down.");
        }

        if (_manager == null || !_manager.IsReady)
        {
            _logWarn("TrainingBridgeServer rejected observation request: scene not ready.");
            RecordRequestOutcome("get_observation", "scene_not_ready", null);
            return CreateObservationErrorResponse("get_observation", "scene_not_ready", "Training scene is not ready.");
        }

        if (_observationBuilder == null)
        {
            _logWarn("TrainingBridgeServer rejected observation request: observation builder unavailable.");
            RecordRequestOutcome("get_observation", "observation_unavailable", null);
            return CreateObservationErrorResponse("get_observation", "observation_unavailable", "Observation builder is unavailable.");
        }

        var pending = new PendingObservationRequest();
        _observationRequests.Enqueue(pending);

        var delayTask = Task.Delay(RequestTimeoutMilliseconds, cancellationToken);
        var completed = await Task.WhenAny(pending.Completion.Task, delayTask).ConfigureAwait(false);
        if (completed != pending.Completion.Task)
        {
            _logWarn("TrainingBridgeServer observation request timed out.");
            pending.TrySetTimeout();
            RecordRequestOutcome("get_observation", "observation_timeout", null);
            return CreateObservationErrorResponse("get_observation", "observation_timeout", "Observation request timed out.");
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task<string> RequestResetAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            _logWarn("TrainingBridgeServer rejected reset request: bridge disposed.");
            RecordRequestOutcome("reset_episode", "bridge_disposed", null);
            return CreateResetErrorResponse("reset_episode", "bridge_disposed", "Bridge is shutting down.");
        }

        if (_manager == null || !_manager.IsReady)
        {
            _logWarn("TrainingBridgeServer rejected reset request: scene not ready.");
            RecordRequestOutcome("reset_episode", "scene_not_ready", null);
            return CreateResetErrorResponse("reset_episode", "scene_not_ready", "Training scene is not ready.");
        }

        if (_actionExecutor == null)
        {
            _logWarn("TrainingBridgeServer rejected reset request: action executor unavailable.");
            RecordRequestOutcome("reset_episode", "action_executor_unavailable", null);
            return CreateResetErrorResponse("reset_episode", "action_executor_unavailable", "Action executor is unavailable.");
        }

        var pending = new PendingResetRequest();
        _resetRequests.Enqueue(pending);

        var delayTask = Task.Delay(RequestTimeoutMilliseconds, cancellationToken);
        var completed = await Task.WhenAny(pending.Completion.Task, delayTask).ConfigureAwait(false);
        if (completed != pending.Completion.Task)
        {
            _logWarn("TrainingBridgeServer reset request timed out.");
            pending.TrySetTimeout();
            RecordRequestOutcome("reset_episode", "reset_timeout", null);
            return CreateResetErrorResponse("reset_episode", "reset_timeout", "Reset request timed out.");
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task<string> RequestStepAsync(TrainingBridgeStepActionRequest action, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            _logWarn("TrainingBridgeServer rejected step request: bridge disposed.");
            RecordRequestOutcome("step", "bridge_disposed", null);
            return CreateStepErrorResponse("step", "bridge_disposed", "Bridge is shutting down.");
        }

        if (_manager == null || !_manager.IsReady)
        {
            _logWarn("TrainingBridgeServer rejected step request: scene not ready.");
            RecordRequestOutcome("step", "scene_not_ready", null);
            return CreateStepErrorResponse("step", "scene_not_ready", "Training scene is not ready.");
        }

        if (_actionExecutor == null)
        {
            _logWarn("TrainingBridgeServer rejected step request: action executor unavailable.");
            RecordRequestOutcome("step", "action_executor_unavailable", null);
            return CreateStepErrorResponse("step", "action_executor_unavailable", "Action executor is unavailable.");
        }

        var pending = new PendingStepRequest(action);
        _stepRequests.Enqueue(pending);

        var delayTask = Task.Delay(RequestTimeoutMilliseconds, cancellationToken);
        var completed = await Task.WhenAny(pending.Completion.Task, delayTask).ConfigureAwait(false);
        if (completed != pending.Completion.Task)
        {
            _logWarn("TrainingBridgeServer step request timed out.");
            pending.TrySetTimeout();
            RecordRequestOutcome("step", "step_timeout", null);
            return CreateStepErrorResponse("step", "step_timeout", "Step request timed out.");
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task<string> RequestDebugProbeAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            _logWarn("TrainingBridgeServer rejected debug probe request: bridge disposed.");
            RecordRequestOutcome("debug_probe", "bridge_disposed", null);
            return CreateDebugProbeErrorResponse("debug_probe", "bridge_disposed", "Bridge is shutting down.");
        }

        if (_manager == null || !_manager.IsReady)
        {
            _logWarn("TrainingBridgeServer rejected debug probe request: scene not ready.");
            RecordRequestOutcome("debug_probe", "scene_not_ready", null);
            return CreateDebugProbeErrorResponse("debug_probe", "scene_not_ready", "Training scene is not ready.");
        }

        if (_explorationService == null)
        {
            _logWarn("TrainingBridgeServer rejected debug probe request: exploration service unavailable.");
            RecordRequestOutcome("debug_probe", "exploration_unavailable", null);
            return CreateDebugProbeErrorResponse("debug_probe", "exploration_unavailable", "Exploration service is unavailable.");
        }

        var pending = new PendingDebugProbeRequest();
        _debugProbeRequests.Enqueue(pending);

        var delayTask = Task.Delay(RequestTimeoutMilliseconds, cancellationToken);
        var completed = await Task.WhenAny(pending.Completion.Task, delayTask).ConfigureAwait(false);
        if (completed != pending.Completion.Task)
        {
            _logWarn("TrainingBridgeServer debug probe request timed out.");
            pending.TrySetTimeout();
            RecordRequestOutcome("debug_probe", "debug_probe_timeout", null);
            return CreateDebugProbeErrorResponse("debug_probe", "debug_probe_timeout", "Debug probe request timed out.");
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private void ProcessPendingObservation(PendingObservationRequest pending)
    {
        if (pending == null || pending.IsCompleted)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                pending.TrySetResult(CreateObservationErrorResponse("get_observation", "bridge_disposed", "Bridge is shutting down."));
                RecordRequestOutcome("get_observation", "bridge_disposed", null);
                return;
            }

            if (_manager == null || !_manager.IsReady)
            {
                _logWarn("TrainingBridgeServer rejected observation request: scene not ready.");
                pending.TrySetResult(CreateObservationErrorResponse("get_observation", "scene_not_ready", "Training scene is not ready."));
                RecordRequestOutcome("get_observation", "scene_not_ready", null);
                return;
            }

            if (_observationBuilder == null)
            {
                _logWarn("TrainingBridgeServer rejected observation request: observation builder unavailable.");
                pending.TrySetResult(CreateObservationErrorResponse("get_observation", "observation_unavailable", "Observation builder is unavailable."));
                RecordRequestOutcome("get_observation", "observation_unavailable", null);
                return;
            }

            var observation = _observationBuilder.BuildObservation(_manager, "bridge-get_observation");
            var payload = new TrainingBridgeObservationResponse
            {
                type = "observation",
                protocolVersion = TrainingProtocol.Version,
                requestType = "get_observation",
                observation = observation,
                error = null
            };

            string json;
            try
            {
                json = JsonSerializer.Serialize(payload, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logError($"TrainingBridgeServer observation serialization failed: {ex.Message}");
                pending.TrySetResult(CreateObservationErrorResponse("get_observation", "serialization_failed", "Bridge response serialization failed."));
                RecordRequestOutcome("get_observation", "serialization_failed", null);
                return;
            }

            pending.TrySetResult(json);
            RecordRequestOutcome("get_observation", null, null);
            MaybeLogDebugSummary("get_observation");
            _logInfo("TrainingBridgeServer served observation request.");
        }
        catch (Exception ex)
        {
            _logError($"TrainingBridgeServer observation request failed: {ex.Message}");
            pending.TrySetResult(CreateObservationErrorResponse("get_observation", "observation_failed", "Observation request failed."));
            RecordRequestOutcome("get_observation", "observation_failed", null);
        }
    }

    private void ProcessPendingReset(PendingResetRequest pending)
    {
        if (pending == null || pending.IsCompleted)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                pending.TrySetResult(CreateResetErrorResponse("reset_episode", "bridge_disposed", "Bridge is shutting down."));
                RecordRequestOutcome("reset_episode", "bridge_disposed", null);
                return;
            }

            if (_manager == null || !_manager.IsReady)
            {
                _logWarn("TrainingBridgeServer rejected reset request: scene not ready.");
                pending.TrySetResult(CreateResetErrorResponse("reset_episode", "scene_not_ready", "Training scene is not ready."));
                RecordRequestOutcome("reset_episode", "scene_not_ready", null);
                return;
            }

            if (_actionExecutor == null)
            {
                _logWarn("TrainingBridgeServer rejected reset request: action executor unavailable.");
                pending.TrySetResult(CreateResetErrorResponse("reset_episode", "action_executor_unavailable", "Action executor is unavailable."));
                RecordRequestOutcome("reset_episode", "action_executor_unavailable", null);
                return;
            }

            var resetInfo = _actionExecutor.ResetEpisodeState(out var resetError);
            if (resetError != null)
            {
                _logWarn($"TrainingBridgeServer reset request failed: {resetError}");
                pending.TrySetResult(CreateResetErrorResponse("reset_episode", resetError, ResolveErrorMessage(resetError), CreateErrorDetails("resetError", resetError)));
                RecordRequestOutcome("reset_episode", resetError, null);
                return;
            }

            if (_activeStepRequest != null && !_activeStepRequest.IsCompleted)
            {
                _activeStepRequest.TrySetResult(CreateStepErrorResponse("step", "step_canceled_by_reset", "Step request was canceled by reset.", CreateErrorDetails("resetReason", "step_canceled_by_reset")));
                RecordRequestOutcome("step", "step_canceled_by_reset", null);
                _activeStepRequest = null;
            }

            while (_stepRequests.TryDequeue(out var canceledStep))
            {
                if (canceledStep == null || canceledStep.IsCompleted)
                {
                    continue;
                }

                canceledStep.TrySetResult(CreateStepErrorResponse("step", "step_canceled_by_reset", "Step request was canceled by reset.", CreateErrorDetails("resetReason", "step_canceled_by_reset")));
                RecordRequestOutcome("step", "step_canceled_by_reset", null);
            }

            if (!_manager.ResetEpisode("bridge-reset"))
            {
                _logWarn("TrainingBridgeServer reset request failed: manager rejected reset.");
                pending.TrySetResult(CreateResetErrorResponse("reset_episode", "reset_rejected", "Reset request was rejected by the manager."));
                RecordRequestOutcome("reset_episode", "reset_rejected", null);
                return;
            }

            TrainingObservation observation;
            try
            {
                observation = _observationBuilder.BuildObservation(_manager, "bridge-reset");
            }
            catch (Exception ex)
            {
                _logError($"TrainingBridgeServer reset observation failed: {ex.Message}");
                pending.TrySetResult(CreateResetErrorResponse("reset_episode", "observation_failed", "Reset observation failed."));
                RecordRequestOutcome("reset_episode", "observation_failed", null);
                return;
            }

            var status = _manager.GetStatus();
            var response = new TrainingBridgeResetResponse
            {
                type = "reset_result",
                protocolVersion = TrainingProtocol.Version,
                requestType = "reset_episode",
                episodeId = status.CurrentEpisodeId,
                observation = observation,
                sceneReady = status.SceneReady,
                resetMode = resetInfo?.resetMode ?? "partial",
                warnings = resetInfo?.warnings ?? new List<string>(),
                error = null
            };

            string json;
            try
            {
                json = JsonSerializer.Serialize(response, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logError($"TrainingBridgeServer reset serialization failed: {ex.Message}");
                pending.TrySetResult(CreateResetErrorResponse("reset_episode", "serialization_failed", "Bridge response serialization failed."));
                RecordRequestOutcome("reset_episode", "serialization_failed", null);
                return;
            }

            pending.TrySetResult(json);
            RecordRequestOutcome("reset_episode", null, null);
            MaybeLogDebugSummary("reset_episode");
            _logInfo("TrainingBridgeServer served reset request.");
        }
        catch (Exception ex)
        {
            _logError($"TrainingBridgeServer reset request failed: {ex.Message}");
            pending.TrySetResult(CreateResetErrorResponse("reset_episode", "reset_failed", "Reset request failed."));
            RecordRequestOutcome("reset_episode", "reset_failed", null);
        }
    }

    private void ProcessPendingStep()
    {
        if (_activeStepRequest == null && !_stepRequests.TryDequeue(out _activeStepRequest))
        {
            return;
        }

        var pending = _activeStepRequest;
        if (pending == null)
        {
            return;
        }

        if (pending.IsCompleted)
        {
            _actionExecutor?.CancelActiveStep("step_timeout", captureResolution: false);
            _activeStepRequest = null;
            return;
        }

        try
        {
            if (_disposed)
            {
                pending.TrySetResult(CreateStepErrorResponse("step", "bridge_disposed", "Bridge is shutting down."));
                RecordRequestOutcome("step", "bridge_disposed", null);
                _activeStepRequest = null;
                return;
            }

            if (_manager == null || !_manager.IsReady)
            {
                _logWarn("TrainingBridgeServer rejected step request: scene not ready.");
                pending.TrySetResult(CreateStepErrorResponse("step", "scene_not_ready", "Training scene is not ready."));
                RecordRequestOutcome("step", "scene_not_ready", null);
                _activeStepRequest = null;
                return;
            }

            if (_actionExecutor == null)
            {
                _logWarn("TrainingBridgeServer rejected step request: action executor unavailable.");
                pending.TrySetResult(CreateStepErrorResponse("step", "action_executor_unavailable", "Action executor is unavailable."));
                RecordRequestOutcome("step", "action_executor_unavailable", null);
                _activeStepRequest = null;
                return;
            }

            if (!pending.Started)
            {
                var startedInfo = _actionExecutor.StartStep(pending.Action, out var actionError);
                if (actionError != null)
                {
                    _logWarn($"TrainingBridgeServer step action failed: {actionError}");
                    pending.TrySetResult(CreateStepErrorResponse("step", actionError, ResolveErrorMessage(actionError), CreateErrorDetails("actionError", actionError)));
                    RecordRequestOutcome("step", actionError, startedInfo?.reward);
                    _activeStepRequest = null;
                    return;
                }

                pending.MarkStarted();
                return;
            }

            if (!_actionExecutor.TryConsumeResolvedStep(out var stepInfo, out var stepError))
            {
                return;
            }

            if (stepError != null)
            {
                pending.TrySetResult(CreateStepErrorResponse("step", stepError, ResolveErrorMessage(stepError), CreateErrorDetails("actionError", stepError)));
                RecordRequestOutcome("step", stepError, stepInfo?.reward);
                _activeStepRequest = null;
                return;
            }

            _manager.AdvanceEpisodeStep();

            TrainingObservation observation;
            try
            {
                observation = _observationBuilder.BuildObservation(_manager, "bridge-step");
            }
            catch (Exception ex)
            {
                _logError($"TrainingBridgeServer step observation failed: {ex.Message}");
                pending.TrySetResult(CreateStepErrorResponse("step", "observation_failed", "Step observation failed."));
                RecordRequestOutcome("step", "observation_failed", stepInfo?.reward);
                _activeStepRequest = null;
                return;
            }

            var response = new TrainingBridgeStepResponse
            {
                type = "step_result",
                protocolVersion = TrainingProtocol.Version,
                requestType = "step",
                observation = observation,
                reward = stepInfo?.reward ?? 0,
                terminated = false,
                truncated = false,
                info = stepInfo,
                error = null
            };

            string json;
            try
            {
                json = JsonSerializer.Serialize(response, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logError($"TrainingBridgeServer step serialization failed: {ex.Message}");
                pending.TrySetResult(CreateStepErrorResponse("step", "serialization_failed", "Bridge response serialization failed."));
                RecordRequestOutcome("step", "serialization_failed", stepInfo?.reward);
                _activeStepRequest = null;
                return;
            }

            pending.TrySetResult(json);
            RecordRequestOutcome("step", null, stepInfo?.reward);
            MaybeLogDebugSummary("step");
            _logInfo("TrainingBridgeServer served step request.");
            _activeStepRequest = null;
        }
        catch (Exception ex)
        {
            _logError($"TrainingBridgeServer step request failed: {ex.Message}");
            pending.TrySetResult(CreateStepErrorResponse("step", "step_failed", "Step request failed."));
            RecordRequestOutcome("step", "step_failed", null);
            _activeStepRequest = null;
        }
    }

    private void ProcessPendingDebugProbe(PendingDebugProbeRequest pending)
    {
        if (pending == null || pending.IsCompleted)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                pending.TrySetResult(CreateDebugProbeErrorResponse("debug_probe", "bridge_disposed", "Bridge is shutting down."));
                RecordRequestOutcome("debug_probe", "bridge_disposed", null);
                return;
            }

            if (_manager == null || !_manager.IsReady)
            {
                _logWarn("TrainingBridgeServer rejected debug probe request: scene not ready.");
                pending.TrySetResult(CreateDebugProbeErrorResponse("debug_probe", "scene_not_ready", "Training scene is not ready."));
                RecordRequestOutcome("debug_probe", "scene_not_ready", null);
                return;
            }

            if (_explorationService == null)
            {
                _logWarn("TrainingBridgeServer rejected debug probe request: exploration service unavailable.");
                pending.TrySetResult(CreateDebugProbeErrorResponse("debug_probe", "exploration_unavailable", "Exploration service is unavailable."));
                RecordRequestOutcome("debug_probe", "exploration_unavailable", null);
                return;
            }

            var probe = _explorationService.BuildDebugProbe(_manager, "bridge-debug_probe");
            var payload = new TrainingBridgeDebugResponse
            {
                type = "debug_probe_result",
                protocolVersion = TrainingProtocol.Version,
                requestType = "debug_probe",
                sceneReady = probe.sceneReady,
                playerRootFound = probe.playerRootFound,
                trainingSceneName = probe.trainingSceneName,
                playerRootPath = probe.playerRootPath,
                probeHostReady = probe.probeHostReady,
                camera = probe.camera,
                types = probe.types,
                warnings = probe.warnings,
                error = probe.error
            };

            pending.TrySetResult(JsonSerializer.Serialize(payload, _jsonOptions));
            RecordRequestOutcome("debug_probe", null, null);
        }
        catch (Exception ex)
        {
            _logError($"TrainingBridgeServer debug probe failed: {ex.Message}");
            pending.TrySetResult(CreateDebugProbeErrorResponse("debug_probe", "internal_error", "An unexpected bridge error occurred.", new Dictionary<string, object>
            {
                { "exception", ex.Message }
            }));
            RecordRequestOutcome("debug_probe", "internal_error", null);
        }
    }

    private RequestReadResult ReadRequest(StreamReader reader)
    {
        var builder = new StringBuilder();

        try
        {
            while (true)
            {
                var next = reader.Read();
                if (next < 0)
                {
                    break;
                }

                if (next == '\n')
                {
                    break;
                }

                if (next != '\r')
                {
                    builder.Append((char)next);
                }

                if (builder.Length > MaxRequestCharacters)
                {
                    return new RequestReadResult
                    {
                        Status = RequestReadStatus.TooLarge,
                        Content = null
                    };
                }
            }
        }
        catch (IOException)
        {
            return new RequestReadResult
            {
                Status = RequestReadStatus.Timeout,
                Content = null
            };
        }
        catch (ObjectDisposedException)
        {
            return new RequestReadResult
            {
                Status = RequestReadStatus.Timeout,
                Content = null
            };
        }

        return new RequestReadResult
        {
            Status = RequestReadStatus.Success,
            Content = builder.ToString().Trim()
        };
    }

    private static bool TryParseStepAction(string requestText, out TrainingBridgeStepActionRequest action, out string error)
    {
        action = null;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(requestText);
            if (!document.RootElement.TryGetProperty("action", out var actionElement) ||
                actionElement.ValueKind != JsonValueKind.Object)
            {
                error = "missing_action";
                return false;
            }

            if (!TryReadVector3Array(actionElement, "leftHandTargetLocal", out var leftTargetLocal, out var leftError))
            {
                error = $"left_{leftError}";
                return false;
            }

            if (!TryReadVector3Array(actionElement, "rightHandTargetLocal", out var rightTargetLocal, out var rightError))
            {
                error = $"right_{rightError}";
                return false;
            }

            var durationMs = 100;
            if (actionElement.TryGetProperty("durationMs", out var durationElement) && durationElement.TryGetInt32(out var parsedDuration))
            {
                durationMs = parsedDuration;
            }

            action = new TrainingBridgeStepActionRequest
            {
                leftHandTargetLocal = leftTargetLocal,
                rightHandTargetLocal = rightTargetLocal,
                durationMs = durationMs
            };

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadVector3Array(JsonElement actionElement, string propertyName, out float[] values, out string error)
    {
        values = null;
        error = null;

        if (!actionElement.TryGetProperty(propertyName, out var vectorElement) ||
            vectorElement.ValueKind != JsonValueKind.Array)
        {
            error = "missing_vector";
            return false;
        }

        values = new float[3];
        var index = 0;
        foreach (var item in vectorElement.EnumerateArray())
        {
            if (index >= 3)
            {
                error = "vector_length";
                return false;
            }

            if (!item.TryGetSingle(out values[index]))
            {
                error = "vector_number";
                return false;
            }

            index++;
        }

        if (index != 3)
        {
            error = "vector_length";
            return false;
        }

        return true;
    }

    private string CreateErrorResponse(string requestType, string errorCode, string message, Dictionary<string, object> details = null)
    {
        var payload = new TrainingBridgeErrorResponse
        {
            type = "error",
            protocolVersion = TrainingProtocol.Version,
            requestType = requestType,
            error = new TrainingBridgeErrorInfo
            {
                code = errorCode,
                message = message ?? ResolveErrorMessage(errorCode),
                details = details
            }
        };

        return JsonSerializer.Serialize(payload, _jsonOptions);
    }

    private string CreateObservationErrorResponse(string requestType, string errorCode, string message, Dictionary<string, object> details = null)
    {
        return CreateErrorResponse(requestType, errorCode, message, details);
    }

    private string CreateResetErrorResponse(string requestType, string errorCode, string message, Dictionary<string, object> details = null)
    {
        return CreateErrorResponse(requestType, errorCode, message, details);
    }

    private string CreateStepErrorResponse(string requestType, string errorCode, string message, Dictionary<string, object> details = null)
    {
        return CreateErrorResponse(requestType, errorCode, message, details);
    }

    private string CreateDebugProbeErrorResponse(string requestType, string errorCode, string message, Dictionary<string, object> details = null)
    {
        return CreateErrorResponse(requestType, errorCode, message, details);
    }

    private static Dictionary<string, object> CreateErrorDetails(string key, string value)
    {
        return new Dictionary<string, object>
        {
            { key, value }
        };
    }

    private static string ResolveErrorMessage(string errorCode)
    {
        switch (errorCode)
        {
            case "empty_request":
                return "Request body was empty.";
            case "malformed_request":
                return "Request JSON was malformed or missing a type.";
            case "request_timeout":
                return "Request read timed out.";
            case "request_too_large":
                return "Request body exceeded the maximum allowed size.";
            case "unknown_request_type":
                return "Unknown request type.";
            case "malformed_step_request":
                return "Step request is missing a valid action.";
            case "bridge_disposed":
                return "Bridge is shutting down.";
            case "scene_not_ready":
                return "Training scene is not ready.";
            case "observation_unavailable":
                return "Observation builder is unavailable.";
            case "observation_timeout":
                return "Observation request timed out.";
            case "observation_failed":
                return "Observation request failed.";
            case "reset_timeout":
                return "Reset request timed out.";
            case "reset_failed":
                return "Reset request failed.";
            case "reset_rejected":
                return "Reset request was rejected.";
            case "action_executor_unavailable":
                return "Action executor is unavailable.";
            case "player_root_missing":
                return "Player root is missing.";
            case "step_timeout":
                return "Step request timed out.";
            case "step_replaced":
                return "Active step was replaced before completion.";
            case "step_canceled_by_reset":
                return "Active step was canceled by reset.";
            case "step_failed":
                return "Step request failed.";
            case "invalid_action":
                return "Action payload is invalid.";
            case "missing_action":
                return "Step action is missing.";
            case "missing_vector":
            case "vector_length":
            case "vector_number":
            case "vector_nan":
                return "Action payload has an invalid target vector.";
            default:
                if (!string.IsNullOrWhiteSpace(errorCode) &&
                    (errorCode.IndexOf("vector", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     errorCode.IndexOf("action", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return "Action payload is invalid.";
                }

                return "Bridge error.";
        }
    }

    private void RecordRequestOutcome(string requestType, string errorCode, float? reward)
    {
        lock (_debugGate)
        {
            _lastRequestType = string.IsNullOrWhiteSpace(requestType) ? "unknown" : requestType;
            _lastErrorCode = errorCode;
            _lastReward = reward;
        }
    }

    private void MaybeLogDebugSummary(string trigger, bool force = false)
    {
        var now = DateTime.UtcNow;
        string requestType;
        string errorCode;
        float? reward;

        lock (_debugGate)
        {
            if (!force && now < _nextDebugSummaryLogUtc)
            {
                return;
            }

            _nextDebugSummaryLogUtc = now.AddSeconds(DebugSummaryIntervalSeconds);
            requestType = _lastRequestType ?? "none";
            errorCode = _lastErrorCode;
            reward = _lastReward;
        }

        var status = _manager?.GetBridgeStatus() ?? new TrainingBridgeStatus
        {
            type = "status_result",
            protocolVersion = TrainingProtocol.Version,
            requestType = "status",
            sceneReady = false,
            trainingSceneName = null,
            playerRootFound = false,
            episodeId = 0,
            episodeStep = 0,
            tick = 0,
            timeSeconds = 0,
            lastError = null,
            error = null
        };

        var rewardText = reward.HasValue
            ? reward.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "none";

        _logInfo(
            $"TrainingBridgeServer debug summary[{trigger}]: " +
            $"bridgeRunning={IsRunning} " +
            $"sceneReady={status.sceneReady} " +
            $"playerRootFound={status.playerRootFound} " +
            $"episodeId={status.episodeId} " +
            $"episodeStep={status.episodeStep} " +
            $"lastRequestType={requestType} " +
            $"lastReward={rewardText} " +
            $"lastError={errorCode ?? status.lastError ?? "none"}");
    }

    private static bool TryParseRequestType(string requestText, out string requestType, out string error)
    {
        requestType = null;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(requestText);
            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                error = "missing_type";
                return false;
            }

            requestType = typeElement.GetString();
            return !string.IsNullOrWhiteSpace(requestType);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed class PendingObservationRequest
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public TaskCompletionSource<string> Completion => _completion;
        public bool IsCompleted => _completed != 0;

        public void TrySetResult(string json)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(json);
            }
        }

        public void TrySetTimeout()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(string.Empty);
            }
        }
    }

    private sealed class PendingResetRequest
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public TaskCompletionSource<string> Completion => _completion;
        public bool IsCompleted => _completed != 0;

        public void TrySetResult(string json)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(json);
            }
        }

        public void TrySetTimeout()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(string.Empty);
            }
        }
    }

    private sealed class PendingStepRequest
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public PendingStepRequest(TrainingBridgeStepActionRequest action)
        {
            Action = action;
        }

        public TrainingBridgeStepActionRequest Action { get; }
        public TaskCompletionSource<string> Completion => _completion;
        public bool IsCompleted => _completed != 0;
        public bool Started { get; private set; }

        public void MarkStarted()
        {
            Started = true;
        }

        public void TrySetResult(string json)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(json);
            }
        }

        public void TrySetTimeout()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(string.Empty);
            }
        }
    }

    private sealed class PendingDebugProbeRequest
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public TaskCompletionSource<string> Completion => _completion;
        public bool IsCompleted => _completed != 0;

        public void TrySetResult(string json)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(json);
            }
        }

        public void TrySetTimeout()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _completion.TrySetResult(string.Empty);
            }
        }
    }

    private sealed class RequestReadResult
    {
        public RequestReadStatus Status { get; set; }
        public string Content { get; set; }
    }

    private enum RequestReadStatus
    {
        Success,
        Timeout,
        TooLarge
    }
}
