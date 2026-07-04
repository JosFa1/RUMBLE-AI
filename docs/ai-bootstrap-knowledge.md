# AI Bootstrap Knowledge

This document is for AI continuation. Every claim is labeled `confirmed`, `likely`, `unconfirmed`, `failed`, or `unsafe`. Runtime claims below come from the July 3, 2026 UTC live runs unless stated otherwise.

## Current Verified State

`confirmed`: `dotnet build mod\AI_Train.csproj -c Debug` passes with zero warnings and zero errors. The built DLL and deployed `RUMBLE\Mods\AI_Train.dll` SHA-256 hashes matched before the final live run.

`confirmed`: RUMBLE starts with the mod, the bridge listens on `127.0.0.1:8765`, staged bootstrap reaches `Ready`, and the game remains responsive.

`confirmed`: `python scripts\run_offline_validation.py` passes all 23 checks.

`confirmed`: `python scripts\run_full_validation.py` passes status, staged bootstrap milestones, required dump existence, observation, malformed protocol handling, reset, step, reset-during-step, scripted sequence, random policy, and 20-cycle stability.

`confirmed`: Final validation report is `trainer-client/runs/20260703_132714_run_full_validation_079e4d250a/validation_report.json`. Its `bootstrap_dump_content` check reports zero invalid files/failures, two scenes, exact head/hands, passive capability discovery, `managerInitialized=true`, and `usableFloorConfirmed=true`.

`confirmed`: Validation artifacts parse as JSON/JSONL. The scripted run has 4 steps, random run has 40 steps, and stability run has 20 cycles plus 60 steps.

## Staged Bootstrap

`confirmed`: `mod/TrainingBootstrapOrchestrator.cs` defines `Uninitialized`, `InitialInventory`, `RequestGymLoad`, `WaitForGymLoaded`, `RemoveLoaderScene`, `GymInventory`, `DiscoverPrimaryActor`, `DiscoverActorCapabilities`, `ProbeSingleActorSummons`, `ProbeMoveModifiers`, `ProbeMultiActorSupport`, `ProbeActorInteraction`, `BuildMinimalArena`, `Ready`, and `Failed`.

`confirmed`: The normal path transitions directly from passive capability discovery to arena build. Heavy probe stages are represented in the state model but run only through manual bridge/operator requests.

`confirmed`: `UseStagedBootstrap=true` and `EnableLegacyBootstrapFallback=false`. The legacy automatic scan/load/build path does not run concurrently.

`confirmed`: The bridge starts before scene readiness and exposes bootstrap progress. Observation, reset, and step remain readiness-gated.

`confirmed`: `retry_bootstrap` is available. On a ready environment it returns `already_ready` and does not disrupt the running scene.

## Scene Order And Gym Load

`confirmed`: Initial inventory contained active `Loader`, build index `0`, likely role `loader`, with four roots: `SCENE`, `!ftraceLightmaps`, `LOGIC`, and `UI`.

`confirmed`: The selected Gym build-settings candidate was index `1`, name `Gym`, score `1000`, path `Assets/Scenes/Master Scenes/Gym/Gym.unity`.

`confirmed`: Gym was requested with additive scene loading. Success was not marked from the method call; `scene_inventory_20260703_132659_082.json` confirmed loaded `Loader` and `Gym`.

`confirmed`: `scene_inventory_20260703_132700_563.json` confirmed only active `Gym`, build index `1`, after Loader cleanup. The later post-arena inventory `scene_inventory_20260703_132702_141.json` confirmed active `AI_Train_Training` with both `Gym` and `AI_Train_Training` loaded before `Ready`.

`confirmed`: Before unloading Loader, the code activated Gym and preserved `LOGIC/BootLoaderPlayer` with `DontDestroyOnLoad`. It then stripped Loader roots and issued one unload request.

`confirmed`: Logs contain `Melon scene callback unloaded: Loader (0)` and the orchestrator transition reason `loader-removal-confirmed-by-inventory`.

`unconfirmed`: Reflection fallbacks `Il2CppRUMBLE.Managers.SceneManager.PerformStartupGymLoad()` and `BootLoaderIntroSystem.DoTransitionToGym()` were not exercised because build-settings loading succeeded first.

