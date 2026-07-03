using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI_Train;

internal sealed class ActorCompletenessService
{
    private static readonly string[] MovementHints = { "PlayerMovement", "Movement", "Locomotion", "Reposition", "Teleport" };
    private static readonly string[] PlayerControllerHints = { "PlayerController", "LocalPlayer", "PlayerRig" };
    private static readonly string[] InputHints = { "Input", "Controller", "XR", "Tracking" };
    private static readonly string[] GestureHints = { "Gesture", "Pose", "Recognizer", "Rune" };
    private static readonly string[] SummonHints = { "Summon", "StructureSpawner", "SpawnStructure", "Spawner" };
    private static readonly string[] StructurePoolHints = { "Structure", "Slab", "Disc", "Ball", "Cube", "Ground", "Prefab", "Pool" };
    private static readonly string[] MoveSystemHints = { "MoveSystem", "PlayerStackProcessor", "StackProcessor", "MoveProcessor" };
    private static readonly string[] ModifierHints = { "Modifier", "Ability", "MoveSystem" };
    private static readonly string[] OwnershipHints = { "Ownership", "Owner", "Authority", "PlayerId", "ActorId", "PhotonView" };
    private static readonly string[] HealthHints = { "PlayerHealth", "Health", "HitPoints" };
    private static readonly string[] DamageHints = { "Damage", "Hitbox", "Hurt", "CollisionHandler", "Impact" };
    private static readonly string[] NetworkHints = { "Networking", "Network", "Photon", "PlayerId", "ActorId" };

    private readonly Func<GameObject> _resolveActor;
    private readonly Func<string, object, string> _writeJson;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;

    public ActorCompletenessService(
        Func<GameObject> resolveActor,
        Func<string, object, string> writeJson,
        Action<string> logInfo,
        Action<string> logWarn)
    {
        _resolveActor = resolveActor;
        _writeJson = writeJson;
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
    }

    public ActorCompletenessRunResult RunActorCompleteness(string reason, string actorMode)
    {
        var actor = _resolveActor?.Invoke();
        var report = InspectActor(actor, reason, actorMode);
        var timestamped = _writeJson(
            $"actor_completeness_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json",
            report);
        _writeJson("latest_actor_completeness.json", report);
        _logInfo(
            $"Actor completeness written. classification={report.classification} " +
            $"score={report.completenessScore} actor={report.actorRootPath ?? "none"}.");
        return new ActorCompletenessRunResult
        {
            report = report,
            reportPath = timestamped
        };
    }

