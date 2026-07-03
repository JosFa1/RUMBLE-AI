using System;
using System.Collections.Generic;
using UnityEngine;

namespace AI_Train;

internal enum TrainingBootstrapStage
{
    Uninitialized = 0,
    InitialInventory = 1,
    RequestGymLoad = 2,
    WaitForGymLoaded = 3,
    RemoveLoaderScene = 4,
    GymInventory = 5,
    DiscoverPrimaryActor = 6,
    DiscoverActorCapabilities = 7,
    ProbeSingleActorSummons = 8,
    ProbeMoveModifiers = 9,
    ProbeMultiActorSupport = 10,
    ProbeActorInteraction = 11,
    BuildMinimalArena = 12,
    Ready = 13,
    Failed = 14
}

internal sealed class TrainingBootstrapState
{
    public string stage { get; set; }
    public bool ready { get; set; }
    public bool failed { get; set; }
    public string failureReason { get; set; }
    public bool gymLoaded { get; set; }
    public bool loaderRemoved { get; set; }
    public bool loaderInert { get; set; }
    public bool primaryActorFound { get; set; }
    public bool arenaBuilt { get; set; }
    public string activeScene { get; set; }
    public List<string> loadedScenes { get; set; } = new();
    public string actorDiscoveryStatus { get; set; } = "not_run";
    public string capabilityDiscoveryStatus { get; set; } = "not_run";
    public string summonProbeStatus { get; set; } = "not_run";
    public string moveProbeStatus { get; set; } = "not_run";
    public string multiActorProbeStatus { get; set; } = "not_run";
    public string actorInteractionProbeStatus { get; set; } = "not_run";
    public string mode { get; set; }
    public string actorMode { get; set; }
    public string lastAction { get; set; }
    public string lastReportPath { get; set; }
    public List<string> latestDumpPaths { get; set; } = new();
}

internal sealed class TrainingBootstrapOrchestrator
{
    private readonly Func<string, bool, TrainingBootstrapScanResult> _scanScenes;
    private readonly Func<string, bool> _requestGymLoad;
    private readonly Func<string, bool> _removeLoaderScene;
    private readonly Func<string, TrainingBootstrapDiscoveryResult> _discoverPrimaryActor;
    private readonly Func<string, TrainingBootstrapDiscoveryResult> _discoverCapabilities;
    private readonly Func<string, bool> _buildTrainingArena;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly Action<string, object> _writeReport;
    private readonly TrainingBootstrapState _state = new();
    private TrainingBootstrapStage _stage = TrainingBootstrapStage.Uninitialized;
    private float _nextTickTime;
    private int _gymWaitScans;
    private int _gymLoadRequestAttempts;
    private bool _loaderCleanupRequested;
    private int _loaderCleanupScans;

    public TrainingBootstrapOrchestrator(
        Func<string, bool, TrainingBootstrapScanResult> scanScenes,
        Func<string, bool> requestGymLoad,
        Func<string, bool> removeLoaderScene,
        Func<string, TrainingBootstrapDiscoveryResult> discoverPrimaryActor,
        Func<string, TrainingBootstrapDiscoveryResult> discoverCapabilities,
        Func<string, bool> buildTrainingArena,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string, object> writeReport)
    {
        _scanScenes = scanScenes;
        _requestGymLoad = requestGymLoad;
        _removeLoaderScene = removeLoaderScene;
        _discoverPrimaryActor = discoverPrimaryActor;
        _discoverCapabilities = discoverCapabilities;
        _buildTrainingArena = buildTrainingArena;
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
        _writeReport = writeReport ?? ((_, _) => { });
        _state.stage = _stage.ToString();
        _state.mode = "staged";
        _state.actorMode = "BootstrapRig";
    }

    public TrainingBootstrapState State => _state;

    public void Start(string reason)
    {
        if (_stage != TrainingBootstrapStage.Uninitialized)
        {
            return;
        }

        TransitionTo(TrainingBootstrapStage.InitialInventory, reason);
    }

