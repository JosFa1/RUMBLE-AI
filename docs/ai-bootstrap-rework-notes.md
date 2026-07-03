# AI Bootstrap Rework Notes

## Current Scene Flow As Found

The current bootstrap owner is `mod/TrainingFoundation.cs`.

Observed flow from code:

- `Core.OnInitializeMelon()` creates `TrainingFoundation` and calls `Initialize()`.
- `TrainingFoundation.Initialize()` creates logging paths under `UserData/AI_Train`, initializes the preserved environment/observation/action systems, monitor camera, passive exploration service, bridge, `TrainingBootstrapOrchestrator`, and `TrainingExplorationProbeService`, then starts either staged mode or the gated legacy path.
- `TrainingFoundation.OnUpdate()` handles hotkeys, ticks manual active probes, updates camera/telemetry, ticks the staged orchestrator, and pumps the bridge. Legacy periodic scan/load/build behavior runs only when staged mode is disabled or legacy fallback is explicitly enabled.
- `ScanAllScenes(reason, allowBootstrapActions)` inventories loaded scenes, scores roots, writes scene bundles/inventories, and returns evidence to the orchestrator. Staged calls pass `allowBootstrapActions: false`; only the gated legacy path permits scan-time loading/building/cleanup.
- `TryForceGymLoad()` tries the strongest build-settings Gym candidate additively, then the exact known transition methods `PerformStartupGymLoad()` and `DoTransitionToGym()` as bounded fallbacks. It records an attempt but does not mark success; the orchestrator waits for inventory evidence.
- `TryUnloadBootstrapScenes()` activates Gym, preserves the actor with `DontDestroyOnLoad`, strips Loader roots, requests one Loader unload, and lets the orchestrator confirm Loader disappearance or inertness through a later inventory.
- `TryBuildTrainingScene()` creates or reuses `AI_Train_Training`, moves the selected actor into it, preserves support/background/floor candidates, prunes only explicit clutter, places the actor on the selected floor collider, initializes `TrainingEnvironmentManager`, and writes the arena report.

## Files And Classes Involved

- `mod/Core.cs`: MelonLoader lifecycle forwarding.
- `mod/TrainingFoundation.cs`: current scene scan, Gym load, Loader cleanup, training scene creation, dump writing, hotkeys, bridge startup.
- `mod/TrainingEnvironmentManager.cs`: training scene/player state and bridge status payload.
- `mod/TrainingBridgeServer.cs`: TCP bridge, status response, request handling, debug probe request.
- `mod/TrainingExplorationService.cs`: reflection/debug probe report builder.
- `mod/TrainingExplorationProbeService.cs`: bounded manual summon, movement, dummy-target, and collision probes with before/after evidence and cleanup.
- `mod/TrainingRuntimeTools.cs`: monitor camera and runtime host support.
- `mod/TrainingActorLocator.cs`: shared exact head/hand transform candidates used by discovery, observations, and actions before score-based fallback.
- `mod/ObservationBuilder.cs`: observation generation from `TrainingEnvironmentManager.CurrentPlayerRoot`.
- `mod/ActionExecutor.cs`: reset and step execution against the registered actor.
- `mod/RewardCalculator.cs`: finite reward breakdown for step validation.
- `trainer-client/rumble_env_client/summaries.py`: operator status formatting.
- `trainer-client/scripts/run_full_validation.py`: live validation checks.

## Remaining Broken Or Uncertain

- The staged orchestrator is the single automatic owner, but low-level scene operations still live in the large `TrainingFoundation` class. Further extraction is cleanup work, not a runtime blocker.
- The preserved Loader actor is a bootstrap player with `PlayerController`; no live `PlayerMovement`, `PlayerHealth`, `StructureSpawner`, or configured summon component was found under it.
- The floor collider and actor placement are live-confirmed by an upward-facing surface sample and a second direct support ray against the same selected collider. A game-specific grounded-state flag is still unavailable.
- The live interaction probe observed paired collider-bounds overlap, but neither injected Unity collision nor trigger callbacks fired. Damage and game combat are unconfirmed.
- Full second-actor creation remains unsafe without a lifecycle-safe prefab or game spawn manager.

