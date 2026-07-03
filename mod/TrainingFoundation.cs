using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityObject = UnityEngine.Object;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace AI_Train;
internal sealed class TrainingFoundation : IDisposable
{
    private const string TrainingSceneName = "AI_Train_Training";
    private const float AutoScanIntervalSeconds = 2.5f;
    private static readonly bool UseStagedBootstrap = true;
    private static readonly bool EnableLegacyBootstrapFallback = false;
    private static readonly bool EnableFullSceneHierarchyDump = false;
    private static readonly bool EnableExplorationProbes = true;
    private static readonly bool EnableArenaPruning = true;
    private static readonly bool EnableActorCloneProbes = false;
    private static readonly bool EnableSummonProbes = false;
    private static readonly bool EnableMoveProbes = false;
    private static readonly bool EnableActorInteractionProbes = false;
    private static readonly bool AutoBuildTrainingScene = true;
    private const int MaxRootsToLogPerScene = 128;

    private static readonly string[] PlayerHints =
    {
        "PlayerController", "PlayerMovement", "PlayerPhysics", "PlayerAnimator", "PlayerCamera",
        "PlayerResetSystem", "BootLoaderPlayer", "LocalPlayer", "PlayerData", "PlayerUIBar",
        "VRIK", "Avatar", "XR", "Rig", "Hand", "Controller"
    };

    private static readonly string[] SupportHints =
    {
        "GameManager", "SceneManager", "PlayerManager", "InputSystem", "EventSystem", "Camera",
        "XR", "Tracking", "BootLoader", "Measurement", "Comfort", "Network", "Photon",
        "PlayFab", "PoolManager", "Audio", "UI", "LocalPlayer", "Controller"
    };

    private static readonly string[] EnvironmentHints =
    {
        "Arena", "Gym", "Environment", "Structure", "Prop", "Decoration", "Terrain",
        "Light", "Lighting", "VFX", "Particle", "Sky", "Backdrop", "Room", "Level",
        "SceneBound", "Challenge", "Hoop", "Pedestal"
    };

    private static readonly string[] ArenaBackgroundHints =
    {
        "Background", "Backdrop", "Mountain", "Sky", "Horizon", "Terrain", "Landscape",
        "World", "Cloud", "Lighting", "Sun", "Moon", "Environment"
    };

    private static readonly string[] ArenaFloorHints =
    {
        "Floor", "Ground", "Terrain", "Platform", "Stage", "ArenaBase", "GymBase"
    };

    private static readonly string[] ExplicitArenaClutterHints =
    {
        "Challenge", "Hoop", "Pedestal", "Decoration", "Decor", "Particle", "VFX",
        "Tutorial", "Scoreboard", "Minigame"
    };

    private static readonly string[] PriorityCapabilityTypeNames =
    {
        "Il2CppRUMBLE.Players.PlayerController",
        "Il2CppRUMBLE.Players.Subsystems.PlayerMovement",
        "Il2CppRUMBLE.Players.Subsystems.PlayerPhysics",
        "Il2CppRUMBLE.Players.Subsystems.PlayerHealth",
        "Il2CppRUMBLE.Players.Subsystems.Hitboxes.PlayerHitboxSystem",
        "Il2CppRUMBLE.Players.Subsystems.PlayerPoseSystem",
        "Il2CppRUMBLE.Players.Subsystems.PlayerNetworking",
        "Il2CppRUMBLE.MoveSystem.PlayerStackProcessor",
        "Il2CppRUMBLE.MoveSystem.StructureSpawner",
        "Il2CppRUMBLE.MoveSystem.SpawnStructureModifier",
        "Il2CppRUMBLE.MoveSystem.SpawnStructureGroundedModifier",
        "Il2CppRUMBLE.MoveSystem.SpawnStructureNonGroundedModifier",
        "Il2CppRUMBLE.MoveSystem.Structure",
        "Il2CppRUMBLE.Slabs.SlabOwnership",
        "Il2CppRUMBLE.Interactions.CollisionHandler",
        "Il2CppRUMBLE.Environment.Howard.Howard"
    };

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HashSet<string> _seenScenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _movedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _destroyedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gymTransitionCandidateAttempts = new(StringComparer.OrdinalIgnoreCase);

    private TrainingEnvironmentManager _trainingEnvironmentManager;
    private ObservationBuilder _observationBuilder;
    private ActionExecutor _actionExecutor;
    private TrainingRuntimeHost _runtimeHost;
    private TrainingMonitorCamera _monitorCamera;
    private TrainingExplorationService _explorationService;
    private TrainingExplorationProbeService _activeProbeService;
    private TrainingBridgeServer _bridgeServer;
    private TrainingBootstrapOrchestrator _bootstrapOrchestrator;
    private string _logRoot;
    private string _dumpRoot;
    private string _logFilePath;
    private StreamWriter _writer;
    private float _nextScanTime;
    private float _nextBootstrapCleanupTime;
    private DateTime _initializedAtUtc;
    private bool _initialScanComplete;
    private bool _gymTransitionAttempted;
    private bool _gymTransitionSucceeded;
    private DateTime _lastGymTransitionAttemptUtc;
    private string _lastActiveSceneName;
    private SceneCandidate _bestPlayerCandidate;
    private GameObject _preservedActorCandidate;
    private List<Type> _capabilityRuntimeTypes;

    public void Initialize()
    {
        _logRoot = Path.Combine(MelonEnvironment.UserDataDirectory, "AI_Train");
        _dumpRoot = Path.Combine(_logRoot, "Dumps");
        Directory.CreateDirectory(_logRoot);
        Directory.CreateDirectory(_dumpRoot);

        _logFilePath = Path.Combine(_logRoot, $"runtime_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
        _writer = new StreamWriter(new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        _initializedAtUtc = DateTime.UtcNow;
        _trainingEnvironmentManager = new TrainingEnvironmentManager(LogInfo, LogWarn, LogError);
        _observationBuilder = new ObservationBuilder(LogInfo, LogWarn);
        _actionExecutor = new ActionExecutor(_trainingEnvironmentManager, LogInfo, LogWarn);
        _runtimeHost = CreateRuntimeHost();
        _monitorCamera = new TrainingMonitorCamera(LogInfo, LogWarn);
        _monitorCamera.EnsureCreated("initialize");
        _explorationService = new TrainingExplorationService(_runtimeHost, _monitorCamera, LogInfo, LogWarn, LogError);
        var bootstrapActions = new TrainingBridgeBootstrapActions
        {
            GetBootstrapReport = GetBootstrapReport,
            RetryBootstrap = RetryBootstrapBridgeRequest,
            RunSceneInventory = RunSceneInventoryBridgeRequest,
            RunActorDiscovery = RunActorDiscoveryBridgeRequest,
            RunCapabilityDiscovery = RunCapabilityDiscoveryBridgeRequest,
            RunSingleActorSummonProbe = RunSingleActorSummonProbeBridgeRequest,
            RunMoveProbe = RunMoveProbeBridgeRequest,
            RunMultiActorProbe = RunMultiActorProbeBridgeRequest,
            RunActorInteractionProbe = RunActorInteractionProbeBridgeRequest,
            RunArenaRebuild = RunArenaRebuildBridgeRequest
        };
        _bridgeServer = new TrainingBridgeServer(_trainingEnvironmentManager, _observationBuilder, _actionExecutor, _explorationService, bootstrapActions, LogInfo, LogWarn, LogError);
        _bootstrapOrchestrator = new TrainingBootstrapOrchestrator(
            ScanAllScenes,
            TryForceGymLoad,
            TryUnloadBootstrapScenes,
            DiscoverPrimaryActor,
            DiscoverActorCapabilities,
            TryBuildTrainingScene,
            LogInfo,
            LogWarn,
            (fileName, payload) => { WriteJson(fileName, payload); });
        _activeProbeService = new TrainingExplorationProbeService(
            ResolvePrimaryActor,
            WriteJson,
            RecordProbeStatus,
            LogInfo,
            LogWarn,
            LogError);

        LogInfo("AI_Train bootstrap initialized.");
        LogInfo($"Bootstrap mode: {(UseStagedBootstrap ? "staged" : "legacy")} legacyFallback={EnableLegacyBootstrapFallback}.");
        LogInfo($"Log file: {_logFilePath}");
        LogInfo("Hotkeys: F6 = toggle monitor camera free-fly, F7 = dump active scenes, F8 = force training scene build, F9 = rescan all, F10 = force gym load, F11 = dump observation, F12 = run debug probe.");

        if (UseStagedBootstrap)
        {
            _bootstrapOrchestrator.Start("initialize");
            _trainingEnvironmentManager.UpdateBootstrapState(_bootstrapOrchestrator.State);
        }
        else
        {
            ScanAllScenes("initialize", true);
        }

        _bridgeServer.StartIfNeeded();
    }

    public void OnUpdate()
    {
        if (Input.GetKeyDown(KeyCode.F7))
        {
            ScanAllScenes("manual-dump", false);
        }

        if (Input.GetKeyDown(KeyCode.F6))
        {
            _monitorCamera?.ToggleFreeFly("manual-toggle");
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            TryBuildTrainingScene("manual-force");
        }

        if (Input.GetKeyDown(KeyCode.F9))
        {
            ScanAllScenes("manual-rescan", !UseStagedBootstrap);
        }

        if (Input.GetKeyDown(KeyCode.F10))
        {
            TryForceGymLoad("manual-force-gym");
        }

        if (Input.GetKeyDown(KeyCode.F11))
        {
            PublishObservation("manual-observation");
        }

        if (Input.GetKeyDown(KeyCode.F12) && EnableExplorationProbes)
        {
            PublishDebugProbe("manual-debug-probe");
        }

        _monitorCamera?.UpdateTarget(_trainingEnvironmentManager?.CurrentPlayerRoot);
        _activeProbeService?.Tick();

        _trainingEnvironmentManager?.UpdateTelemetry(Time.frameCount, Time.unscaledTime);
        if (UseStagedBootstrap)
        {
            _bootstrapOrchestrator?.Tick();
            _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        }
        _bridgeServer?.Pump();
        _bridgeServer?.StartIfNeeded();

        if (!UseStagedBootstrap &&
            (_trainingEnvironmentManager?.IsReady ?? false) &&
            Time.unscaledTime >= _nextBootstrapCleanupTime)
        {
            _nextBootstrapCleanupTime = Time.unscaledTime + 1.0f;
            TryUnloadBootstrapScenes("bootstrap-enforcement");
        }

        if ((!UseStagedBootstrap || EnableLegacyBootstrapFallback) && Time.unscaledTime >= _nextScanTime)
        {
            _nextScanTime = Time.unscaledTime + AutoScanIntervalSeconds;
            if (_initialScanComplete && !(_trainingEnvironmentManager?.IsReady ?? false))
            {
                ScanAllScenes("periodic", true);
            }
        }

        if ((!UseStagedBootstrap || EnableLegacyBootstrapFallback) &&
            !(_trainingEnvironmentManager?.IsReady ?? false) &&
            !_gymTransitionSucceeded &&
            !_gymTransitionAttempted &&
            DateTime.UtcNow - _initializedAtUtc >= TimeSpan.FromSeconds(15))
        {
            TryForceGymLoad("auto-loader-timeout");
        }

        if (_trainingEnvironmentManager?.IsReady ?? false)
        {
            _bootstrapOrchestrator?.MarkReady("environment-manager-ready");
            _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
            _bridgeServer?.StartIfNeeded();
        }
    }

    public void OnLateUpdate()
    {
        _actionExecutor?.Pump(Time.unscaledDeltaTime);
        _monitorCamera?.Tick(Time.unscaledDeltaTime);
    }

    public void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        LogInfo($"Melon scene callback loaded: {sceneName} ({buildIndex})");
        if (!UseStagedBootstrap)
        {
            TryUnloadBootstrapScenes($"loaded:{sceneName}");
            ScanAllScenes($"melon-loaded:{sceneName}", true);
        }
        else
        {
            ScanAllScenes($"melon-loaded:{sceneName}", false);
        }
    }

    public void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        LogInfo($"Melon scene callback unloaded: {sceneName} ({buildIndex})");
    }

    public void Dispose()
    {
        try
        {
            _bridgeServer?.Dispose();
            _activeProbeService?.Dispose();
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // Best effort shutdown.
        }
    }

    private TrainingBootstrapScanResult ScanAllScenes(string reason, bool allowBootstrapActions)
    {
        var scenes = GetLoadedScenes();
        var reports = new List<SceneReport>(scenes.Count);
        var currentBest = _bestPlayerCandidate;
        var detailed = reason.StartsWith("manual", StringComparison.OrdinalIgnoreCase) ||
                       reason.StartsWith("initialize", StringComparison.OrdinalIgnoreCase) ||
                       reason.Contains("loaded", StringComparison.OrdinalIgnoreCase) ||
                       reason.Contains("force", StringComparison.OrdinalIgnoreCase) ||
                       reason.Contains("rescan", StringComparison.OrdinalIgnoreCase);

        foreach (var scene in scenes)
        {
            var report = AnalyzeScene(scene, detailed || !_seenScenes.Contains(scene.name));
            reports.Add(report);
            currentBest = PickBetterCandidate(currentBest, report.BestPlayerCandidate);
            _seenScenes.Add(scene.name);
        }

        currentBest = PickBetterCandidate(currentBest, FindRuntimeActorCandidate(scenes));
        _bestPlayerCandidate = currentBest;
        _initialScanComplete = true;
        _lastActiveSceneName = UnitySceneManager.GetActiveScene().name;

        var sceneBundlePath = WriteSceneBundle(reason, reports, _bestPlayerCandidate);
        var inventoryPath = WriteSceneInventory(reason, reports, _bestPlayerCandidate);
        var dumpPath = inventoryPath ?? sceneBundlePath;

        var hasGymScene = HasGymLikeScene(reports);
        _gymTransitionSucceeded = hasGymScene;
        var scanResult = CreateScanResult(hasGymScene, _bestPlayerCandidate, dumpPath, reports);

        if (!allowBootstrapActions)
        {
            return scanResult;
        }

        if (!hasGymScene)
        {
            if (_trainingEnvironmentManager?.IsReady ?? false)
            {
                TryUnloadBootstrapScenes(reason);
                return scanResult;
            }

            LogInfo($"Gym-like scene not yet loaded ({reason}); requesting gym transition and deferring training scene build.");
            TryForceGymLoad(reason);
            return scanResult;
        }

        if (AutoBuildTrainingScene && !(_trainingEnvironmentManager?.IsReady ?? false) && _bestPlayerCandidate != null)
        {
            TryBuildTrainingScene(reason);
        }

        TryUnloadBootstrapScenes(reason);
        return scanResult;
    }

