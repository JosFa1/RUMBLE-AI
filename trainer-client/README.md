# trainer-client

Minimal localhost probes and rollout scripts for the RUMBLE training bridge.

The client talks to the mod over plain TCP on `127.0.0.1:8765` by default.
Runtime settings live in `config.json` and can be overridden on the command line.

Config fields:

- `host`
- `port`
- `timeoutMs`
- `episodeLength`
- `actionDurationMs`
- `logDirectory`
- `safeHandBounds`
- `protocolVersion`

Run logs are written under `runs/` as one folder per execution. Each run gets a
`metadata.json` file plus a step-by-step `steps.jsonl` file.

## Start the bridge

1. Launch RUMBLE from Steam with the mod installed.
2. Wait for the training log to show the bridge listening on `127.0.0.1:8765`.
3. Keep the game running while you use the client scripts.

## Probes

```bash
python scripts/probe_status.py
python scripts/probe_observation.py
python scripts/probe_reset.py
python scripts/probe_step_once.py
python scripts/probe_step_sequence.py
python scripts/probe_reward_sequence.py
```

## Rollouts

Scripted pose sequence:

```bash
python scripts/run_scripted_pose_sequence.py --episode-length 5 --action-duration-ms 50
```

Random safe policy:

```bash
python scripts/run_random_policy.py --episodes 5 --episode-length 3 --seed 42
```

Bridge stability sweep:

```bash
python scripts/run_bridge_stability.py --cycles 10 --steps-per-cycle 3
python scripts/run_bridge_stability.py --cycles 100 --steps-per-cycle 3
```

Milestone demo:

```bash
python scripts/run_milestone_demo.py --steps 8
```

All scripts use the shared config loader, print bridge results to the console,
and write JSONL logs for later inspection.

## What success looks like

- `probe_status.py` returns a live bridge status with `sceneReady=True` and `playerRootFound=True`.
- `probe_observation.py`, `probe_reset.py`, and `probe_step_once.py` all return valid JSON without bridge errors.
- `run_bridge_stability.py --cycles 100 --steps-per-cycle 3` finishes with zero failures and zero timeouts.
- `run_milestone_demo.py` completes from one command, prints status plus per-step rewards, and writes a JSONL run log.

## Where logs are saved

- Python run logs: `trainer-client/runs/<timestamp>_<script>_<runid>/`
- Each run directory contains `metadata.json` and `steps.jsonl`
- Stability runs also write `cycles.jsonl`
- Game-side bridge logs stay under `RUMBLE/UserData/AI_Train/`

## If the bridge will not connect

- Confirm RUMBLE is open and the mod DLL is loaded.
- Check the game log for `TrainingBridgeServer listening on 127.0.0.1:8765`.
- Make sure `config.json` still points at the right `host` and `port`.
- If the status probe times out, restart the game and try again after the training scene loads.