## Actor Discovery

`confirmed`: Selected actor root is `BootLoaderPlayer`. Discovery scene at selection time is `DontDestroyOnLoad`, because the actor was preserved before Loader removal.

`confirmed`: Actor confidence score is `76000`; evidence is `preserved_from_bootstrap_scene`, original path `LOGIC/BootLoaderPlayer`, and distinct head/hands.

`confirmed`: Strong typed actor evidence is `Il2CppRUMBLE.Players.PlayerController`.

`confirmed`: Actor discovery and `ObservationBuilder` resolve the same working transforms:

- head: `BootLoaderPlayer/Headset Offset/Headset`
- left hand: `BootLoaderPlayer/Left Controller/IkTarget/InteractionHand`
- right hand: `BootLoaderPlayer/Right Controller/IkTarget/InteractionHand`

`confirmed`: `TrainingEnvironmentManager`, `ObservationBuilder`, and `ActionExecutor` all use `BootLoaderPlayer`. Live observation/reset/step calls work.

`failed`: No health value was found under this actor. Live observations return `health=null` with warning `Health value not found under BootLoaderPlayer.`

`failed`: No live actor-bound `PlayerMovement`, `PlayerHealth`, summon, modifier, or attack/damage component was discovered under this bootstrap actor.

## Capability Discovery

`confirmed`: Capability discovery is passive and does not invoke candidate gameplay methods.

`confirmed`: Final startup report is `capability_discovery_20260703_132701.json`; the prior manual bridge rerun wrote `capability_discovery_20260703_132042.json`.

`confirmed`: The final manual passive discovery produced 394 typed candidates with `genericFallbackUsed=false`: 19 actor-source entries, 48 summon-hint, 48 move-hint, 48 modifier-hint, 24 ownership-hint, 41 damage/hit-hint, and 48 input/gesture-hint entries.

`confirmed`: Live global candidates include:

- `Il2CppRUMBLE.Slabs.SlabOwnership` under the Match Slab pool, including owner fields and `SetOwnership`
- `Il2CppRUMBLE.MoveSystem.CombatManager` under `Game Instance/Other/CombatManager`
- `Il2CppRUMBLE.Interactions.CollisionHandler` under an Info Slab pooled object

`likely`: Generated installed metadata contains exact methods including `StructureSpawner.Spawn()`, `PlayerMovement.Move(Vector2)`, `PlayerMovement.Reposition(Vector3, Quaternion)`, modifier execution methods, `PooledMonoBehaviour.ReturnToPool()`, health/hit members, and ownership members.

`failed`: No active loaded-scene `StructureSpawner` instance with a configured structure was found.

`failed`: No exact `PlayerMovement.Move(Vector2)` component was found under the selected actor.

`unsafe`: Automatically invoking every reflected candidate is prohibited. Ownership, health, damage, hit, RPC, object-pool, and modifier methods require a separately justified probe context.

## Probe Results

`confirmed`: Full hierarchy logging and all active probe gates are false by default: `EnableFullSceneHierarchyDump`, `EnableActorCloneProbes`, `EnableSummonProbes`, `EnableMoveProbes`, and `EnableActorInteractionProbes`. Passive JSON inventory remains available without normal-start hierarchy log spam.

`confirmed`: On the final production build, all four probe bridge requests returned `disabled_by_config` and wrote reports without invoking gameplay methods.

`failed`: Active summon artifact `single_actor_summon_probe_20260703_032100_852.json` has status `no_safe_candidate`, failure `structure_spawner_component_not_found`, zero attempts, and no spawned objects.

`failed`: Active move artifact `move_probe_20260703_032114_847.json` has status `no_safe_candidate`, failure `player_movement_move_vector2_not_found`, and modifier status `not_found`. No gameplay method was invoked.

`confirmed`: Active multi-actor artifact `multi_actor_probe_20260703_032126_666.json` has status `dummy_target_confirmed`. The mod-owned target moved `0.5` units while the primary actor moved `0`.

`unsafe`: Full actor clone was not attempted. Instantiation would run active player lifecycle methods before duplicate input, camera, and networking components could be neutralized.

`confirmed`: A follow-up inventory found zero `AI_Train_DummyTarget_*` roots.

