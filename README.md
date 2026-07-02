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

The console is the recommended operator app. It can check bridge status, show observation summaries, reset, send a safe step, run scripted and random tests, run validation, edit basic config, and print the latest run folder path.

## What do I run?

Run `python scripts/operator_console.py` for normal human operation. Use the individual probe scripts only when debugging a specific endpoint or reproducing a narrow failure.

## Project layout

- `mod/`: the MelonLoader mod project that builds `AI_Train.dll`
- `trainer-client/`: Python operator console, client package, probes, rollout scripts, validation scripts, config, and run logs
- `protocol/`: transport documentation, schemas, and the manual validation checklist
- `docs/project-map.md`: short architecture map of the current workspace
- `docs/cleanup-report.md`: cleanup status, retained scripts, ignored files, and next phase notes

## Current status

The project is an environment and validation bridge, not an ML training stack. The operator console is the main app, protocol version `0.3` is current, generated runs are ignored, and offline validation should pass without RUMBLE running. Full validation still requires a live RUMBLE session with the mod bridge listening on loopback.

## Build the mod

Canonical build command:

```powershell
dotnet build mod\AI_Train.csproj
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

## Troubleshooting

- If the console reports connection refused, start RUMBLE with the mod installed and wait for `TrainingBridgeServer listening on 127.0.0.1:8765`.
- If `sceneReady=false`, wait for the Gym scene setup or inspect `TrainingEnvironmentManager` log lines.
- If `playerRootFound=false`, inspect actor discovery in `TrainingActorLocator` and the current scene logs.
- If protocol warnings appear, keep the mod, `trainer-client/config.json`, docs, and schemas aligned on protocol `0.3`.

## Known limitations

- The bridge is single-scene, single-actor, and single-step-at-a-time.
- Reset remains a partial actor reset; the player root is preserved.
- Reward shaping is meant for bridge validation, not final training quality.
- Live game validation still depends on a local RUMBLE install and a working mod loader environment.

## What not to do yet

- Do not add PyTorch, Gymnasium, self-play, or model training code.
- Do not widen the protocol without updating the mod, client, docs, schemas, and validation together.
- Do not treat offline validation as proof that the live game bridge works.
- Do not commit generated run folders, build output, cache files, or local backups.

## Next recommended work

Run full validation from the operator against a live RUMBLE session, then do a narrow live-informed mod cleanup pass focused on debug logging, transform lookup caching, and response-creation consistency.

## What “done” means for this stage

This stage is complete when:

- generated run outputs and build artifacts are not tracked
- protocol docs, schemas, mod code, and Python config all agree on protocol version `0.3`
- `status`, `get_observation`, `reset_episode`, and `step` behave consistently
- `step` blocks until the requested action window completes, or returns a safe protocol error
- offline validation passes from a clean checkout
- the human operator can reproduce live validation with the documented commands
