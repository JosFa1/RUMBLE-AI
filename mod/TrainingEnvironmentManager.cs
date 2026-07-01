using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI_Train;

internal sealed class TrainingEnvironmentManager
{
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly Action<string> _logError;
    private readonly object _gate = new();

    private Scene _currentTrainingScene;
    private GameObject _currentPlayerRoot;
    private bool _isReady;
    private bool _playerRootFound;
    private bool _episodeActive;
    private int _currentEpisodeId;
    private int _currentEpisodeStepCount;
    private DateTime? _currentEpisodeStartTimeUtc;
    private string _lastError;
    private string _statusMessage;
    private string _sourceSceneName;
    private string _sourceCandidatePath;
    private string _currentTrainingSceneName;
    private string _currentPlayerRootName;
    private string _currentPlayerRootPath;
    private DateTime _lastUpdatedUtc;
    private int _lastTick;
    private float _lastTimeSeconds;

    public TrainingEnvironmentManager(Action<string> logInfo, Action<string> logWarn, Action<string> logError)
    {
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
        _logError = logError ?? (_ => { });
    }

    public bool IsReady => _isReady;

    public string ProtocolVersion => TrainingProtocol.Version;

    public Scene CurrentTrainingScene => _currentTrainingScene;

    public GameObject CurrentPlayerRoot => _currentPlayerRoot;

    public bool InitializeFromDiscoveredScene(Scene trainingScene, GameObject playerRoot, string sourceSceneName, string sourceCandidatePath)
    {
        bool isReady;
        lock (_gate)
        {
            _currentTrainingScene = trainingScene;
            _currentTrainingSceneName = trainingScene.name;
            _currentPlayerRoot = playerRoot;
            _currentPlayerRootName = playerRoot != null ? playerRoot.name : string.Empty;
            _currentPlayerRootPath = GetPath(playerRoot != null ? playerRoot.transform : null);
            _sourceSceneName = sourceSceneName ?? string.Empty;
            _sourceCandidatePath = sourceCandidatePath ?? string.Empty;
            _playerRootFound = playerRoot != null;
            _isReady = trainingScene.IsValid() && trainingScene.isLoaded && _playerRootFound;
            _episodeActive = false;
            _currentEpisodeStepCount = 0;
            _currentEpisodeStartTimeUtc = null;
            _lastError = null;
            _statusMessage = _isReady
                ? $"Training environment ready from '{_sourceSceneName}'."
                : "Training environment initialization incomplete.";
            _lastUpdatedUtc = DateTime.UtcNow;
            isReady = _isReady;
        }

        if (!isReady)
        {
            SetError("Training environment initialization failed: scene or player root missing.");
            EmitStatus("InitializeFromDiscoveredScene");
            return false;
        }

        StartEpisodeInternal("InitializeFromDiscoveredScene", logStatus: false);
        EmitStatus("InitializeFromDiscoveredScene");
        return true;
    }

    public bool BeginEpisode(string reason = null)
    {
        if (!IsReady)
        {
            SetError("BeginEpisode rejected: training environment is not ready.");
            EmitStatus(string.IsNullOrWhiteSpace(reason) ? "BeginEpisode" : reason);
            return false;
        }

        return StartEpisodeInternal(reason ?? "BeginEpisode", logStatus: true);
    }

    public bool ResetEpisode(string reason = null)
    {
        if (!IsReady)
        {
            SetError("ResetEpisode rejected: training environment is not ready.");
            EmitStatus(string.IsNullOrWhiteSpace(reason) ? "ResetEpisode" : reason);
            return false;
        }

        return StartEpisodeInternal(reason ?? "ResetEpisode", logStatus: true);
    }

    public bool EndEpisode(string reason = null)
    {
        if (!IsReady)
        {
            SetError("EndEpisode rejected: training environment is not ready.");
            EmitStatus(string.IsNullOrWhiteSpace(reason) ? "EndEpisode" : reason);
            return false;
        }

        lock (_gate)
        {
            _episodeActive = false;
            _statusMessage = string.IsNullOrWhiteSpace(reason)
                ? $"Episode {_currentEpisodeId} ended."
                : $"{reason}: episode {_currentEpisodeId} ended.";
            _lastError = null;
            _lastUpdatedUtc = DateTime.UtcNow;
        }
        EmitStatus(string.IsNullOrWhiteSpace(reason) ? "EndEpisode" : reason);
        return true;
    }

    public int AdvanceEpisodeStep()
    {
        lock (_gate)
        {
            if (!_isReady || !_episodeActive)
            {
                return _currentEpisodeStepCount;
            }

            _currentEpisodeStepCount++;
            _lastUpdatedUtc = DateTime.UtcNow;
            return _currentEpisodeStepCount;
        }
    }

