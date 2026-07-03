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
    private string _bootstrapStage = "Uninitialized";
    private bool _bootstrapReady;
    private bool _bootstrapFailed;
    private string _bootstrapFailureReason;
    private bool _bootstrapGymLoaded;
    private bool _bootstrapLoaderRemoved;
    private bool _bootstrapLoaderInert;
    private bool _bootstrapPrimaryActorFound;
    private bool _bootstrapArenaBuilt;
    private string _bootstrapActiveScene;
    private List<string> _bootstrapLoadedScenes = new();
    private string _actorDiscoveryStatus = "not_run";
    private string _capabilityDiscoveryStatus = "not_run";
    private string _summonProbeStatus = "not_run";
    private string _moveProbeStatus = "not_run";
    private string _multiActorProbeStatus = "not_run";
    private string _actorInteractionProbeStatus = "not_run";
    private string _bootstrapLatestDumpPath;
    private List<string> _bootstrapLatestDumpPaths = new();

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

    public void UpdateBootstrapState(TrainingBootstrapState state)
    {
        if (state == null)
        {
            return;
        }

        lock (_gate)
        {
            _bootstrapStage = state.stage ?? "Unknown";
            _bootstrapReady = state.ready;
            _bootstrapFailed = state.failed;
            _bootstrapFailureReason = state.failureReason;
            _bootstrapGymLoaded = state.gymLoaded;
            _bootstrapLoaderRemoved = state.loaderRemoved;
            _bootstrapLoaderInert = state.loaderInert;
            _bootstrapPrimaryActorFound = state.primaryActorFound;
            _bootstrapArenaBuilt = state.arenaBuilt;
            _bootstrapActiveScene = state.activeScene;
            _bootstrapLoadedScenes = state.loadedScenes != null ? new List<string>(state.loadedScenes) : new List<string>();
            _actorDiscoveryStatus = state.actorDiscoveryStatus ?? "unknown";
            _capabilityDiscoveryStatus = state.capabilityDiscoveryStatus ?? "unknown";
            _summonProbeStatus = state.summonProbeStatus ?? "unknown";
            _moveProbeStatus = state.moveProbeStatus ?? "unknown";
            _multiActorProbeStatus = state.multiActorProbeStatus ?? "unknown";
            _actorInteractionProbeStatus = state.actorInteractionProbeStatus ?? "unknown";
            _bootstrapLatestDumpPath = state.lastReportPath;
            _bootstrapLatestDumpPaths = state.latestDumpPaths != null ? new List<string>(state.latestDumpPaths) : new List<string>();
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
                CurrentTimeSeconds = _lastTimeSeconds,
                BootstrapStage = _bootstrapStage,
                BootstrapReady = _bootstrapReady,
                BootstrapFailed = _bootstrapFailed,
                BootstrapFailureReason = _bootstrapFailureReason,
                BootstrapGymLoaded = _bootstrapGymLoaded,
                BootstrapLoaderRemoved = _bootstrapLoaderRemoved,
                BootstrapLoaderInert = _bootstrapLoaderInert,
                BootstrapPrimaryActorFound = _bootstrapPrimaryActorFound,
                BootstrapArenaBuilt = _bootstrapArenaBuilt,
                BootstrapActiveScene = _bootstrapActiveScene,
                BootstrapLoadedScenes = new List<string>(_bootstrapLoadedScenes),
                ActorDiscoveryStatus = _actorDiscoveryStatus,
                CapabilityDiscoveryStatus = _capabilityDiscoveryStatus,
                SummonProbeStatus = _summonProbeStatus,
                MoveProbeStatus = _moveProbeStatus,
                MultiActorProbeStatus = _multiActorProbeStatus,
                ActorInteractionProbeStatus = _actorInteractionProbeStatus,
                BootstrapLatestDumpPath = _bootstrapLatestDumpPath,
                BootstrapLatestDumpPaths = new List<string>(_bootstrapLatestDumpPaths)
            };
        }
    }

    public TrainingBridgeStatus GetBridgeStatus()
    {
        lock (_gate)
        {
            return new TrainingBridgeStatus
            {
                type = "status_result",
                protocolVersion = TrainingProtocol.Version,
                requestType = "status",
                bridgeRunning = false,
                sceneReady = _isReady,
                sourceSceneName = _sourceSceneName,
                trainingSceneName = _currentTrainingSceneName,
                actorName = _currentPlayerRootName,
                playerRootPath = _currentPlayerRootPath,
                playerRootFound = _playerRootFound,
                episodeId = _currentEpisodeId,
                episodeStep = _currentEpisodeStepCount,
                tick = _lastTick,
                timeSeconds = _lastTimeSeconds,
                lastRequestType = null,
                lastReward = null,
                lastError = _lastError,
                bootstrapStage = _bootstrapStage,
                bootstrapReady = _bootstrapReady,
                bootstrapFailed = _bootstrapFailed,
                bootstrapFailureReason = _bootstrapFailureReason,
                gymLoaded = _bootstrapGymLoaded,
                loaderRemoved = _bootstrapLoaderRemoved,
                loaderInert = _bootstrapLoaderInert,
                primaryActorFound = _bootstrapPrimaryActorFound,
                arenaBuilt = _bootstrapArenaBuilt,
                activeScene = _bootstrapActiveScene,
                loadedScenes = new List<string>(_bootstrapLoadedScenes),
                actorDiscoveryStatus = _actorDiscoveryStatus,
                capabilityDiscoveryStatus = _capabilityDiscoveryStatus,
                summonProbeStatus = _summonProbeStatus,
                moveProbeStatus = _moveProbeStatus,
                multiActorProbeStatus = _multiActorProbeStatus,
                actorInteractionProbeStatus = _actorInteractionProbeStatus,
                latestDumpPath = _bootstrapLatestDumpPath,
                latestDumpPaths = new List<string>(_bootstrapLatestDumpPaths),
                error = null
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
            $"bootstrapStage={OrNone(status.BootstrapStage)} " +
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
    public string BootstrapStage { get; set; }
    public bool BootstrapReady { get; set; }
    public bool BootstrapFailed { get; set; }
    public string BootstrapFailureReason { get; set; }
    public bool BootstrapGymLoaded { get; set; }
    public bool BootstrapLoaderRemoved { get; set; }
    public bool BootstrapLoaderInert { get; set; }
    public bool BootstrapPrimaryActorFound { get; set; }
    public bool BootstrapArenaBuilt { get; set; }
    public string BootstrapActiveScene { get; set; }
    public List<string> BootstrapLoadedScenes { get; set; }
    public string ActorDiscoveryStatus { get; set; }
    public string CapabilityDiscoveryStatus { get; set; }
    public string SummonProbeStatus { get; set; }
    public string MoveProbeStatus { get; set; }
    public string MultiActorProbeStatus { get; set; }
    public string ActorInteractionProbeStatus { get; set; }
    public string BootstrapLatestDumpPath { get; set; }
    public List<string> BootstrapLatestDumpPaths { get; set; }
}

internal sealed class TrainingBridgeStatus
{
    public string type { get; set; }
    public string protocolVersion { get; set; }
    public string requestType { get; set; }
    public bool bridgeRunning { get; set; }
    public bool sceneReady { get; set; }
    public string sourceSceneName { get; set; }
    public string trainingSceneName { get; set; }
    public string actorName { get; set; }
    public string playerRootPath { get; set; }
    public bool playerRootFound { get; set; }
    public int episodeId { get; set; }
    public int episodeStep { get; set; }
    public int tick { get; set; }
    public float timeSeconds { get; set; }
    public string lastRequestType { get; set; }
    public float? lastReward { get; set; }
    public string lastError { get; set; }
    public string bootstrapStage { get; set; }
    public bool bootstrapReady { get; set; }
    public bool bootstrapFailed { get; set; }
    public string bootstrapFailureReason { get; set; }
    public bool gymLoaded { get; set; }
    public bool loaderRemoved { get; set; }
    public bool loaderInert { get; set; }
    public bool primaryActorFound { get; set; }
    public bool arenaBuilt { get; set; }
    public string activeScene { get; set; }
    public List<string> loadedScenes { get; set; }
    public string actorDiscoveryStatus { get; set; }
    public string capabilityDiscoveryStatus { get; set; }
    public string summonProbeStatus { get; set; }
    public string moveProbeStatus { get; set; }
    public string multiActorProbeStatus { get; set; }
    public string actorInteractionProbeStatus { get; set; }
    public string latestDumpPath { get; set; }
    public List<string> latestDumpPaths { get; set; }
    public TrainingBridgeErrorInfo error { get; set; }
}
