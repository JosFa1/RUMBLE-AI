# AGENTS.md

This repository is for building a stable training environment for the game
RUMBLE using a MelonLoader mod plus a Python trainer client.

The project goal is not model training yet. The goal is to make the game
environment reliable, inspectable, and repeatable so later ML work can plug in
without changing the foundation.

## Project goals

- Find and isolate the player actor in the live game.
- Keep the training scene stable and repeatable.
- Expose a small localhost bridge for status, observation, reset, and step.
- Keep the Python client simple and deterministic.
- Add a Gym-style wrapper shape only as an interface stub for future ML work.
- Produce logs and JSONL artifacts for every meaningful run.
- Verify behavior in the running game before calling work complete.

## Current working model

- `mod/` contains the Unity and MelonLoader bridge code.
- `protocol/` contains the bridge contract and JSON schemas.
- `trainer-client/` contains probes, rollouts, the wrapper, and run logging.
- `trainer-client/runs/` stores per-run artifacts.

## Engineering loop

When working on this project, follow this loop:

1. Inspect the current state first.
2. Make the smallest change that moves the real goal forward.
3. Run the relevant script or live verification against the running game.
4. Inspect the Unity runtime log, Python console output, and saved run files.
5. Fix integration issues, mismatched schemas, or crash conditions.
6. Rerun the affected checks until the behavior is stable.
7. Only mark a goal complete when the requirement is verified in practice.

## Verification rule

Code that looks correct is not enough. A milestone is only complete when the
current state proves it.

For each requested requirement, look for direct evidence such as:

- a live bridge response from the running game
- a successful probe or rollout command
- a run directory with valid `metadata.json`, `steps.jsonl`, and, when needed,
  `cycles.jsonl`
- matching protocol docs and schemas
- a Unity runtime log showing the expected scene, player, and bridge state

If any required artifact is missing, stale, or only indirectly implied, the
goal is not complete yet.

## Practical completion checks

- For bridge work, run `probe_status.py`, `probe_observation.py`,
  `probe_reset.py`, and `probe_step_once.py`.
- For stability work, run `run_bridge_stability.py` with a small cycle count
  first, then a larger one.
- For wrapper/demo work, run `run_milestone_demo.py` and confirm it completes
  from one command.
- For protocol changes, update the docs and schemas at the same time as the
  runtime behavior.

## Guardrails

- Do not add PyTorch, Gymnasium, or training code before the environment work
  is verified.
- Do not assume scene or player layout. Verify from the live game and logs.
- Do not widen the scope to unrelated features when a concrete integration issue
  is the real blocker.
- Do not call a goal done just because the code compiles.
- Do not change protocol shapes in the client or docs without matching runtime
  behavior.

## Useful commands

- `python scripts/probe_status.py`
- `python scripts/probe_observation.py`
- `python scripts/probe_reset.py`
- `python scripts/probe_step_once.py`
- `python scripts/run_bridge_stability.py --cycles 10 --steps-per-cycle 3`
- `python scripts/run_bridge_stability.py --cycles 100 --steps-per-cycle 3`
- `python scripts/run_milestone_demo.py --steps 8`

## Definition of done

A milestone is done only when:

- the requested files exist
- the code runs against the live game
- the run artifacts are saved and parse cleanly
- the runtime log shows the expected bridge behavior
- the README or protocol docs reflect the current workflow