    public ActorCompletenessRunResult RunLifecycleDiscovery(string reason, string actorMode)
    {
        var currentActor = _resolveActor?.Invoke();
        var runtimeTypes = GetRelevantRuntimeTypes();
        var typedInstances = FindTypedRuntimeInstances(runtimeTypes);
        var roots = typedInstances
            .Select(instance => instance.gameObject?.transform?.root?.gameObject)
            .Where(root => root != null)
            .Concat(currentActor != null ? new[] { currentActor.transform.root.gameObject } : Array.Empty<GameObject>())
            .GroupBy(root => root.GetInstanceID())
            .Select(group => group.First())
            .ToList();
        var candidates = new List<LocalPlayerLifecycleCandidate>();
        foreach (var root in roots)
        {
            var componentNames = typedInstances
                .Where(instance => instance.gameObject != null &&
                                   IsDescendantOrSelf(instance.gameObject.transform, root.transform))
                .Select(instance => instance.type.FullName ?? instance.type.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var text = $"{SafePath(root)} {string.Join(" ", componentNames)}";
            var score = ScoreLifecycleText(text);
            var transforms = SafeTransforms(root);
            var head = FindTransform(transforms, "head", "headset");
            var left = FindTransform(transforms, "left", "hand");
            var right = FindTransform(transforms, "right", "hand");
            if (head != null) score += 80;
            if (left != null) score += 80;
            if (right != null) score += 80;
            if (root == currentActor) score += 1000;
            if (score < 80)
            {
                continue;
            }

            var rendererCount = SafeComponents(root).Count(component => component is Renderer);
            candidates.Add(new LocalPlayerLifecycleCandidate
            {
                rootPath = SafePath(root),
                scene = SafeScene(root),
                activeInHierarchy = root.activeInHierarchy,
                isCurrentActor = root == currentActor,
                score = score,
                componentEvidence = componentNames.Take(160).ToList(),
                rendererCount = rendererCount,
                headPath = SafePath(head),
                leftHandPath = SafePath(left),
                rightHandPath = SafePath(right),
                movement = Match(componentNames, MovementHints),
                health = Match(componentNames, HealthHints),
                summon = Match(componentNames, SummonHints),
                ownership = Match(componentNames, OwnershipHints),
                gesture = Match(componentNames, GestureHints),
                whyItMayBeReal = BuildLifecycleReasons(componentNames, head, left, right),
                whyItMayNotBe = BuildLifecycleMissing(componentNames, head, left, right, rendererCount)
            });
        }

        candidates = candidates
            .OrderByDescending(candidate => candidate.score)
            .ThenBy(candidate => candidate.rootPath, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();
        var best = candidates.FirstOrDefault();
        var report = new LocalPlayerLifecycleDiscoveryReport
        {
            timestampUtc = DateTime.UtcNow,
            reason = reason,
            actorMode = actorMode,
            currentActorPath = SafePath(currentActor),
            scannedRootCount = roots.Count,
            candidateCount = candidates.Count,
            bestCandidatePath = best?.rootPath,
            bestCandidateScene = best?.scene,
            completeCandidateFound = candidates.Any(IsPotentiallyCompleteCandidate),
            conclusion = candidates.Any(IsPotentiallyCompleteCandidate)
                ? "A candidate with tracking, movement, and multiple gameplay-system signals is loaded; it requires actor completeness verification."
                : "No loaded or persistent root combines tracking evidence with movement and multiple gameplay-system signals.",
            candidates = candidates
        };
        var timestamped = _writeJson(
            $"local_player_lifecycle_discovery_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json",
            report);
        _writeJson("latest_local_player_lifecycle_discovery.json", report);
        _logInfo(
            $"Local-player lifecycle discovery written. roots={roots.Count} candidates={candidates.Count} " +
            $"best={best?.rootPath ?? "none"} completeCandidate={report.completeCandidateFound}.");
        return new ActorCompletenessRunResult { report = report, reportPath = timestamped };
    }

    public ActorCompletenessRunResult RunSummonContextDiscovery(string reason, string actorMode)
    {
        var actor = _resolveActor?.Invoke();
        var entries = new List<SummonContextCandidate>();
        var relevantTypes = GetRelevantRuntimeTypes()
            .Where(type =>
            {
                var name = type.FullName ?? type.Name;
                return ContainsAny(name, SummonHints) ||
                       ContainsAny(name, StructurePoolHints) ||
                       ContainsAny(name, MoveSystemHints) ||
                       ContainsAny(name, GestureHints) ||
                       ContainsAny(name, OwnershipHints);
            })
            .ToList();
        var typedInstances = FindTypedRuntimeInstances(relevantTypes);
        foreach (var instance in typedInstances)
        {
            var component = instance.component;
            var type = instance.type;
            var typeName = type.FullName ?? type.Name;
            var combined = $"{typeName} {SafePath(component.gameObject)}";
            var category = ClassifySummonContext(typeName);
            var members = SafeMemberNames(type);
            entries.Add(new SummonContextCandidate
            {
                componentPath = SafePath(component.gameObject),
                scene = SafeScene(component.gameObject),
                typeFullName = typeName,
                category = category,
                actorBound = actor != null && IsDescendantOrSelf(component.transform, actor.transform),
                activeInHierarchy = component.gameObject.activeInHierarchy,
                memberEvidence = members,
                safety = category == "unsafe_network_path" ? "unsafe" : "passive_only",
                configuredState = SummonConfiguredState(typeName, SafePath(component.gameObject), component.gameObject.activeInHierarchy),
                nextProbe = NextSummonProbe(category)
            });
        }

        entries = entries
            .Where(IsRelevantSummonCandidate)
            .GroupBy(entry => $"{entry.typeFullName}:{entry.componentPath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(SummonCandidatePriority)
            .ThenByDescending(entry => entry.actorBound)
            .ThenBy(entry => entry.category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.typeFullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.componentPath, StringComparer.OrdinalIgnoreCase)
            .Take(400)
            .ToList();
        var structurePoolTemplateCount = entries.Count(entry =>
            string.Equals(entry.configuredState, "pooled_structure_template_present", StringComparison.OrdinalIgnoreCase));
        var actorBoundSummonContextFound = entries.Any(entry =>
            entry.actorBound &&
            string.Equals(entry.category, "direct_actor_component", StringComparison.OrdinalIgnoreCase) &&
            entry.activeInHierarchy);
        var safeCandidate = entries.FirstOrDefault(entry =>
            entry.activeInHierarchy &&
            entry.typeFullName.Contains("PoolManager", StringComparison.OrdinalIgnoreCase) &&
            structurePoolTemplateCount > 0);
        var report = new SummonContextDiscoveryReport
        {
            timestampUtc = DateTime.UtcNow,
            reason = reason,
            actorMode = actorMode,
            actorRootPath = SafePath(actor),
            scannedComponentCount = typedInstances.Count,
            scannedTypeCount = relevantTypes.Count,
            candidateCount = entries.Count,
            actorBoundCandidateCount = entries.Count(entry => entry.actorBound),
            actorBoundSummonContextFound = actorBoundSummonContextFound,
            pooledStructureTemplateCount = structurePoolTemplateCount,
            directActorComponents = entries.Where(entry => entry.category == "direct_actor_component").ToList(),
            globalManagers = entries.Where(entry => entry.category == "global_manager").ToList(),
            objectPools = entries.Where(entry => entry.category == "object_pool").ToList(),
            structurePrefabTemplates = entries.Where(entry =>
                string.Equals(entry.configuredState, "pooled_structure_template_present", StringComparison.OrdinalIgnoreCase)).ToList(),
            ownershipSystems = entries.Where(entry =>
                ContainsAny(entry.typeFullName, OwnershipHints) || entry.memberEvidence.Any(member => ContainsAny(member, OwnershipHints))).ToList(),
            unconfiguredSpawners = entries.Where(entry =>
                entry.typeFullName.Contains("StructureSpawner", StringComparison.OrdinalIgnoreCase)).ToList(),
            gestureDriven = entries.Where(entry => entry.category == "gesture_driven").ToList(),
            stackDriven = entries.Where(entry => entry.category == "stack_driven").ToList(),
            unsafeNetworkPaths = entries.Where(entry => entry.category == "unsafe_network_path").ToList(),
            rankedSafestCandidate = safeCandidate,
            realSummonProbeSafeNow = false,
            blockedReason = safeCandidate != null
                ? "global_pool_template_without_confirmed_local_ownership_or_checkout_arguments"
                : "no_loaded_actor_bound_configured_summon_context",
            missingLiveConfiguredSpawner = !entries.Any(entry =>
                entry.typeFullName.Contains("StructureSpawner", StringComparison.OrdinalIgnoreCase) &&
                entry.activeInHierarchy &&
                !string.Equals(entry.configuredState, "spawner_instance_loaded_configuration_unread", StringComparison.OrdinalIgnoreCase)),
            priorProbeExplanation = entries.Any(entry => entry.typeFullName.Contains("StructureSpawner", StringComparison.OrdinalIgnoreCase))
                ? "StructureSpawner types or instances exist outside the previously selected actor or still require configured-state verification."
                : "No loaded StructureSpawner instance was found; the previous actor-bound probe failed because the component was absent, not merely unconfigured.",
            conclusion = safeCandidate != null
                ? "The global PoolManager and structure templates are loaded. A one-object pool checkout is the next technical path, but local ownership/initialization requirements remain unconfirmed, so it is not yet safe to invoke."
                : "No currently loaded passive candidate justifies invoking a real summon method.",
            candidates = entries
        };
        var blockedProbe = new
        {
            timestampUtc = DateTime.UtcNow,
            reason,
            status = "blocked",
            blockedReason = safeCandidate != null
                ? "global_pool_template_without_confirmed_local_ownership_or_checkout_arguments"
                : "no_loaded_actor_bound_configured_summon_context",
            requiredMissingContext = safeCandidate != null
                ? new[] { "local_player_owner_identity", "safe_pool_checkout_arguments", "cleanup_return_path", "non_network_initialization_path" }
                : new[] { "actor_bound_summon_component", "configured_structure_spawner", "local_player_owner_identity" },
            bestCandidateIfAny = safeCandidate,
            whyInvocationWasNotAttempted = safeCandidate != null
                ? "The candidate is global pool/template evidence, not actor-bound summon context; invoking it could create an orphaned or wrongly-owned object."
                : "No loaded candidate had the required actor-bound configured spawner, prefab, owner, and initialization context.",
            actorPath = SafePath(actor),
            actorCompletenessClassification = InspectActor(actor, $"{reason}-real-summon-blocked-context", actorMode).classification,
            generatedObjectCount = 0,
            realSummonConfirmed = false,
            objectCreated = false,
            methodInvoked = false,
            blockingPoint = safeCandidate != null
                ? "PoolManager and structure templates are loaded, but required local ownership/player initialization and exact checkout arguments are not confirmed."
                : "No loaded summon candidate justified an invocation.",
            placeholderCreated = false,
            placeholderIsRealRumbleSummon = false,
            safety = "No pool, gameplay, ownership, RPC, or network method was invoked."
        };
        report.realSummonProbeReportPath = _writeJson(
            $"real_summon_probe_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json",
            blockedProbe);
        _writeJson("latest_real_summon_probe.json", blockedProbe);
        var timestamped = _writeJson(
            $"summon_context_discovery_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json",
            report);
        _writeJson("latest_summon_context_discovery.json", report);
        _logInfo(
            $"Summon context discovery written. types={relevantTypes.Count} instances={typedInstances.Count} candidates={entries.Count} " +
            $"actorBound={report.actorBoundCandidateCount}.");
        return new ActorCompletenessRunResult { report = report, reportPath = timestamped };
    }

    private static ActorCompletenessReport InspectActor(GameObject actor, string reason, string actorMode)
    {
        var report = new ActorCompletenessReport
        {
            timestampUtc = DateTime.UtcNow,
            reason = reason,
            actorMode = actorMode,
            actorRootPath = SafePath(actor),
            actorScene = SafeScene(actor),
            confirmed = new List<string>(),
            likely = new List<string>(),
            unconfirmed = new List<string>(),
            failed = new List<string>(),
            unsafeEvidence = new List<string>(),
            missingCriticalSystems = new List<string>()
        };
        if (actor == null)
        {
            report.classification = "unknown";
            report.failed.Add("actor_root_missing");
            report.missingCriticalSystems.Add("actorRoot");
            return report;
        }

        var components = SafeComponents(actor);
        var transforms = SafeTransforms(actor);
        var head = FindTransform(transforms, "head", "headset");
        var left = FindTransform(transforms, "left", "hand");
        var right = FindTransform(transforms, "right", "hand");
        report.headPath = SafePath(head);
        report.leftHandPath = SafePath(left);
        report.rightHandPath = SafePath(right);
        report.bodyRootPath = SafePath(actor);
        report.hasHead = head != null;
        report.hasLeftHand = left != null;
        report.hasRightHand = right != null;
        report.hasHands = left != null && right != null && left != right;
        report.hasBody = false;

        var renderers = components.OfType<Renderer>().ToList();
        report.renderers = renderers.Select(InspectRenderer).ToList();
        report.rendererCount = renderers.Count;
        report.skinnedMeshRendererCount = renderers.Count(renderer => renderer is SkinnedMeshRenderer);
        report.meshRendererCount = renderers.Count(renderer => renderer is MeshRenderer);
        report.enabledRendererCount = renderers.Count(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy);
        report.visualModelEvidence = report.renderers
            .Where(renderer => renderer.enabled && renderer.activeInHierarchy)
            .Select(renderer => renderer.path)
            .ToList();
        report.hasVisibleModel = report.enabledRendererCount > 0;
        report.onlyGhostHandsDetected = report.hasHands &&
            (report.rendererCount == 0 || report.skinnedMeshRendererCount == 0) &&
            (!report.hasVisibleModel || report.visualModelEvidence.All(IsHandOrControllerPath));
        report.visibleBounds = CombineRendererBounds(renderers);

        var rigidbodies = components.OfType<Rigidbody>().ToList();
        var controllers = components.OfType<CharacterController>().ToList();
        var colliders = components.OfType<Collider>().ToList();
        report.rigidbodyCount = rigidbodies.Count;
        report.characterControllerCount = controllers.Count;
        report.colliderCount = colliders.Count;
        report.hasRigidbody = report.rigidbodyCount > 0;
        report.hasCharacterController = report.characterControllerCount > 0;
        report.rigidbodies = rigidbodies.Select(InspectRigidbody).ToList();
        report.gravityEvidence = rigidbodies.Count == 0
            ? "no_rigidbody_under_actor"
            : rigidbodies.Any(body => body.useGravity && !body.isKinematic)
                ? "non_kinematic_gravity_rigidbody_present"
                : rigidbodies.All(body => body.isKinematic)
                    ? "all_rigidbodies_kinematic"
                    : "rigidbodies_present_without_enabled_gravity";
        report.floorContact = InspectFloorSupport(actor);
        report.groundedOrFloorContactEvidence = report.floorContact.status;
        report.physicsClassification = rigidbodies.Any(body => body.useGravity && !body.isKinematic)
            ? "physics_driven_candidate"
            : report.hasHands
                ? "tracking_or_manually_positioned"
                : "unknown";

        var componentNames = components
            .Where(component => component != null)
            .Select(component => component.GetType().FullName ?? component.GetType().Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var typedActorNames = FindTypedRuntimeInstances(GetRelevantRuntimeTypes())
            .Where(instance => instance.gameObject != null &&
                               IsDescendantOrSelf(instance.gameObject.transform, actor.transform))
            .Select(instance => instance.type.FullName ?? instance.type.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        componentNames = componentNames
            .Concat(typedActorNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        report.componentTypes = componentNames;
        report.movementComponentCandidates = Match(componentNames, MovementHints);
        report.playerControllerCandidates = Match(componentNames, PlayerControllerHints);
        report.inputComponentCandidates = Match(componentNames, InputHints);
        report.gestureComponentCandidates = Match(componentNames, GestureHints);
        report.summonComponentCandidates = Match(componentNames, SummonHints);
        report.structureSpawnerCandidates = Match(componentNames, new[] { "StructureSpawner", "SpawnStructure" });
        report.moveSystemCandidates = Match(componentNames, MoveSystemHints);
        report.modifierCandidates = Match(componentNames, ModifierHints);
        report.ownershipCandidates = Match(componentNames, OwnershipHints);
        report.healthCandidates = Match(componentNames, HealthHints);
        report.damageHitCandidates = Match(componentNames, DamageHints);
        report.networkPlayerIdCandidates = Match(componentNames, NetworkHints);
        report.poolPrefabCandidates = Match(componentNames, StructurePoolHints);
        report.hasMovementSystem = report.movementComponentCandidates.Count > 0;
        report.hasPhysicsOrGrounding = report.hasRigidbody ||
                                      report.hasCharacterController ||
                                      report.colliderCount > 0;
        report.hasHealth = report.healthCandidates.Count > 0;
        report.hasOwnership = report.ownershipCandidates.Count > 0;
        report.hasSummonContext = report.summonComponentCandidates.Count > 0;
        report.realSummonConfirmed = false;
        report.rootMotionConfirmed = false;
        report.handMotionConfirmed = false;
        report.currentBestActorPath = report.actorRootPath;
        report.currentBestActorScene = report.actorScene;

        AddConfirmed(report, report.hasHead, "head_transform");
        AddConfirmed(report, report.hasHands, "distinct_hand_transforms");
        AddConfirmed(report, report.hasVisibleModel, "enabled_renderer_evidence");
        AddConfirmed(report, report.colliderCount > 0, "actor_colliders");
        AddConfirmed(report, report.movementComponentCandidates.Count > 0, "movement_component_candidate");
        AddConfirmed(report, report.healthCandidates.Count > 0, "health_component_candidate");
        AddConfirmed(report, report.ownershipCandidates.Count > 0, "ownership_component_candidate");
        AddConfirmed(report, report.summonComponentCandidates.Count > 0, "summon_component_candidate");
        AddMissing(report, report.hasHead, "head");
        AddMissing(report, report.hasHands, "hands");
        AddMissing(report, report.hasVisibleModel, "visibleModel");
        AddMissing(report, report.movementComponentCandidates.Count > 0, "movementSystem");
        AddMissing(report, report.hasPhysicsOrGrounding, "physicsOrGrounding");
        AddMissing(report, report.healthCandidates.Count > 0, "health");
        AddMissing(report, report.ownershipCandidates.Count > 0, "ownership");
        AddMissing(report, report.summonComponentCandidates.Count > 0, "summonContext");

        var score = 0;
        if (report.hasHead) score += 10;
        if (report.hasHands) score += 15;
        if (report.hasVisibleModel) score += 10;
        if (!report.onlyGhostHandsDetected && report.hasVisibleModel) score += 5;
        if (report.hasPhysicsOrGrounding) score += 15;
        if (report.movementComponentCandidates.Count > 0) score += 15;
        if (report.healthCandidates.Count > 0) score += 10;
        if (report.ownershipCandidates.Count > 0) score += 10;
        if (report.summonComponentCandidates.Count > 0 || report.moveSystemCandidates.Count > 0) score += 10;
        report.completenessScore = score;
        var complete = report.hasHead &&
                       report.hasHands &&
                       report.movementComponentCandidates.Count > 0 &&
                       report.hasPhysicsOrGrounding &&
                       report.ownershipCandidates.Count > 0 &&
                       report.hasSummonContext &&
                       report.realSummonConfirmed;
        report.classification = complete
            ? "complete_local_actor"
            : report.hasHead && report.hasHands
                ? "partial_tracking_rig"
                : report.hasVisibleModel
                    ? "visual_only_actor"
                    : "unknown";
        report.likely.Add(report.hasHands
            ? "head_and_hands_are_tracking_targets_or_first_person_rig_parts"
            : "actor_role_not_established");
        report.unconfirmed.Add("game_design_expectation_for_first_person_body_visibility");
        report.unconfirmed.Add("runtime_health_value");
        report.unconfirmed.Add("runtime_player_identity_value");
        report.unconfirmed.Add("real_summon_execution");
        report.unconfirmed.Add("damage_or_combat_registration");
        report.unsafeEvidence.Add("network_rpc_and_damage_methods_not_invoked");
        return report;
    }

    private static List<GameObject> FindRuntimeRoots()
    {
        GameObject[] objects;
        try
        {
            objects = Resources.FindObjectsOfTypeAll<GameObject>();
        }
        catch
        {
            return new List<GameObject>();
        }

        return objects
            .Where(IsRuntimeObject)
            .Select(gameObject => gameObject.transform != null && gameObject.transform.root != null
                ? gameObject.transform.root.gameObject
                : gameObject)
            .Where(root => root != null)
            .GroupBy(root => root.GetInstanceID())
            .Select(group => group.First())
            .ToList();
    }

    private static List<Type> GetRelevantRuntimeTypes()
    {
        var hints = MovementHints
            .Concat(PlayerControllerHints)
            .Concat(InputHints)
            .Concat(GestureHints)
            .Concat(SummonHints)
            .Concat(StructurePoolHints)
            .Concat(MoveSystemHints)
            .Concat(ModifierHints)
            .Concat(OwnershipHints)
            .Concat(HealthHints)
            .Concat(DamageHints)
            .Concat(NetworkHints)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var types = new List<Type>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] assemblyTypes;
            try
            {
                assemblyTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                assemblyTypes = ex.Types.Where(type => type != null).ToArray();
            }
            catch
            {
                continue;
            }
            foreach (var type in assemblyTypes)
            {
                var name = type?.FullName;
                if (type == null ||
                    string.IsNullOrWhiteSpace(name) ||
                    !name.StartsWith("Il2CppRUMBLE.", StringComparison.OrdinalIgnoreCase) ||
                    !typeof(Component).IsAssignableFrom(type) ||
                    !ContainsAny(name, hints))
                {
                    continue;
                }
                types.Add(type);
            }
        }
        return types
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<TypedRuntimeComponent> FindTypedRuntimeInstances(IEnumerable<Type> types)
    {
        var results = new List<TypedRuntimeComponent>();
        foreach (var type in types ?? Array.Empty<Type>())
        {
            foreach (var value in FindRuntimeObjects(type))
            {
                if (value is not Component component ||
                    component.gameObject == null ||
                    !IsRuntimeObject(component.gameObject))
                {
                    continue;
                }
                results.Add(new TypedRuntimeComponent
                {
                    type = type,
                    component = component,
                    gameObject = component.gameObject
                });
            }
        }
        return results
            .GroupBy(result => $"{result.type.FullName}:{result.gameObject.GetInstanceID()}")
            .Select(group => group.First())
            .ToList();
    }

    private static IEnumerable<object> FindRuntimeObjects(Type type)
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
        System.Collections.IEnumerable values;
        try
        {
            values = method.MakeGenericMethod(type).Invoke(null, Array.Empty<object>())
                as System.Collections.IEnumerable;
        }
        catch
        {
            yield break;
        }
        if (values == null)
        {
            yield break;
        }
        foreach (var value in values)
        {
            if (value != null)
            {
                yield return value;
            }
        }
    }

    private static bool IsRuntimeObject(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return false;
        }
        var scene = gameObject.scene;
        return (scene.IsValid() && scene.isLoaded) ||
               string.Equals(scene.name, "DontDestroyOnLoad", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Component> SafeComponents(GameObject root)
    {
        if (root == null)
        {
            return new List<Component>();
        }
        try
        {
            return root.GetComponentsInChildren<Component>(true)
                .Where(component => component != null)
                .ToList();
        }
        catch
        {
            return new List<Component>();
        }
    }

    private static List<Transform> SafeTransforms(GameObject root)
    {
        if (root == null)
        {
            return new List<Transform>();
        }
        try
        {
            return root.GetComponentsInChildren<Transform>(true)
                .Where(transform => transform != null)
                .ToList();
        }
        catch
        {
            return new List<Transform>();
        }
    }

    private static Transform FindTransform(IEnumerable<Transform> transforms, params string[] requiredTokens)
    {
        return transforms
            .Where(transform => requiredTokens.All(token =>
                SafePath(transform).Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(transform => SafePath(transform).Length)
            .FirstOrDefault();
    }

    private static ActorRendererEvidence InspectRenderer(Renderer renderer)
    {
        var materials = new List<string>();
        try
        {
            materials = renderer.sharedMaterials
                .Where(material => material != null)
                .Select(material => material.name)
                .ToList();
        }
        catch
        {
            materials.Add("unavailable");
        }
        return new ActorRendererEvidence
        {
            path = SafePath(renderer.gameObject),
            type = renderer.GetType().FullName,
            enabled = renderer.enabled,
            activeInHierarchy = renderer.gameObject.activeInHierarchy,
            scene = SafeScene(renderer.gameObject),
            materials = materials,
            boundsCenter = ActorEvidenceVector3.From(renderer.bounds.center),
            boundsSize = ActorEvidenceVector3.From(renderer.bounds.size)
        };
    }

    private static ActorRigidbodyEvidence InspectRigidbody(Rigidbody body)
    {
        return new ActorRigidbodyEvidence
        {
            path = SafePath(body.gameObject),
            useGravity = body.useGravity,
            isKinematic = body.isKinematic,
            mass = body.mass,
            velocity = ActorEvidenceVector3.From(body.velocity)
        };
    }

    private static ActorFloorContactEvidence InspectFloorSupport(GameObject actor)
    {
        var result = new ActorFloorContactEvidence
        {
            status = "not_confirmed",
            rayOrigin = actor != null ? ActorEvidenceVector3.From(actor.transform.position + Vector3.up * 0.25f) : null
        };
        if (actor == null)
        {
            result.status = "actor_missing";
            return result;
        }
        try
        {
            var origin = actor.transform.position + Vector3.up * 0.25f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, 2.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                result.status = "downward_raycast_hit";
                result.colliderPath = SafePath(hit.collider?.gameObject);
                result.distance = hit.distance;
                result.point = ActorEvidenceVector3.From(hit.point);
                result.normal = ActorEvidenceVector3.From(hit.normal);
            }
            else
            {
                result.status = "downward_raycast_no_hit";
            }
        }
        catch (Exception ex)
        {
            result.status = "raycast_failed";
            result.warning = ex.Message;
        }
        return result;
    }

    private static ActorBoundsEvidence CombineRendererBounds(List<Renderer> renderers)
    {
        var active = renderers.Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy).ToList();
        if (active.Count == 0)
        {
            return null;
        }
        var bounds = active[0].bounds;
        foreach (var renderer in active.Skip(1))
        {
            bounds.Encapsulate(renderer.bounds);
        }
        return new ActorBoundsEvidence
        {
            center = ActorEvidenceVector3.From(bounds.center),
            size = ActorEvidenceVector3.From(bounds.size)
        };
    }

    private static List<string> Match(IEnumerable<string> values, IEnumerable<string> hints)
    {
        return values
            .Where(value => ContainsAny(value, hints))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsAny(string value, IEnumerable<string> hints)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               hints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHandOrControllerPath(string path)
    {
        return ContainsAny(path, new[] { "hand", "controller", "wrist", "finger", "glove" });
    }

    private static int ScoreLifecycleText(string text)
    {
        var score = 0;
        if (ContainsAny(text, PlayerControllerHints)) score += 120;
        if (ContainsAny(text, MovementHints)) score += 100;
        if (ContainsAny(text, HealthHints)) score += 80;
        if (ContainsAny(text, SummonHints)) score += 80;
        if (ContainsAny(text, OwnershipHints)) score += 70;
        if (ContainsAny(text, GestureHints)) score += 60;
        if (ContainsAny(text, InputHints)) score += 50;
        return score;
    }

    private static List<string> BuildLifecycleReasons(
        List<string> components,
        Transform head,
        Transform left,
        Transform right)
    {
        var reasons = new List<string>();
        if (head != null) reasons.Add("head_transform");
        if (left != null && right != null && left != right) reasons.Add("distinct_hands");
        if (Match(components, MovementHints).Count > 0) reasons.Add("movement_component");
        if (Match(components, HealthHints).Count > 0) reasons.Add("health_component");
        if (Match(components, SummonHints).Count > 0) reasons.Add("summon_component");
        if (Match(components, OwnershipHints).Count > 0) reasons.Add("ownership_component");
        return reasons;
    }

    private static List<string> BuildLifecycleMissing(
        List<string> components,
        Transform head,
        Transform left,
        Transform right,
        int rendererCount)
    {
        var missing = new List<string>();
        if (head == null) missing.Add("head");
        if (left == null || right == null || left == right) missing.Add("distinct_hands");
        if (rendererCount == 0) missing.Add("renderers");
        if (Match(components, MovementHints).Count == 0) missing.Add("movement");
        if (Match(components, HealthHints).Count == 0) missing.Add("health");
        if (Match(components, SummonHints).Count == 0) missing.Add("summon");
        if (Match(components, OwnershipHints).Count == 0) missing.Add("ownership");
        return missing;
    }

    private static bool IsPotentiallyCompleteCandidate(LocalPlayerLifecycleCandidate candidate)
    {
        return candidate != null &&
               !string.IsNullOrWhiteSpace(candidate.headPath) &&
               !string.IsNullOrWhiteSpace(candidate.leftHandPath) &&
               !string.IsNullOrWhiteSpace(candidate.rightHandPath) &&
               candidate.movement.Count > 0 &&
               (candidate.health.Count + candidate.summon.Count + candidate.ownership.Count) >= 2;
    }

    private static string ClassifySummonContext(string text)
    {
        if (ContainsAny(text, NetworkHints)) return "unsafe_network_path";
        if (ContainsAny(text, GestureHints)) return "gesture_driven";
        if (ContainsAny(text, MoveSystemHints)) return "stack_driven";
        if (ContainsAny(text, new[] { "Pool", "Prefab" })) return "object_pool";
        if (ContainsAny(text, new[] { "Manager", "Registry" })) return "global_manager";
        if (ContainsAny(text, SummonHints)) return "direct_actor_component";
        return "unknown";
    }

    private static string NextSummonProbe(string category)
    {
        return category switch
        {
            "direct_actor_component" => "confirm configured structure and ownership fields; then allow one exact spawn",
            "global_manager" => "confirm manager API, prefab argument, and local ownership context",
            "object_pool" => "confirm one pool checkout API and cleanup return path",
            "gesture_driven" => "identify required recognized pose and local stack state without invoking",
            "stack_driven" => "identify processor input and configured move state without invoking",
            "unsafe_network_path" => "do_not_invoke",
            _ => "passive_review"
        };
    }

    private static bool IsRelevantSummonCandidate(SummonContextCandidate entry)
    {
        if (entry == null)
        {
            return false;
        }
        if (!entry.typeFullName.Contains("PooledMonoBehaviour", StringComparison.OrdinalIgnoreCase) &&
            !entry.typeFullName.Contains("PooledAudioSource", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return string.Equals(entry.configuredState, "pooled_structure_template_present", StringComparison.OrdinalIgnoreCase);
    }

    private static int SummonCandidatePriority(SummonContextCandidate entry)
    {
        if (entry == null) return 0;
        if (entry.typeFullName.Contains("StructureSpawner", StringComparison.OrdinalIgnoreCase)) return 1000;
        if (entry.typeFullName.Contains("PlayerStackProcessor", StringComparison.OrdinalIgnoreCase)) return 900;
        if (entry.typeFullName.Contains("PoolManager", StringComparison.OrdinalIgnoreCase)) return 800;
        if (string.Equals(entry.configuredState, "pooled_structure_template_present", StringComparison.OrdinalIgnoreCase)) return 700;
        if (entry.actorBound) return 600;
        return 100;
    }

    private static string SummonConfiguredState(string typeName, string path, bool active)
    {
        if (typeName.Contains("PoolManager", StringComparison.OrdinalIgnoreCase))
        {
            return active ? "manager_instance_loaded" : "manager_instance_inactive";
        }
        if (typeName.Contains("PooledMonoBehaviour", StringComparison.OrdinalIgnoreCase) &&
            path != null &&
            path.Contains("RUMBLE.MoveSystem.Structure", StringComparison.OrdinalIgnoreCase))
        {
            return "pooled_structure_template_present";
        }
        if (typeName.Contains("StructureSpawner", StringComparison.OrdinalIgnoreCase))
        {
            return "spawner_instance_loaded_configuration_unread";
        }
        return "unconfirmed_not_read";
    }

    private static List<string> SafeMemberNames(Type type)
    {
        try
        {
            return type
                .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property or MemberTypes.Method)
                .Where(member => ContainsAny(member.Name, SummonHints.Concat(StructurePoolHints).Concat(OwnershipHints)))
                .Select(FormatMember)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string FormatMember(MemberInfo member)
    {
        if (member is MethodInfo method)
        {
            var parameters = string.Join(", ", method.GetParameters()
                .Select(parameter => $"{parameter.ParameterType.FullName ?? parameter.ParameterType.Name} {parameter.Name}"));
            return $"Method:{method.Name}({parameters})->{method.ReturnType.FullName ?? method.ReturnType.Name}";
        }
        if (member is FieldInfo field)
        {
            return $"Field:{field.Name}:{field.FieldType.FullName ?? field.FieldType.Name}";
        }
        if (member is PropertyInfo property)
        {
            return $"Property:{property.Name}:{property.PropertyType.FullName ?? property.PropertyType.Name}";
        }
        return $"{member.MemberType}:{member.Name}";
    }

    private static bool IsDescendantOrSelf(Transform candidate, Transform root)
    {
        if (candidate == null || root == null) return false;
        while (candidate != null)
        {
            if (candidate == root) return true;
            candidate = candidate.parent;
        }
        return false;
    }

    private static void AddConfirmed(ActorCompletenessReport report, bool value, string evidence)
    {
        if (value) report.confirmed.Add(evidence);
        else report.failed.Add(evidence);
    }

    private static void AddMissing(ActorCompletenessReport report, bool value, string system)
    {
        if (!value) report.missingCriticalSystems.Add(system);
    }

    private static string SafePath(GameObject gameObject) => SafePath(gameObject?.transform);

    private static string SafePath(Component component) => SafePath(component?.transform);

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
        if (gameObject == null) return null;
        try
        {
            return gameObject.scene.name;
        }
        catch
        {
            return "unknown";
        }
    }
}

internal sealed class ActorCompletenessRunResult
{
    public object report { get; set; }
    public string reportPath { get; set; }
}

internal sealed class ActorCompletenessReport
{
    public DateTime timestampUtc { get; set; }
    public string reason { get; set; }
    public string actorRootPath { get; set; }
    public string actorScene { get; set; }
    public string actorMode { get; set; }
    public string visualModelEvidenceSummary => hasVisibleModel
        ? onlyGhostHandsDetected ? "enabled renderers appear limited to hand/controller paths" : "enabled renderer evidence exists"
        : "no enabled renderer evidence";
    public List<string> visualModelEvidence { get; set; } = new();
    public int rendererCount { get; set; }
    public int skinnedMeshRendererCount { get; set; }
    public int meshRendererCount { get; set; }
    public int enabledRendererCount { get; set; }
    public ActorBoundsEvidence visibleBounds { get; set; }
    public List<ActorRendererEvidence> renderers { get; set; } = new();
    public bool hasVisibleModel { get; set; }
    public bool onlyGhostHandsDetected { get; set; }
    public string headPath { get; set; }
    public string leftHandPath { get; set; }
    public string rightHandPath { get; set; }
    public string bodyRootPath { get; set; }
    public bool hasBody { get; set; }
    public bool hasHead { get; set; }
    public bool hasLeftHand { get; set; }
    public bool hasRightHand { get; set; }
    public bool hasHands { get; set; }
    public int rigidbodyCount { get; set; }
    public int characterControllerCount { get; set; }
    public int colliderCount { get; set; }
    public bool hasRigidbody { get; set; }
    public bool hasCharacterController { get; set; }
    public List<ActorRigidbodyEvidence> rigidbodies { get; set; } = new();
    public string groundedOrFloorContactEvidence { get; set; }
    public string gravityEvidence { get; set; }
    public string physicsClassification { get; set; }
    public ActorFloorContactEvidence floorContact { get; set; }
    public List<string> componentTypes { get; set; } = new();
    public List<string> movementComponentCandidates { get; set; } = new();
    public List<string> playerControllerCandidates { get; set; } = new();
    public List<string> inputComponentCandidates { get; set; } = new();
    public List<string> gestureComponentCandidates { get; set; } = new();
    public List<string> summonComponentCandidates { get; set; } = new();
    public List<string> structureSpawnerCandidates { get; set; } = new();
    public List<string> moveSystemCandidates { get; set; } = new();
    public List<string> modifierCandidates { get; set; } = new();
    public List<string> ownershipCandidates { get; set; } = new();
    public List<string> healthCandidates { get; set; } = new();
    public List<string> damageHitCandidates { get; set; } = new();
    public List<string> networkPlayerIdCandidates { get; set; } = new();
    public List<string> poolPrefabCandidates { get; set; } = new();
    public bool hasMovementSystem { get; set; }
    public bool hasPhysicsOrGrounding { get; set; }
    public bool hasHealth { get; set; }
    public bool hasOwnership { get; set; }
    public bool hasSummonContext { get; set; }
    public bool realSummonConfirmed { get; set; }
    public bool rootMotionConfirmed { get; set; }
    public bool handMotionConfirmed { get; set; }
    public string currentBestActorPath { get; set; }
    public string currentBestActorScene { get; set; }
    public List<string> missingCriticalSystems { get; set; } = new();
    public int completenessScore { get; set; }
    public string classification { get; set; }
    public List<string> confirmed { get; set; } = new();
    public List<string> likely { get; set; } = new();
    public List<string> unconfirmed { get; set; } = new();
    public List<string> failed { get; set; } = new();
    public List<string> unsafeEvidence { get; set; } = new();
}

internal sealed class ActorRendererEvidence
{
    public string path { get; set; }
    public string scene { get; set; }
    public string type { get; set; }
    public bool enabled { get; set; }
    public bool activeInHierarchy { get; set; }
    public List<string> materials { get; set; } = new();
    public ActorEvidenceVector3 boundsCenter { get; set; }
    public ActorEvidenceVector3 boundsSize { get; set; }
}

internal sealed class ActorRigidbodyEvidence
{
    public string path { get; set; }
    public bool useGravity { get; set; }
    public bool isKinematic { get; set; }
    public float mass { get; set; }
    public ActorEvidenceVector3 velocity { get; set; }
}

internal sealed class ActorFloorContactEvidence
{
    public string status { get; set; }
    public ActorEvidenceVector3 rayOrigin { get; set; }
    public string colliderPath { get; set; }
    public float? distance { get; set; }
    public ActorEvidenceVector3 point { get; set; }
    public ActorEvidenceVector3 normal { get; set; }
    public string warning { get; set; }
}

internal sealed class ActorBoundsEvidence
{
    public ActorEvidenceVector3 center { get; set; }
    public ActorEvidenceVector3 size { get; set; }
}

internal sealed class ActorEvidenceVector3
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public static ActorEvidenceVector3 From(Vector3 value) => new() { x = value.x, y = value.y, z = value.z };
}

internal sealed class LocalPlayerLifecycleDiscoveryReport
{
    public DateTime timestampUtc { get; set; }
    public string reason { get; set; }
    public string actorMode { get; set; }
    public string currentActorPath { get; set; }
    public int scannedRootCount { get; set; }
    public int candidateCount { get; set; }
    public string bestCandidatePath { get; set; }
    public string bestCandidateScene { get; set; }
    public bool completeCandidateFound { get; set; }
    public string conclusion { get; set; }
    public List<LocalPlayerLifecycleCandidate> candidates { get; set; } = new();
}

internal sealed class LocalPlayerLifecycleCandidate
{
    public string rootPath { get; set; }
    public string scene { get; set; }
    public bool activeInHierarchy { get; set; }
    public bool isCurrentActor { get; set; }
    public int score { get; set; }
    public int rendererCount { get; set; }
    public string headPath { get; set; }
    public string leftHandPath { get; set; }
    public string rightHandPath { get; set; }
    public List<string> componentEvidence { get; set; } = new();
    public List<string> movement { get; set; } = new();
    public List<string> health { get; set; } = new();
    public List<string> summon { get; set; } = new();
    public List<string> ownership { get; set; } = new();
    public List<string> gesture { get; set; } = new();
    public List<string> whyItMayBeReal { get; set; } = new();
    public List<string> whyItMayNotBe { get; set; } = new();
}

internal sealed class SummonContextDiscoveryReport
{
    public DateTime timestampUtc { get; set; }
    public string reason { get; set; }
    public string actorMode { get; set; }
    public string actorRootPath { get; set; }
    public int scannedComponentCount { get; set; }
    public int scannedTypeCount { get; set; }
    public int candidateCount { get; set; }
    public int actorBoundCandidateCount { get; set; }
    public bool actorBoundSummonContextFound { get; set; }
    public int pooledStructureTemplateCount { get; set; }
    public List<SummonContextCandidate> directActorComponents { get; set; } = new();
    public List<SummonContextCandidate> globalManagers { get; set; } = new();
    public List<SummonContextCandidate> objectPools { get; set; } = new();
    public List<SummonContextCandidate> structurePrefabTemplates { get; set; } = new();
    public List<SummonContextCandidate> ownershipSystems { get; set; } = new();
    public List<SummonContextCandidate> unconfiguredSpawners { get; set; } = new();
    public List<SummonContextCandidate> gestureDriven { get; set; } = new();
    public List<SummonContextCandidate> stackDriven { get; set; } = new();
    public List<SummonContextCandidate> unsafeNetworkPaths { get; set; } = new();
    public SummonContextCandidate rankedSafestCandidate { get; set; }
    public bool realSummonProbeSafeNow { get; set; }
    public string blockedReason { get; set; }
    public bool missingLiveConfiguredSpawner { get; set; }
    public string priorProbeExplanation { get; set; }
    public string conclusion { get; set; }
    public string realSummonProbeReportPath { get; set; }
    public List<SummonContextCandidate> candidates { get; set; } = new();
}

internal sealed class TypedRuntimeComponent
{
    public Type type { get; set; }
    public Component component { get; set; }
    public GameObject gameObject { get; set; }
}

internal sealed class SummonContextCandidate
{
    public string componentPath { get; set; }
    public string scene { get; set; }
    public string typeFullName { get; set; }
    public string category { get; set; }
    public bool actorBound { get; set; }
    public bool activeInHierarchy { get; set; }
    public List<string> memberEvidence { get; set; } = new();
    public string configuredState { get; set; }
    public string safety { get; set; }
    public string nextProbe { get; set; }
}