    private static TrainingBootstrapScanResult CreateScanResult(bool hasGymScene, SceneCandidate candidate, string dumpPath, IEnumerable<SceneReport> reports)
    {
        var reportList = reports.ToList();
        var loaderReports = reportList
            .Where(report => string.Equals(report.LikelySceneRole, "loader", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new TrainingBootstrapScanResult
        {
            Succeeded = true,
            HasGymLikeScene = hasGymScene,
            HasLoaderLikeScene = loaderReports.Count > 0,
            LoaderInert = loaderReports.Count > 0 &&
                          loaderReports.All(report =>
                              report.RootCount == 0 ||
                              report.Roots.All(root => !root.ActiveInHierarchy)),
            HasPlayerCandidate = candidate != null,
            BestPlayerCandidatePath = candidate?.Path,
            ActiveScene = reportList.FirstOrDefault(report => report.IsActive)?.SceneName,
            LoadedScenes = reportList.Select(report => report.SceneName).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            LatestDumpPath = dumpPath
        };
    }

    private List<Scene> GetLoadedScenes()
    {
        var scenes = new List<Scene>();
        for (var i = 0; i < UnitySceneManager.sceneCount; i++)
        {
            var scene = UnitySceneManager.GetSceneAt(i);
            if (scene.IsValid() && scene.isLoaded)
            {
                scenes.Add(scene);
            }
        }

        return scenes;
    }

    private TrainingBootstrapDiscoveryResult DiscoverPrimaryActor(string reason)
    {
        var timestampUtc = DateTime.UtcNow;
        var scenes = GetLoadedScenes();
        var sourceRoot = ResolveActorCandidateObject(scenes);
        var actorRoot = sourceRoot != null ? FindPreferredTrainingActor(sourceRoot) : null;
        var warnings = new List<string>();
        var missing = new List<string>();

        if (_bestPlayerCandidate == null)
        {
            warnings.Add("no_scene_player_candidate");
        }

        if (sourceRoot == null)
        {
            warnings.Add("candidate_root_not_found");
            missing.Add("actorRoot");
        }

        if (actorRoot == null)
        {
            warnings.Add("preferred_actor_root_not_found");
            missing.Add("actorRoot");
        }

        var head = actorRoot != null ? FindActorTransform(actorRoot, ActorTransformRole.Head) : null;
        var leftHand = actorRoot != null ? FindActorTransform(actorRoot, ActorTransformRole.LeftHand) : null;
        var rightHand = actorRoot != null ? FindActorTransform(actorRoot, ActorTransformRole.RightHand) : null;
        var actorPath = actorRoot != null ? GetPath(actorRoot.transform) : null;
        var rejectedActorPath = IsRejectedActorPath(actorPath);

        if (head == null)
        {
            missing.Add("head");
        }
        if (leftHand == null)
        {
            missing.Add("leftHand");
        }
        if (rightHand == null)
        {
            missing.Add("rightHand");
        }
        if (leftHand != null && rightHand != null && leftHand == rightHand)
        {
            warnings.Add("left_and_right_hand_resolved_to_same_transform");
            missing.Add("distinctHands");
        }
        if (rejectedActorPath)
        {
            warnings.Add("selected_path_is_preview_or_non_actor_container");
            missing.Add("realActorEvidence");
        }
        if (_bestPlayerCandidate?.IsStrongActor != true)
        {
            warnings.Add("strong_actor_component_evidence_missing");
            missing.Add("strongActorEvidence");
        }

        var componentEntries = actorRoot != null
            ? GetTypedCapabilityEntries(actorRoot, includeMembers: false, includeGlobal: false, maxEntries: 160)
            : new List<ComponentDiscoveryEntry>();
        var actorValid = actorRoot != null &&
                         head != null &&
                         leftHand != null &&
                         rightHand != null &&
                         leftHand != rightHand &&
                         !rejectedActorPath &&
                         _bestPlayerCandidate?.IsStrongActor == true;
        var report = new
        {
            timestampUtc,
            reason,
            actorRootFound = actorRoot != null,
            actorValidated = actorValid,
            actorRootPath = actorPath,
            sourceRootPath = sourceRoot != null ? GetPath(sourceRoot.transform) : null,
            sceneName = actorRoot != null ? actorRoot.scene.name : sourceRoot != null ? sourceRoot.scene.name : null,
            actorConfidenceScore = _bestPlayerCandidate?.Score ?? 0,
            actorEvidence = _bestPlayerCandidate?.Evidence ?? new List<string>(),
            strongActorEvidence = _bestPlayerCandidate?.IsStrongActor ?? false,
            actorRootComponents = actorRoot != null ? GetComponentTypeNames(actorRoot).ToList() : new List<string>(),
            actorTypedComponentTypes = componentEntries
                .Where(entry => string.Equals(entry.MemberKind, "component", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.TypeFullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(typeName => typeName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            headPath = head != null ? GetPath(head) : null,
            leftHandPath = leftHand != null ? GetPath(leftHand) : null,
            rightHandPath = rightHand != null ? GetPath(rightHand) : null,
            possibleInputComponents = FilterEntries(componentEntries, ActorCapabilityHints.Input),
            possibleMoveComponents = FilterEntries(componentEntries, ActorCapabilityHints.Move),
            possibleSummonComponents = FilterEntries(componentEntries, ActorCapabilityHints.Summon),
            possibleModifierComponents = FilterEntries(componentEntries, ActorCapabilityHints.Modifier),
            possibleAttackHealthDamageComponents = FilterEntries(componentEntries, ActorCapabilityHints.AttackHealthDamage),
            warnings,
            missingRequiredPieces = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        var path = WriteJson($"actor_discovery_{timestampUtc:yyyyMMdd_HHmmss}.json", report);
        WriteJson("latest_actor_discovery.json", report);
        LogInfo(
            $"Actor discovery report written ({reason}). actor={(actorRoot != null ? GetPath(actorRoot.transform) : "none")} " +
            $"validated={actorValid} strong={_bestPlayerCandidate?.IsStrongActor == true} missing={string.Join(",", missing)}.");

        return new TrainingBootstrapDiscoveryResult
        {
            Succeeded = actorValid,
            PrimaryActorFound = actorValid,
            PrimaryActorPath = actorValid ? actorPath : null,
            LatestDumpPath = path,
            FailureReason = actorValid ? null : "primary-actor-validation-failed"
        };
    }

    private TrainingBootstrapDiscoveryResult DiscoverActorCapabilities(string reason)
    {
        var timestampUtc = DateTime.UtcNow;
        var scenes = GetLoadedScenes();
        var sourceRoot = ResolveActorCandidateObject(scenes);
        var actorRoot = sourceRoot != null ? FindPreferredTrainingActor(sourceRoot) : null;
        var typedEntries = GetTypedCapabilityEntries(
            actorRoot,
            includeMembers: true,
            includeGlobal: true,
            maxEntries: 420);
        var typedCandidateCount = typedEntries.Count;
        var genericFallbackUsed = false;
        if (typedEntries.Count == 0)
        {
            genericFallbackUsed = true;
            typedEntries = (actorRoot != null
                    ? GetComponentDiscoveryEntries(actorRoot, ActorCapabilityHints.All, includeMembers: true, maxEntries: 220)
                    : new List<ComponentDiscoveryEntry>())
                .Concat(GetGlobalCapabilityEntries(maxEntries: 220))
                .ToList();
        }
        var allCandidates = SelectCapabilityCandidates(typedEntries, maxEntries: 420);

        var warnings = new List<string>();
        if (actorRoot == null)
        {
            warnings.Add("primary_actor_missing");
        }
        if (allCandidates.Count == 0)
        {
            warnings.Add("no_capability_candidates_found");
        }

        var report = new
        {
            timestampUtc,
            reason,
            actorRootPath = actorRoot != null ? GetPath(actorRoot.transform) : null,
            sceneName = actorRoot != null ? actorRoot.scene.name : null,
            passiveOnly = true,
            typedCandidateCount,
            genericFallbackUsed,
            candidateCount = allCandidates.Count,
            likelySummonSystems = FilterEntries(allCandidates, ActorCapabilityHints.Summon),
            likelyMoveSystems = FilterEntries(allCandidates, ActorCapabilityHints.Move),
            likelyModifierSystems = FilterEntries(allCandidates, ActorCapabilityHints.Modifier),
            likelyOwnershipSystems = FilterEntries(allCandidates, ActorCapabilityHints.Ownership),
            likelyDamageHitSystems = FilterEntries(allCandidates, ActorCapabilityHints.AttackHealthDamage),
            likelyInputGestureSystems = FilterEntries(allCandidates, ActorCapabilityHints.InputGesture),
            candidates = allCandidates,
            warnings
        };

        var path = WriteJson($"capability_discovery_{timestampUtc:yyyyMMdd_HHmmss}.json", report);
        WriteJson("latest_capability_discovery.json", report);
        LogInfo($"Capability discovery report written ({reason}). candidates={allCandidates.Count} actor={(actorRoot != null ? GetPath(actorRoot.transform) : "none")}.");

        return new TrainingBootstrapDiscoveryResult
        {
            Succeeded = actorRoot != null,
            PrimaryActorFound = actorRoot != null,
            PrimaryActorPath = actorRoot != null ? GetPath(actorRoot.transform) : null,
            LatestDumpPath = path,
            FailureReason = actorRoot != null ? null : "capability-discovery-primary-actor-missing"
        };
    }

    private static List<ComponentDiscoveryEntry> SelectCapabilityCandidates(
        IEnumerable<ComponentDiscoveryEntry> entries,
        int maxEntries)
    {
        var source = entries
            .Where(entry => entry != null)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.TypeFullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selected = new List<ComponentDiscoveryEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRange(IEnumerable<ComponentDiscoveryEntry> candidates, int limit)
        {
            var added = 0;
            foreach (var candidate in candidates)
            {
                if (selected.Count >= maxEntries || added >= limit)
                {
                    break;
                }

                var key =
                    $"{candidate.Source}|{candidate.ComponentPath}|{candidate.TypeFullName}|" +
                    $"{candidate.MemberKind}|{candidate.MemberName}|{candidate.ParameterSummary}";
                if (!keys.Add(key))
                {
                    continue;
                }

                selected.Add(candidate);
                added++;
            }
        }

        AddRange(
            source.Where(entry => string.Equals(entry.Source, "actor", StringComparison.OrdinalIgnoreCase)),
            96);
        AddRange(FilterEntries(source, ActorCapabilityHints.Summon), 48);
        AddRange(FilterEntries(source, ActorCapabilityHints.Move), 48);
        AddRange(FilterEntries(source, ActorCapabilityHints.Modifier), 48);
        AddRange(FilterEntries(source, ActorCapabilityHints.Ownership), 48);
        AddRange(FilterEntries(source, ActorCapabilityHints.AttackHealthDamage), 48);
        AddRange(FilterEntries(source, ActorCapabilityHints.InputGesture), 48);
        AddRange(source, maxEntries);

        return selected
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.TypeFullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private TrainingBridgeBootstrapActionResult GetBootstrapReport(string reason)
    {
        var status = _trainingEnvironmentManager?.GetBridgeStatus();
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = true,
            Status = status?.bootstrapFailed == true ? "failed" : status?.bootstrapReady == true ? "ready" : "running",
            ReportPath = status?.latestDumpPath,
            Message = $"Bootstrap stage is {status?.bootstrapStage ?? "unknown"}."
        };
    }

    private TrainingBridgeBootstrapActionResult RetryBootstrapBridgeRequest(string reason)
    {
        if (!UseStagedBootstrap)
        {
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = false,
                Status = "unavailable",
                ErrorCode = "staged_bootstrap_disabled",
                Message = "Bootstrap retry is only available in staged mode."
            };
        }

        if (_trainingEnvironmentManager?.IsReady == true)
        {
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = true,
                Status = "already_ready",
                ReportPath = _bootstrapOrchestrator?.State?.lastReportPath,
                Message = "The training environment is already ready; bootstrap state was not reset."
            };
        }

        _bootstrapOrchestrator?.ResetAndRetry(reason);
        _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = _bootstrapOrchestrator != null,
            Status = _bootstrapOrchestrator != null ? "running" : "failed",
            ErrorCode = _bootstrapOrchestrator != null ? null : "bootstrap_orchestrator_missing",
            ReportPath = _bootstrapOrchestrator?.State?.lastReportPath,
            Message = _bootstrapOrchestrator != null
                ? "Staged bootstrap reset and restarted at InitialInventory."
                : "The staged bootstrap orchestrator is unavailable."
        };
    }

    private TrainingBridgeBootstrapActionResult RunSceneInventoryBridgeRequest(string reason)
    {
        var result = ScanAllScenes(reason, false);
        _bootstrapOrchestrator?.RecordSceneInventory(result);
        _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = result != null,
            Status = "complete",
            ReportPath = result?.LatestDumpPath,
            Message = $"Scene inventory written. activeScene={result?.ActiveScene ?? "unknown"} loadedScenes={result?.LoadedScenes?.Count ?? 0}."
        };
    }

    private TrainingBridgeBootstrapActionResult RunActorDiscoveryBridgeRequest(string reason)
    {
        var result = DiscoverPrimaryActor(reason);
        _bootstrapOrchestrator?.RecordActorDiscovery(result);
        _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = result?.Succeeded ?? false,
            Status = result?.Succeeded == true ? "confirmed" : "failed",
            ReportPath = result?.LatestDumpPath,
            ErrorCode = result?.Succeeded == true ? null : "actor_discovery_failed",
            Message = result?.Succeeded == true
                ? $"Actor discovery written for {result.PrimaryActorPath}."
                : result?.FailureReason ?? "Actor discovery failed."
        };
    }

    private TrainingBridgeBootstrapActionResult RunCapabilityDiscoveryBridgeRequest(string reason)
    {
        var result = DiscoverActorCapabilities(reason);
        _bootstrapOrchestrator?.RecordCapabilityDiscovery(result);
        _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = result?.Succeeded ?? false,
            Status = result?.Succeeded == true ? "complete" : "failed",
            ReportPath = result?.LatestDumpPath,
            ErrorCode = result?.Succeeded == true ? null : "capability_discovery_failed",
            Message = result?.Succeeded == true
                ? $"Capability discovery written for {result.PrimaryActorPath}."
                : result?.FailureReason ?? "Capability discovery failed."
        };
    }