    public void ResetAndRetry(string reason)
    {
        var previous = _stage;
        _stage = TrainingBootstrapStage.Uninitialized;
        _gymWaitScans = 0;
        _gymLoadRequestAttempts = 0;
        _loaderCleanupRequested = false;
        _loaderCleanupScans = 0;
        _nextTickTime = 0f;
        _state.stage = _stage.ToString();
        _state.ready = false;
        _state.failed = false;
        _state.failureReason = null;
        _state.gymLoaded = false;
        _state.loaderRemoved = false;
        _state.loaderInert = false;
        _state.primaryActorFound = false;
        _state.arenaBuilt = false;
        _state.activeScene = null;
        _state.loadedScenes = new List<string>();
        _state.actorDiscoveryStatus = "not_run";
        _state.capabilityDiscoveryStatus = "not_run";
        _state.lastAction = reason;
        _logInfo($"Training bootstrap reset requested: {previous} -> {TrainingBootstrapStage.Uninitialized} ({reason}).");
        _writeReport($"bootstrap_stage_{DateTime.UtcNow:yyyyMMdd_HHmmss}_Reset.json", _state);
        TransitionTo(TrainingBootstrapStage.InitialInventory, reason);
    }

    public void Tick()
    {
        if (_stage == TrainingBootstrapStage.Ready || _stage == TrainingBootstrapStage.Failed)
        {
            return;
        }

        if (Time.unscaledTime < _nextTickTime)
        {
            return;
        }

        _nextTickTime = Time.unscaledTime + 0.5f;

        switch (_stage)
        {
            case TrainingBootstrapStage.InitialInventory:
                RunInitialInventory();
                break;
            case TrainingBootstrapStage.RequestGymLoad:
                RunRequestGymLoad();
                break;
            case TrainingBootstrapStage.WaitForGymLoaded:
                RunWaitForGymLoaded();
                break;
            case TrainingBootstrapStage.RemoveLoaderScene:
                RunRemoveLoaderScene();
                break;
            case TrainingBootstrapStage.GymInventory:
                RunGymInventory();
                break;
            case TrainingBootstrapStage.DiscoverPrimaryActor:
                RunDiscoverPrimaryActor();
                break;
            case TrainingBootstrapStage.DiscoverActorCapabilities:
                RunDiscoverActorCapabilities();
                break;
            case TrainingBootstrapStage.BuildMinimalArena:
                RunBuildMinimalArena();
                break;
        }
    }

    public void MarkReady(string reason)
    {
        _state.ready = true;
        _state.failed = false;
        _state.failureReason = null;
        _state.arenaBuilt = true;
        TransitionTo(TrainingBootstrapStage.Ready, reason);
    }

    public void Fail(string reason)
    {
        _state.ready = false;
        _state.failed = true;
        _state.failureReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        TransitionTo(TrainingBootstrapStage.Failed, reason);
    }

    public void RecordSceneInventory(TrainingBootstrapScanResult result)
    {
        ApplyScanResult(result);
    }

    public void RecordActorDiscovery(TrainingBootstrapDiscoveryResult result)
    {
        _state.actorDiscoveryStatus = result != null && result.Succeeded && result.PrimaryActorFound ? "confirmed" : "failed";
        ApplyDiscoveryResult(result);
    }

    public void RecordCapabilityDiscovery(TrainingBootstrapDiscoveryResult result)
    {
        _state.capabilityDiscoveryStatus = result != null && result.Succeeded ? "complete" : "failed";
        ApplyDiscoveryResult(result);
    }

    public void RecordProbeStatus(string probeName, string status, string dumpPath)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status;
        switch (probeName)
        {
            case "summon":
                _state.summonProbeStatus = normalizedStatus;
                break;
            case "move":
                _state.moveProbeStatus = normalizedStatus;
                break;
            case "multiActor":
                _state.multiActorProbeStatus = normalizedStatus;
                break;
            case "interaction":
                _state.actorInteractionProbeStatus = normalizedStatus;
                break;
        }

