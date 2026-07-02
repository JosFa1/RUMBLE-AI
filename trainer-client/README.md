# trainer-client

`trainer-client` is the Python side of the RUMBLE training bridge. It talks to the mod over plain TCP on `127.0.0.1:8765` by default, using one newline-delimited JSON request per connection and one JSON response per connection.

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

## Start the bridge

1. Launch RUMBLE with the mod installed.
2. Wait for the mod log to show the bridge listening on `127.0.0.1:8765` or your configured host and port.
3. Keep the game running while you execute the live probes or validation scripts.

## Scripts

Probe scripts:

```powershell
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