    private TrainingBridgeBootstrapActionResult RunArenaRebuildBridgeRequest(string reason)
    {
        var succeeded = TryBuildTrainingScene(reason);
        if (succeeded)
        {
            _bootstrapOrchestrator?.MarkReady(reason);
            _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        }

        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = succeeded,
            Status = succeeded ? "complete" : "failed",
            ReportPath = _bootstrapOrchestrator?.State?.lastReportPath,
            ErrorCode = succeeded ? null : "arena_rebuild_failed",
            Message = succeeded ? "Arena build/rebuild completed." : "Arena build/rebuild failed."
        };
    }

    private TrainingBridgeBootstrapActionResult RunSingleActorSummonProbeBridgeRequest(string reason)
    {
        if (!EnableExplorationProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "single_actor_summon_probe",
                "latest_single_actor_summon_probe.json",
                "summon",
                "disabled_by_config",
                "EnableExplorationProbes is false; no gameplay method was invoked.",
                "Enable EnableExplorationProbes only after passive discovery identifies safe candidate systems.");
        }

        if (!EnableSummonProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "single_actor_summon_probe",
                "latest_single_actor_summon_probe.json",
                "summon",
                "disabled_by_config",
                "EnableSummonProbes is false. No gameplay method was invoked.",
                "Review latest_capability_discovery.json, then enable the individual probe gate for one bounded StructureSpawner attempt.");
        }

        return _activeProbeService?.StartSingleActorSummonProbe(reason) ?? new TrainingBridgeBootstrapActionResult
        {
            Succeeded = false,
            Status = "failed",
            ErrorCode = "active_probe_service_missing",
            Message = "The active probe service was not initialized."
        };
    }

    private TrainingBridgeBootstrapActionResult RunMoveProbeBridgeRequest(string reason)
    {
        if (!EnableExplorationProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "move_probe",
                "latest_move_probe.json",
                "move",
                "disabled_by_config",
                "EnableExplorationProbes is false; no gameplay method was invoked.",
                "Enable EnableExplorationProbes only after passive discovery identifies safe candidate systems.");
        }

        if (!EnableMoveProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "move_probe",
                "latest_move_probe.json",
                "move",
                "disabled_by_config",
                "EnableMoveProbes is false. No gameplay method was invoked.",
                "Review actor and capability discovery, then enable the gate for one bounded PlayerMovement.Move(Vector2) sample.");
        }

        return _activeProbeService?.StartMoveProbe(reason) ?? new TrainingBridgeBootstrapActionResult
        {
            Succeeded = false,
            Status = "failed",
            ErrorCode = "active_probe_service_missing",
            Message = "The active probe service was not initialized."
        };
    }

    private TrainingBridgeBootstrapActionResult RunMultiActorProbeBridgeRequest(string reason)
    {
        if (!EnableExplorationProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "multi_actor_probe",
                "latest_multi_actor_probe.json",
                "multiActor",
                "disabled_by_config",
                "EnableExplorationProbes is false; no target was created.",
                "Enable exploration probes only after actor discovery is confirmed.");
        }

        if (!EnableActorCloneProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "multi_actor_probe",
                "latest_multi_actor_probe.json",
                "multiActor",
                "disabled_by_config",
                "EnableActorCloneProbes is false. No actor or dummy target was created.",
                "Enable the gate for one bounded feasibility attempt; the current implementation uses a dummy target and does not clone an active actor root.");
        }

        return _activeProbeService?.StartMultiActorProbe(reason) ?? new TrainingBridgeBootstrapActionResult
        {
            Succeeded = false,
            Status = "failed",
            ErrorCode = "active_probe_service_missing",
            Message = "The active probe service was not initialized."
        };
    }

    private TrainingBridgeBootstrapActionResult RunActorInteractionProbeBridgeRequest(string reason)
    {
        if (!EnableExplorationProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "actor_interaction_probe",
                "latest_actor_interaction_probe.json",
                "interaction",
                "disabled_by_config",
                "EnableExplorationProbes is false; no interaction objects were created.",
                "Enable exploration probes only after actor discovery is confirmed.");
        }

        if (!EnableActorInteractionProbes)
        {
            return WriteProbeStatusReport(
                reason,
                "actor_interaction_probe",
                "latest_actor_interaction_probe.json",
                "interaction",
                "disabled_by_config",
                "EnableActorInteractionProbes is false. No interaction objects were created.",
                "Enable the gate for one bounded mod-owned collision probe; this does not invoke damage or combat methods.");
        }

        return _activeProbeService?.StartActorInteractionProbe(reason) ?? new TrainingBridgeBootstrapActionResult
        {
            Succeeded = false,
            Status = "failed",
            ErrorCode = "active_probe_service_missing",
            Message = "The active probe service was not initialized."
        };
    }

    private void RecordProbeStatus(string probeName, string status, string dumpPath)
    {
        _bootstrapOrchestrator?.RecordProbeStatus(probeName, status, dumpPath);
        _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
    }

    private List<ComponentDiscoveryEntry> GetTypedCapabilityEntries(
        GameObject actorRoot,
        bool includeMembers,
        bool includeGlobal,
        int maxEntries)
    {
        var entries = new List<ComponentDiscoveryEntry>();
        foreach (var type in GetCapabilityRuntimeTypes())
        {
            if (entries.Count >= maxEntries)
            {
                break;
            }

            var typeEntries = new List<ComponentDiscoveryEntry>();
            var acceptedForType = 0;
            var maxAcceptedForType = includeGlobal ? 8 : 32;
            var maxEntriesForType = includeGlobal ? 20 : 64;
            var runtimeInstances = FindRuntimeInstances(type)
                .OrderByDescending(instance =>
                    instance is Component component &&
                    component != null &&
                    actorRoot != null &&
                    IsTransformUnder(component.transform, actorRoot.transform))
                .ToList();
            foreach (var instance in runtimeInstances)
            {
                if (typeEntries.Count >= maxEntriesForType || acceptedForType >= maxAcceptedForType)
                {
                    break;
                }

                if (instance is Component component && component != null)
                {
                    var actorBound = actorRoot != null &&
                                     IsTransformUnder(component.transform, actorRoot.transform);
                    if (!actorBound && !includeGlobal)
                    {
                        continue;
                    }

                    if (!actorBound && !IsLoadedOrPersistentRuntimeObject(component.gameObject))
                    {
                        continue;
                    }

                    AddComponentCapabilityEntries(
                        typeEntries,
                        component,
                        ActorCapabilityHints.All,
                        includeMembers,
                        actorBound ? "actor" : "global",
                        maxEntriesForType,
                        type);
                    acceptedForType++;
                    continue;
                }

                if (!includeGlobal || instance is not UnityObject unityObject || unityObject == null)
                {
                    continue;
                }

                var objectName = SafeUnityObjectName(unityObject);
                var typeName = type.FullName ?? type.Name;
                var typeScore = ScoreCapabilityText(
                    $"{typeName} {objectName}",
                    ActorCapabilityHints.All,
                    out var matches);
                if (typeScore <= 0)
                {
                    continue;
                }

                typeEntries.Add(new ComponentDiscoveryEntry
                {
                    ComponentPath = $"asset:{objectName}",
                    TypeFullName = typeName,
                    MemberName = null,
                    MemberKind = "object",
                    ParameterSummary = null,
                    ReturnType = null,
                    DeclaringType = typeName,
                    Score = typeScore,
                    RiskLevel = "passive",
                    SuggestedProbe = SuggestedProbeFor(typeName),
                    MatchedHints = matches,
                    Source = "asset"
                });
                acceptedForType++;

                if (!includeMembers)
                {
                    continue;
                }

                foreach (var member in GetPassiveMemberEntries(
                             $"asset:{objectName}",
                             type,
                             ActorCapabilityHints.All,
                             "asset"))
                {
                    if (typeEntries.Count >= maxEntriesForType)
                    {
                        break;
                    }
                    typeEntries.Add(member);
                }
            }

            entries.AddRange(typeEntries.Take(Math.Max(0, maxEntries - entries.Count)));
        }

        return entries
            .GroupBy(
                entry => $"{entry.Source}|{entry.ComponentPath}|{entry.TypeFullName}|{entry.MemberKind}|{entry.MemberName}|{entry.ParameterSummary}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.TypeFullName, StringComparer.OrdinalIgnoreCase)
            .Take(maxEntries)
            .ToList();
    }

    private IReadOnlyList<Type> GetCapabilityRuntimeTypes()
    {
        if (_capabilityRuntimeTypes != null)
        {
            return _capabilityRuntimeTypes;
        }

        var priorityRanks = PriorityCapabilityTypeNames
            .Select((typeName, index) => new { typeName, index })
            .ToDictionary(entry => entry.typeName, entry => entry.index, StringComparer.Ordinal);
        var types = new List<(Type Type, int Score)>();
        foreach (var typeName in PriorityCapabilityTypeNames)
        {
            var priorityType = FindLoadedType(typeName);
            if (priorityType != null)
            {
                types.Add((priorityType, 10000));
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name ?? string.Empty;
            if (assemblyName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) < 0 &&
                assemblyName.IndexOf("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            Type[] assemblyTypes;
            try
            {
                assemblyTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                assemblyTypes = ex.Types;
            }
            catch
            {
                continue;
            }

            if (assemblyTypes == null)
            {
                continue;
            }

            foreach (var type in assemblyTypes)
            {
                if (type == null || !IsGameRuntimeType(type))
                {
                    continue;
                }

                var fullName = type.FullName ?? type.Name;
                var score = ScoreCapabilityText(fullName, ActorCapabilityHints.All, out _);
                if (score > 0)
                {
                    types.Add((type, score));
                }
            }
        }

        _capabilityRuntimeTypes = types
            .GroupBy(entry => entry.Type.FullName ?? entry.Type.Name, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(entry => entry.Score).First())
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry =>
                priorityRanks.TryGetValue(entry.Type.FullName ?? entry.Type.Name, out var rank)
                    ? rank
                    : int.MaxValue)
            .ThenBy(entry => entry.Type.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Type)
            .Take(260)
            .ToList();
        LogInfo($"Typed capability catalog built. types={_capabilityRuntimeTypes.Count}.");
        return _capabilityRuntimeTypes;
    }

    private static bool IsTransformUnder(Transform candidate, Transform root)
    {
        var current = candidate;
        while (current != null)
        {
            if (current == root)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static bool IsLoadedOrPersistentRuntimeObject(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return false;
        }

        try
        {
            var scene = gameObject.scene;
            return scene.IsValid() &&
                   (scene.isLoaded ||
                    string.Equals(scene.name, "DontDestroyOnLoad", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string SafeUnityObjectName(UnityObject unityObject)
    {
        try
        {
            return unityObject != null ? unityObject.name : "unknown";
        }
        catch
        {
            return unityObject?.GetType().Name ?? "unknown";
        }
    }

    private TrainingBridgeBootstrapActionResult WriteProbeStatusReport(string reason, string reportPrefix, string latestFileName, string probeName, string status, string message, string requiredNextEvidence)
    {
        var timestampUtc = DateTime.UtcNow;
        var report = new
        {
            timestampUtc,
            reason,
            status,
            probeName,
            passiveOnly = true,
            message,
            config = new
            {
                UseStagedBootstrap,
                EnableLegacyBootstrapFallback,
                EnableFullSceneHierarchyDump,
                EnableExplorationProbes,
                EnableArenaPruning,
                EnableActorCloneProbes,
                EnableSummonProbes,
                EnableMoveProbes,
                EnableActorInteractionProbes
            },
            requiredNextEvidence
        };
        var path = WriteJson($"{reportPrefix}_{timestampUtc:yyyyMMdd_HHmmss}.json", report);
        WriteJson(latestFileName, report);
        _bootstrapOrchestrator?.RecordProbeStatus(probeName, status, path);
        _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = true,
            Status = status,
            ReportPath = path,
            Message = $"{reportPrefix} wrote a {status} report; no gameplay method was invoked."
        };
    }

    private List<ComponentDiscoveryEntry> GetComponentDiscoveryEntries(GameObject root, IEnumerable<string> hints, bool includeMembers, int maxEntries)
    {
        var entries = new List<ComponentDiscoveryEntry>();
        if (root == null)
        {
            return entries;
        }

        Component[] components;
        try
        {
            components = root.GetComponentsInChildren<Component>(true);
        }
        catch
        {
            return entries;
        }

        foreach (var component in components)
        {
            if (component == null || entries.Count >= maxEntries)
            {
                continue;
            }

            AddComponentCapabilityEntries(entries, component, hints, includeMembers, "actor", maxEntries);
        }

        return entries;
    }

    private List<ComponentDiscoveryEntry> GetGlobalCapabilityEntries(int maxEntries)
    {
        var entries = new List<ComponentDiscoveryEntry>();
        Component[] components;
        try
        {
            components = Resources.FindObjectsOfTypeAll<Component>();
        }
        catch
        {
            return entries;
        }

        foreach (var component in components)
        {
            if (component == null || entries.Count >= maxEntries)
            {
                continue;
            }

            var type = component.GetType();
            var typeText = $"{type.FullName} {type.Name} {SafeObjectPath(component)}";
            var score = ScoreCapabilityText(typeText, ActorCapabilityHints.All, out _);
            if (score < 20 && !LooksLikeManagerOrPool(typeText))
            {
                continue;
            }

            AddComponentCapabilityEntries(entries, component, ActorCapabilityHints.All, includeMembers: true, source: "global", maxEntries: maxEntries);
        }

        return entries;
    }

    private static void AddComponentCapabilityEntries(
        List<ComponentDiscoveryEntry> entries,
        Component component,
        IEnumerable<string> hints,
        bool includeMembers,
        string source,
        int maxEntries,
        Type reflectedType = null)
    {
        var type = reflectedType ?? component.GetType();
        var typeName = type.FullName ?? type.Name ?? "unknown";
        var componentPath = SafeObjectPath(component);
        var typeText = $"{typeName} {componentPath}";
        var typeScore = ScoreCapabilityText(typeText, hints, out var typeMatches);
        if (typeScore > 0)
        {
            entries.Add(new ComponentDiscoveryEntry
            {
                ComponentPath = componentPath,
                TypeFullName = typeName,
                MemberName = null,
                MemberKind = "component",
                ParameterSummary = null,
                ReturnType = null,
                DeclaringType = typeName,
                Score = typeScore,
                RiskLevel = "passive",
                SuggestedProbe = SuggestedProbeFor(typeText),
                MatchedHints = typeMatches,
                Source = source
            });
        }

        if (!includeMembers || entries.Count >= maxEntries)
        {
            return;
        }

        foreach (var member in GetPassiveMemberEntries(componentPath, type, hints, source))
        {
            if (entries.Count >= maxEntries)
            {
                break;
            }

            entries.Add(member);
        }
    }

    private static IEnumerable<ComponentDiscoveryEntry> GetPassiveMemberEntries(string componentPath, Type type, IEnumerable<string> hints, string source)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        MemberInfo[] members;
        try
        {
            members = type.GetMembers(Flags);
        }
        catch
        {
            yield break;
        }

        foreach (var member in members)
        {
            if (member == null)
            {
                continue;
            }

            if (member.DeclaringType != type)
            {
                continue;
            }

            if (member.MemberType != MemberTypes.Field &&
                member.MemberType != MemberTypes.Property &&
                member.MemberType != MemberTypes.Method)
            {
                continue;
            }

            var memberText = $"{type.FullName} {member.Name}";
            var score = ScoreCapabilityText(memberText, hints, out var matches);
            if (score <= 0)
            {
                continue;
            }

            yield return new ComponentDiscoveryEntry
            {
                ComponentPath = componentPath,
                TypeFullName = type.FullName ?? type.Name,
                MemberName = member.Name,
                MemberKind = member.MemberType.ToString().ToLowerInvariant(),
                ParameterSummary = GetParameterSummary(member),
                ReturnType = GetReturnTypeName(member),
                DeclaringType = member.DeclaringType?.FullName,
                Score = score,
                RiskLevel = RiskLevelFor(member),
                SuggestedProbe = SuggestedProbeFor(memberText),
                MatchedHints = matches,
                Source = source
            };
        }
    }

    private static List<ComponentDiscoveryEntry> FilterEntries(IEnumerable<ComponentDiscoveryEntry> entries, IEnumerable<string> hints)
    {
        return entries
            .Where(entry => entry != null && ScoreCapabilityText($"{entry.TypeFullName} {entry.MemberName} {entry.ComponentPath}", hints, out _) > 0)
            .OrderByDescending(entry => entry.Score)
            .Take(48)
            .ToList();
    }

    private static int ScoreCapabilityText(string text, IEnumerable<string> hints, out List<string> matchedHints)
    {
        matchedHints = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var score = 0;
        foreach (var hint in hints.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                continue;
            }

            if (text.IndexOf(hint, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            matchedHints.Add(hint);
            score += CapabilityWeight(hint);
        }

        return score;
    }

    private static int CapabilityWeight(string hint)
    {
        if (HasAny(hint, new[] { "Summon", "Spawn", "Move", "Modifier", "Damage", "Health", "Owner", "PlayerId" }))
        {
            return 35;
        }

        if (HasAny(hint, new[] { "Gesture", "Attack", "Hit", "Prefab", "Pool", "Input" }))
        {
            return 25;
        }

        return 12;
    }

    private static string RiskLevelFor(MemberInfo member)
    {
        if (member.MemberType == MemberTypes.Field || member.MemberType == MemberTypes.Property)
        {
            return "passive";
        }

        var name = member.Name ?? string.Empty;
        if (HasAny(name, new[] { "Destroy", "Delete", "Remove", "Unload", "Quit", "Disconnect", "Purchase" }))
        {
            return "do not invoke";
        }

        if (HasAny(name, new[] { "Summon", "Spawn", "Attack", "Damage", "Hit", "Fire", "Cast", "Throw", "Apply" }))
        {
            return "risky";
        }

        if (name.StartsWith("get_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "ToString", StringComparison.OrdinalIgnoreCase))
        {
            return "likely safe";
        }

        return "likely safe";
    }

    private static string SuggestedProbeFor(string text)
    {
        if (HasAny(text, ActorCapabilityHints.Summon))
        {
            return "single_actor_summon_probe";
        }

        if (HasAny(text, ActorCapabilityHints.Modifier))
        {
            return "modifier_probe";
        }

        if (HasAny(text, ActorCapabilityHints.Move))
        {
            return "move_probe";
        }

        if (HasAny(text, ActorCapabilityHints.AttackHealthDamage))
        {
            return "actor_interaction_probe";
        }

        if (HasAny(text, ActorCapabilityHints.Ownership))
        {
            return "multi_actor_probe";
        }

        return "passive-review";
    }

    private static string GetParameterSummary(MemberInfo member)
    {
        if (member is MethodBase method)
        {
            try
            {
                return string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string GetReturnTypeName(MemberInfo member)
    {
        if (member is MethodInfo method)
        {
            return method.ReturnType?.FullName;
        }

        if (member is PropertyInfo property)
        {
            return property.PropertyType?.FullName;
        }

        if (member is FieldInfo field)
        {
            return field.FieldType?.FullName;
        }

        return null;
    }

    private static bool LooksLikeManagerOrPool(string text)
    {
        return HasAny(text, new[] { "Manager", "System", "Service", "Pool", "Factory", "Spawner", "Summon", "Move", "Damage", "Health", "Input", "Gesture" });
    }

    private static Transform FindActorTransform(GameObject actorRoot, ActorTransformRole role)
    {
        if (actorRoot == null)
        {
            return null;
        }

        var knownCandidates = role switch
        {
            ActorTransformRole.Head => TrainingActorLocator.HeadCandidates,
            ActorTransformRole.LeftHand => TrainingActorLocator.LeftHandCandidates,
            ActorTransformRole.RightHand => TrainingActorLocator.RightHandCandidates,
            _ => null
        };
        var knownTransform = TrainingActorLocator.Resolve(actorRoot.transform, knownCandidates).Transform;
        if (knownTransform != null)
        {
            return knownTransform;
        }

        Transform[] transforms;
        try
        {
            transforms = actorRoot.GetComponentsInChildren<Transform>(true);
        }
        catch
        {
            return null;
        }

        Transform best = null;
        var bestScore = 0;
        foreach (var transform in transforms)
        {
            var score = ScoreActorTransform(transform, role);
            if (score > bestScore)
            {
                best = transform;
                bestScore = score;
            }
        }

        return best;
    }

    private static int ScoreActorTransform(Transform transform, ActorTransformRole role)
    {
        if (transform == null)
        {
            return 0;
        }

        var path = GetPath(transform);
        var normalized = path.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        var score = 0;

        switch (role)
        {
            case ActorTransformRole.Head:
                AddPathScore(normalized, "head", 100, ref score);
                AddPathScore(normalized, "headset", 90, ref score);
                AddPathScore(normalized, "camera", 60, ref score);
                AddPathScore(normalized, "hmd", 80, ref score);
                break;
            case ActorTransformRole.LeftHand:
                AddPathScore(normalized, "lefthand", 120, ref score);
                AddPathScore(normalized, "leftcontroller", 90, ref score);
                if (normalized.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (normalized.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     normalized.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    score += 75;
                }
                break;
            case ActorTransformRole.RightHand:
                AddPathScore(normalized, "righthand", 120, ref score);
                AddPathScore(normalized, "rightcontroller", 90, ref score);
                if (normalized.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (normalized.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     normalized.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    score += 75;
                }
                break;
        }

        return score;
    }

    private static string SafeObjectPath(Component component)
    {
        try
        {
            return component != null ? GetPath(component.transform) : null;
        }
        catch
        {
            return null;
        }
    }

    private SceneReport AnalyzeScene(Scene scene, bool logHierarchy)
    {
        var rootObjects = scene.GetRootGameObjects();
        var roots = new List<RootReport>(rootObjects.Length);
        RootReport bestCandidate = null;

        foreach (var root in rootObjects)
        {
            if (root == null)
            {
                continue;
            }

            var report = AnalyzeRoot(root);
            roots.Add(report);

            if (IsBetterPlayerCandidate(report, bestCandidate))
            {
                bestCandidate = report;
            }
        }

        var reportSet = new SceneReport
        {
            SceneName = scene.name,
            BuildIndex = scene.buildIndex,
            IsValid = scene.IsValid(),
            IsLoaded = scene.isLoaded,
            IsActive = scene == UnitySceneManager.GetActiveScene(),
            IsGymLike = IsGymLikeScene(scene.name, roots),
            LikelySceneRole = ClassifySceneRole(scene, roots),
            RootCount = rootObjects.Length,
            RootNames = roots.Select(root => root.Name).ToList(),
            CandidatePlayerRoots = roots.Where(root => root.Classification == RootClassification.Player).OrderByDescending(root => root.Score).Select(root => root.Path).ToList(),
            CandidateSupportRoots = roots.Where(root => root.Classification == RootClassification.Support).OrderByDescending(root => root.Score).Select(root => root.Path).ToList(),
            CandidateEnvironmentRoots = roots.Where(root => root.Classification == RootClassification.Environment).OrderByDescending(root => root.Score).Select(root => root.Path).ToList(),
            Roots = roots.OrderByDescending(r => r.Score).ToList(),
            BestPlayerCandidate = bestCandidate
        };

        if (EnableFullSceneHierarchyDump && logHierarchy)
        {
            LogSceneReport(reportSet);
            DumpInterestingRoots(reportSet);
        }

        return reportSet;
    }

    private RootReport AnalyzeRoot(GameObject root)
    {
        var componentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        var score = 0;

        var path = GetPath(root.transform);
        ScoreText(root.name, ref score, reasons);

        var transforms = root.GetComponentsInChildren<Transform>(true);
        foreach (var transform in transforms)
        {
            if (transform == null)
            {
                continue;
            }

            ScoreText(transform.name, ref score, reasons);
        }

        var components = root.GetComponentsInChildren<Component>(true);
        foreach (var component in components)
        {
            if (component == null)
            {
                continue;
            }

            var typeName = component.GetType().FullName ?? component.GetType().Name;
            if (!componentTypes.Add(typeName))
            {
                continue;
            }

            ScoreText(typeName, ref score, reasons);
        }

        var classification = ClassifyRoot(root.name, componentTypes, score);
        var matchingHints = reasons.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        return new RootReport
        {
            Name = root.name,
            Path = path,
            Score = score,
            Classification = classification,
            ClassificationName = classification.ToString().ToLowerInvariant(),
            SuggestedAction = GetSuggestedRootAction(root.scene, classification),
            Reasons = matchingHints,
            MatchingHints = matchingHints,
            ComponentTypes = componentTypes.OrderBy(x => x).Take(24).ToList(),
            ChildCount = root.transform.childCount,
            ActiveSelf = root.activeSelf,
            ActiveInHierarchy = root.activeInHierarchy,
            Layer = root.layer,
            Tag = root.tag
        };
    }

    private void ScoreText(string value, ref int score, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddScore(value, "PlayerController", 90, ref score, reasons);
        AddScore(value, "PlayerMovement", 65, ref score, reasons);
        AddScore(value, "PlayerPhysics", 55, ref score, reasons);
        AddScore(value, "PlayerAnimator", 45, ref score, reasons);
        AddScore(value, "PlayerCamera", 45, ref score, reasons);
        AddScore(value, "PlayerResetSystem", 40, ref score, reasons);
        AddScore(value, "BootLoaderPlayer", 70, ref score, reasons);
        AddScore(value, "LocalPlayer", 85, ref score, reasons);
        AddScore(value, "PlayerData", 30, ref score, reasons);
        AddScore(value, "VRIK", 35, ref score, reasons);
        AddScore(value, "Avatar", 25, ref score, reasons);
        AddScore(value, "Hand", 18, ref score, reasons);
        AddScore(value, "Controller", 18, ref score, reasons);
        AddScore(value, "XR", 20, ref score, reasons);
        AddScore(value, "Rig", 20, ref score, reasons);
        AddScore(value, "Camera", 18, ref score, reasons);
        AddScore(value, "Measurement", 20, ref score, reasons);
        AddScore(value, "BootLoader", 18, ref score, reasons);
        AddScore(value, "SceneManager", 24, ref score, reasons);
        AddScore(value, "PlayerManager", 24, ref score, reasons);
        AddScore(value, "EventSystem", 18, ref score, reasons);
        AddScore(value, "InputSystem", 18, ref score, reasons);
        AddScore(value, "Photon", 18, ref score, reasons);
        AddScore(value, "PlayFab", 18, ref score, reasons);
        AddScore(value, "Network", 18, ref score, reasons);

        AddPenalty(value, "Arena", 25, ref score, reasons);
        AddPenalty(value, "Gym", 20, ref score, reasons);
        AddPenalty(value, "Environment", 18, ref score, reasons);
        AddPenalty(value, "Structure", 18, ref score, reasons);
        AddPenalty(value, "Prop", 12, ref score, reasons);
        AddPenalty(value, "Decoration", 12, ref score, reasons);
        AddPenalty(value, "Terrain", 18, ref score, reasons);
        AddPenalty(value, "Lighting", 10, ref score, reasons);
        AddPenalty(value, "Particle", 10, ref score, reasons);
        AddPenalty(value, "SceneBound", 10, ref score, reasons);
        AddPenalty(value, "Hoop", 8, ref score, reasons);
        AddPenalty(value, "Pedestal", 8, ref score, reasons);
    }

    private static void AddScore(string value, string token, int amount, ref int score, List<string> reasons)
    {
        if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += amount;
            reasons.Add($"+{amount}:{token}");
        }
    }

    private static void AddPenalty(string value, string token, int amount, ref int score, List<string> reasons)
    {
        if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score -= amount;
            reasons.Add($"-{amount}:{token}");
        }
    }

    private RootClassification ClassifyRoot(string name, HashSet<string> componentTypes, int score)
    {
        var hasPlayerSignal = HasAny(name, PlayerHints) || componentTypes.Any(x => HasAny(x, PlayerHints));
        var hasSupportSignal = HasAny(name, SupportHints) || componentTypes.Any(x => HasAny(x, SupportHints));
        var hasEnvironmentSignal = HasAny(name, EnvironmentHints) || componentTypes.Any(x => HasAny(x, EnvironmentHints));

        if (hasPlayerSignal || score >= 90)
        {
            return RootClassification.Player;
        }

        if (hasSupportSignal || score >= 35)
        {
            return RootClassification.Support;
        }

        if (hasEnvironmentSignal || score <= -10)
        {
            return RootClassification.Environment;
        }

        return RootClassification.Unknown;
    }

    private static string ClassifySceneRole(Scene scene, IEnumerable<RootReport> roots)
    {
        var sceneName = scene.name ?? string.Empty;
        if (string.Equals(sceneName, TrainingSceneName, StringComparison.OrdinalIgnoreCase) ||
            sceneName.IndexOf("training", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "training";
        }

        if (IsBootstrapScene(scene))
        {
            return "loader";
        }

        if (sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "menu";
        }

        if (sceneName.IndexOf("gym", StringComparison.OrdinalIgnoreCase) >= 0 ||
            sceneName.IndexOf("practice", StringComparison.OrdinalIgnoreCase) >= 0 ||
            IsGymLikeScene(sceneName, roots))
        {
            return "gym";
        }

        if (sceneName.IndexOf("arena", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "arena";
        }

        return "unknown";
    }

    private static string GetSuggestedRootAction(Scene scene, RootClassification classification)
    {
        if (IsBootstrapScene(scene))
        {
            return "destroy";
        }

        if (string.Equals(scene.name, TrainingSceneName, StringComparison.OrdinalIgnoreCase))
        {
            return "preserve";
        }

        switch (classification)
        {
            case RootClassification.Player:
                return "move";
            case RootClassification.Support:
            case RootClassification.Environment:
                return "preserve";
            default:
                return "unknown";
        }
    }

    private static bool HasAny(string value, IEnumerable<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBetterPlayerCandidate(RootReport candidate, RootReport incumbent)
    {
        if (candidate == null)
        {
            return false;
        }

        if (incumbent == null)
        {
            return true;
        }

        if (candidate.Score != incumbent.Score)
        {
            return candidate.Score > incumbent.Score;
        }

        if (candidate.Classification != incumbent.Classification)
        {
            return candidate.Classification == RootClassification.Player;
        }

        return candidate.Path.Length < incumbent.Path.Length;
    }

    private static SceneCandidate PickBetterCandidate(SceneCandidate incumbent, RootReport candidate)
    {
        if (candidate == null)
        {
            return incumbent;
        }

        if (incumbent == null)
        {
            return new SceneCandidate(candidate);
        }

        if (candidate.Score > incumbent.Score)
        {
            return new SceneCandidate(candidate);
        }

        return incumbent;
    }

    private static SceneCandidate PickBetterCandidate(SceneCandidate incumbent, SceneCandidate candidate)
    {
        if (candidate == null)
        {
            return incumbent;
        }

        if (incumbent == null || candidate.Score > incumbent.Score)
        {
            return candidate;
        }

        return incumbent;
    }

    private SceneCandidate FindRuntimeActorCandidate(List<Scene> scenes)
    {
        SceneCandidate best = null;
        if (_preservedActorCandidate != null)
        {
            best = new SceneCandidate(
                _preservedActorCandidate,
                60000,
                true,
                new[] { "preserved_actor_candidate", "loader_cleanup_survivor" });
        }

        var playerControllerType =
            FindLoadedType("RUMBLE.Players.PlayerController") ??
            FindLoadedType("Il2CppRUMBLE.Players.PlayerController");
        foreach (var instance in FindRuntimeInstances(playerControllerType))
        {
            if (instance is not Component component || component == null)
            {
                continue;
            }

            var gameObject = component.gameObject;
            var path = GetPath(gameObject.transform);
            if (!IsObjectInScenes(gameObject, scenes) || IsRejectedActorPath(path))
            {
                continue;
            }

            var score = 75000;
            if (gameObject.activeInHierarchy)
            {
                score += 1000;
            }
            if (gameObject.scene.name.IndexOf("Gym", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 2000;
            }
            if (path.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 1000;
            }

            best = PickBetterCandidate(
                best,
                new SceneCandidate(
                    gameObject,
                    score,
                    true,
                    new[] { "exact_player_controller_component", component.GetType().FullName ?? component.GetType().Name }));
        }

        foreach (var scene in scenes)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null)
                {
                    continue;
                }

                Transform[] transforms;
                try
                {
                    transforms = root.GetComponentsInChildren<Transform>(true);
                }
                catch
                {
                    continue;
                }

                foreach (var transform in transforms)
                {
                    if (transform == null)
                    {
                        continue;
                    }

                    var path = GetPath(transform);
                    var exactBootLoaderPlayer = string.Equals(
                        transform.name,
                        "BootLoaderPlayer",
                        StringComparison.OrdinalIgnoreCase);
                    var exactLocalPlayer = string.Equals(
                        transform.name,
                        "LocalPlayer",
                        StringComparison.OrdinalIgnoreCase);
                    if ((!exactBootLoaderPlayer && !exactLocalPlayer) || IsRejectedActorPath(path))
                    {
                        continue;
                    }

                    var head = FindActorTransform(transform.gameObject, ActorTransformRole.Head);
                    var left = FindActorTransform(transform.gameObject, ActorTransformRole.LeftHand);
                    var right = FindActorTransform(transform.gameObject, ActorTransformRole.RightHand);
                    var hasDistinctRig = head != null && left != null && right != null && left != right;
                    var score = exactBootLoaderPlayer ? 50000 : 55000;
                    if (hasDistinctRig)
                    {
                        score += 5000;
                    }

                    best = PickBetterCandidate(
                        best,
                        new SceneCandidate(
                            transform.gameObject,
                            score,
                            hasDistinctRig,
                            new[]
                            {
                                exactBootLoaderPlayer ? "exact_bootloader_player_name" : "exact_local_player_name",
                                hasDistinctRig ? "distinct_head_and_hands" : "rig_transforms_incomplete"
                            }));
                }
            }
        }

        return best;
    }

    private static IEnumerable<object> FindRuntimeInstances(Type type)
    {
        if (type == null)
        {
            yield break;
        }

        var method = typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                candidate.Name == "FindObjectsOfTypeAll" &&
                candidate.IsGenericMethodDefinition &&
                candidate.GetGenericArguments().Length == 1 &&
                candidate.GetParameters().Length == 0);
        if (method == null)
        {
            yield break;
        }

        System.Collections.IEnumerable results;
        try
        {
            results = method.MakeGenericMethod(type)
                .Invoke(null, Array.Empty<object>()) as System.Collections.IEnumerable;
        }
        catch
        {
            yield break;
        }

        if (results == null)
        {
            yield break;
        }

        foreach (var result in results)
        {
            if (result != null)
            {
                yield return result;
            }
        }
    }

    private static bool IsObjectInScenes(GameObject gameObject, IEnumerable<Scene> scenes)
    {
        if (gameObject == null)
        {
            return false;
        }

        return scenes.Any(scene => scene.IsValid() && gameObject.scene == scene);
    }

    private static bool IsRejectedActorPath(string path)
    {
        return HasAny(
            path,
            new[]
            {
                "INTERACTABLES",
                "Preview Player",
                "PreviewPlayer",
                "Dressing Room",
                "Pose Ghost",
                "PoseGhost",
                "Static Ghost",
                "Tutorial"
            });
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        var parts = new Stack<string>();
        while (transform != null)
        {
            parts.Push(transform.name);
            transform = transform.parent;
        }

        return string.Join("/", parts);
    }

    private void DumpInterestingRoots(SceneReport report)
    {
        foreach (var root in report.Roots)
        {
            if (root.Classification == RootClassification.Player ||
                root.Classification == RootClassification.Support ||
                string.Equals(root.Name, "SCENE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root.Name, "LOGIC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root.Name, "UI", StringComparison.OrdinalIgnoreCase))
            {
                DumpHierarchy(root.Path, report.SceneName);
            }
        }
    }

    private void DumpHierarchy(string rootPath, string sceneName)
    {
        var scene = GetLoadedScenes().FirstOrDefault(s => string.Equals(s.name, sceneName, StringComparison.OrdinalIgnoreCase));
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return;
        }

        var root = FindRootByPath(rootPath, new List<Scene> { scene });
        if (root == null)
        {
            return;
        }

        LogInfo($"Hierarchy dump for {rootPath} in {sceneName}:");
        DumpTransform(root.transform, 0, 4, 32);
    }

    private void DumpTransform(Transform transform, int depth, int maxDepth, int maxNodes)
    {
        if (transform == null || depth > maxDepth || maxNodes <= 0)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        var typeList = string.Join(", ", GetComponentTypeNames(transform.gameObject).Take(8));
        LogInfo($"{indent}- {transform.name} [{typeList}] active={transform.gameObject.activeSelf} children={transform.childCount}");

        maxNodes--;
        if (maxNodes <= 0)
        {
            return;
        }

        var childCount = transform.childCount;
        for (var i = 0; i < childCount && maxNodes > 0; i++)
        {
            DumpTransform(transform.GetChild(i), depth + 1, maxDepth, maxNodes);
            maxNodes--;
        }
    }

    private static IEnumerable<string> GetComponentTypeNames(GameObject gameObject)
    {
        if (gameObject == null)
        {
            yield break;
        }

        Component[] components;
        try
        {
            components = gameObject.GetComponents<Component>();
        }
        catch
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components)
        {
            if (component == null)
            {
                continue;
            }

            var typeName = component.GetType().FullName ?? component.GetType().Name;
            if (seen.Add(typeName))
            {
                yield return typeName;
            }
        }
    }

    private bool TryForceGymLoad(string reason)
    {
        if (_gymTransitionSucceeded)
        {
            return true;
        }

        if (_gymTransitionAttempted && DateTime.UtcNow - _lastGymTransitionAttemptUtc < TimeSpan.FromSeconds(10))
        {
            LogInfo($"Gym transition request skipped ({reason}): previous attempt is still cooling down.");
            return false;
        }

        _gymTransitionAttempted = true;
        _lastGymTransitionAttemptUtc = DateTime.UtcNow;

        LogInfo($"Attempting gym transition ({reason}).");

        if (TryLoadGymSceneFromBuildSettings(reason))
        {
            LogInfo("Gym load requested from build settings.");
            return true;
        }

        var targetTypes = new[]
        {
            "RUMBLE.Managers.SceneManager",
            "RUMBLE.BootLoader",
            "RUMBLE.Players.BootLoader.BootLoaderIntroSystem",
            "RUMBLE.Players.BootLoader.BootLoaderMeasurementSystem",
            "RUMBLE.Players.BootLoader.BootloaderBridgeSystem"
        };

        foreach (var typeName in targetTypes)
        {
            var typeCandidates = GetGymProbeTypeCandidates(typeName).ToList();
            if (typeCandidates.Count == 0)
            {
                LogWarn($"Gym probe type not found: {typeName}");
                continue;
            }

            foreach (var type in typeCandidates)
            {
                LogInfo($"Gym probe type found: {type.FullName} in {type.Assembly.GetName().Name}");
                DumpRelevantMethods(type);

                if (TryInvokeGymTransition(type, null))
                {
                    LogInfo($"Gym transition request invoked via static call on {type.FullName}; awaiting scene inventory confirmation.");
                    return true;
                }

                var instance = FindFirstRuntimeInstance(type);
                if (instance != null)
                {
                    LogInfo($"Gym probe instance found: {type.FullName} at {GetRuntimeObjectPath(instance)}");
                    if (TryInvokeGymTransition(type, instance))
                    {
                        LogInfo($"Gym transition request invoked via {type.FullName} instance; awaiting scene inventory confirmation.");
                        return true;
                    }
                }
            }
        }

        if (TryInvokeGymTransitionFromComponentSweep())
        {
            LogInfo("Gym transition request invoked via component sweep; awaiting scene inventory confirmation.");
            return true;
        }

        LogWarn("Gym transition attempt did not find a callable method.");
        return false;
    }

    private bool TryLoadGymSceneFromBuildSettings(string reason)
    {
        var buildSceneCount = UnitySceneManager.sceneCountInBuildSettings;
        if (buildSceneCount <= 0)
        {
            LogWarn($"Build-settings gym load skipped ({reason}): no build scenes reported.");
            return false;
        }

        var candidates = new List<(int Index, string Path, string Name, int Score)>();
        for (var i = 0; i < buildSceneCount; i++)
        {
            string path;
            try
            {
                path = SceneUtility.GetScenePathByBuildIndex(i);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var score = 0;
            var lowered = $"{name} {path}".ToLowerInvariant();
            if (lowered.Contains("gym"))
            {
                score += 1000;
            }
            if (lowered.Contains("arena"))
            {
                score += 500;
            }
            if (lowered.Contains("practice"))
            {
                score += 250;
            }
            if (lowered.Contains("training"))
            {
                score += 200;
            }
            if (lowered.Contains("loading") || lowered.Contains("boot"))
            {
                score -= 250;
            }

            candidates.Add((i, path, name, score));
        }

        if (candidates.Count == 0)
        {
            LogWarn($"Build-settings gym load skipped ({reason}): no scene paths could be resolved.");
            return false;
        }

        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Index))
        {
            LogInfo($"Build scene candidate: index={candidate.Index} name={candidate.Name} score={candidate.Score} path={candidate.Path}");
        }

        var viableCandidates = candidates
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .ToList();
        if (viableCandidates.Count == 0)
        {
            LogWarn($"Build-settings gym load skipped ({reason}): no obvious gym scene found in build settings.");
            return false;
        }

        var gymCandidate = viableCandidates[0];
        var candidateKey = $"build-settings:{gymCandidate.Index}:{gymCandidate.Path}";
        if (!_gymTransitionCandidateAttempts.Add(candidateKey))
        {
            LogWarn($"Build-settings gym load strongest candidate was already attempted ({reason}).");
            return false;
        }

        try
        {
            LogInfo(
                $"Requesting gym load from build settings: index={gymCandidate.Index} " +
                $"name={gymCandidate.Name} score={gymCandidate.Score} path={gymCandidate.Path}.");
            UnitySceneManager.LoadScene(gymCandidate.Index, LoadSceneMode.Additive);
            return true;
        }
        catch (Exception ex)
        {
            LogWarn($"Direct gym load failed for scene index {gymCandidate.Index} ({gymCandidate.Name}): {ex.Message}");
            return false;
        }
    }

    private bool TryUnloadBootstrapScenes(string reason)
    {
        var managerReady = _trainingEnvironmentManager?.IsReady ?? false;
        var trainingScene = default(Scene);
        if (managerReady)
        {
            trainingScene = _trainingEnvironmentManager.CurrentTrainingScene;
        }

        if (managerReady &&
            (!trainingScene.IsValid() || !trainingScene.isLoaded) &&
            !string.IsNullOrWhiteSpace(_trainingEnvironmentManager.CurrentTrainingScene.name))
        {
            var freshTrainingScene = UnitySceneManager.GetSceneByName(_trainingEnvironmentManager.CurrentTrainingScene.name);
            if (freshTrainingScene.IsValid() && freshTrainingScene.isLoaded)
            {
                trainingScene = freshTrainingScene;
            }
        }

        var bootstrapScenes = new List<Scene>();

        var loaderScene = UnitySceneManager.GetSceneByName("Loader");
        if (loaderScene.IsValid() && loaderScene.isLoaded)
        {
            bootstrapScenes.Add(loaderScene);
        }

        if (bootstrapScenes.Count == 0)
        {
            var scenes = GetLoadedScenes();
            bootstrapScenes = scenes
                .Where(IsBootstrapScene)
                .ToList();
        }

        if (bootstrapScenes.Count == 0)
        {
            LogInfo($"Bootstrap scene cleanup verified ({reason}): no loader/bootstrap scenes found.");
            return true;
        }

        var activeScene = UnitySceneManager.GetActiveScene();
        var replacementScene = trainingScene.IsValid() && trainingScene.isLoaded
            ? trainingScene
            : GetLoadedScenes().FirstOrDefault(scene => !IsBootstrapScene(scene));
        if (IsBootstrapScene(activeScene) &&
            replacementScene.IsValid() &&
            replacementScene.isLoaded &&
            activeScene != replacementScene)
        {
            try
            {
                UnitySceneManager.SetActiveScene(replacementScene);
                LogInfo($"Set non-bootstrap scene '{replacementScene.name}' active before bootstrap unload ({reason}).");
            }
            catch (Exception ex)
            {
                LogWarn($"Failed to set replacement scene active before bootstrap unload ({reason}): {ex.Message}");
            }
        }

        var cleanupRequestsSucceeded = true;
        foreach (var scene in bootstrapScenes)
        {
            if (trainingScene.IsValid() && scene == trainingScene)
            {
                continue;
            }

            var stripSucceeded = StripBootstrapScene(scene, reason);
            var unloadRequested = false;

            try
            {
                LogInfo($"Unloading bootstrap scene '{scene.name}' ({scene.buildIndex}) ({reason}).");
                var operation = UnitySceneManager.UnloadSceneAsync(scene);
                unloadRequested = operation != null;
                if (!unloadRequested && !string.IsNullOrWhiteSpace(scene.name))
                {
                    LogWarn($"Scene-handle unload returned no operation for '{scene.name}'; trying the scene name once.");
                    operation = UnitySceneManager.UnloadSceneAsync(scene.name);
                    unloadRequested = operation != null;
                }
                if (!unloadRequested && scene.buildIndex >= 0)
                {
                    LogWarn($"Scene-name unload returned no operation for '{scene.name}'; trying build index {scene.buildIndex} once.");
                    operation = UnitySceneManager.UnloadSceneAsync(scene.buildIndex);
                    unloadRequested = operation != null;
                }
            }
            catch (Exception ex)
            {
                LogWarn($"Failed to unload bootstrap scene '{scene.name}' ({reason}): {ex.Message}");
            }

            if (!unloadRequested && !stripSucceeded)
            {
                cleanupRequestsSucceeded = false;
            }
        }

        return cleanupRequestsSucceeded;
    }

    private bool StripBootstrapScene(Scene scene, string reason)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return true;
        }

        GameObject[] roots;
        try
        {
            roots = scene.GetRootGameObjects();
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to enumerate bootstrap roots for scene '{scene.name}' ({reason}): {ex.Message}");
            return false;
        }

        if (roots == null || roots.Length == 0)
        {
            return true;
        }

        PreserveActorCandidateFromBootstrapScene(scene, reason);
        var succeeded = true;
        foreach (var root in roots)
        {
            if (root == null)
            {
                continue;
            }

            try
            {
                if (IsModOwnedRoot(root))
                {
                    UnityObject.DontDestroyOnLoad(root);
                    LogInfo($"Preserved mod-owned root '{root.name}' before stripping scene '{scene.name}' ({reason}).");
                    continue;
                }

                LogInfo($"Destroying bootstrap root '{root.name}' from scene '{scene.name}' ({reason}).");
                UnityObject.Destroy(root);
            }
            catch (Exception ex)
            {
                succeeded = false;
                LogWarn($"Failed to destroy bootstrap root '{root.name}' from scene '{scene.name}' ({reason}): {ex.Message}");
            }
        }

        return succeeded;
    }

    private void PreserveActorCandidateFromBootstrapScene(Scene scene, string reason)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return;
        }

        var actor = _bestPlayerCandidate != null
            ? FindRootByPath(_bestPlayerCandidate.Path, new List<Scene> { scene })
            : null;
        if (actor == null)
        {
            var nestedCandidate = FindRuntimeActorCandidate(new List<Scene> { scene });
            actor = nestedCandidate != null
                ? FindRootByPath(nestedCandidate.Path, new List<Scene> { scene })
                : null;
            if (nestedCandidate != null)
            {
                _bestPlayerCandidate = nestedCandidate;
            }
        }

        if (actor == null || IsRejectedActorPath(GetPath(actor.transform)))
        {
            LogWarn($"No validated actor candidate was available to preserve from bootstrap scene '{scene.name}' ({reason}).");
            return;
        }

        var head = FindActorTransform(actor, ActorTransformRole.Head);
        var leftHand = FindActorTransform(actor, ActorTransformRole.LeftHand);
        var rightHand = FindActorTransform(actor, ActorTransformRole.RightHand);
        if (head == null || leftHand == null || rightHand == null || leftHand == rightHand)
        {
            LogWarn(
                $"Actor candidate '{GetPath(actor.transform)}' was not preserved from '{scene.name}' because head/hand evidence was incomplete.");
            return;
        }

        var originalPath = GetPath(actor.transform);
        if (actor.transform.parent != null)
        {
            actor.transform.SetParent(null, true);
        }

        UnityObject.DontDestroyOnLoad(actor);
        _preservedActorCandidate = actor;
        _bestPlayerCandidate = new SceneCandidate(
            actor,
            Math.Max(_bestPlayerCandidate?.Score ?? 0, 60000),
            true,
            new[] { "preserved_from_bootstrap_scene", originalPath, "distinct_head_and_hands" });
        LogInfo(
            $"Preserved actor candidate '{originalPath}' from bootstrap scene '{scene.name}' as '{GetPath(actor.transform)}' ({reason}).");
    }

    private static bool IsModOwnedRoot(GameObject root)
    {
        if (root == null)
        {
            return false;
        }

        if (root.name.StartsWith("AI_Train", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return root.GetComponentsInChildren<Component>(true)
                .Where(component => component != null)
                .Select(component => component.GetType())
                .Any(type =>
                    string.Equals(type.Namespace, typeof(TrainingFoundation).Namespace, StringComparison.Ordinal) ||
                    (type.FullName?.StartsWith("AI_Train.", StringComparison.Ordinal) ?? false));
        }
        catch
        {
            return false;
        }
    }
    private static Type FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type;
            try
            {
                type = assembly.GetType(fullName, false, false);
            }
            catch
            {
                continue;
            }

            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static IEnumerable<Type> GetGymProbeTypeCandidates(string typeName)
    {
        var candidates = new List<Type>();

        var exactType = FindLoadedType(typeName) ?? FindLoadedType($"Il2Cpp{typeName}");
        if (exactType != null)
        {
            candidates.Add(exactType);
        }

        var broadMatches = FindTypeMatches(typeName)
            .OrderByDescending(ScoreTypeCandidate)
            .ThenBy(type => type.FullName)
            .Take(10)
            .ToList();

        if (broadMatches.Count == 0)
        {
            var token = typeName.Split('.').Last();
            broadMatches = FindTypeMatches(token)
                .OrderByDescending(ScoreTypeCandidate)
                .ThenBy(type => type.FullName)
                .Take(10)
                .ToList();
        }

        foreach (var match in broadMatches)
        {
            if (!candidates.Contains(match))
            {
                candidates.Add(match);
            }
        }

        return candidates
            .Where(IsGameRuntimeType)
            .OrderByDescending(ScoreTypeCandidate)
            .ThenBy(type => type.FullName);
    }

    private static IEnumerable<Type> FindTypeMatches(string token)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                var fullName = type.FullName ?? string.Empty;
                if (fullName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    yield return type;
                }
            }
        }
    }

    private static bool IsGameRuntimeType(Type type)
    {
        if (type == null)
        {
            return false;
        }

        var fullName = type.FullName ?? string.Empty;
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;

        if (fullName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fullName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
               assemblyName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ScoreTypeCandidate(Type type)
    {
        if (type == null)
        {
            return int.MinValue;
        }

        var fullName = type.FullName ?? string.Empty;
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        var score = 0;

        if (fullName.StartsWith("Il2CppRUMBLE.", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("RUMBLE.", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }

        if (assemblyName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 400;
        }

        if (fullName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 250;
        }

        if (fullName.IndexOf("SceneManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
            fullName.IndexOf("BootLoader", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 150;
        }

        if (fullName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase))
        {
            score -= 1000;
        }

        if (assemblyName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
        {
            score -= 800;
        }

        if (assemblyName.IndexOf("Photon", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score -= 150;
        }

        return score;
    }

    private static object FindFirstRuntimeInstance(Type type)
    {
        try
        {
            if (type == null)
            {
                return null;
            }

            foreach (var result in FindRuntimeInstances(type))
            {
                if (result != null)
                {
                    return result;
                }
            }

            var linkedInstance = FindReferencedRuntimeObject(type);
            if (linkedInstance != null)
            {
                return linkedInstance;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsLikelySingletonMember(string name, Type memberType, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(name) || memberType == null || targetType == null)
        {
            return false;
        }

        if (memberType == targetType || memberType.IsAssignableFrom(targetType) || targetType.IsAssignableFrom(memberType))
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("instance") ||
                lower.Contains("singleton") ||
                lower.Contains("current") ||
                lower.Contains("manager") ||
                lower.Contains("system"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMatchingRuntimeInstance(object candidate, Type targetType)
    {
        if (candidate == null || targetType == null)
        {
            return false;
        }

        var candidateType = candidate.GetType();
        if (candidateType == null)
        {
            return false;
        }

        if (targetType == candidateType || targetType.IsAssignableFrom(candidateType))
        {
            return true;
        }

        var candidateFullName = candidateType.FullName ?? string.Empty;
        var targetFullName = targetType.FullName ?? string.Empty;
        var targetName = targetType.Name ?? string.Empty;

        return candidateFullName.Equals(targetFullName, StringComparison.OrdinalIgnoreCase) ||
               candidateFullName.IndexOf(targetFullName, StringComparison.OrdinalIgnoreCase) >= 0 ||
               candidateFullName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static object FindReferencedRuntimeObject(Type targetType)
    {
        if (targetType == null)
        {
            return null;
        }

        var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var targetName = targetType.Name ?? string.Empty;
        var unprefixedTargetName = targetName.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase)
            ? targetName.Substring("Il2Cpp".Length)
            : targetName;
        var targetNameTokens = new[]
        {
            targetName,
            unprefixedTargetName
        };

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var hostType in types)
            {
                if (hostType == null)
                {
                    continue;
                }

                foreach (var field in hostType.GetFields(bindingFlags))
                {
                    if (!IsLikelyLinkedMember(field.Name, field.FieldType, targetType, targetNameTokens))
                    {
                        continue;
                    }

                    object value;
                    try
                    {
                        value = field.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value != null)
                    {
                        return value;
                    }
                }

                foreach (var property in hostType.GetProperties(bindingFlags))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                        !IsLikelyLinkedMember(property.Name, property.PropertyType, targetType, targetNameTokens))
                    {
                        continue;
                    }

                    object value;
                    try
                    {
                        value = property.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value != null)
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsLikelyLinkedMember(string name, Type memberType, Type targetType, IReadOnlyList<string> targetNameTokens)
    {
        if (string.IsNullOrWhiteSpace(name) || memberType == null || targetType == null)
        {
            return false;
        }

        if (!(memberType == targetType || targetType.IsAssignableFrom(memberType)))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        if (lower.Contains("instance") ||
            lower.Contains("singleton") ||
            lower.Contains("current") ||
            lower.Contains("manager") ||
            lower.Contains("system"))
        {
            return true;
        }

        foreach (var token in targetNameTokens)
        {
            if (!string.IsNullOrWhiteSpace(token) && lower.Contains(token.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetRuntimeObjectPath(object obj)
    {
        if (obj is Component component)
        {
            return GetPath(component.transform);
        }

        if (obj is UnityEngine.Object unityObject)
        {
            return unityObject.name;
        }

        return obj != null ? obj.GetType().FullName ?? obj.ToString() : string.Empty;
    }

    private void DumpRelevantMethods(Type type)
    {
        var bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var methods = type.GetMethods(bindingFlags)
            .Where(method =>
                method.Name.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0 ||
                method.Name.IndexOf("Gym", StringComparison.OrdinalIgnoreCase) >= 0 ||
                method.Name.IndexOf("Transition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                method.Name.IndexOf("Scene", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(method => method.Name)
            .Take(32)
            .ToList();

        foreach (var method in methods)
        {
            LogInfo($"  method {method.Name} static={method.IsStatic} params={method.GetParameters().Length}");
        }

        var fullName = type.FullName ?? string.Empty;
        if (fullName.IndexOf("SceneManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
            fullName.IndexOf("BootLoader", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var fields = type.GetFields(bindingFlags)
                .Where(field => field.DeclaringType == type)
                .OrderBy(field => field.Name)
                .Take(24)
                .ToList();

            foreach (var field in fields)
            {
                LogInfo($"  field {field.Name} static={field.IsStatic} type={field.FieldType.FullName}");
            }

            var properties = type.GetProperties(bindingFlags)
                .Where(property => property.DeclaringType == type)
                .OrderBy(property => property.Name)
                .Take(24)
                .ToList();

            foreach (var property in properties)
            {
                var getter = property.GetGetMethod(true);
                LogInfo($"  prop {property.Name} static={(getter?.IsStatic ?? false)} type={property.PropertyType.FullName}");
            }

            var linkedInstance = FindReferencedRuntimeObject(type);
            if (linkedInstance != null)
            {
                LogInfo($"  linked instance candidate: {GetRuntimeObjectPath(linkedInstance)}");
            }
        }
    }

    private bool TryInvokeGymTransition(Type type, object target)
    {
        var bindingFlags = BindingFlags.Instance |
                           BindingFlags.Static |
                           BindingFlags.Public |
                           BindingFlags.NonPublic;

        var methodNames = new[]
        {
            "PerformStartupGymLoad",
            "DoTransitionToGym"
        };

        foreach (var methodName in methodNames)
        {
            var method = FindInvokableGymMethod(type, methodName, bindingFlags);
            if (method == null)
            {
                continue;
            }

            try
            {
                if (!method.IsStatic && target == null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var invokeTarget = method.IsStatic ? null : target;
                var targetPath = method.IsStatic ? "static" : GetRuntimeObjectPath(target);
                var candidateKey =
                    $"reflection:{type.FullName}:{method.Name}:{GetParameterSummary(method)}:{targetPath}";
                if (!_gymTransitionCandidateAttempts.Add(candidateKey))
                {
                    continue;
                }

                if (parameters.Length == 0)
                {
                    method.Invoke(invokeTarget, Array.Empty<object>());
                    LogInfo($"Invoked gym transition candidate {type.FullName}.{methodName}() target={targetPath}.");
                    return true;
                }

            }
            catch (Exception ex)
            {
                LogWarn($"Invocation failed for {type.FullName}.{methodName}: {ex.Message}");
            }
        }

        return false;
    }

    private static MethodInfo FindInvokableGymMethod(Type type, string methodName, BindingFlags bindingFlags)
    {
        var methods = type.GetMethods(bindingFlags)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToList();

        if (methods.Count == 0)
        {
            return null;
        }

        var zeroArgMethod = methods.FirstOrDefault(method => method.GetParameters().Length == 0);
        if (zeroArgMethod != null)
        {
            return zeroArgMethod;
        }

        var intArgMethod = methods.FirstOrDefault(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
        });
        if (intArgMethod != null)
        {
            return intArgMethod;
        }

        var stringArgMethod = methods.FirstOrDefault(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
        });
        if (stringArgMethod != null)
        {
            return stringArgMethod;
        }

        return methods[0];
    }

    private bool TryInvokeGymTransitionFromComponentSweep()
    {
        Component[] components;
        try
        {
            components = Resources.FindObjectsOfTypeAll<Component>();
        }
        catch
        {
            return false;
        }

        if (components == null || components.Length == 0)
        {
            return false;
        }

        foreach (var component in components)
        {
            if (component == null)
            {
                continue;
            }

            var type = component.GetType();
            if (type == null)
            {
                continue;
            }

            var fullName = type.FullName ?? type.Name ?? string.Empty;
            if (fullName.IndexOf("SceneManager", StringComparison.OrdinalIgnoreCase) < 0 &&
                fullName.IndexOf("BootLoader", StringComparison.OrdinalIgnoreCase) < 0 &&
                fullName.IndexOf("Gym", StringComparison.OrdinalIgnoreCase) < 0 &&
                fullName.IndexOf("Arena", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (TryInvokeGymTransition(type, component))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBuildTrainingScene(string reason)
    {
        if (_trainingEnvironmentManager?.IsReady ?? false)
        {
            return true;
        }

        var scenes = GetLoadedScenes();
        if (scenes.Count == 0)
        {
            LogWarn($"Training scene build skipped ({reason}): no loaded scenes.");
            return false;
        }

        if (_bestPlayerCandidate == null)
        {
            LogWarn($"Training scene build skipped ({reason}): no player candidate yet.");
            return false;
        }

        var sourceRoot = ResolveActorCandidateObject(scenes);
        if (sourceRoot == null)
        {
            LogWarn($"Training scene build skipped ({reason}): lost candidate root {_bestPlayerCandidate.Path}.");
            return false;
        }

        var sourceScene = scenes.FirstOrDefault(scene =>
            scene.IsValid() &&
            scene.isLoaded &&
            scene.name.IndexOf("Gym", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!sourceScene.IsValid() || !sourceScene.isLoaded)
        {
            sourceScene = sourceRoot.scene;
        }
        if (!sourceScene.IsValid() || !sourceScene.isLoaded)
        {
            LogWarn($"Training scene build skipped ({reason}): source scene is not valid.");
            return false;
        }

        var trainingActor = FindPreferredTrainingActor(sourceRoot);
        if (trainingActor == null)
        {
            LogWarn($"Training scene build skipped ({reason}): no training actor resolved under {_bestPlayerCandidate.Path}.");
            return false;
        }

        var trainingActorPath = GetPath(trainingActor.transform);
        var sourceActorPath = GetPath(sourceRoot.transform);
        LogInfo($"Building training scene from source scene '{sourceScene.name}' using candidate '{_bestPlayerCandidate.Path}' (score {_bestPlayerCandidate.Score}).");
        if (!string.Equals(trainingActorPath, sourceActorPath, StringComparison.OrdinalIgnoreCase))
        {
            LogInfo($"Resolved training actor subroot '{trainingActorPath}' from source root '{sourceActorPath}'.");
        }

        var existingTrainingScene = UnitySceneManager.GetSceneByName(TrainingSceneName);
        var trainingSceneAlreadyLoaded = existingTrainingScene.IsValid() && existingTrainingScene.isLoaded;
        var trainingScene = GetOrCreateTrainingScene();
        var movedRoots = new List<string>();
        var prunedRoots = new List<string>();
        var retainedUnknownRoots = new List<string>();
        var keptRoots = new List<string>();
        var createdObjects = new List<string>();
        var pruneDecisions = new List<ArenaRootDecision>();
        if (!trainingSceneAlreadyLoaded)
        {
            createdObjects.Add(TrainingSceneName);
        }

        var actorPosition = trainingActor.transform.position;
        var floorCandidate = FindFloorCandidate(sourceScene, actorPosition);
        MoveRootIntoScene(trainingActor, trainingScene, movedRoots);

        var sourceRoots = sourceScene.GetRootGameObjects();
        foreach (var root in sourceRoots)
        {
            if (root == null || root == trainingActor)
            {
                continue;
            }

            var path = GetPath(root.transform);
            if (_movedRoots.Contains(path))
            {
                keptRoots.Add(path);
                continue;
            }

            var report = AnalyzeRoot(root);
            var preserveReason = GetArenaPreservationReason(
                root,
                report,
                sourceRoot,
                floorCandidate);
            if (!string.IsNullOrWhiteSpace(preserveReason))
            {
                keptRoots.Add(report.Path);
                pruneDecisions.Add(new ArenaRootDecision
                {
                    path = report.Path,
                    action = "preserve",
                    reason = preserveReason,
                    classification = report.ClassificationName
                });
                continue;
            }

            if (EnableArenaPruning && IsExplicitArenaClutter(report))
            {
                UnityObject.Destroy(root);
                _destroyedRoots.Add(report.Path);
                prunedRoots.Add(report.Path);
                pruneDecisions.Add(new ArenaRootDecision
                {
                    path = report.Path,
                    action = "destroy",
                    reason = "explicit_clutter_hint",
                    classification = report.ClassificationName
                });
                continue;
            }

            retainedUnknownRoots.Add(report.Path);
            keptRoots.Add(report.Path);
            pruneDecisions.Add(new ArenaRootDecision
            {
                path = report.Path,
                action = "preserve",
                reason = EnableArenaPruning ? "no_explicit_prune_evidence" : "arena_pruning_disabled",
                classification = report.ClassificationName
            });
        }

        UnitySceneManager.SetActiveScene(trainingScene);
        var actorPlacement = PlaceActorForArena(trainingActor, floorCandidate);
        var floorResolution = EnsureArenaFloor(
            trainingScene,
            trainingActor,
            floorCandidate,
            createdObjects);
        VerifyArenaFloorSupport(trainingActor, floorResolution, actorPlacement);
        var backgroundRoots = sourceRoots
            .Where(root => root != null && IsBackgroundRoot(root))
            .Select(root => GetPath(root.transform))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skyboxPresent = RenderSettings.skybox != null;
        var backgroundStatus = backgroundRoots.Count > 0
            ? "preserved_candidates"
            : skyboxPresent
                ? "skybox_preserved"
                : "not_found";

        var summary = new
        {
            reason,
            sourceScene = sourceScene.name,
            trainingScene = trainingScene.name,
            candidate = _bestPlayerCandidate,
            movedRoots,
            prunedRoots,
            retainedUnknownRoots,
            floorStatus = floorResolution.status,
            actorPlacementStatus = actorPlacement.status,
            backgroundStatus
        };

        WriteJson($"training_plan_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", summary);
        LogInfo($"Training scene ready. Moved={movedRoots.Count}, pruned={prunedRoots.Count}, retainedUnknown={retainedUnknownRoots.Count}.");

        var managerInitialized = _trainingEnvironmentManager.InitializeFromDiscoveredScene(trainingScene, trainingActor, sourceScene.name, _bestPlayerCandidate.Path);
        PublishObservation("training-ready");
        WriteJson($"training_status_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", _trainingEnvironmentManager.GetStatus());
        WriteArenaBuildReport(
            reason,
            sourceScene.name,
            trainingScene.name,
            trainingActorPath,
            sourceActorPath,
            trainingSceneAlreadyLoaded,
            managerInitialized,
            movedRoots,
            keptRoots,
            prunedRoots,
            retainedUnknownRoots,
            createdObjects,
            pruneDecisions,
            floorResolution,
            actorPlacement,
            backgroundRoots,
            backgroundStatus,
            skyboxPresent);
        _bridgeServer?.StartIfNeeded();
        _bridgeServer?.Pump();
        _gymTransitionSucceeded = true;
        LogInfo("Gym source confirmed and training scene is now the bootstrap target.");
        if (!managerInitialized)
        {
            LogWarn("TrainingEnvironmentManager reported an initialization failure after the training scene was built.");
        }

        return managerInitialized;
    }

    private FloorCandidate FindFloorCandidate(Scene scene, Vector3 actorPosition)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return null;
        }

        FloorCandidate best = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root == null)
            {
                continue;
            }

            Collider[] colliders;
            try
            {
                colliders = root.GetComponentsInChildren<Collider>(true);
            }
            catch
            {
                continue;
            }

            foreach (var collider in colliders)
            {
                if (collider == null || collider.isTrigger || !collider.enabled)
                {
                    continue;
                }

                Bounds bounds;
                try
                {
                    bounds = collider.bounds;
                }
                catch
                {
                    continue;
                }

                var horizontalArea = bounds.size.x * bounds.size.z;
                var topDelta = bounds.max.y - actorPosition.y;
                if (horizontalArea < 9f)
                {
                    continue;
                }

                var path = GetPath(collider.transform);
                var score = 0;
                if (HasAny(path, ArenaFloorHints))
                {
                    score += 200;
                }
                if (HasAny(path, new[] { "GYM_Collission", "GYM_Collision", "Collission floor", "Collision floor" }))
                {
                    score += 600;
                }
                if (HasAny(path, new[] { "Gondola", "Matchmake", "INTERACTABLES", "Dressing Room" }))
                {
                    score -= 500;
                }
                if (collider is TerrainCollider)
                {
                    score += 180;
                }
                if (horizontalArea >= 100f)
                {
                    score += 80;
                }
                else if (horizontalArea >= 25f)
                {
                    score += 40;
                }
                if (Math.Abs(topDelta) <= 0.5f)
                {
                    score += 60;
                }
                if (bounds.size.y <= 2f)
                {
                    score += 30;
                }

                if (best != null && best.score >= score)
                {
                    continue;
                }

                best = new FloorCandidate
                {
                    root = root,
                    collider = collider,
                    rootPath = GetPath(root.transform),
                    colliderPath = path,
                    colliderType = collider.GetType().FullName ?? collider.GetType().Name,
                    score = score,
                    boundsCenter = TrainingProbeVector3.From(bounds.center),
                    boundsSize = TrainingProbeVector3.From(bounds.size),
                    boundsTopY = bounds.max.y
                };
            }
        }

        return best;
    }

    private static ActorPlacementResolution PlaceActorForArena(
        GameObject actor,
        FloorCandidate floorCandidate)
    {
        if (actor == null)
        {
            return new ActorPlacementResolution
            {
                status = "actor_missing",
                warning = "Actor placement could not run because the actor was missing."
            };
        }

        var original = actor.transform.position;
        if (floorCandidate == null || floorCandidate.score <= 0)
        {
            return new ActorPlacementResolution
            {
                status = "kept_original_position",
                originalPosition = TrainingProbeVector3.From(original),
                targetPosition = TrainingProbeVector3.From(original),
                warning = "No positive-confidence Gym floor candidate was available; fallback floor placement remains live-unverified."
            };
        }

        Vector3 target;
        if (TryFindFloorSurfacePoint(
                floorCandidate.collider,
                original,
                out var surfaceHit,
                out var surfaceRayOrigin))
        {
            target = surfaceHit.point + Vector3.up * 0.08f;
            actor.transform.position = target;
            return new ActorPlacementResolution
            {
                status = "placed_on_sampled_floor_surface",
                originalPosition = TrainingProbeVector3.From(original),
                targetPosition = TrainingProbeVector3.From(target),
                floorColliderPath = floorCandidate.colliderPath,
                surfaceProbeStatus = "upward_surface_hit",
                surfaceRayOrigin = TrainingProbeVector3.From(surfaceRayOrigin),
                surfacePoint = TrainingProbeVector3.From(surfaceHit.point),
                surfaceNormal = TrainingProbeVector3.From(surfaceHit.normal),
                surfaceDistance = surfaceHit.distance,
                warning = "Sampled floor surface found; final support ray verification is pending."
            };
        }

        target = new Vector3(
            floorCandidate.boundsCenter.x,
            floorCandidate.boundsTopY + 0.08f,
            floorCandidate.boundsCenter.z);
        actor.transform.position = target;
        return new ActorPlacementResolution
        {
            status = "placed_on_floor_bounds_fallback",
            originalPosition = TrainingProbeVector3.From(original),
            targetPosition = TrainingProbeVector3.From(target),
            floorColliderPath = floorCandidate.colliderPath,
            surfaceProbeStatus = "no_upward_surface_hit",
            warning = "No upward-facing collider surface sample was found; bounds placement still requires live verification."
        };
    }

    private static bool TryFindFloorSurfacePoint(
        Collider collider,
        Vector3 preferredPosition,
        out RaycastHit bestHit,
        out Vector3 bestOrigin)
    {
        bestHit = default;
        bestOrigin = default;
        if (collider == null || !collider.enabled)
        {
            return false;
        }

        Bounds bounds;
        try
        {
            Physics.SyncTransforms();
            bounds = collider.bounds;
        }
        catch
        {
            return false;
        }

        var samplePoints = new List<Vector2>
        {
            new(
                Mathf.Clamp(preferredPosition.x, bounds.min.x, bounds.max.x),
                Mathf.Clamp(preferredPosition.z, bounds.min.z, bounds.max.z)),
            new(bounds.center.x, bounds.center.z)
        };
        var fractions = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f };
        foreach (var xFraction in fractions)
        {
            foreach (var zFraction in fractions)
            {
                samplePoints.Add(new Vector2(
                    Mathf.Lerp(bounds.min.x, bounds.max.x, xFraction),
                    Mathf.Lerp(bounds.min.z, bounds.max.z, zFraction)));
            }
        }

        var rayStartY = bounds.max.y + 1f;
        var maxDistance = Mathf.Max(2f, bounds.size.y + 2f);
        var found = false;
        var bestHorizontalDistance = float.MaxValue;
        foreach (var sample in samplePoints)
        {
            var origin = new Vector3(sample.x, rayStartY, sample.y);
            if (!collider.Raycast(new Ray(origin, Vector3.down), out var hit, maxDistance) ||
                hit.normal.y < 0.25f)
            {
                continue;
            }

            var dx = hit.point.x - preferredPosition.x;
            var dz = hit.point.z - preferredPosition.z;
            var horizontalDistance = dx * dx + dz * dz;
            if (found && horizontalDistance >= bestHorizontalDistance)
            {
                continue;
            }

            found = true;
            bestHorizontalDistance = horizontalDistance;
            bestHit = hit;
            bestOrigin = origin;
        }

        return found;
    }

    private static string GetArenaPreservationReason(
        GameObject root,
        RootReport report,
        GameObject sourceRoot,
        FloorCandidate floorCandidate)
    {
        if (root == null || report == null)
        {
            return null;
        }

        if (root == sourceRoot)
        {
            return "actor_source_container";
        }

        if (floorCandidate?.root == root)
        {
            return "floor_collider_candidate";
        }

        if (IsBackgroundRoot(root))
        {
            return "background_or_environment_candidate";
        }

        switch (report.Classification)
        {
            case RootClassification.Player:
                return "additional_player_or_rig_candidate";
            case RootClassification.Support:
                return "support_system_candidate";
            case RootClassification.Unknown:
                return "unknown_requires_live_review";
            default:
                return null;
        }
    }

    private static bool IsBackgroundRoot(GameObject root)
    {
        if (root == null)
        {
            return false;
        }

        var path = GetPath(root.transform);
        if (HasAny(path, ArenaBackgroundHints))
        {
            return true;
        }

        try
        {
            return root.GetComponentsInChildren<Terrain>(true).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExplicitArenaClutter(RootReport report)
    {
        if (report == null)
        {
            return false;
        }

        return HasAny($"{report.Name} {report.Path}", ExplicitArenaClutterHints);
    }

    private static FloorResolution EnsureArenaFloor(
        Scene trainingScene,
        GameObject trainingActor,
        FloorCandidate candidate,
        List<string> createdObjects)
    {
        if (candidate != null &&
            candidate.score > 0 &&
            candidate.root != null &&
            candidate.root.scene.IsValid() &&
            candidate.root.scene.isLoaded)
        {
            return new FloorResolution
            {
                status = "preserved_collider_candidate",
                usableFloorConfirmed = false,
                colliderPresent = true,
                supportCollider = candidate.collider,
                rootPath = candidate.rootPath,
                colliderPath = candidate.colliderPath,
                colliderType = candidate.colliderType,
                candidateScore = candidate.score,
                boundsCenter = candidate.boundsCenter,
                boundsSize = candidate.boundsSize,
                warning = "Collider geometry is plausible, but actor support requires live physics verification."
            };
        }

        var existing = trainingScene.GetRootGameObjects()
            .FirstOrDefault(root =>
                root != null &&
                string.Equals(root.name, "AI_Train_FallbackFloor", StringComparison.Ordinal));
        if (existing != null)
        {
            var existingCollider = existing.GetComponent<Collider>();
            return new FloorResolution
            {
                status = "fallback_floor_reused",
                usableFloorConfirmed = false,
                colliderPresent = existingCollider != null && existingCollider.enabled,
                supportCollider = existingCollider,
                rootPath = GetPath(existing.transform),
                colliderPath = existingCollider != null ? GetPath(existingCollider.transform) : null,
                colliderType = existingCollider?.GetType().FullName,
                warning = "Fallback floor exists, but actor support requires live physics verification."
            };
        }

        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "AI_Train_FallbackFloor";
        floor.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
        floor.transform.position = new Vector3(
            trainingActor.transform.position.x,
            trainingActor.transform.position.y - 0.3f,
            trainingActor.transform.position.z);
        floor.transform.localScale = new Vector3(24f, 0.5f, 24f);
        if (floor.scene != trainingScene)
        {
            UnitySceneManager.MoveGameObjectToScene(floor, trainingScene);
        }

        var collider = floor.GetComponent<Collider>();
        createdObjects?.Add(GetPath(floor.transform));
        return new FloorResolution
        {
            status = "fallback_floor_created",
            usableFloorConfirmed = false,
            colliderPresent = collider != null && collider.enabled,
            supportCollider = collider,
            rootPath = GetPath(floor.transform),
            colliderPath = collider != null ? GetPath(collider.transform) : null,
            colliderType = collider?.GetType().FullName,
            boundsCenter = collider != null ? TrainingProbeVector3.From(collider.bounds.center) : null,
            boundsSize = collider != null ? TrainingProbeVector3.From(collider.bounds.size) : null,
            warning = "Fallback collider was created beneath the actor, but support requires live physics verification."
        };
    }

    private static void VerifyArenaFloorSupport(
        GameObject actor,
        FloorResolution floor,
        ActorPlacementResolution actorPlacement)
    {
        if (floor == null)
        {
            return;
        }

        var supportCollider = floor.supportCollider;
        if (actor == null || supportCollider == null || !supportCollider.enabled)
        {
            floor.supportProbeStatus = actor == null ? "actor_missing" : "collider_missing_or_disabled";
            return;
        }

        try
        {
            Physics.SyncTransforms();
            var origin = actor.transform.position + Vector3.up * 0.5f;
            var ray = new Ray(origin, Vector3.down);
            floor.supportRayOrigin = TrainingProbeVector3.From(origin);
            if (!supportCollider.Raycast(ray, out var hit, 2f))
            {
                floor.supportProbeStatus = "selected_collider_raycast_missed";
                floor.warning = "The selected floor collider did not intersect the bounded support ray beneath the actor.";
                return;
            }

            floor.usableFloorConfirmed = true;
            floor.supportProbeStatus = "selected_collider_raycast_confirmed";
            floor.supportPoint = TrainingProbeVector3.From(hit.point);
            floor.supportDistance = hit.distance;
            floor.warning = null;
            if (actorPlacement != null)
            {
                actorPlacement.status = "placed_on_confirmed_floor";
                actorPlacement.warning = null;
            }
        }
        catch (Exception ex)
        {
            floor.supportProbeStatus = "support_raycast_failed";
            floor.warning = $"Floor support raycast failed: {ex.Message}";
        }
    }

    private void PublishObservation(string reason)
    {
        if (_trainingEnvironmentManager == null || _observationBuilder == null)
        {
            LogWarn($"Observation publish skipped ({reason}): manager or builder unavailable.");
            return;
        }

        try
        {
            LogInfo($"Building observation snapshot ({reason}).");
            var observation = _observationBuilder.BuildObservation(_trainingEnvironmentManager, reason);
            WriteJson($"observation_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", observation);
            LogInfo($"Observation snapshot written ({reason}). sceneReady={observation.sceneReady} episodeId={observation.episodeId} episodeStep={observation.episodeStep} warnings={observation.warnings?.Count ?? 0}.");
            if (_trainingEnvironmentManager?.IsReady ?? false)
            {
                _bridgeServer?.StartIfNeeded();
                _bridgeServer?.Pump();
            }
        }
        catch (Exception ex)
        {
            LogError($"Observation snapshot failed ({reason}): {ex.Message}");
        }
    }

    private void PublishDebugProbe(string reason)
    {
        if (_trainingEnvironmentManager == null || _explorationService == null)
        {
            LogWarn($"Debug probe skipped ({reason}): manager or exploration service unavailable.");
            return;
        }

        try
        {
            LogInfo($"Building debug probe snapshot ({reason}).");
            var probe = _explorationService.BuildDebugProbe(_trainingEnvironmentManager, reason);
            WriteJson($"debug_probe_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", probe);
            LogInfo(
                $"Debug probe snapshot written ({reason}). sceneReady={probe.sceneReady} playerRootFound={probe.playerRootFound} " +
                $"probeHostReady={probe.probeHostReady} targets={probe.types?.Count ?? 0} warnings={probe.warnings?.Count ?? 0}.");
        }
        catch (Exception ex)
        {
            LogError($"Debug probe failed ({reason}): {ex.Message}");
        }
    }

    private static Scene GetOrCreateTrainingScene()
    {
        var existing = UnitySceneManager.GetSceneByName(TrainingSceneName);
        if (existing.IsValid() && existing.isLoaded)
        {
            return existing;
        }

        return UnitySceneManager.CreateScene(TrainingSceneName);
    }

    private static TrainingRuntimeHost CreateRuntimeHost()
    {
        ClassInjector.RegisterTypeInIl2Cpp<TrainingRuntimeHost>();
        ClassInjector.RegisterTypeInIl2Cpp<TrainingProbeCollisionRecorder>();
        var hostObject = new GameObject("AI_Train_RuntimeHost");
        hostObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
        UnityEngine.Object.DontDestroyOnLoad(hostObject);
        return hostObject.AddComponent<TrainingRuntimeHost>();
    }

    private GameObject ResolvePrimaryActor()
    {
        var managerActor = _trainingEnvironmentManager?.CurrentPlayerRoot;
        if (managerActor != null)
        {
            return managerActor;
        }

        var scenes = GetLoadedScenes();
        var sourceRoot = ResolveActorCandidateObject(scenes);
        return sourceRoot != null ? FindPreferredTrainingActor(sourceRoot) : null;
    }

    private GameObject ResolveActorCandidateObject(List<Scene> scenes)
    {
        if (_preservedActorCandidate != null)
        {
            return _preservedActorCandidate;
        }

        return _bestPlayerCandidate != null
            ? FindRootByPath(_bestPlayerCandidate.Path, scenes)
            : null;
    }

    private GameObject FindPreferredTrainingActor(GameObject sourceRoot)
    {
        if (sourceRoot == null)
        {
            return null;
        }

        if (string.Equals(sourceRoot.name, "BootLoaderPlayer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceRoot.name, "LocalPlayer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceRoot.name, "PlayerController", StringComparison.OrdinalIgnoreCase))
        {
            return sourceRoot;
        }

        var preferredDirectChildren = new[]
        {
            "BootLoaderPlayer",
            "PlayerController",
            "LocalPlayer",
            "VRIK",
            "Player"
        };

        foreach (var childName in preferredDirectChildren)
        {
            var child = sourceRoot.transform.Find(childName);
            if (child != null)
            {
                return child.gameObject;
            }
        }

        var best = sourceRoot;
        var bestScore = ScoreTrainingActorCandidate(sourceRoot.transform);
        foreach (var child in sourceRoot.transform)
        {
            if (child is not Transform transform)
            {
                continue;
            }

            var score = ScoreTrainingActorCandidate(transform);
            if (score > bestScore)
            {
                best = transform.gameObject;
                bestScore = score;
            }
        }

        return best;
    }

    private static int ScoreTrainingActorCandidate(Transform transform)
    {
        if (transform == null)
        {
            return int.MinValue;
        }

        var path = GetPath(transform);
        var score = 0;

        AddPathScore(path, "BootLoaderPlayer", 1000, ref score);
        AddPathScore(path, "LocalPlayer", 800, ref score);
        AddPathScore(path, "PlayerController", 700, ref score);
        AddPathScore(path, "VRIK", 500, ref score);
        AddPathScore(path, "Avatar", 250, ref score);
        AddPathScore(path, "Rig", 180, ref score);
        AddPathScore(path, "Player", 120, ref score);
        AddPathScore(path, "Controller", 80, ref score);
        AddPathScore(path, "Hand", 80, ref score);
        AddPathScore(path, "Headset", 75, ref score);

        var depth = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '/')
            {
                depth++;
            }
        }

        score -= depth * 75;

        if (transform.parent != null)
        {
            score += 35;
        }

        if (transform.childCount > 0)
        {
            score += 20;
        }

        return score;
    }

    private static void AddPathScore(string path, string token, int amount, ref int score)
    {
        if (!string.IsNullOrWhiteSpace(path) && path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += amount;
        }
    }

    private static bool HasGymLikeScene(IEnumerable<SceneReport> reports)
    {
        foreach (var report in reports)
        {
            if (report.IsGymLike)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGymLikeScene(string sceneName, IEnumerable<RootReport> roots)
    {
        if (!string.IsNullOrWhiteSpace(sceneName) &&
            (sceneName.IndexOf("gym", StringComparison.OrdinalIgnoreCase) >= 0 ||
             sceneName.IndexOf("arena", StringComparison.OrdinalIgnoreCase) >= 0 ||
             sceneName.IndexOf("practice", StringComparison.OrdinalIgnoreCase) >= 0 ||
             sceneName.IndexOf("training", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        foreach (var root in roots)
        {
            if (root == null)
            {
                continue;
            }

            if ((root.Path != null && (
                    root.Path.IndexOf("gym", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    root.Path.IndexOf("arena", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    root.Path.IndexOf("practice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    root.Path.IndexOf("training", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                (root.Name != null && (
                    root.Name.IndexOf("gym", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    root.Name.IndexOf("arena", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    root.Name.IndexOf("practice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    root.Name.IndexOf("training", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBootstrapScene(Scene scene)
    {
        if (!scene.IsValid())
        {
            return false;
        }

        var sceneName = scene.name ?? string.Empty;
        return sceneName.IndexOf("loader", StringComparison.OrdinalIgnoreCase) >= 0 ||
               sceneName.IndexOf("bootloader", StringComparison.OrdinalIgnoreCase) >= 0 ||
               sceneName.IndexOf("calibration", StringComparison.OrdinalIgnoreCase) >= 0 ||
               sceneName.IndexOf("measurement", StringComparison.OrdinalIgnoreCase) >= 0 ||
               sceneName.IndexOf("intro", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void MoveRootIntoScene(GameObject root, Scene targetScene, List<string> movedRoots)
    {
        if (root == null || !root.scene.IsValid() || root.scene == targetScene)
        {
            return;
        }

        var path = GetPath(root.transform);
        if (root.transform.parent != null)
        {
            root.transform.SetParent(null, true);
        }

        if (_movedRoots.Add(path))
        {
            movedRoots.Add(path);
            UnitySceneManager.MoveGameObjectToScene(root, targetScene);
            LogInfo($"Moved root into training scene: {path}");
        }
    }

    private GameObject FindRootByPath(string path, List<Scene> scenes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var scene in scenes)
        {
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                var rootPath = GetPath(root.transform);
                if (string.Equals(rootPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return root;
                }

                Transform[] transforms;
                try
                {
                    transforms = root.GetComponentsInChildren<Transform>(true);
                }
                catch
                {
                    continue;
                }

                foreach (var transform in transforms)
                {
                    if (transform != null &&
                        string.Equals(GetPath(transform), path, StringComparison.OrdinalIgnoreCase))
                    {
                        return transform.gameObject;
                    }
                }
            }
        }

        return null;
    }

    private string WriteSceneBundle(string reason, List<SceneReport> reports, SceneCandidate candidate)
    {
        var timestampUtc = DateTime.UtcNow;
        var bundle = new
        {
            timestampUtc,
            reason,
            activeScene = UnitySceneManager.GetActiveScene().name,
            lastActiveScene = _lastActiveSceneName,
            candidate,
            scenes = reports
        };

        var path = WriteJson($"scene_bundle_{timestampUtc:yyyyMMdd_HHmmss_fff}.json", bundle);
        LogInfo($"Scene bundle written ({reason}). Scenes={reports.Count}, candidate={(candidate?.Path ?? "none")}.");
        return path;
    }

    private string WriteSceneInventory(string reason, List<SceneReport> reports, SceneCandidate candidate)
    {
        var timestampUtc = DateTime.UtcNow;
        var inventory = new
        {
            timestampUtc,
            reason,
            activeScene = UnitySceneManager.GetActiveScene().name,
            lastActiveScene = _lastActiveSceneName,
            gymLoaded = HasGymLikeScene(reports),
            candidate,
            scenes = reports
        };

        var timestampedPath = WriteJson($"scene_inventory_{timestampUtc:yyyyMMdd_HHmmss_fff}.json", inventory);
        WriteJson("latest_scene_inventory.json", inventory);
        LogInfo($"Scene inventory written ({reason}). Scenes={reports.Count}, candidate={(candidate?.Path ?? "none")}.");
        return timestampedPath;
    }

    private string WriteArenaBuildReport(
        string reason,
        string sourceSceneName,
        string trainingSceneName,
        string trainingActorPath,
        string sourceActorPath,
        bool trainingSceneReused,
        bool managerInitialized,
        List<string> movedRoots,
        List<string> keptRoots,
        List<string> destroyedRoots,
        List<string> retainedUnknownRoots,
        List<string> createdObjects,
        List<ArenaRootDecision> pruneDecisions,
        FloorResolution floorResolution,
        ActorPlacementResolution actorPlacement,
        List<string> backgroundRoots,
        string backgroundStatus,
        bool skyboxPresent)
    {
        var timestampUtc = DateTime.UtcNow;
        var report = new
        {
            timestampUtc,
            reason,
            sourceScene = sourceSceneName,
            trainingScene = trainingSceneName,
            trainingSceneReused,
            managerInitialized,
            actorRootPath = trainingActorPath,
            sourceActorPath,
            keptRoots = keptRoots ?? new List<string>(),
            movedRoots = movedRoots ?? new List<string>(),
            destroyedRoots = destroyedRoots ?? new List<string>(),
            retainedUnknownRoots = retainedUnknownRoots ?? new List<string>(),
            createdObjects = createdObjects ?? new List<string>(),
            pruneDecisions = pruneDecisions ?? new List<ArenaRootDecision>(),
            floor = floorResolution,
            floorStatus = floorResolution?.status ?? "not_evaluated",
            actorPlacement,
            backgroundRoots = backgroundRoots ?? new List<string>(),
            backgroundStatus = backgroundStatus ?? "not_evaluated",
            skyboxPresent,
            cameraStatus = _monitorCamera != null ? "monitor-camera-present" : "monitor-camera-missing",
            bridgeStatus = _bridgeServer != null ? "bridge-present" : "bridge-missing",
            warnings = managerInitialized
                ? new List<string>()
                : new List<string> { "training_environment_manager_initialization_failed" }
        };

        var path = WriteJson($"arena_build_report_{timestampUtc:yyyyMMdd_HHmmss}.json", report);
        WriteJson("latest_arena_build_report.json", report);
        _bootstrapOrchestrator?.RecordArenaBuild(path, managerInitialized);
        _trainingEnvironmentManager?.UpdateBootstrapState(_bootstrapOrchestrator?.State);
        LogInfo($"Arena build report written ({reason}). managerInitialized={managerInitialized} path={path ?? "none"}.");
        return path;
    }

    private void LogSceneReport(SceneReport report)
    {
        var header = new StringBuilder();
        header.Append("Scene ");
        header.Append(report.SceneName);
        header.Append(" roots=");
        header.Append(report.RootCount);
        header.Append(" active=");
        header.Append(report.IsActive);
        header.Append(" best=");
        header.Append(report.BestPlayerCandidate?.Path ?? "none");
        header.Append(" score=");
        header.Append(report.BestPlayerCandidate?.Score ?? 0);
        LogInfo(header.ToString());

        var roots = report.Roots.Take(MaxRootsToLogPerScene).ToList();
        foreach (var root in roots)
        {
            LogInfo($"  [{root.Classification}] {root.Path} score={root.Score} children={root.ChildCount} active={root.ActiveInHierarchy} reasons={string.Join(",", root.Reasons)}");
        }
    }

    private string WriteJson(string fileName, object payload)
    {
        try
        {
            var path = Path.Combine(_dumpRoot, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(payload, _jsonOptions));
            LogInfo($"Wrote dump: {path}");
            return path;
        }
        catch (Exception ex)
        {
            LogError($"Failed to write JSON dump {fileName}: {ex.Message}");
            return null;
        }
    }

    private void LogInfo(string message)
    {
        WriteLine("INFO", message);
        MelonLogger.Msg(message);
    }

    private void LogWarn(string message)
    {
        WriteLine("WARN", message);
        MelonLogger.Warning(message);
    }

    private void LogError(string message)
    {
        WriteLine("ERROR", message);
        MelonLogger.Error(message);
    }

    private void WriteLine(string level, string message)
    {
        var line = $"[{DateTime.UtcNow:O}] [{level}] {message}";
        try
        {
            _writer?.WriteLine(line);
        }
        catch
        {
            // Logging must never break the mod.
        }
    }

    private sealed class SceneCandidate
    {
        public SceneCandidate(RootReport report)
        {
            Path = report.Path;
            Score = report.Score;
            Classification = report.Classification;
            Reasons = report.Reasons;
            ComponentTypes = report.ComponentTypes;
            ChildCount = report.ChildCount;
            Evidence = report.Reasons?.ToList() ?? new List<string>();
            IsStrongActor = false;
        }

        public SceneCandidate(
            GameObject gameObject,
            int score,
            bool isStrongActor,
            IEnumerable<string> evidence)
        {
            Path = gameObject != null ? GetPath(gameObject.transform) : null;
            Score = score;
            Classification = RootClassification.Player;
            Reasons = evidence?.ToList() ?? new List<string>();
            ComponentTypes = gameObject != null
                ? GetComponentTypeNames(gameObject).ToList()
                : new List<string>();
            ChildCount = gameObject != null ? gameObject.transform.childCount : 0;
            Evidence = Reasons;
            IsStrongActor = isStrongActor;
        }

        public string Path { get; }
        public int Score { get; }
        public RootClassification Classification { get; }
        public IReadOnlyList<string> Reasons { get; }
        public IReadOnlyList<string> ComponentTypes { get; }
        public int ChildCount { get; }
        public IReadOnlyList<string> Evidence { get; }
        public bool IsStrongActor { get; }
    }

    private sealed class SceneReport
    {
        public string SceneName { get; set; }
        public int BuildIndex { get; set; }
        public bool IsValid { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsActive { get; set; }
        public bool IsGymLike { get; set; }
        public string LikelySceneRole { get; set; }
        public int RootCount { get; set; }
        public List<string> RootNames { get; set; }
        public List<string> CandidatePlayerRoots { get; set; }
        public List<string> CandidateSupportRoots { get; set; }
        public List<string> CandidateEnvironmentRoots { get; set; }
        public List<RootReport> Roots { get; set; }
        public RootReport BestPlayerCandidate { get; set; }
    }

    private sealed class RootReport
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int Score { get; set; }
        public RootClassification Classification { get; set; }
        public string ClassificationName { get; set; }
        public string SuggestedAction { get; set; }
        public List<string> Reasons { get; set; }
        public List<string> MatchingHints { get; set; }
        public List<string> ComponentTypes { get; set; }
        public int ChildCount { get; set; }
        public bool ActiveSelf { get; set; }
        public bool ActiveInHierarchy { get; set; }
        public int Layer { get; set; }
        public string Tag { get; set; }
    }

    private sealed class ComponentDiscoveryEntry
    {
        public string ComponentPath { get; set; }
        public string TypeFullName { get; set; }
        public string MemberName { get; set; }
        public string MemberKind { get; set; }
        public string ParameterSummary { get; set; }
        public string ReturnType { get; set; }
        public string DeclaringType { get; set; }
        public int Score { get; set; }
        public string RiskLevel { get; set; }
        public string SuggestedProbe { get; set; }
        public List<string> MatchedHints { get; set; }
        public string Source { get; set; }
    }

    private sealed class FloorCandidate
    {
        public GameObject root { get; set; }
        public Collider collider;
        public string rootPath { get; set; }
        public string colliderPath { get; set; }
        public string colliderType { get; set; }
        public int score { get; set; }
        public TrainingProbeVector3 boundsCenter { get; set; }
        public TrainingProbeVector3 boundsSize { get; set; }
        public float boundsTopY { get; set; }
    }

    private sealed class FloorResolution
    {
        public string status { get; set; }
        public bool usableFloorConfirmed { get; set; }
        public bool colliderPresent { get; set; }
        public Collider supportCollider;
        public string rootPath { get; set; }
        public string colliderPath { get; set; }
        public string colliderType { get; set; }
        public int candidateScore { get; set; }
        public TrainingProbeVector3 boundsCenter { get; set; }
        public TrainingProbeVector3 boundsSize { get; set; }
        public string supportProbeStatus { get; set; }
        public TrainingProbeVector3 supportRayOrigin { get; set; }
        public TrainingProbeVector3 supportPoint { get; set; }
        public float? supportDistance { get; set; }
        public string warning { get; set; }
    }

    private sealed class ArenaRootDecision
    {
        public string path { get; set; }
        public string action { get; set; }
        public string reason { get; set; }
        public string classification { get; set; }
    }

    private sealed class ActorPlacementResolution
    {
        public string status { get; set; }
        public TrainingProbeVector3 originalPosition { get; set; }
        public TrainingProbeVector3 targetPosition { get; set; }
        public string floorColliderPath { get; set; }
        public string surfaceProbeStatus { get; set; }
        public TrainingProbeVector3 surfaceRayOrigin { get; set; }
        public TrainingProbeVector3 surfacePoint { get; set; }
        public TrainingProbeVector3 surfaceNormal { get; set; }
        public float? surfaceDistance { get; set; }
        public string warning { get; set; }
    }

    private static class ActorCapabilityHints
    {
        public static readonly string[] Input = { "Input", "Controller", "Hand", "Grab", "Grip", "Button", "Trigger" };
        public static readonly string[] Move = { "Move", "Movement", "Locomotion", "Velocity", "Force", "Rigidbody", "Teleport", "Dash" };
        public static readonly string[] Summon = { "Summon", "Spawn", "Spawner", "Spawnable", "Create", "Prefab", "Pool", "Cube", "Structure", "Projectile", "Ball", "Ground" };
        public static readonly string[] Modifier = { "Modifier", "Modify", "Spell", "Cast", "Charge", "Effect", "Power", "Ability" };
        public static readonly string[] Ownership = { "Owner", "Ownership", "PlayerId", "ActorId", "Network", "Photon", "LocalPlayer", "Authority" };
        public static readonly string[] AttackHealthDamage = { "Attack", "Damage", "Hit", "Health", "Hurt", "Collision", "Contact", "Weapon", "Knock", "Impact" };
        public static readonly string[] InputGesture = Input.Concat(new[] { "Gesture", "Recognizer", "Pose", "Rune" }).ToArray();
        public static readonly string[] All = Input
            .Concat(Move)
            .Concat(Summon)
            .Concat(Modifier)
            .Concat(Ownership)
            .Concat(AttackHealthDamage)
            .Concat(InputGesture)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private enum ActorTransformRole
    {
        Head,
        LeftHand,
        RightHand
    }

    private enum RootClassification
    {
        Unknown = 0,
        Support = 1,
        Player = 2,
        Environment = 3
    }
}






