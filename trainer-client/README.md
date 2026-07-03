# trainer-client

`trainer-client` is the Python side of the RUMBLE training bridge. It talks to the mod over plain TCP on `127.0.0.1:8765` by default, using one newline-delimited JSON request per connection and one JSON response per connection. The recommended human-facing app is `scripts/operator_console.py`.

## Setup

The client uses the Python standard library only.

From this folder:

```powershell
python --version
python -m compileall .
```

Runtime settings live in `config.json`. The client reads:

- `host`
- `port`
- `timeoutMs`
- `episodeLength`
- `actionDurationMs`
- `logDirectory`
- `safeHandBounds`
- `protocolVersion`
- `strictProtocolVersion`

`strictProtocolVersion=false` is the default. When the server reports a different protocol version, the client warns once instead of crashing. Set it to `true` if you want mismatches to fail hard.

## Operator console

Start the operator app from this folder:

```powershell
python scripts/operator_console.py
```

Useful option:

```powershell
python scripts/operator_console.py --verbose
```

`--verbose` prints raw JSON after the concise summaries. The default keeps output compact so the operator can see status, readiness, player-root state, rewards, errors, and run-log paths without digging through large payloads.

The console menu can connect and status-check, get observations, reset, send one safe step, run a scripted pose sequence, run a random policy test, run a short stability test, run offline or full validation, run bootstrap diagnostics, show config, edit basic config, print the latest run folder, and show troubleshooting help.

## Start the bridge

1. Launch RUMBLE with the mod installed.
2. Wait for the mod log to show the bridge listening on `127.0.0.1:8765` or your configured host and port.
3. Keep the game running while you execute the live probes or validation scripts.

## Scripts

Probe scripts:

```powershell
python scripts/operator_console.py
python scripts/probe_status.py
python scripts/probe_observation.py
python scripts/probe_reset.py
python scripts/probe_step_once.py
python scripts/probe_step_sequence.py
python scripts/probe_reward_sequence.py
python scripts/probe_debug.py
```

What they prove:

- `probe_status.py`: bridge is reachable and the scene reports ready state
- `probe_observation.py`: the observation envelope is valid JSON and includes bridge telemetry
- `probe_reset.py`: reset returns a new observation and clears `episodeStep`
- `probe_step_once.py`: one safe action returns a reward plus post-step observation
- `probe_step_sequence.py`: repeated actions advance the episode step cleanly
- `probe_reward_sequence.py`: reachable targets score better than bad targets and reward stays finite
- `probe_debug.py`: the local exploration hook can inspect likely summon/kick/camera entry points and save the report

Rollout scripts:

```powershell
python scripts/run_scripted_pose_sequence.py --episode-length 5 --action-duration-ms 100
python scripts/run_random_policy.py --episodes 5 --episode-length 3 --seed 42
python scripts/run_bridge_stability.py --cycles 10 --steps-per-cycle 3
python scripts/run_milestone_demo.py --steps 8
```

What they prove:

- `run_scripted_pose_sequence.py`: deterministic hand targets still work end to end
- `run_random_policy.py`: safe bounded actions keep the bridge healthy across many steps
- `run_bridge_stability.py`: repeated reset/step loops do not accumulate protocol failures or timeouts
- `run_milestone_demo.py`: the operator can run a concise milestone walkthrough from one command

Validation scripts:

```powershell
python scripts/run_offline_validation.py
python scripts/run_full_validation.py
```

- `run_offline_validation.py`: import and syntax checks, schema parsing, README command checks, protocol doc coverage, ignore rules, tracked-output checks, and absolute-path scanning
- `run_full_validation.py`: live bridge validation against an already-running game, including malformed request handling, safe and clamped steps, reset behavior, scripted rollout, random rollout, and a short stability run

## Logs

Run logs are written under `runs/` with one folder per execution. Each run folder contains:

- `metadata.json`: run metadata and summary
- `steps.jsonl`: one JSON object per recorded step
- `cycles.jsonl`: stability-only cycle summaries
- `validation_report.json`: full-validation-only report