## Preserved Systems

- `TrainingEnvironmentManager`
- `ObservationBuilder`
- `ActionExecutor`
- `RewardCalculator`
- `TrainingBridgeServer`
- trainer-client/operator compatibility
- monitor camera
- existing dump/log infrastructure
- existing exploration helpers
- existing Gym transition and training scene build code as staged callbacks/fallback behavior

## Replaced Or Contained

- Automatic scan/load/build/unload decisions in `TrainingFoundation.OnUpdate()` should be owned by a staged orchestrator when staged mode is enabled.
- Legacy behavior should remain available behind a controlled fallback switch until staged startup is verified live.
- Heavy exploration probes should stay manual or gated.

## New Staged Bootstrap Plan

Stages:

- `Uninitialized`
- `InitialInventory`
- `RequestGymLoad`
- `WaitForGymLoaded`
- `RemoveLoaderScene`
- `GymInventory`
- `DiscoverPrimaryActor`
- `DiscoverActorCapabilities`
- `ProbeSingleActorSummons`
- `ProbeMoveModifiers`
- `ProbeMultiActorSupport`
- `ProbeActorInteraction`
- `BuildMinimalArena`
- `Ready`
- `Failed`

Initial implementation plan:

- Add `TrainingBootstrapOrchestrator`, `TrainingBootstrapStage`, and `TrainingBootstrapState`.
- Let `TrainingFoundation` keep low-level scene operations, but call them through orchestrator-owned stage transitions.
- Add a passive inventory mode so scene reports can be written without triggering Gym load or training scene build.
- Expose bootstrap stage/status fields through `TrainingEnvironmentManager.GetBridgeStatus()`.
- Show bootstrap stage in the operator.
- Keep legacy automatic path available only when staged bootstrap is disabled or fallback is explicitly enabled.

## Implemented Staged Bootstrap Surface

Current implementation state:

