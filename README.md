# AI_Train

AI_Train is the current RUMBLE training bridge project. The mod hosts a localhost TCP bridge inside the game, and the `trainer-client` folder contains Python probes, rollout scripts, and validation tooling for that bridge.

## Project layout

- `mod/`: the MelonLoader mod project that builds `AI_Train.dll`
- `trainer-client/`: Python client, probes, rollout scripts, and validation scripts
- `protocol/`: transport documentation, schemas, and the manual validation checklist

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

## Known limitations

- The bridge is single-scene, single-actor, and single-step-at-a-time.
- Reset remains a partial actor reset; the player root is preserved.
- Reward shaping is meant for bridge validation, not final training quality.
- Live game validation still depends on a local RUMBLE install and a working mod loader environment.

## What “done” means for this stage

This stage is complete when:

- generated run outputs and build artifacts are not tracked
- protocol docs, schemas, mod code, and Python config all agree on protocol version `0.3`
- `status`, `get_observation`, `reset_episode`, and `step` behave consistently
- `step` blocks until the requested action window completes, or returns a safe protocol error
- offline validation passes from a clean checkout
- the human operator can reproduce live validation with the documented commands