        AddDumpPath(dumpPath);
    }

    public void RecordArenaBuild(string dumpPath, bool succeeded)
    {
        _state.arenaBuilt = succeeded;
        AddDumpPath(dumpPath);
    }

    private void RunInitialInventory()
    {
        var result = SafeScan("bootstrap-initial-inventory");
        ApplyScanResult(result);
        if (!result.Succeeded)
        {
            Fail(result.FailureReason ?? "initial-scene-inventory-failed");
            return;
        }

        TransitionTo(result.HasGymLikeScene ? TrainingBootstrapStage.RemoveLoaderScene : TrainingBootstrapStage.RequestGymLoad, result.HasGymLikeScene ? "gym-already-loaded" : "gym-not-loaded");
    }

    private void RunRequestGymLoad()
    {
        if (_requestGymLoad("staged-request-gym-load"))
        {
            _gymLoadRequestAttempts++;
            _gymWaitScans = 0;
            TransitionTo(
                TrainingBootstrapStage.WaitForGymLoaded,
                $"gym-load-requested-attempt-{_gymLoadRequestAttempts}");
            return;
        }

        Fail(_gymLoadRequestAttempts == 0
            ? "gym-load-request-failed-no-candidate"
            : $"gym-load-candidates-exhausted-after-{_gymLoadRequestAttempts}-unconfirmed-attempts");
    }

    private void RunWaitForGymLoaded()
    {
        var result = SafeScan("bootstrap-wait-for-gym");
        ApplyScanResult(result);
        _gymWaitScans++;
        if (result.HasGymLikeScene)
        {
            TransitionTo(TrainingBootstrapStage.RemoveLoaderScene, "gym-confirmed-by-inventory");
            return;
        }

        if (_gymWaitScans >= 20)
        {
            if (_gymLoadRequestAttempts < 4)
            {
                TransitionTo(
                    TrainingBootstrapStage.RequestGymLoad,
                    $"gym-not-confirmed-after-attempt-{_gymLoadRequestAttempts}-trying-next-candidate");
                return;
            }

            Fail($"gym-load-requested-but-scene-not-confirmed-after-{_gymLoadRequestAttempts}-attempts");
        }
    }

    private void RunRemoveLoaderScene()
    {
        if (!_loaderCleanupRequested)
        {
            var before = SafeScan("bootstrap-before-loader-cleanup");
            ApplyScanResult(before);
            if (!before.Succeeded)
            {
                Fail(before.FailureReason ?? "loader-pre-cleanup-inventory-failed");
                return;
            }

            if (!before.HasLoaderLikeScene || before.LoaderInert)
            {
                _state.loaderRemoved = !before.HasLoaderLikeScene;
                _state.loaderInert = before.LoaderInert;
                TransitionTo(
                    TrainingBootstrapStage.GymInventory,
                    before.LoaderInert ? "loader-already-inert" : "loader-not-present");
                return;
            }

            if (!_removeLoaderScene("staged-remove-loader"))
            {
                Fail("loader-cleanup-request-failed");
                return;
            }

            _loaderCleanupRequested = true;
            _loaderCleanupScans = 0;
            _state.lastAction = "loader-cleanup-requested-awaiting-inventory";
            _writeReport(
                $"bootstrap_stage_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{_stage}_cleanup_requested.json",
                _state);
            return;
        }

        var after = SafeScan("bootstrap-after-loader-cleanup");
        ApplyScanResult(after);
        _loaderCleanupScans++;
        if (!after.Succeeded)
        {
            if (_loaderCleanupScans >= 10)
            {
                Fail(after.FailureReason ?? "loader-post-cleanup-inventory-failed");
            }
            return;
        }

        if (!after.HasLoaderLikeScene || after.LoaderInert)
        {
            _state.loaderRemoved = !after.HasLoaderLikeScene;
            _state.loaderInert = after.LoaderInert;
            TransitionTo(
                TrainingBootstrapStage.GymInventory,
                after.LoaderInert ? "loader-confirmed-inert-by-inventory" : "loader-removal-confirmed-by-inventory");
            return;
        }

        if (_loaderCleanupScans >= 10)
        {
            Fail("loader-remained-active-after-cleanup");
        }
    }

    private void RunGymInventory()
    {
        var result = SafeScan("bootstrap-gym-inventory");
        ApplyScanResult(result);
        if (!result.HasGymLikeScene)
        {
            Fail("gym-missing-after-loader-cleanup");
            return;
        }

        TransitionTo(TrainingBootstrapStage.DiscoverPrimaryActor, "gym-inventory-complete");
    }

    private void RunDiscoverPrimaryActor()
    {
        var result = SafeDiscover(_discoverPrimaryActor, "bootstrap-discover-primary-actor");
        ApplyDiscoveryResult(result);
        if (!result.Succeeded || !result.PrimaryActorFound)
        {
            _state.actorDiscoveryStatus = "failed";
            Fail(result.FailureReason ?? "primary-actor-not-found");
            return;
        }

        _state.actorDiscoveryStatus = "confirmed";
        TransitionTo(TrainingBootstrapStage.DiscoverActorCapabilities, "primary-actor-candidate-found");
    }

    private void RunDiscoverActorCapabilities()
    {
        var result = SafeDiscover(_discoverCapabilities, "bootstrap-discover-actor-capabilities");
        ApplyDiscoveryResult(result);
        if (!result.Succeeded)
        {
            _state.capabilityDiscoveryStatus = "failed";
            Fail(result.FailureReason ?? "capability-discovery-failed");
            return;
        }

        _state.capabilityDiscoveryStatus = "complete";
        TransitionTo(TrainingBootstrapStage.BuildMinimalArena, "capability-discovery-report-written");
    }

    private void RunBuildMinimalArena()
    {
        if (!_buildTrainingArena("staged-build-minimal-arena"))
        {
            Fail("training-arena-build-failed");
            return;
        }

        var finalInventory = SafeScan("bootstrap-post-arena-inventory");
        ApplyScanResult(finalInventory);
        if (!finalInventory.Succeeded)
        {
            Fail(finalInventory.FailureReason ?? "post-arena-scene-inventory-failed");
            return;
        }

        if (!finalInventory.HasGymLikeScene)
        {
            Fail("gym-missing-after-arena-build");
            return;
        }

        if (!finalInventory.HasPlayerCandidate)
        {
            Fail("actor-missing-after-arena-build");
            return;
        }

        MarkReady("training-arena-built-and-confirmed-by-inventory");
    }

    private TrainingBootstrapScanResult SafeScan(string reason)
    {
        try
        {
            return _scanScenes(reason, false) ?? new TrainingBootstrapScanResult();
        }
        catch (Exception ex)
        {
            _logWarn($"Bootstrap scan failed ({reason}): {ex.Message}");
            return new TrainingBootstrapScanResult { FailureReason = ex.Message };
        }
    }

    private TrainingBootstrapDiscoveryResult SafeDiscover(Func<string, TrainingBootstrapDiscoveryResult> discovery, string reason)
    {
        try
        {
            return discovery?.Invoke(reason) ?? new TrainingBootstrapDiscoveryResult
            {
                Succeeded = false,
                FailureReason = "discovery-callback-missing"
            };
        }
        catch (Exception ex)
        {
            _logWarn($"Bootstrap discovery failed ({reason}): {ex.Message}");
            return new TrainingBootstrapDiscoveryResult
            {
                Succeeded = false,
                FailureReason = ex.Message
            };
        }
    }

    private void ApplyScanResult(TrainingBootstrapScanResult result)
    {
        if (result == null)
        {
            return;
        }

        _state.gymLoaded = result.HasGymLikeScene;
        _state.loaderRemoved = !result.HasLoaderLikeScene;
        _state.loaderInert = result.LoaderInert;
        _state.primaryActorFound = result.HasPlayerCandidate;
        _state.activeScene = result.ActiveScene;
        _state.loadedScenes = result.LoadedScenes != null ? new List<string>(result.LoadedScenes) : new List<string>();
        AddDumpPath(result.LatestDumpPath);
    }

    private void ApplyDiscoveryResult(TrainingBootstrapDiscoveryResult result)
    {
        if (result == null)
        {
            return;
        }

        _state.primaryActorFound = result.PrimaryActorFound;
        AddDumpPath(result.LatestDumpPath);
    }

    private void AddDumpPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _state.lastReportPath = path;
        _state.latestDumpPaths.Add(path);
        if (_state.latestDumpPaths.Count > 32)
        {
            _state.latestDumpPaths.RemoveAt(0);
        }
    }

    private void TransitionTo(TrainingBootstrapStage nextStage, string reason)
    {
        if (_stage == nextStage)
        {
            return;
        }

        var previous = _stage;
        _stage = nextStage;
        _state.stage = _stage.ToString();
        _state.lastAction = reason;
        _state.ready = _stage == TrainingBootstrapStage.Ready;
        _state.failed = _stage == TrainingBootstrapStage.Failed;
        _logInfo($"Training bootstrap stage: {previous} -> {_stage} ({reason}).");
        _writeReport($"bootstrap_stage_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{_stage}.json", _state);
    }
}

internal sealed class TrainingBootstrapScanResult
{
    public bool Succeeded { get; set; }
    public bool HasGymLikeScene { get; set; }
    public bool HasLoaderLikeScene { get; set; }
    public bool LoaderInert { get; set; }
    public bool HasPlayerCandidate { get; set; }
    public string BestPlayerCandidatePath { get; set; }
    public string ActiveScene { get; set; }
    public List<string> LoadedScenes { get; set; }
    public string LatestDumpPath { get; set; }
    public string FailureReason { get; set; }
}

internal sealed class TrainingBootstrapDiscoveryResult
{
    public bool Succeeded { get; set; }
    public bool PrimaryActorFound { get; set; }
    public string PrimaryActorPath { get; set; }
    public string LatestDumpPath { get; set; }
    public string FailureReason { get; set; }
}