- `mod/TrainingBootstrapOrchestrator.cs` owns the staged state machine and writes `bootstrap_stage_*.json` reports on transitions.
- `TrainingFoundation.Initialize()` creates the orchestrator when `UseStagedBootstrap` is enabled and no longer starts the legacy `ScanAllScenes(..., allowBootstrapActions: true)` path at the same time.
- `TrainingFoundation.ScanAllScenes(reason, allowBootstrapActions)` now supports passive inventory; `allowBootstrapActions: false` writes a scene bundle and returns `TrainingBootstrapScanResult` without Gym load, training scene build, or Loader cleanup side effects.
- Passive inventory now writes `scene_inventory_<timestamp>.json` and `latest_scene_inventory.json` in addition to the existing `scene_bundle_<timestamp>.json`.
- Scene inventory reports include scene validity/load/active state, likely scene role, root names, candidate player/support/environment roots, root active state, layer, tag, component type summaries, score, classification, matching hints, and suggested action.
- `DiscoverPrimaryActor` now writes `actor_discovery_<timestamp>.json` and `latest_actor_discovery.json` passively before arena build. It records the selected actor root, scene, confidence score, root components, head/hand paths, candidate input/move/summon/modifier/attack/health components, warnings, and missing required pieces.
- `DiscoverActorCapabilities` now writes `capability_discovery_<timestamp>.json` and `latest_capability_discovery.json` passively before arena build. It records actor and global capability candidates, reflected field/property/method summaries, risk levels, matched hints, and suggested probes without invoking gameplay methods.
- `BuildMinimalArena` now writes `arena_build_report_<timestamp>.json` and `latest_arena_build_report.json` with source/training scenes, actor paths, moved/kept/destroyed/created roots, manager initialization result, floor selection/placement evidence, background candidates, and explicit remaining verification warnings.
- A successful arena build is followed by passive `bootstrap-post-arena-inventory`; `Ready` is published only if that inventory still confirms Gym and the actor, and the final `activeScene`/`loadedScenes` fields include the training scene without an operator-triggered refresh.
- Arena floor placement samples a bounded grid against only the selected floor collider, requires an upward-facing surface hit, and places the actor just above the nearest valid hit. A second bounded support ray must hit that same collider before `usableFloorConfirmed` becomes true; sample and confirmation points/distances are recorded in the arena report.
- Legacy automatic periodic scan/load/build cleanup is gated behind `UseStagedBootstrap` and `EnableLegacyBootstrapFallback`.
- Manual hotkeys are retained. `F7` and staged scene-load callbacks use passive inventory while staged mode is active.
- `TrainingEnvironmentManager.GetBridgeStatus()` now exposes `bootstrapStage`, `bootstrapReady`, `bootstrapFailed`, `bootstrapFailureReason`, `activeScene`, `loadedScenes`, `gymLoaded`, `loaderRemoved`, `primaryActorFound`, `arenaBuilt`, passive discovery status, probe status defaults, `latestDumpPath`, and `latestDumpPaths`.
- `TrainingBridgeServer` now starts before the training scene is ready so `status` can report staged bootstrap progress while observation, reset, step, and legacy debug probe requests remain scene-ready gated.
- The bridge exposes operator-triggered bootstrap diagnostics: `get_bootstrap_report`, `run_scene_inventory`, `run_actor_discovery`, `run_capability_discovery`, `run_single_actor_summon_probe`, `run_move_probe`, `run_multi_actor_probe`, `run_actor_interaction_probe`, and `run_arena_rebuild`.
- Bootstrap mode is controlled by `UseStagedBootstrap`, `EnableLegacyBootstrapFallback`, `EnableFullSceneHierarchyDump`, `EnableExplorationProbes`, `EnableArenaPruning`, `NoPruneActorValidationMode`, `EnableActorCloneProbes`, `EnableSummonProbes`, `EnableMoveProbes`, and `EnableActorInteractionProbes`.
- Full hierarchy logging is disabled by default for normal startup. Passive JSON scene inventory remains enabled and can still be requested through the bridge or `F7`.
- Active probe diagnostic requests are config-gated. With default probe gates off, summon, move, multi-actor, and interaction requests write explicit `disabled_by_config` reports and do not invoke gameplay methods.
- The summon probe allows one exact active `Il2CppRUMBLE.MoveSystem.StructureSpawner.Spawn()` candidate with a non-null configured structure. It snapshots loaded-scene objects before and after, requires an observed new or pool-activated structure-like object, distinguishes actor-bound confirmation from scene-spawner partial evidence, and attempts `ReturnToPool()` cleanup.
- The move report applies one small `Il2CppRUMBLE.Players.Subsystems.PlayerMovement.Move(UnityEngine.Vector2)` sample, sends zero input, measures actor displacement, and records `Reposition(UnityEngine.Vector3, UnityEngine.Quaternion)` cleanup. Modifier `Execute(...)` candidates are listed passively as `unsafe_not_invoked` when their required processor/configuration context is unavailable.
- The multi-actor report does not clone an active actor root. It documents the duplicate-lifecycle risk, creates one mod-owned dummy target, verifies target movement independently from the primary actor, and schedules cleanup. A successful report says `dummy_target_confirmed`, not full second actor.
- The interaction report launches one mod-owned rigidbody toward one mod-owned dummy target and records `OnCollisionEnter`, `OnTriggerEnter`, and per-frame paired collider-bounds overlap separately. It never invokes damage, health, hit, ownership, or RPC methods, and always keeps `damageEvidence=false` unless a future probe adds direct state-change evidence.
- `trainer-client/rumble_env_client/summaries.py` shows the staged bootstrap state in the operator.
- `trainer-client/scripts/run_full_validation.py` fails with a concrete bootstrap reason if staged status is missing/failed/not ready, required dumps are absent or invalid JSON, actor transforms are incomplete, capability discovery is not passive, manager initialization is false, or floor support is not confirmed.

Verified on the final normal-mode run:

