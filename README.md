# AI_Train

AI_Train is the current RUMBLE training bridge project. The mod hosts a localhost TCP bridge inside the game, and the `trainer-client` folder contains the human-facing operator console, Python probes, rollout scripts, and validation tooling for that bridge.

## Quick Start

From the repository root:

```powershell
.\run_operator.ps1
```

Or run it directly:

```powershell
cd trainer-client
python scripts/operator_console.py
```

The console is the recommended operator app. It can check bridge status, show observation summaries, reset, send a safe step, run scripted and random tests, run validation, run bootstrap diagnostics, edit basic config, and print the latest run folder path.

## What do I run?

Run `python scripts/operator_console.py` for normal human operation. Use the individual probe scripts only when debugging a specific endpoint or reproducing a narrow failure.

## Project layout

- `mod/`: the MelonLoader mod project that builds `AI_Train.dll`
- `trainer-client/`: Python operator console, client package, probes, rollout scripts, validation scripts, config, and run logs
- `protocol/`: transport documentation, schemas, and the manual validation checklist
- `docs/project-map.md`: short architecture map of the current workspace
- `docs/ai-bootstrap-rework-notes.md`: staged bootstrap audit, implementation, and acceptance evidence
- `docs/ai-bootstrap-knowledge.md`: claim-labeled facts and remaining exploration goals for AI continuation
- `docs/cleanup-report.md`: cleanup status, retained scripts, ignored files, and next phase notes

## Current status

The project is an environment and validation bridge, not an ML training stack. The staged bootstrap is the default owner, protocol version `0.3` is current, generated runs are ignored, and offline validation passes without RUMBLE running. A live normal-mode run has reached `Ready` with Gym loaded, Loader removed, `BootLoaderPlayer` registered, the minimal arena built, and the full validation suite passing. Reproducing that result still requires a local RUMBLE session with the mod bridge listening on loopback.

## Build the mod

Canonical build command:

```powershell
dotnet build mod\AI_Train.csproj -c Debug
```

Requirements:

- .NET SDK 6 or newer
- RUMBLE installed with MelonLoader assemblies available
- Either `RUMBLE_ROOT` set explicitly, or the default Steam install path resolving to the game directory

The project file resolves references from:

- `$(RUMBLE_ROOT)\MelonLoader\net6`
- `$(RUMBLE_ROOT)\MelonLoader\Il2CppAssemblies`

If `RUMBLE_ROOT` is not set, the project falls back to `%ProgramFiles(x86)%\Steam\steamapps\common\RUMBLE`.

After a successful build, the post-build step attempts to copy `AI_Train.dll` into `$(RUMBLE_ROOT)\Mods`.

## Build verification

Use a clean command line when validating the mod:

```powershell
dotnet build mod\AI_Train.csproj -c Debug
```

Expected result:

- the build completes without compile errors
- `mod\bin\Debug\net6.0\AI_Train.dll` is produced
- the post-build copy succeeds if the target RUMBLE install is available

## Python tooling

From `trainer-client/`:

```powershell
python scripts/operator_console.py
python scripts/probe_status.py
python scripts/probe_observation.py
python scripts/probe_reset.py
python scripts/probe_step_once.py
python scripts/probe_debug.py
python scripts/run_scripted_pose_sequence.py
python scripts/run_random_policy.py
python scripts/run_bridge_stability.py --cycles 10 --steps-per-cycle 3
python scripts/run_offline_validation.py
python scripts/run_full_validation.py
```

## Validation

Offline validation does not require the game:

```powershell
cd trainer-client
python scripts/run_offline_validation.py
```

Live validation requires RUMBLE to already be running with the mod loaded:

```powershell
cd trainer-client
python scripts/run_full_validation.py
```

The full manual operator flow lives in `protocol/manual-validation-checklist.md`.

## Inspect logs

The operator console prints the latest run folder path. Generated logs live under `trainer-client/runs/` and are ignored by git except for `.gitkeep`.

The mod writes staged bootstrap evidence under `UserData\AI_Train\Dumps\`, including `bootstrap_stage_*.json`, scene/actor/capability/arena reports, gated probe reports, `scene_bundle_*.json`, and `training_status_*.json`.

## Troubleshooting

- If the console reports connection refused, start RUMBLE with the mod installed and wait for `TrainingBridgeServer listening on 127.0.0.1:8765`.
- If `bootstrapFailed=true`, inspect `bootstrapFailureReason`, `latestDumpPath`, and the latest `bootstrap_stage_*.json`.
- If `bootstrapReady=false`, wait for `bootstrapStage` to advance or inspect the latest stage dump if it stalls.
- If `sceneReady=false`, wait for the Gym scene setup or inspect `TrainingEnvironmentManager` log lines.
- If `playerRootFound=false`, inspect `latest_actor_discovery.json` and the current scene logs.
- If protocol warnings appear, keep the mod, `trainer-client/config.json`, docs, and schemas aligned on protocol `0.3`.

## Known limitations

- The bridge registers one primary actor and processes one step at a time.
- Reset remains a partial actor reset; the player root is preserved.
- Reward shaping is meant for bridge validation, not final training quality.
- The preserved bootstrap actor currently exposes no live health, movement, or configured summon component.
- Full second-actor support, real summon/modifier execution, and combat damage remain unconfirmed.

## Complete Local-Player Lifecycle

The selected actor before this lifecycle pass is `BootLoaderPlayer` (`confirmed`). It remains treated as a fallback partial tracking rig until a live run proves a better candidate (`confirmed`). The new lifecycle diagnostics write timeline, trigger discovery, lifecycle mode comparison, trigger probe, actor candidate ranking, and missing-dependency reports under `UserData\AI_Train\Dumps` (`confirmed by build/offline validation; live dump creation still requires RUMBLE running`). The trigger probe is passive by default and records zero reflected invocations unless a future report proves a safe local owner/init context (`confirmed`). The next exact live goal is to relaunch RUMBLE, run full validation, inspect the new reports, and then decide whether Loader-held, Gym-only, no-move, or no-prune startup modes are needed (`unconfirmed until live`).

## What not to do yet

- Do not add PyTorch, Gymnasium, self-play, or model training code.
- Do not widen the protocol without updating the mod, client, docs, schemas, and validation together.
- Do not treat offline validation as proof that the live game bridge works.
- Do not commit generated run folders, build output, cache files, or local backups.

## Next recommended work

Discover or transition to a complete local-player actor state with movement, health, gesture, summon, and ownership systems while preserving the now-green staged bootstrap. Re-run passive actor/capability discovery first, then enable only the next bounded summon or movement probe justified by that evidence.

## What "done" means for this stage

This stage is complete when:

- generated run outputs and build artifacts are not tracked
- protocol docs, schemas, mod code, and Python config all agree on protocol version `0.3`
- `status`, `get_observation`, `reset_episode`, and `step` behave consistently
- `status` exposes `bootstrapStage`, `bootstrapReady`, `bootstrapFailed`, loaded scenes, Gym/actor/arena milestone flags, discovery/probe statuses, and latest dump paths
- `step` blocks until the requested action window completes, or returns a safe protocol error
- offline validation passes from a clean checkout
- the human operator can reproduce live validation with the documented commands
