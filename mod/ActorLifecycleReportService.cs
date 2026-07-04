using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI_Train;

internal sealed class ActorLifecycleReportService
{
    private static readonly string[] LifecycleTokens =
    {
        "LocalPlayer", "Player", "PlayerController", "PlayerManager", "PlayerSpawner",
        "SpawnPlayer", "CreatePlayer", "InitializePlayer", "InitPlayer", "SetupPlayer",
        "RegisterPlayer", "ActivatePlayer", "EnablePlayer", "CompleteBoot", "BootLoader",
        "Gym", "Practice", "Training", "Match", "GameMode", "Round", "Session",
        "GameInstance", "CombatManager", "Movement", "PlayerMovement", "PlayerHealth",
        "StructureSpawner", "Ownership", "Avatar", "Rig", "Hand", "Controller",
        "Input", "Gesture", "Processor", "Stack", "Move", "Modifier"
    };

    private static readonly string[] UnsafeInvokeTokens =
    {
        "RPC", "Network", "Photon", "Spawn", "Create", "Damage", "Hit", "Attack",
        "Ownership", "Transfer", "Matchmaking", "Session", "Pool", "Destroy", "Unload"
    };

    private readonly Func<GameObject> _resolveActor;
    private readonly Func<string, object, string> _writeJson;
    private readonly Action<string> _logInfo;

    public ActorLifecycleReportService(
        Func<GameObject> resolveActor,
        Func<string, object, string> writeJson,
        Action<string> logInfo)
    {
        _resolveActor = resolveActor;
        _writeJson = writeJson;
        _logInfo = logInfo ?? (_ => { });
    }

