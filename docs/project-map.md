# RUMBLE AI Project Map

This repository is a RUMBLE environment bridge workspace. It is not a model-training project yet.

## Repository Shape

- `mod/` contains the MelonLoader mod. The project file is `mod/AI_Train.csproj`, and `mod/Core.cs` is the main mod entry point.
- `trainer-client/` contains the Python operator, bridge client, probes, rollout scripts, validation scripts, config, and generated run logs.
- `protocol/` contains protocol documentation, JSON schemas, and the manual live validation checklist.
- `docs/` contains human-facing project notes, including this architecture map.

## Mod Architecture

The mod builds a runtime training scene, locates the playable actor, exposes bridge endpoints, builds observations, executes safe hand-target actions, computes validation rewards, and logs bridge state through MelonLoader.

Important mod files:

- `mod/Core.cs`: MelonLoader entry point and lifecycle wiring.
- `mod/TrainingFoundation.cs`: lifecycle wiring, hotkeys, low-level scene operations, and staged bootstrap integration.
- `mod/TrainingBootstrapOrchestrator.cs`: staged bootstrap state machine for inventory, Gym load confirmation, Loader cleanup, passive actor/capability discovery, minimal arena build, ready, and failed states.
- `mod/TrainingActorLocator.cs`: player actor discovery.
- `mod/TrainingEnvironmentManager.cs`: current scene, episode, player-root, and status state.
- `mod/TrainingBridgeServer.cs`: localhost TCP server and request dispatch.
- `mod/TrainingBridgeContracts.cs`: response and request DTOs.
- `mod/ObservationBuilder.cs`: observation payload builder.
- `mod/ActionExecutor.cs`: safe action execution and reset handling.
- `mod/RewardCalculator.cs`: validation-oriented reward breakdown.
- `mod/TrainingProtocol.cs`: current protocol version.
- `mod/TrainingExplorationService.cs`: retained legacy debug report support.
- `mod/TrainingExplorationProbeService.cs`: bounded manual summon, movement, dummy-target, and interaction probes.
- `mod/ActorLifecycleReportService.cs`: passive lifecycle timeline, trigger discovery, mode comparison, trigger probe, actor ranking, and missing-dependency reports for Actor Ready work.
- `mod/TrainingRuntimeTools.cs`: runtime host, monitor camera, and probe contact recorder.

## Trainer Client Architecture

The trainer client uses only the Python standard library. Its main human-facing entry point is:

```powershell
cd trainer-client
python scripts/operator_console.py
```

Important package files:

- `trainer-client/config.json`: host, port, timeout, episode, action, safe-bounds, logging, and protocol settings.
- `trainer-client/rumble_env_client/client.py`: TCP JSON client and protocol-version checking.
- `trainer-client/rumble_env_client/config.py`: config loading, overrides, safe bounds, and config saving.
- `trainer-client/rumble_env_client/env.py`: Gym-style wrapper stub for reset and step.
- `trainer-client/rumble_env_client/logging.py`: per-run metadata and JSONL logging.
- `trainer-client/rumble_env_client/actions.py`: shared safe, scripted, random, and stability actions.
- `trainer-client/rumble_env_client/summaries.py`: concise operator summaries for status, observations, steps, and config.
- `trainer-client/rumble_env_client/validation.py`: shared subprocess runners for validation scripts.
- `trainer-client/rumble_env_client/console.py`: interactive operator console.

## TCP Communication

The bridge listens on loopback, `127.0.0.1:8765` by default. Each connection sends one newline-delimited JSON request and receives one newline-delimited JSON response. The current protocol version is `0.3`.

Implemented request types are:

- `status`
- `get_observation`
- `reset_episode`
- `step`
- `debug_probe`
- `get_bootstrap_report`
- `retry_bootstrap`
- `run_scene_inventory`
- `run_actor_discovery`
- `run_capability_discovery`
- `run_actor_completeness`
- `run_lifecycle_timeline`
- `run_local_player_lifecycle_discovery`
- `run_lifecycle_trigger_discovery`
- `run_lifecycle_mode_comparison`
- `run_lifecycle_trigger_probe`
- `run_actor_candidate_ranking`
- `run_missing_lifecycle_dependency_report`
- `run_summon_context_discovery`
- `run_single_actor_summon_probe`
- `run_move_probe`
- `run_multi_actor_probe`
- `run_actor_interaction_probe`
- `run_arena_rebuild`

Responses include `protocolVersion`, `requestType`, an endpoint-specific payload, and either `error: null` or a structured protocol error.

## Operator Entry Point

The recommended app is the Python operator console. It lets an operator connect, see bridge status, staged bootstrap state, get observations, reset, send a safe step, run a scripted sequence, run a random policy test, run a short stability test, run validation, inspect config, edit basic config, and locate the latest run folder.

Low-level scripts are still useful for debugging individual endpoints:

- `probe_status.py`
- `probe_observation.py`
- `probe_reset.py`
- `probe_step_once.py`
- `probe_step_sequence.py`
- `probe_reward_sequence.py`
- `probe_debug.py`

Rollout and validation scripts remain available when automation is preferable to the menu:

- `run_scripted_pose_sequence.py`
- `run_random_policy.py`
- `run_bridge_stability.py`
- `run_milestone_demo.py`
- `run_offline_validation.py`
- `run_full_validation.py`

## Generated Files

Generated files are expected under:

- `trainer-client/runs/`
- `mod/bin/`
- `mod/obj/`
- Python cache folders such as `__pycache__/`

Only `trainer-client/runs/.gitkeep` is tracked under the run directory. Run metadata uses paths relative to `trainer-client/` where possible.

## Stable Workflow

1. Build the mod with `dotnet build mod\AI_Train.csproj -c Debug`.
2. Launch RUMBLE with the mod installed.
3. Wait for the mod log to show the bridge listening on loopback.
4. Run `cd trainer-client && python scripts/operator_console.py`.
5. Use the console status check, observation, reset, safe step, and validation commands.
6. Inspect generated run folders under `trainer-client/runs/`.

Offline validation does not require the game:

```powershell
cd trainer-client
python scripts/run_offline_validation.py
```

Full validation requires RUMBLE and the mod bridge to already be running:

```powershell
cd trainer-client
python scripts/run_full_validation.py
```