- `dotnet build mod\AI_Train.csproj -c Debug` passed with zero warnings and zero errors; the built and deployed DLL hashes matched.
- RUMBLE started without crashing and logged staged transitions from `Uninitialized` through `Ready`.
- Build-settings candidate `Assets/Scenes/Master Scenes/Gym/Gym.unity`, index `1`, score `1000`, loaded additively and was confirmed by inventory.
- Scene inventories recorded `Loader` index `0` first, `Loader` plus `Gym` while waiting, only active `Gym` after Loader cleanup, and active `AI_Train_Training` with both final scenes loaded before `Ready`.
- Loader actor `LOGIC/BootLoaderPlayer` was preserved before Loader roots were stripped and scene `Loader` was unloaded.
- Actor discovery validated `BootLoaderPlayer` with strong `PlayerController` evidence and distinct head/hand roots.
- `ObservationBuilder` resolved `BootLoaderPlayer/Headset Offset/Headset`, `BootLoaderPlayer/Left Controller/IkTarget/InteractionHand`, and `BootLoaderPlayer/Right Controller/IkTarget/InteractionHand`.
- Typed passive capability discovery produced `394` candidates without generic fallback: `19` actor-source, `48` summon-hint, `48` move-hint, `48` modifier-hint, `24` ownership-hint, `41` damage/hit-hint, and `48` input/gesture-hint entries.
- The arena preserved Gym floor collider `SCENE/GYM_Collission/Collission combat floor` with score `880`, sampled an upward-facing surface, placed the actor at `(-3.0971565, 6.53971, -2.4678192)`, and confirmed support with a second collider ray. The report records `usableFloorConfirmed=true`.
- Normal startup wrote zero full-hierarchy log lines, while JSON scene inventories remained available.
- Live active-probe artifacts recorded: summon `no_safe_candidate`, move/modifier `no_safe_candidate`, multi-actor `dummy_target_confirmed`, and interaction `contact_confirmed` at evidence level `collider_bounds_overlap`. Follow-up inventories found no probe roots.
- After restoring probe gates to false, all four bridge probe requests returned `disabled_by_config` without gameplay invocation.
- The operator connected to the live bridge and displayed `Ready`, Gym loaded, Loader removed, actor confirmed, arena built, and dump path fields.
- `python scripts\run_offline_validation.py` passed all `23` offline checks.
- `python scripts\run_full_validation.py` passed with the strengthened dump-content gate. Report: `trainer-client/runs/20260703_132714_run_full_validation_079e4d250a/validation_report.json`.
- The validation child runs contain valid `metadata.json` and JSONL: scripted `4` steps, random policy `40` steps, stability `20` cycles and `60` steps.

## Stage Acceptance Criteria

Stage 0:

- This document exists.
- It names actual files/classes and current flow.
- It labels uncertain behavior as uncertain.
- Mod build and offline validation still pass.

Stage 1:

- Mod builds.
- Startup logs show staged bootstrap starting and reaching `Ready`.
- Bridge status reports `bootstrapStage=Ready`.
- Old automatic bootstrap does not run in parallel with staged mode.
- Game startup is live-verified without a crash.

Stage 2:

- Logs explicitly say staged or legacy mode.
- Only one automatic bootstrap path is active by default.
- Manual hotkeys remain available or are documented.

Stages 3-7 and 12-16:

- Passed with live inventory, actor, capability, arena, bridge/operator, and validation evidence.

Stages 8-11:

- Stage 8 is a verified failure: no active configured `StructureSpawner` candidate was available, so no summon method was invoked.
- Stage 9 is a verified failure: the selected actor exposed no exact `PlayerMovement.Move(Vector2)` or loaded modifier `Execute` candidate.
- Stage 10 passed only through the documented dummy-target fallback; full second actor remains unconfirmed.
- Stage 11 passed at contact-only level through collider-bounds overlap; Unity callbacks, damage, and combat remain unconfirmed.

## Remaining Unknowns

