# Cleanup Report

## Current Project Layout

- `README.md`: root project guide and quick start.
- `run_operator.ps1`: Windows convenience launcher for the operator console.
- `docs/project-map.md`: architecture map for the mod, trainer client, protocol, and workflow.
- `docs/cleanup-report.md`: this cleanup record.
- `mod/AI_Train.csproj`: RUMBLE MelonLoader mod project.
- `mod/*.cs`: bridge, scene management, actor location, action execution, observation, reward, and debug support.
- `protocol/training-protocol.md`: TCP protocol contract.
- `protocol/manual-validation-checklist.md`: concise live validation checklist.
- `protocol/schemas/*.json`: protocol `0.3` schemas.
- `trainer-client/config.json`: safe default trainer-client config.
- `trainer-client/README.md`: Python client and operator guide.
- `trainer-client/rumble_env_client/`: shared Python package.
- `trainer-client/scripts/`: thin operator, probe, rollout, and validation entry points.
- `trainer-client/runs/`: ignored generated run artifacts, with only `.gitkeep` tracked.

## Main User Entry Point

```powershell
cd trainer-client
python scripts/operator_console.py
```

From the repository root, Windows users can also run:

```powershell
.\run_operator.ps1
```

## Main Developer Entry Points

- Build mod: `dotnet build mod\AI_Train.csproj -c Debug`
- Offline validation: `cd trainer-client && python scripts/run_offline_validation.py`
- Full live validation: `cd trainer-client && python scripts/run_full_validation.py`
- Protocol docs: `protocol/training-protocol.md`
- Manual live checklist: `protocol/manual-validation-checklist.md`

## Scripts That Should Stay

- `operator_console.py`: main human-facing app.
- `probe_status.py`, `probe_observation.py`, `probe_reset.py`, `probe_step_once.py`: low-level endpoint probes.
- `probe_step_sequence.py`, `probe_reward_sequence.py`, `probe_debug.py`: targeted debugging probes.
- `run_scripted_pose_sequence.py`: deterministic movement rollout.
- `run_random_policy.py`: bounded random-action smoke test.
- `run_bridge_stability.py`: repeated reset/step stability test.
- `run_milestone_demo.py`: compact wrapper demonstration.
- `run_offline_validation.py`: no-game repo validation.
- `run_full_validation.py`: live bridge validation.

## Deprecated Or Merged Scripts

No scripts were removed in this pass. The operator console is now the recommended entry point, and the individual scripts are documented as debugging or automation tools rather than the normal user flow.

## Files Removed

No source files were deleted. Generated run folders, build output, and Python cache directories remain local but are ignored by git.

## Files Ignored

The ignore rules cover:

- `trainer-client/runs/`
- `**/__pycache__/`
- `*.pyc`
- `.pytest_cache/`, `.mypy_cache/`, `.ruff_cache/`
- `.venv/`, `venv/`
- `build/`, `dist/`, `bin/`, `obj/`, `mod/bin/`, `mod/obj/`
- `*.user`, `*.suo`, IDE folders
- `*.log`, `*.tmp`, `*.bak`, `*.jsonl`
- local validation outputs such as `training_status_*.json` and `validation_report*.json`

## Cleanup Changes Made

- Added a clear operator-first workflow in the root and trainer-client READMEs.
- Added `docs/project-map.md` and this cleanup report.
- Replaced the long manual validation checklist with a shorter operator-first checklist.
- Added shared Python action helpers for safe, scripted, milestone, random, reward, and stability actions.
- Reused shared action helpers across probe and rollout scripts.
- Added concise Python summary helpers for operator status, observation, step, and config output.
- Added shared validation subprocess helpers for operator-triggered validation.
- Added config saving with timestamped backups for safe operator config editing.
- Added stricter offline validation for operator docs, script entry points, script `--help`, ignore rules, generated files, absolute paths, ML dependencies, and protocol version consistency.
- Extended status responses with bridge telemetry used by the operator: `bridgeRunning`, `sourceSceneName`, `actorName`, `playerRootPath`, `lastRequestType`, and `lastReward`.
- Kept the bridge loopback-only and did not add heavy dependencies or training code.

## Remaining Known Issues

- Full live validation requires RUMBLE and the mod bridge to be running; it cannot pass in a headless environment where `127.0.0.1:8765` is closed.
- Some low-level probe scripts intentionally print raw JSON because they are debugging tools.
- `run_full_validation.py` keeps a small raw-socket helper for malformed, empty, and unknown request checks.
- Existing local generated run folders are ignored but not deleted.
- The mod classes remain large; deeper C# decomposition should wait until live behavior can be repeatedly verified.

## Next Recommended Phase

Run the operator against a live RUMBLE session, complete full validation, and then do a narrow live-informed mod cleanup pass. Good candidates are reducing debug-probe log noise, reviewing transform lookup caching in `ObservationBuilder`, and centralizing C# response creation only after live tests confirm no protocol regressions.