`confirmed`: Active interaction artifact `actor_interaction_probe_20260703_032501_930.json` has status `contact_confirmed`, evidence `collider_bounds_overlap`, and `damageEvidence=false`.

`failed`: Interaction `OnCollisionEnter` and `OnTriggerEnter` counts were both zero. The report does not claim Unity callback, damage, hit, or combat success.

`confirmed`: A follow-up inventory found zero `AI_Train_InteractionTarget_*` or `AI_Train_InteractionProjectile_*` roots.

## Minimal Arena

`confirmed`: Arena source is `Gym`; training scene is `AI_Train_Training`.

`confirmed`: Actor `BootLoaderPlayer` was moved into the training scene and manager initialization succeeded.

`confirmed`: Selected floor collider is `SCENE/GYM_Collission/Collission combat floor`, score `880`, with a collider present.

`confirmed`: A bounded grid sample on the selected collider found an upward-facing surface at `(-3.0971565, 6.45971, -2.4678192)`. The actor was placed at `(-3.0971565, 6.53971, -2.4678192)`, 0.08 units above that surface.

`confirmed`: A second ray against only the selected collider hit the same support surface at distance `0.57999945`; `usableFloorConfirmed=true`, `supportProbeStatus=selected_collider_raycast_confirmed`, and both floor and placement warnings are empty. The corrected placement then passed full live validation.

`confirmed`: Preserved roots are `!ftraceLightmaps`, `ProbeVolumePerSceneData`, `SCENE`, `INTERACTABLES`, `LIGHTING`, and `LOGIC`.

`confirmed`: Only explicit clutter roots `SCENE VFX/SFX` and `TUTORIAL` were destroyed.

`confirmed`: `LIGHTING` is the recorded background candidate. `skyboxPresent=false`.

`unconfirmed`: A distinct mountain/background geometry root was not positively classified. `SCENE` remains preserved, so embedded environment geometry was not pruned.

## Bridge And Operator

`confirmed`: Status exposes stage, readiness/failure, loaded/active scenes, Gym, Loader removed/inert, actor, arena, probe statuses, and dump paths.

`confirmed`: Live status reported `bootstrapStage=Ready`, `gymLoaded=true`, `loaderRemoved=true`, `primaryActorFound=true`, `arenaBuilt=true`, and `sceneReady=true`.

`confirmed`: The first post-ready status and operator both reported `activeScene=AI_Train_Training` and `loadedScenes=[Gym, AI_Train_Training]`; no manual inventory refresh was required.

`confirmed`: The operator bootstrap diagnostics menu connected to the live bridge, requested `get_bootstrap_report`, and displayed the same `Ready` state without raw-log inspection.

`confirmed`: Protocol version is consistently `0.3` across mod, schemas, docs, config, and client.

## Evidence Paths

`confirmed`: Final normal startup evidence:

- `RUMBLE/MelonLoader/Latest.log`
- `latest_scene_inventory.json`
- `latest_actor_discovery.json`
- `latest_capability_discovery.json`
- `latest_arena_build_report.json`
- `scene_inventory_20260703_132658_036.json`
- `scene_inventory_20260703_132659_082.json`
- `scene_inventory_20260703_132700_563.json`
- `scene_inventory_20260703_132702_141.json`
- `actor_discovery_20260703_132701.json`
- `capability_discovery_20260703_132701.json`
- `arena_build_report_20260703_132702.json`

`confirmed`: Active probe evidence:

- `single_actor_summon_probe_20260703_032100_852.json`
- `move_probe_20260703_032114_847.json`
- `multi_actor_probe_20260703_032126_666.json`
- `actor_interaction_probe_20260703_032501_930.json`

## Remaining Unknowns And Next Goal

`unconfirmed`: Whether a later scene or local-player lifecycle state exposes a complete actor with movement, health, gesture, summon, and ownership systems.

`unconfirmed`: The valid processor, stack, prefab, and ownership context needed for real summon/modifier probes.

`unconfirmed`: A lifecycle-safe full second-player path.

`unconfirmed`: A game-specific grounded-state flag, health change, damage, hit event, ownership transfer, and real combat evidence.