    public void UpdateTelemetry(int tick, float timeSeconds)
    {
        lock (_gate)
        {
            _lastTick = tick;
            _lastTimeSeconds = timeSeconds;
            _lastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public TrainingEnvironmentStatus GetStatus()
    {
        lock (_gate)
        {
            return new TrainingEnvironmentStatus
            {
                SceneReady = _isReady,
                PlayerRootFound = _playerRootFound,
                EpisodeActive = _episodeActive,
                CurrentEpisodeId = _currentEpisodeId,
                CurrentEpisodeStepCount = _currentEpisodeStepCount,
                CurrentEpisodeStartTimeUtc = _currentEpisodeStartTimeUtc,
                LastError = _lastError,
                StatusMessage = _statusMessage,
                SourceSceneName = _sourceSceneName,
                SourceCandidatePath = _sourceCandidatePath,
                CurrentTrainingSceneName = _currentTrainingSceneName,
                CurrentPlayerRootName = _currentPlayerRootName,
                CurrentPlayerRootPath = _currentPlayerRootPath,
                LastUpdatedUtc = _lastUpdatedUtc,
                CurrentTick = _lastTick,
                CurrentTimeSeconds = _lastTimeSeconds
            };
        }
    }

    public TrainingBridgeStatus GetBridgeStatus()
    {
        lock (_gate)
        {
            return new TrainingBridgeStatus
            {
                protocolVersion = TrainingProtocol.Version,
                sceneReady = _isReady,
                trainingSceneName = _currentTrainingSceneName,
                playerRootFound = _playerRootFound,
                episodeId = _currentEpisodeId,
                episodeStep = _currentEpisodeStepCount,
                tick = _lastTick,
                timeSeconds = _lastTimeSeconds,
                lastError = _lastError
            };
        }
    }

    private bool StartEpisodeInternal(string reason, bool logStatus)
    {
        lock (_gate)
        {
            _currentEpisodeId = _currentEpisodeId <= 0 ? 1 : _currentEpisodeId + 1;
            _currentEpisodeStepCount = 0;
            _currentEpisodeStartTimeUtc = DateTime.UtcNow;
            _episodeActive = true;
            _lastError = null;
            _statusMessage = string.IsNullOrWhiteSpace(reason)
                ? $"Episode {_currentEpisodeId} started."
                : $"{reason}: episode {_currentEpisodeId} started.";
            _lastUpdatedUtc = DateTime.UtcNow;
        }

        if (logStatus)
        {
            EmitStatus(reason);
        }

        return true;
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
            _statusMessage = message;
            _lastUpdatedUtc = DateTime.UtcNow;
        }
        _logError(message);
    }

    private void EmitStatus(string context)
    {
        var status = GetStatus();
        _logInfo(
            $"TrainingEnvironmentManager status[{context}]: " +
            $"sceneReady={status.SceneReady} " +
            $"playerRootFound={status.PlayerRootFound} " +
            $"episodeActive={status.EpisodeActive} " +
            $"episodeId={status.CurrentEpisodeId} " +
            $"stepCount={status.CurrentEpisodeStepCount} " +
            $"episodeStartUtc={(status.CurrentEpisodeStartTimeUtc.HasValue ? status.CurrentEpisodeStartTimeUtc.Value.ToString("O") : "none")} " +
            $"trainingScene={OrNone(status.CurrentTrainingSceneName)} " +
            $"playerRoot={OrNone(status.CurrentPlayerRootPath)} " +
            $"lastError={OrNone(status.LastError)} " +
            $"status={OrNone(status.StatusMessage)}");
    }

    private static string OrNone(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value;
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        var parts = new System.Collections.Generic.Stack<string>();
        while (transform != null)
        {
            parts.Push(transform.name);
            transform = transform.parent;
        }

        return string.Join("/", parts);
    }
}

internal sealed class TrainingEnvironmentStatus
{
    public bool SceneReady { get; set; }
    public bool PlayerRootFound { get; set; }
    public bool EpisodeActive { get; set; }
    public int CurrentEpisodeId { get; set; }
    public int CurrentEpisodeStepCount { get; set; }
    public DateTime? CurrentEpisodeStartTimeUtc { get; set; }
    public string LastError { get; set; }
    public string StatusMessage { get; set; }
    public string SourceSceneName { get; set; }
    public string SourceCandidatePath { get; set; }
    public string CurrentTrainingSceneName { get; set; }
    public string CurrentPlayerRootName { get; set; }
    public string CurrentPlayerRootPath { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
    public int CurrentTick { get; set; }
    public float CurrentTimeSeconds { get; set; }
}

internal sealed class TrainingBridgeStatus
{
    public string protocolVersion { get; set; }
    public bool sceneReady { get; set; }
    public string trainingSceneName { get; set; }
    public bool playerRootFound { get; set; }
    public int episodeId { get; set; }
    public int episodeStep { get; set; }
    public int tick { get; set; }
    public float timeSeconds { get; set; }
    public string lastError { get; set; }
}
