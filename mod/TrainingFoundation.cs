using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
    private const bool AutoBuildTrainingScene = true;
    private const bool AutoPruneSourceScene = true;
    private const bool LogFullSceneHierarchy = true;
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

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HashSet<string> _seenScenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _movedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _destroyedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownRoots = new(StringComparer.OrdinalIgnoreCase);

    private TrainingEnvironmentManager _trainingEnvironmentManager;
    private ObservationBuilder _observationBuilder;
    private ActionExecutor _actionExecutor;
    private TrainingBridgeServer _bridgeServer;
    private string _logRoot;
    private string _dumpRoot;
    private string _logFilePath;
    private StreamWriter _writer;
    private float _nextScanTime;
    private DateTime _initializedAtUtc;
    private bool _initialScanComplete;
    private bool _gymTransitionAttempted;
    private bool _gymTransitionSucceeded;
    private string _lastActiveSceneName;
    private SceneCandidate _bestPlayerCandidate;

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
        _bridgeServer = new TrainingBridgeServer(_trainingEnvironmentManager, _observationBuilder, _actionExecutor, LogInfo, LogWarn, LogError);

        LogInfo("AI_Train bootstrap initialized.");
        LogInfo($"Log file: {_logFilePath}");
        LogInfo("Hotkeys: F7 = dump active scenes, F8 = force training scene build, F9 = rescan all, F11 = dump observation.");

        ScanAllScenes("initialize");
    }

    public void OnUpdate()
    {
        if (Input.GetKeyDown(KeyCode.F7))
        {
            ScanAllScenes("manual-dump");
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            TryBuildTrainingScene("manual-force");
        }

        if (Input.GetKeyDown(KeyCode.F9))
        {
            ScanAllScenes("manual-rescan");
        }

        if (Input.GetKeyDown(KeyCode.F10))
        {
            TryForceGymLoad("manual-force-gym");
        }

        if (Input.GetKeyDown(KeyCode.F11))
        {
            PublishObservation("manual-observation");
        }

        _trainingEnvironmentManager?.UpdateTelemetry(Time.frameCount, Time.unscaledTime);
        _bridgeServer?.Pump();

        if (Time.unscaledTime >= _nextScanTime)
        {
            _nextScanTime = Time.unscaledTime + AutoScanIntervalSeconds;
            if (_initialScanComplete && !(_trainingEnvironmentManager?.IsReady ?? false))
            {
                ScanAllScenes("periodic");
            }
        }

        if (!(_trainingEnvironmentManager?.IsReady ?? false) &&
            !_gymTransitionSucceeded &&
            !_gymTransitionAttempted &&
            DateTime.UtcNow - _initializedAtUtc >= TimeSpan.FromSeconds(15))
        {
            TryForceGymLoad("auto-loader-timeout");
        }

        if (_trainingEnvironmentManager?.IsReady ?? false)
        {
            _bridgeServer?.StartIfNeeded();
        }
    }

    public void OnLateUpdate()
    {
        _actionExecutor?.Pump(Time.unscaledDeltaTime);
    }

    public void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        LogInfo($"Melon scene callback loaded: {sceneName} ({buildIndex})");
        ScanAllScenes($"melon-loaded:{sceneName}");
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
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // Best effort shutdown.
        }
    }

    private void ScanAllScenes(string reason)
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

        _bestPlayerCandidate = currentBest;
        _initialScanComplete = true;
        _lastActiveSceneName = UnitySceneManager.GetActiveScene().name;

        WriteSceneBundle(reason, reports, _bestPlayerCandidate);

        if (AutoBuildTrainingScene && !(_trainingEnvironmentManager?.IsReady ?? false) && _bestPlayerCandidate != null)
        {
            TryBuildTrainingScene(reason);
        }
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
            IsActive = scene == UnitySceneManager.GetActiveScene(),
            RootCount = rootObjects.Length,
            Roots = roots.OrderByDescending(r => r.Score).ToList(),
            BestPlayerCandidate = bestCandidate
        };

        if (LogFullSceneHierarchy && logHierarchy)
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
        return new RootReport
        {
            Name = root.name,
            Path = path,
            Score = score,
            Classification = classification,
            Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
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

    private void TryForceGymLoad(string reason)
    {
        if (_gymTransitionAttempted)
        {
            return;
        }

        _gymTransitionAttempted = true;

        LogInfo($"Attempting gym transition ({reason}).");
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
                    _gymTransitionSucceeded = true;
                    LogInfo($"Gym transition invocation succeeded via static call on {type.FullName}");
                    return;
                }

                var instance = FindFirstRuntimeInstance(type);
                if (instance != null)
                {
                    LogInfo($"Gym probe instance found: {type.FullName} at {GetRuntimeObjectPath(instance)}");
                    if (TryInvokeGymTransition(type, instance))
                    {
                        _gymTransitionSucceeded = true;
                        LogInfo($"Gym transition invocation succeeded via {type.FullName} instance");
                        return;
                    }
                }
            }
        }

        LogWarn("Gym transition attempt did not find a callable method.");
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

            var method = typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" &&
                                     m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType == typeof(Type));

            if (method != null)
            {
                try
                {
                    var results = method.Invoke(null, new object[] { type }) as System.Collections.IEnumerable;
                    if (results != null)
                    {
                        foreach (var result in results)
                        {
                            if (result != null)
                            {
                                return result;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore and fall back to static reference discovery.
                }
            }

            var sweepTypes = new[]
            {
                typeof(Component),
                typeof(GameObject),
                typeof(UnityEngine.Object)
            };

            foreach (var sweepType in sweepTypes)
            {
                var allObjects = method?.Invoke(null, new object[] { sweepType }) as System.Collections.IEnumerable;
                if (allObjects == null)
                {
                    continue;
                }

                foreach (var candidate in allObjects)
                {
                    if (IsMatchingRuntimeInstance(candidate, type))
                    {
                        return candidate;
                    }
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
            "DoTransitionToGym",
            "BootLoaderToGymTransition",
            "LoadArenaScene",
            "LoadScene",
            "LoadSceneAsync"
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
                if (parameters.Length == 0)
                {
                    method.Invoke(invokeTarget, Array.Empty<object>());
                    LogInfo($"Invoked {type.FullName}.{methodName}()");
                    return true;
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                {
                    method.Invoke(invokeTarget, new object[] { 1 });
                    LogInfo($"Invoked {type.FullName}.{methodName}(1)");
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

    private void TryBuildTrainingScene(string reason)
    {
        if (_trainingEnvironmentManager?.IsReady ?? false)
        {
            return;
        }

        var scenes = GetLoadedScenes();
        if (scenes.Count == 0)
        {
            LogWarn($"Training scene build skipped ({reason}): no loaded scenes.");
            return;
        }

        if (_bestPlayerCandidate == null)
        {
            LogWarn($"Training scene build skipped ({reason}): no player candidate yet.");
            return;
        }

        var sourceRoot = FindRootByPath(_bestPlayerCandidate.Path, scenes);
        if (sourceRoot == null)
        {
            LogWarn($"Training scene build skipped ({reason}): lost candidate root {_bestPlayerCandidate.Path}.");
            return;
        }

        var sourceScene = sourceRoot.scene;
        if (!sourceScene.IsValid() || !sourceScene.isLoaded)
        {
            LogWarn($"Training scene build skipped ({reason}): source scene is not valid.");
            return;
        }

        var trainingActor = FindPreferredTrainingActor(sourceRoot);
        if (trainingActor == null)
        {
            LogWarn($"Training scene build skipped ({reason}): no training actor resolved under {_bestPlayerCandidate.Path}.");
            return;
        }

        var trainingActorPath = GetPath(trainingActor.transform);
        var sourceActorPath = GetPath(sourceRoot.transform);
        LogInfo($"Building training scene from source scene '{sourceScene.name}' using candidate '{_bestPlayerCandidate.Path}' (score {_bestPlayerCandidate.Score}).");
        if (!string.Equals(trainingActorPath, sourceActorPath, StringComparison.OrdinalIgnoreCase))
        {
            LogInfo($"Resolved training actor subroot '{trainingActorPath}' from source root '{sourceActorPath}'.");
        }

        var trainingScene = GetOrCreateTrainingScene();
        var movedRoots = new List<string>();
        var prunedRoots = new List<string>();
        var retainedUnknownRoots = new List<string>();

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
                continue;
            }

            var report = AnalyzeRoot(root);
            if (AutoPruneSourceScene)
            {
                UnityObject.Destroy(root);
                _destroyedRoots.Add(report.Path);
                prunedRoots.Add(report.Path);
                continue;
            }
        }

        UnitySceneManager.SetActiveScene(trainingScene);

        var summary = new
        {
            reason,
            sourceScene = sourceScene.name,
            trainingScene = trainingScene.name,
            candidate = _bestPlayerCandidate,
            movedRoots,
            prunedRoots,
            retainedUnknownRoots
        };

        WriteJson($"training_plan_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", summary);
        LogInfo($"Training scene ready. Moved={movedRoots.Count}, pruned={prunedRoots.Count}, retainedUnknown={retainedUnknownRoots.Count}.");

        var managerInitialized = _trainingEnvironmentManager.InitializeFromDiscoveredScene(trainingScene, trainingActor, sourceScene.name, _bestPlayerCandidate.Path);
        PublishObservation("training-ready");
        WriteJson($"training_status_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", _trainingEnvironmentManager.GetStatus());
        _bridgeServer?.StartIfNeeded();
        _bridgeServer?.Pump();
        if (!managerInitialized)
        {
            LogWarn("TrainingEnvironmentManager reported an initialization failure after the training scene was built.");
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

    private static Scene GetOrCreateTrainingScene()
    {
        var existing = UnitySceneManager.GetSceneByName(TrainingSceneName);
        if (existing.IsValid() && existing.isLoaded)
        {
            return existing;
        }

        return UnitySceneManager.CreateScene(TrainingSceneName);
    }

    private GameObject FindPreferredTrainingActor(GameObject sourceRoot)
    {
        if (sourceRoot == null)
        {
            return null;
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
            if (report.SceneName != null && report.SceneName.IndexOf("gym", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            foreach (var root in report.Roots)
            {
                if (root.Path != null && root.Path.IndexOf("gym", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
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
            }
        }

        return null;
    }

    private void WriteSceneBundle(string reason, List<SceneReport> reports, SceneCandidate candidate)
    {
        var bundle = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            activeScene = UnitySceneManager.GetActiveScene().name,
            lastActiveScene = _lastActiveSceneName,
            candidate,
            scenes = reports
        };

        WriteJson($"scene_bundle_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", bundle);
        LogInfo($"Scene bundle written ({reason}). Scenes={reports.Count}, candidate={(candidate?.Path ?? "none")}.");
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

    private void WriteJson(string fileName, object payload)
    {
        try
        {
            var path = Path.Combine(_dumpRoot, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(payload, _jsonOptions));
            LogInfo($"Wrote dump: {path}");
        }
        catch (Exception ex)
        {
            LogError($"Failed to write JSON dump {fileName}: {ex.Message}");
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
        }

        public string Path { get; }
        public int Score { get; }
        public RootClassification Classification { get; }
        public IReadOnlyList<string> Reasons { get; }
        public IReadOnlyList<string> ComponentTypes { get; }
        public int ChildCount { get; }
    }

    private sealed class SceneReport
    {
        public string SceneName { get; set; }
        public int BuildIndex { get; set; }
        public bool IsActive { get; set; }
        public int RootCount { get; set; }
        public List<RootReport> Roots { get; set; }
        public RootReport BestPlayerCandidate { get; set; }
    }

    private sealed class RootReport
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int Score { get; set; }
        public RootClassification Classification { get; set; }
        public List<string> Reasons { get; set; }
        public List<string> ComponentTypes { get; set; }
        public int ChildCount { get; set; }
        public bool ActiveSelf { get; set; }
        public bool ActiveInHierarchy { get; set; }
        public int Layer { get; set; }
        public string Tag { get; set; }
    }

    private enum RootClassification
    {
        Unknown = 0,
        Support = 1,
        Player = 2,
        Environment = 3
    }
}