`confirmed`: The next recommended goal is to discover or transition to a complete local-player actor state while preserving this now-green staged bootstrap, then rerun only summon and movement discovery before attempting any additional active method.

## Actor Completeness Status

`confirmed`: Bootstrap readiness and actor completeness are separate states. `bootstrapReady=true` means the staged bridge scene is usable; it does not mean the selected actor is a complete playable RUMBLE character.

`confirmed`: The selected actor remains `BootLoaderPlayer`. It has head and distinct left/right hand transforms, and live step actions can move the hand transforms through `ActionExecutor`.

`confirmed`: The current actor-completeness report classifies `BootLoaderPlayer` as `partial_tracking_rig` unless newer live evidence proves a better candidate. The expected report fields are `onlyGhostHandsDetected=true`, `hasVisibleModel=false`, `rendererCount=0`, `hasBody=false`, `hasMovementSystem=false`, `hasPhysicsOrGrounding=false`, `hasHealth=false`, `hasOwnership=false`, `hasSummonContext=false`, `realSummonConfirmed=false`, and `rootMotionConfirmed=false`.

`failed`: No actor-bound visible model, movement/root-motion system, actor-side physics/grounding, health, ownership, or configured summon context has been found under the selected hierarchy.

`failed`: Real summon is not confirmed. `latest_real_summon_probe.json` must still exist; when summon is blocked it reports `status=blocked`, `generatedObjectCount=0`, and `realSummonConfirmed=false` with the missing ownership/init context.

`confirmed`: Actor pruning comparison is required before blaming the training scene. If the selected actor inventory is unchanged before and after Loader removal and arena build, the report should say useful systems were never present rather than stripped by pruning.

`unconfirmed`: A complete local-player lifecycle state after Gym load may exist elsewhere or require an additional game transition.

`confirmed`: The current Ready state is enough for bridge/environment validation, observation, reset, and hand-target stepping. It is not enough for AI training that needs a complete local actor, game movement, health, ownership, combat, or real summon mechanics.

`confirmed`: Next exact goal: discover and trigger the complete local-player lifecycle after Gym load, without breaking the now-green staged bootstrap, then re-run actor completeness and summon context discovery.

## Complete Local-Player Lifecycle Diagnostics

`confirmed`: The July 3, 2026 lifecycle code pass adds `mod/ActorLifecycleReportService.cs` and does not rewrite the staged bootstrap, arena builder, bridge, observation, action, reward, or active probe systems.

`confirmed`: The bridge now accepts passive lifecycle requests `run_lifecycle_timeline`, `run_lifecycle_trigger_discovery`, `run_lifecycle_mode_comparison`, `run_lifecycle_trigger_probe`, `run_actor_candidate_ranking`, and `run_missing_lifecycle_dependency_report`.

`confirmed`: Status now exposes `completeActorFound`, `bestCompleteActorPath`, `lifecycleMode`, `lifecycleProbeStatus`, `missingLifecycleDependency`, and latest dump paths for lifecycle timeline, trigger discovery, mode comparison, trigger probe, actor candidate ranking, and missing dependency reports.

`confirmed`: The new lifecycle trigger probe is blocked/passive by default. It records before/after snapshots and skipped candidates, with `invokedCount=0`; it does not call reflected lifecycle, spawn, ownership, pool, network, damage, or gameplay methods.

`confirmed`: `dotnet build mod\AI_Train.csproj -c Debug` passed after the lifecycle diagnostic changes with zero warnings and zero errors.

`confirmed`: `python scripts\run_offline_validation.py` passed after the lifecycle diagnostic changes.

`failed`: Live bridge validation was blocked because `127.0.0.1:8765` refused connections; RUMBLE was not running with the mod bridge loaded during this pass.

`unconfirmed`: The new lifecycle reports have not yet been produced by a live RUMBLE relaunch in this pass. They are generated after arena initialization and can also be requested from operator diagnostics once the bridge is reachable.

`confirmed`: Next exact goal is to relaunch RUMBLE with the rebuilt DLL, run `python scripts\run_full_validation.py`, inspect the new lifecycle reports, and only then decide whether a fresh Loader-held, Gym-only, no-move, or no-prune startup mode is needed.
