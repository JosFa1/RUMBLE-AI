# Manual Validation Checklist

Use this checklist for a real RUMBLE session. The operator console is the preferred path; raw scripts remain available when isolating one endpoint.

## 1. Build The Mod

Command:

```powershell
dotnet build mod\AI_Train.csproj -c Debug
```

Expected pass signs: build succeeds, `mod\bin\Debug\net6.0\AI_Train.dll` exists, and there are no compile errors.

Failure signs: missing .NET SDK, missing MelonLoader references, missing RUMBLE install, or compile errors.

## 2. Copy Or Install The Mod

If the post-build copy did not place the DLL automatically, copy `mod\bin\Debug\net6.0\AI_Train.dll` into the RUMBLE `Mods` folder.

Expected pass signs: the current DLL is present in `RUMBLE\Mods`.

Failure signs: stale DLL, wrong install path, or copy permission errors.

## 3. Start RUMBLE

Launch RUMBLE normally through Steam with MelonLoader installed.

Expected pass signs: the log contains `TrainingFoundation initialized.` and later training-scene setup messages.

Failure signs: no `AI_Train` log lines, startup crash, or MelonLoader load errors.

## 4. Confirm Bridge Log

Open the current MelonLoader or game log.

Expected pass signs:

- `Training scene ready:`
- `TrainingBridgeServer listening on 127.0.0.1:8765.`

Failure signs: `sceneReady=false`, `playerRootFound=false`, port already in use, or no listener message.
Bootstrap failure signs: `bootstrapFailed=true`, a stalled `bootstrapStage`, missing `latestDumpPath`, or no `bootstrap_stage_*.json` files under `UserData\AI_Train\Dumps\`. Discovery-stage failures should also leave `latest_actor_discovery.json` or `latest_capability_discovery.json`.

## 5. Run The Operator App

Command:

```powershell
cd trainer-client
python scripts/operator_console.py
```

Use menu option `1` for status, `2` for observation, `3` for reset, and `4` for a safe step.
Use menu option `13` for bootstrap diagnostics such as scene inventory, actor discovery, and capability discovery reports.

Expected pass signs: status shows `bootstrapStage=Ready`, `bootstrapReady=true`, `gymLoaded=true`, `primaryActorFound=true`, `arenaBuilt=true`, `sceneReady=true`, `playerRootFound=true`, protocol `0.3`, and no last error. The live validator must also parse the current scene/actor/capability/arena dumps, confirm exact head/hand paths, passive capability discovery, `managerInitialized=true`, `usableFloorConfirmed=true`, and a finite safe-step reward.

Failure signs: connection refused, timeout, protocol mismatch, missing player root, or step error.

## 6. Run Gated Exploration Probes

Leave all active probe gates false for normal startup. To test a probe, set only its matching constant in `mod\TrainingFoundation.cs` to `true`, rebuild, relaunch RUMBLE, and use operator menu `13`:

- option `5`: single summon; wait for `summonProbeStatus` to leave `running`, then inspect `latest_single_actor_summon_probe.json`
- option `6`: move/modifier; wait for `moveProbeStatus` to leave `running`, then inspect `latest_move_probe.json`
- option `7`: multi-actor feasibility; expect at most `dummy_target_confirmed`, then inspect `latest_multi_actor_probe.json`
- option `8`: interaction; expect `collision_confirmed`, `contact_confirmed`, or evidence-backed `failed`, always with `damageEvidence=false`, then inspect `latest_actor_interaction_probe.json`

Expected pass signs: every request records one bounded attempt, evidence is explicit, cleanup is recorded, and the game remains responsive. `partial`, `no_safe_candidate`, `unsafe_not_invoked`, and evidence-backed `failed` are valid exploration outcomes; they are not feature success.

Failure signs: status remains `running`, more than one candidate is invoked, probe objects accumulate, the actor keeps moving after the move probe, damage is claimed without state evidence, or RUMBLE becomes unstable.

## 7. Run Offline Validation

Command:

```powershell
python scripts/run_offline_validation.py
```

Expected pass signs: JSON output has `"passed": true` and the final line is `PASS`.

Failure signs: missing docs, stale script references, generated files tracked by git, protocol version drift, or broken imports.

## 8. Run Live Validation

Command:

```powershell
python scripts/run_full_validation.py
```

Expected pass signs: final line is `PASS`, a run folder is created, and `validation_report.json` is saved.

Failure signs: connection failure, `bootstrapFailed=true`, `bootstrapReady=false`, `sceneReady=false`, malformed request checks fail, safe step reward is not finite, clamped step lacks clamp info, or stability run fails.

## 9. Run Scripted Sequence

Operator menu: `5. Run scripted pose sequence`

Script equivalent:

```powershell
python scripts/run_scripted_pose_sequence.py --episode-length 5 --action-duration-ms 100
```

Expected pass signs: exit code `0`, `failureCount=0`, and a run folder with `steps.jsonl`.

Failure signs: hand paths missing, action not applied when not already at target, movement blocked, or no target progress.

## 10. Run Stability Test

Operator menu: `7. Run short bridge stability test`

Script equivalent:

```powershell
python scripts/run_bridge_stability.py --cycles 10 --steps-per-cycle 3
```

Expected pass signs: exit code `0`, no failures or timeouts, and `cycles.jsonl` written.

Failure signs: intermittent timeouts, reset hangs, repeated bridge errors, or nonzero failure counts.

## 11. Inspect Logs

Run folders are under `trainer-client/runs/`.

Expected files:

- `metadata.json`
- `steps.jsonl`
- `cycles.jsonl` for stability runs
- `validation_report.json` for full validation

Expected pass signs: JSON and JSONL parse cleanly, rewards are finite, step observations are present, and paths are not user-specific absolute paths.

Failure signs: missing files, truncated rows, raw console text inside JSONL, NaN or Infinity rewards, or stale absolute local paths.