    public ActorCompletenessRunResult RunLifecycleTimeline(string reason, string actorMode)
    {
        var snapshot = BuildSnapshot("operator-triggered-lifecycle-rescan", reason, actorMode);
        var report = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            actorMode,
            passiveOnly = true,
            note = "This report records the currently loaded runtime state. Startup milestones are represented by current evidence and timestamped dumps; it does not replay or mutate bootstrap.",
            expectedStartupMilestones = new[]
            {
                "mod_initialize", "initial_loader_inventory", "before_gym_load_request",
                "after_gym_load_request", "gym_confirmed_loaded", "before_loader_removal",
                "after_loader_removal", "before_arena_build", "after_arena_build",
                "bootstrap_ready", "ready_delay_1s", "ready_delay_3s", "ready_delay_5s",
                "after_first_reset", "after_first_step", "after_operator_triggered_lifecycle_rescan"
            },
            snapshots = new[] { snapshot },
            conclusion = snapshot.completeActorFound
                ? "A complete-looking candidate is present in the current runtime snapshot."
                : "The current runtime snapshot does not expose a candidate with tracking plus movement, health, ownership, and summon context."
        };
        var path = _writeJson($"lifecycle_timeline_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json", report);
        _writeJson("latest_lifecycle_timeline.json", report);
        _logInfo($"Lifecycle timeline report written. completeActorFound={snapshot.completeActorFound}.");
        return new ActorCompletenessRunResult { report = report, reportPath = path };
    }

    public ActorCompletenessRunResult RunLifecycleTriggerDiscovery(string reason, string actorMode)
    {
        var liveInstances = FindLiveComponents()
            .Where(entry => ContainsAny(entry.typeFullName, LifecycleTokens) ||
                            ContainsAny(entry.path, LifecycleTokens))
            .ToList();
        var candidates = new List<LifecycleTriggerCandidate>();
        foreach (var component in liveInstances)
        {
            foreach (var member in SafeMembers(component.type))
            {
                if (!ContainsAny($"{member.Name} {component.typeFullName}", LifecycleTokens))
                {
                    continue;
                }
                candidates.Add(BuildTriggerCandidate(component, member));
            }
        }

        candidates = candidates
            .GroupBy(candidate => $"{candidate.declaringType}:{candidate.memberKind}:{candidate.memberName}:{candidate.liveInstancePath}")
            .Select(group => group.First())
            .OrderBy(candidate => candidate.riskRank)
            .ThenByDescending(candidate => candidate.score)
            .ThenBy(candidate => candidate.declaringType, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();

        var report = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            actorMode,
            passiveOnly = true,
            scannedLiveInstanceCount = liveInstances.Count,
            candidateCount = candidates.Count,
            safestNextProbes = candidates
                .Where(candidate => candidate.safety == "passive_getter_or_status")
                .Take(25)
                .ToList(),
            riskyOrUnsafeCount = candidates.Count(candidate => candidate.safety != "passive_getter_or_status"),
            liveManagerInstances = liveInstances
                .Where(entry => ContainsAny(entry.typeFullName, new[] { "Manager", "GameInstance", "CombatManager", "Spawner" }))
                .Take(120)
                .ToList(),
            conclusion = candidates.Any(candidate => candidate.safety == "passive_getter_or_status")
                ? "Passive getter/status candidates exist. Mutating lifecycle methods remain blocked until a specific owner/init context is confirmed."
                : "No safe no-argument lifecycle trigger candidate was found; mutating lifecycle, spawn, ownership, and network paths remain unsafe.",
            candidates
        };
        var path = _writeJson($"lifecycle_trigger_discovery_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json", report);
        _writeJson("latest_lifecycle_trigger_discovery.json", report);
        _logInfo($"Lifecycle trigger discovery written. candidates={candidates.Count}.");
        return new ActorCompletenessRunResult { report = report, reportPath = path };
    }

    public ActorCompletenessRunResult RunActorCandidateRanking(string reason, string actorMode)
    {
        var candidates = BuildActorCandidates();
        var bestComplete = candidates.FirstOrDefault(candidate => candidate.classification == "complete_candidate");
        var current = SafePath(_resolveActor?.Invoke());
        var report = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            actorMode,
            selectedActorPath = current,
            bestCompleteActorPath = bestComplete?.rootPath,
            completeActorFound = bestComplete != null,
            fallbackActorPath = current,
            rankingRule = "movement, health, ownership, summon, physics, renderers, and complete lifecycle evidence outrank head/hands-only tracking rigs",
            candidates
        };
        var path = _writeJson($"actor_candidate_ranking_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json", report);
        _writeJson("latest_actor_candidate_ranking.json", report);
        _logInfo($"Actor candidate ranking written. candidates={candidates.Count} completeActorFound={bestComplete != null}.");
        return new ActorCompletenessRunResult { report = report, reportPath = path };
    }

    public ActorCompletenessRunResult RunLifecycleModeComparison(string reason, string actorMode)
    {
        var candidates = BuildActorCandidates();
        var current = candidates.FirstOrDefault(candidate => candidate.isSelectedActor) ?? candidates.FirstOrDefault();
        var complete = candidates.FirstOrDefault(candidate => candidate.classification == "complete_candidate");
        var modeSummaries = new[]
        {
            BuildModeSummary("CurrentNormalMode", "observed_current_runtime", candidates, current, complete, "Existing staged bootstrap, Loader cleanup, arena build, and selected fallback actor."),
            BuildModeSummary("NoPruneMode", "not_invoked_in_running_session", candidates, current, complete, "Requires startup config before pruning decisions; current evidence can only show whether useful systems exist now."),
            BuildModeSummary("NoMoveMode", "not_invoked_in_running_session", candidates, current, complete, "Requires startup config before actor move; current evidence can only compare post-move inventory."),
            BuildModeSummary("LoaderHeldMode", "not_invoked_in_running_session", candidates, current, complete, "Requires bounded startup delay while Loader remains loaded; not invoked from this passive request."),
            BuildModeSummary("GymOnlyObservationMode", "not_invoked_in_running_session", candidates, current, complete, "Requires a fresh Gym-only observation startup; not invoked from a live ready bridge.")
        };
        var report = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            actorMode,
            passiveOnly = true,
            selectedActorPath = SafePath(_resolveActor?.Invoke()),
            completeActorFound = complete != null,
            conclusion = complete != null
                ? "A complete actor candidate is already visible in the current runtime; mode-specific restart validation is the next confirmation step."
                : "The observed current runtime has no complete candidate. Alternative no-prune/no-move/loader-held modes still require fresh startup runs to prove causal effects.",
            modes = modeSummaries
        };
        var path = _writeJson($"lifecycle_mode_comparison_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json", report);
        _writeJson("latest_lifecycle_mode_comparison.json", report);
        _logInfo($"Lifecycle mode comparison written. completeActorFound={complete != null}.");
        return new ActorCompletenessRunResult { report = report, reportPath = path };
    }

    public ActorCompletenessRunResult RunLifecycleTriggerProbe(string reason, string actorMode)
    {
        var before = BuildSnapshot("before-trigger-probe", reason, actorMode);
        var safeCandidates = FindLiveComponents()
            .SelectMany(component => SafeMembers(component.type).Select(member => BuildTriggerCandidate(component, member)))
            .Where(candidate => candidate.safety == "passive_getter_or_status")
            .OrderByDescending(candidate => candidate.score)
            .Take(25)
            .ToList();
        var after = BuildSnapshot("after-trigger-probe", reason, actorMode);
        var report = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            actorMode,
            status = "blocked",
            passiveOnly = true,
            invokedCount = 0,
            skippedCount = safeCandidates.Count,
            blockedReason = "No lifecycle trigger was invoked because no candidate was proven to be a no-side-effect local-player activation path with confirmed owner/init context.",
            safety = "No reflected method, property getter, spawn, ownership, RPC, network, damage, pool, or gameplay method was invoked.",
            before,
            skippedCandidates = safeCandidates,
            after,
            completeActorAppeared = after.completeActorFound && !before.completeActorFound
        };
        var path = _writeJson($"lifecycle_trigger_probe_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json", report);
        _writeJson("latest_lifecycle_trigger_probe.json", report);
        _logInfo("Lifecycle trigger probe report written. invokedCount=0.");
        return new ActorCompletenessRunResult { report = report, reportPath = path };
    }

    public ActorCompletenessRunResult RunMissingLifecycleDependencyReport(string reason, string actorMode)
    {
        var candidates = BuildActorCandidates();
        var current = candidates.FirstOrDefault(candidate => candidate.isSelectedActor) ?? candidates.FirstOrDefault();
        var complete = candidates.FirstOrDefault(candidate => candidate.classification == "complete_candidate");
        var report = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            actorMode,
            completeActorFound = complete != null,
            selectedActorPath = SafePath(_resolveActor?.Invoke()),
            selectedActorClassification = current?.classification ?? "unknown",
            answers = new Dictionary<string, string>
            {
                ["Is Gym alone enough"] = complete != null ? "confirmed: complete candidate currently visible" : "unconfirmed: current ready runtime does not expose a complete actor",
                ["Does keeping Loader longer help"] = "unconfirmed: requires LoaderHeldMode fresh startup; not proven from current ready runtime",
                ["Does avoiding pruning help"] = "unconfirmed: current candidate inventory does not show stripped useful actor systems; no-prune fresh startup remains the causal test",
                ["Does avoiding actor movement help"] = "unconfirmed: no-move fresh startup remains the causal test",
                ["Does waiting longer help"] = "failed in current snapshot: no complete candidate is visible at operator-triggered rescan",
                ["Does reset or step create systems"] = "unconfirmed from passive report; live validation step can update hand-motion evidence but has not exposed movement/health/ownership",
                ["Requires VR hardware or input"] = "likely: selected actor is a tracking rig and full lifecycle may require OpenXR/SteamVR-local player init",
                ["Requires practice or match state"] = "likely: manager and movement systems may be bound by practice/match/session startup rather than direct Gym scene load",
                ["Requires profile/session/ownership"] = "likely: summon and ownership context remains missing under the selected actor",
                ["Requires network session"] = "unconfirmed and unsafe to force: network/session paths are not probed automatically",
                ["Requires bootloader transition"] = "likely: direct additive Gym load may bypass part of the local-player lifecycle"
            },
            mostLikelyMissingDependency = complete != null
                ? "none_current_candidate_requires_verification"
                : "practice_or_bootloader_local_player_initialization_context",
            nextExactGoal = complete != null
                ? "Switch actor selection to the complete candidate in a gated mode and re-run observation/reset/step."
                : "Run a fresh startup comparison that holds Loader and observes Gym-only/no-move/no-prune modes, then inspect whether practice or bootloader transition methods initialize the full local player.",
            candidateRanking = candidates.Take(30).ToList()
        };
        var path = _writeJson($"missing_lifecycle_dependency_report_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json", report);
        _writeJson("latest_missing_lifecycle_dependency_report.json", report);
        _logInfo($"Missing lifecycle dependency report written. completeActorFound={complete != null}.");
        return new ActorCompletenessRunResult { report = report, reportPath = path };
    }

    private LifecycleSnapshot BuildSnapshot(string label, string reason, string actorMode)
    {
        var candidates = BuildActorCandidates();
        var selected = SafePath(_resolveActor?.Invoke());
        return new LifecycleSnapshot
        {
            label = label,
            timestampUtc = DateTime.UtcNow,
            frameCount = Time.frameCount,
            timeSeconds = Time.unscaledTime,
            reason = reason,
            actorMode = actorMode,
            activeScene = SceneManager.GetActiveScene().name,
            loadedScenes = LoadedSceneNames(),
            dontDestroyOnLoadRoots = candidates
                .Where(candidate => string.Equals(candidate.scene, "DontDestroyOnLoad", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.rootPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            selectedActorPath = selected,
            selectedActorClassification = candidates.FirstOrDefault(candidate => candidate.rootPath == selected)?.classification,
            completeActorFound = candidates.Any(candidate => candidate.classification == "complete_candidate"),
            candidateLocalPlayerRoots = candidates.Where(candidate => candidate.localPlayerSignals.Count > 0).Take(80).ToList(),
            candidateActorRoots = candidates.Take(80).ToList(),
            candidateManagerSystems = FindLiveComponents()
                .Where(entry => ContainsAny(entry.typeFullName, new[] { "Manager", "GameInstance", "CombatManager", "Session", "Round" }))
                .Take(120)
                .ToList(),
            majorSceneRoots = MajorSceneRoots()
        };
    }

    private List<ActorCandidateRankingEntry> BuildActorCandidates()
    {
        var selected = _resolveActor?.Invoke();
        var selectedPath = SafePath(selected);
        var roots = FindRuntimeRoots();
        if (selected != null)
        {
            roots.Add(selected.transform.root.gameObject);
        }

        return roots
            .Where(root => root != null)
            .GroupBy(root => root.GetInstanceID())
            .Select(group => BuildActorCandidate(group.First(), selectedPath))
            .Where(candidate => candidate.score > 0)
            .OrderByDescending(candidate => candidate.completenessScore)
            .ThenByDescending(candidate => candidate.score)
            .ThenBy(candidate => candidate.rootPath, StringComparer.OrdinalIgnoreCase)
            .Take(120)
            .ToList();
    }

    private static ActorCandidateRankingEntry BuildActorCandidate(GameObject root, string selectedPath)
    {
        var components = SafeComponents(root);
        var componentNames = components
            .Select(component => component.GetType().FullName ?? component.GetType().Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var transforms = SafeTransforms(root);
        var path = SafePath(root);
        var renderers = components.OfType<Renderer>().ToList();
        var head = FindTransform(transforms, "head", "headset");
        var left = FindTransform(transforms, "left", "hand");
        var right = FindTransform(transforms, "right", "hand");
        var movement = Match(componentNames, new[] { "PlayerMovement", "Movement", "Locomotion" });
        var health = Match(componentNames, new[] { "PlayerHealth", "Health" });
        var ownership = Match(componentNames, new[] { "Ownership", "Owner", "PlayerId", "ActorId", "Photon" });
        var summon = Match(componentNames, new[] { "StructureSpawner", "Summon", "Spawner", "MoveSystem", "StackProcessor" });
        var physics = components.Count(component => component is Rigidbody or CharacterController or Collider);
        var score = 0;
        if (head != null) score += 80;
        if (left != null && right != null && left != right) score += 120;
        if (renderers.Count > 0) score += 80;
        if (renderers.Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy)) score += 60;
        if (physics > 0) score += 90;
        score += movement.Count * 160;
        score += health.Count * 140;
        score += ownership.Count * 130;
        score += summon.Count * 120;
        if (ContainsAny(path, new[] { "BootLoaderPlayer" })) score -= 120;
        if (string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase)) score += 30;
        var missing = new List<string>();
        if (head == null) missing.Add("head");
        if (left == null || right == null || left == right) missing.Add("distinctHands");
        if (renderers.Count == 0) missing.Add("renderers");
        if (movement.Count == 0) missing.Add("movement");
        if (health.Count == 0) missing.Add("health");
        if (ownership.Count == 0) missing.Add("ownership");
        if (summon.Count == 0) missing.Add("summonContext");
        if (physics == 0) missing.Add("physicsOrGrounding");
        var complete = head != null &&
                       left != null &&
                       right != null &&
                       left != right &&
                       movement.Count > 0 &&
                       health.Count > 0 &&
                       ownership.Count > 0 &&
                       summon.Count > 0 &&
                       physics > 0;
        return new ActorCandidateRankingEntry
        {
            rootPath = path,
            scene = SafeScene(root),
            activeInHierarchy = root.activeInHierarchy,
            isSelectedActor = string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase),
            score = score,
            completenessScore = Math.Max(0, score),
            classification = complete
                ? "complete_candidate"
                : head != null && left != null && right != null && left != right
                    ? "partial_tracking_rig"
                    : renderers.Count > 0 ? "visual_or_template_candidate" : "support_or_manager_candidate",
            rendererCount = renderers.Count,
            enabledRendererCount = renderers.Count(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy),
            physicsComponentCount = physics,
            headPath = SafePath(head),
            leftHandPath = SafePath(left),
            rightHandPath = SafePath(right),
            movementComponents = movement,
            healthComponents = health,
            ownershipComponents = ownership,
            summonComponents = summon,
            localPlayerSignals = Match(componentNames.Concat(new[] { path }), new[] { "LocalPlayer", "PlayerController", "PlayerRig", "Avatar", "Rig" }),
            missingSystems = missing,
            componentEvidence = componentNames.Take(80).ToList()
        };
    }

    private static object BuildModeSummary(
        string mode,
        string status,
        List<ActorCandidateRankingEntry> candidates,
        ActorCandidateRankingEntry current,
        ActorCandidateRankingEntry complete,
        string note)
    {
        return new
        {
            mode,
            status,
            note,
            candidateCount = candidates.Count,
            selectedActorPath = current?.rootPath,
            selectedActorClassification = current?.classification,
            bestCompleteActorPath = complete?.rootPath,
            completeActorFound = complete != null,
            bootLoaderPlayerRemainsPartial = candidates.Any(candidate =>
                candidate.rootPath != null &&
                candidate.rootPath.Contains("BootLoaderPlayer", StringComparison.OrdinalIgnoreCase) &&
                candidate.classification == "partial_tracking_rig")
        };
    }

    private static LifecycleTriggerCandidate BuildTriggerCandidate(LiveComponentEntry component, MemberInfo member)
    {
        var method = member as MethodInfo;
        var parameters = method != null ? SafeParameters(method) : new List<string>();
        var text = $"{component.typeFullName} {member.Name} {string.Join(" ", parameters)}";
        var unsafeToken = ContainsAny(text, UnsafeInvokeTokens);
        var isGetter = member.MemberType is MemberTypes.Field or MemberTypes.Property ||
                       member.Name.StartsWith("get_", StringComparison.OrdinalIgnoreCase) ||
                       member.Name.IndexOf("Status", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       member.Name.IndexOf("Is", StringComparison.OrdinalIgnoreCase) >= 0;
        var noArgs = method == null || method.GetParameters().Length == 0;
        var safety = unsafeToken
            ? "unsafe"
            : isGetter && noArgs
                ? "passive_getter_or_status"
                : noArgs ? "bounded_review_required" : "blocked_unknown_parameters";
        return new LifecycleTriggerCandidate
        {
            declaringType = member.DeclaringType?.FullName ?? component.typeFullName,
            fullTypeName = component.typeFullName,
            assembly = component.type.Assembly.GetName().Name,
            liveInstancePath = component.path,
            scene = component.scene,
            memberKind = member.MemberType.ToString().ToLowerInvariant(),
            memberName = member.Name,
            parameterTypes = parameters,
            returnType = method?.ReturnType.FullName ??
                         (member as PropertyInfo)?.PropertyType.FullName ??
                         (member as FieldInfo)?.FieldType.FullName,
            isStatic = IsStatic(member),
            riskLevel = safety == "unsafe" ? "unsafe" : safety == "passive_getter_or_status" ? "low" : "medium",
            safety = safety,
            riskRank = safety == "passive_getter_or_status" ? 0 : safety == "bounded_review_required" ? 5 : 10,
            reasonItMayMatter = ContainsAny(text, new[] { "Player", "Local", "Boot", "Gym", "Practice" })
                ? "name_matches_local_player_lifecycle"
                : "name_matches_runtime_lifecycle_token",
            shouldProbeLater = safety != "unsafe",
            wasPassiveOnly = true,
            score = LifecycleTokens.Count(token => text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
        };
    }

    private static List<GameObject> FindRuntimeRoots()
    {
        try
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(IsRuntimeObject)
                .Select(gameObject => gameObject.transform?.root?.gameObject ?? gameObject)
                .Where(root => root != null)
                .GroupBy(root => root.GetInstanceID())
                .Select(group => group.First())
                .ToList();
        }
        catch
        {
            return new List<GameObject>();
        }
    }

    private static List<LiveComponentEntry> FindLiveComponents()
    {
        try
        {
            return Resources.FindObjectsOfTypeAll<Component>()
                .Where(component => component != null && IsRuntimeObject(component.gameObject))
                .Select(component => new LiveComponentEntry
                {
                    path = SafePath(component.gameObject),
                    scene = SafeScene(component.gameObject),
                    type = component.GetType(),
                    typeFullName = component.GetType().FullName ?? component.GetType().Name,
                    activeInHierarchy = component.gameObject.activeInHierarchy
                })
                .ToList();
        }
        catch
        {
            return new List<LiveComponentEntry>();
        }
    }

    private static List<string> LoadedSceneNames()
    {
        var names = new List<string>();
        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (scene.IsValid() && scene.isLoaded)
            {
                names.Add(scene.name);
            }
        }
        return names;
    }

    private static List<object> MajorSceneRoots()
    {
        var roots = new List<object>();
        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                continue;
            }
            roots.AddRange(scene.GetRootGameObjects()
                .Where(root => root != null)
                .Select(root => new
                {
                    scene = scene.name,
                    path = SafePath(root),
                    root.activeSelf,
                    root.activeInHierarchy
                }));
        }
        return roots;
    }

    private static IEnumerable<MemberInfo> SafeMembers(Type type)
    {
        if (type == null)
        {
            return Array.Empty<MemberInfo>();
        }
        try
        {
            return type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property or MemberTypes.Method &&
                                 member.DeclaringType == type)
                .Take(220)
                .ToList();
        }
        catch
        {
            return Array.Empty<MemberInfo>();
        }
    }

    private static List<Component> SafeComponents(GameObject root)
    {
        try
        {
            return root.GetComponentsInChildren<Component>(true).Where(component => component != null).ToList();
        }
        catch
        {
            return new List<Component>();
        }
    }

    private static List<Transform> SafeTransforms(GameObject root)
    {
        try
        {
            return root.GetComponentsInChildren<Transform>(true).Where(transform => transform != null).ToList();
        }
        catch
        {
            return new List<Transform>();
        }
    }

    private static Transform FindTransform(IEnumerable<Transform> transforms, params string[] requiredTokens)
    {
        return transforms
            .Where(transform => requiredTokens.All(token => SafePath(transform).Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(transform => SafePath(transform).Length)
            .FirstOrDefault();
    }

    private static List<string> Match(IEnumerable<string> values, IEnumerable<string> hints)
    {
        return values
            .Where(value => ContainsAny(value, hints))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }

    private static bool ContainsAny(string value, IEnumerable<string> tokens)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               tokens.Any(token => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsRuntimeObject(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return false;
        }
        try
        {
            var scene = gameObject.scene;
            return (scene.IsValid() && scene.isLoaded) ||
                   string.Equals(scene.name, "DontDestroyOnLoad", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsStatic(MemberInfo member)
    {
        return member switch
        {
            MethodBase method => method.IsStatic,
            PropertyInfo property => (property.GetGetMethod(true) ?? property.GetSetMethod(true))?.IsStatic ?? false,
            FieldInfo field => field.IsStatic,
            _ => false
        };
    }

    private static List<string> SafeParameters(MethodBase method)
    {
        try
        {
            return method.GetParameters()
                .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
                .ToList();
        }
        catch
        {
            return new List<string> { "unavailable" };
        }
    }

    private static string SafePath(GameObject gameObject) => SafePath(gameObject?.transform);

    private static string SafePath(Transform transform)
    {
        if (transform == null) return null;
        var parts = new Stack<string>();
        while (transform != null)
        {
            parts.Push(transform.name);
            transform = transform.parent;
        }
        return string.Join("/", parts);
    }

    private static string SafeScene(GameObject gameObject)
    {
        try
        {
            return gameObject != null ? gameObject.scene.name : null;
        }
        catch
        {
            return "unknown";
        }
    }
}

internal sealed class LifecycleSnapshot
{
    public string label { get; set; }
    public DateTime timestampUtc { get; set; }
    public int frameCount { get; set; }
    public float timeSeconds { get; set; }
    public string reason { get; set; }
    public string actorMode { get; set; }
    public string activeScene { get; set; }
    public List<string> loadedScenes { get; set; } = new();
    public List<string> dontDestroyOnLoadRoots { get; set; } = new();
    public string selectedActorPath { get; set; }
    public string selectedActorClassification { get; set; }
    public bool completeActorFound { get; set; }
    public List<ActorCandidateRankingEntry> candidateLocalPlayerRoots { get; set; } = new();
    public List<ActorCandidateRankingEntry> candidateActorRoots { get; set; } = new();
    public List<LiveComponentEntry> candidateManagerSystems { get; set; } = new();
    public List<object> majorSceneRoots { get; set; } = new();
}

internal sealed class ActorCandidateRankingEntry
{
    public string rootPath { get; set; }
    public string scene { get; set; }
    public bool activeInHierarchy { get; set; }
    public bool isSelectedActor { get; set; }
    public int score { get; set; }
    public int completenessScore { get; set; }
    public string classification { get; set; }
    public int rendererCount { get; set; }
    public int enabledRendererCount { get; set; }
    public int physicsComponentCount { get; set; }
    public string headPath { get; set; }
    public string leftHandPath { get; set; }
    public string rightHandPath { get; set; }
    public List<string> movementComponents { get; set; } = new();
    public List<string> healthComponents { get; set; } = new();
    public List<string> ownershipComponents { get; set; } = new();
    public List<string> summonComponents { get; set; } = new();
    public List<string> localPlayerSignals { get; set; } = new();
    public List<string> missingSystems { get; set; } = new();
    public List<string> componentEvidence { get; set; } = new();
}

internal sealed class LiveComponentEntry
{
    public string path { get; set; }
    public string scene { get; set; }
    public Type type;
    public string typeFullName { get; set; }
    public bool activeInHierarchy { get; set; }
}

internal sealed class LifecycleTriggerCandidate
{
    public string declaringType { get; set; }
    public string fullTypeName { get; set; }
    public string assembly { get; set; }
    public string liveInstancePath { get; set; }
    public string scene { get; set; }
    public string memberKind { get; set; }
    public string memberName { get; set; }
    public List<string> parameterTypes { get; set; } = new();
    public string returnType { get; set; }
    public bool isStatic { get; set; }
    public string riskLevel { get; set; }
    public string safety { get; set; }
    public int riskRank { get; set; }
    public string reasonItMayMatter { get; set; }
    public bool shouldProbeLater { get; set; }
    public bool wasPassiveOnly { get; set; }
    public int score { get; set; }
}