Paths stored in metadata are relative to `trainer-client/`, so generated logs no longer embed user-specific absolute paths.

## Status fields

- `sceneReady`: the mod has built the runtime training scene.
- `playerRootFound`: actor discovery found the player root accepted for training.
- `bootstrapStage`: the staged bootstrap state, such as `InitialInventory`, `WaitForGymLoaded`, `BuildMinimalArena`, `Ready`, or `Failed`.
- `bootstrapReady` and `bootstrapFailed`: whether the staged bootstrap reached a usable state or a terminal failure.
- `bootstrapFailureReason`: the concrete failure reason reported by the staged orchestrator when it fails.
- `gymLoaded`, `loaderRemoved`, `loaderInert`, `primaryActorFound`, and `arenaBuilt`: milestone flags from the staged bootstrap pipeline.
- `activeScene` and `loadedScenes`: current scene inventory summary reported by the staged bootstrap.
- `actorDiscoveryStatus` and `capabilityDiscoveryStatus`: passive discovery progress before arena build.
- `summonProbeStatus`, `moveProbeStatus`, `multiActorProbeStatus`, and `actorInteractionProbeStatus`: probe states. Normal startup leaves them `not_run`; default-off requests report `disabled_by_config`, and manually enabled probes report their evidence-backed outcome.
- `latestDumpPath` and `latestDumpPaths`: recent mod dump files to inspect after startup or failure, including scene inventory, actor discovery, and capability discovery reports when those stages have run.
- `protocolVersion`: server protocol version; expected client value is in `config.json`.
- `episodeId` and `episodeStep`: current episode counters.
- `lastError`: last manager or bridge error exposed by the mod when available.

## Common errors

- Connection refused: RUMBLE is not running, the mod did not load, or the bridge is not listening on the configured host and port.
- Protocol mismatch: config, docs, schemas, and mod code are not aligned on `0.3`.
- `bootstrapFailed=true`: inspect `bootstrapFailureReason`, `latestDumpPath`, and the `UserData/AI_Train/Dumps/bootstrap_stage_*.json` files. For discovery failures, also inspect `latest_actor_discovery.json` and `latest_capability_discovery.json`.
- `bootstrapReady=false`: wait for the current `bootstrapStage` to advance, then inspect stage dumps if it stalls.
- `sceneReady=false`: the training scene has not been built yet.
- `playerRootFound=false`: the actor locator did not find a suitable player root.
- Timeout: the bridge accepted a request but the game did not complete the work within the configured timeout.

## Offline validation

```powershell
python scripts/run_offline_validation.py
```

Expected result:

- JSON output with `"passed": true`
- a trailing `PASS`

If it fails, start with:

- tracked generated files in git
- schema or README drift
- user-specific absolute paths in committed files
- broken imports or syntax errors

## Full validation

```powershell
python scripts/run_full_validation.py
```

Expected result:

- `status` reports `sceneReady=true` and `playerRootFound=true`
- `status` reports `bootstrapStage=Ready`, `bootstrapReady=true`, `gymLoaded=true`, `primaryActorFound=true`, and `arenaBuilt=true`
- malformed, empty, and unknown requests return safe protocol errors
- `reset_episode` returns `episodeStep=0`
- safe `step` returns a finite reward and a realistic `elapsedMs`
- clamped targets report clamping or a blocked reason
- scripted, random, and stability scripts exit with code `0`
- a trailing `PASS`

## Troubleshooting

- If the client cannot connect, verify the mod log says `TrainingBridgeServer listening on 127.0.0.1:8765`.
- If the protocol version warning appears, update either `trainer-client/config.json` or the mod/protocol docs so they agree on `0.3`.
- If `elapsedMs` is far below the requested duration, the mod is returning too early and `ActionExecutor` / `TrainingBridgeServer` need attention.
- If reset cancels an active step and the step socket hangs, inspect `ActionExecutor.CancelActiveStep` and the bridge step queue handling.
- If live validation times out, confirm the game is actually pumping the bridge on the Unity main thread.