- Whether exact reflection fallbacks `PerformStartupGymLoad()` or `DoTransitionToGym()` work; the verified build-settings path succeeds first.
- Whether another game state exposes a complete local actor with `PlayerMovement`, `PlayerHealth`, and configured `StructureSpawner`.
- The safe processor/configuration context for modifier `Execute(...)` methods.
- A lifecycle-safe full second-actor spawn path that does not steal local input, camera, or networking ownership.
- Why injected `OnCollisionEnter` and `OnTriggerEnter` callbacks remained silent while paired collider bounds overlapped.
- Direct grounded state, damage, health-change, hit-event, ownership-transfer, and real combat evidence.

## Actor Completeness Status

The current target state is Outcome B unless new live evidence proves otherwise: `BootLoaderPlayer` is the selected actor and is treated as a partial tracking rig. It has head and hand transforms and the bridge can move those hands, but the actor completeness reports must not imply a complete local character when renderer count is zero, visible model evidence is absent, root motion is unconfirmed, and actor-bound movement, physics/grounding, health, ownership, summon context, and real summon are missing.

`Ready` in the operator means Bootstrap Ready, not playable actor ready. The operator/status view should show Actor Complete as false through `actorCompletenessClassification=partial_tracking_rig`, `onlyGhostHandsDetected=true`, `hasVisibleModel=false`, `hasMovementSystem=false`, `hasPhysicsOrGrounding=false`, `hasHealth=false`, `hasOwnership=false`, `hasSummonContext=false`, `realSummonConfirmed=false`, and `rootMotionConfirmed=false`.

The real summon probe report is required even when no invocation is safe. A blocked report is valid when it records the missing owner/init/spawner context, `generatedObjectCount=0`, and `realSummonConfirmed=false`.

Next exact goal after this work: discover and trigger the complete local-player lifecycle after Gym load, without breaking the now-green staged bootstrap, then re-run actor completeness and summon context discovery.

## First Manual Test Commands

Build:

```powershell
dotnet build mod\AI_Train.csproj -c Debug
```

Offline validation:

```powershell
cd trainer-client
python scripts/run_offline_validation.py
```

Operator:

```powershell
cd trainer-client
python scripts/operator_console.py
```

Live validation after RUMBLE is running:

```powershell
cd trainer-client
python scripts/run_full_validation.py
```

Files to inspect after live startup:

- `UserData/AI_Train/runtime_*.log`
- `UserData/AI_Train/Dumps/bootstrap_stage_*.json`
- `UserData/AI_Train/Dumps/latest_scene_inventory.json`
- `UserData/AI_Train/Dumps/scene_inventory_*.json`
- `UserData/AI_Train/Dumps/latest_actor_discovery.json`
- `UserData/AI_Train/Dumps/actor_discovery_*.json`
- `UserData/AI_Train/Dumps/latest_capability_discovery.json`
- `UserData/AI_Train/Dumps/capability_discovery_*.json`
- `UserData/AI_Train/Dumps/latest_single_actor_summon_probe.json`
- `UserData/AI_Train/Dumps/latest_actor_completeness.json`
- `UserData/AI_Train/Dumps/latest_local_player_lifecycle_discovery.json`
- `UserData/AI_Train/Dumps/latest_summon_context_discovery.json`
- `UserData/AI_Train/Dumps/latest_real_summon_probe.json`
- `UserData/AI_Train/Dumps/latest_actor_pruning_comparison.json`
- `UserData/AI_Train/Dumps/single_actor_summon_probe_*.json`
- `UserData/AI_Train/Dumps/latest_move_probe.json`
- `UserData/AI_Train/Dumps/move_probe_*.json`
- `UserData/AI_Train/Dumps/latest_multi_actor_probe.json`
- `UserData/AI_Train/Dumps/multi_actor_probe_*.json`
- `UserData/AI_Train/Dumps/latest_actor_interaction_probe.json`
- `UserData/AI_Train/Dumps/actor_interaction_probe_*.json`
- `UserData/AI_Train/Dumps/latest_arena_build_report.json`
- `UserData/AI_Train/Dumps/arena_build_report_*.json`
- `UserData/AI_Train/Dumps/scene_bundle_*.json`
- `UserData/AI_Train/Dumps/training_status_*.json`
