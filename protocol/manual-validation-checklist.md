# Manual validation checklist

Use this checklist when you want to validate the bridge against a real RUMBLE session. Run the commands from the repository root unless a step says otherwise.

## 1. Build mod

- Command: `dotnet build mod\AI_Train.csproj -c Debug`
- Expected output: build succeeds and produces `mod\bin\Debug\net6.0\AI_Train.dll`
- Expected mod log line: none yet
- Failure signs: missing SDK, missing MelonLoader references, compile errors
- Likely fix area: `mod/AI_Train.csproj`, `.NET SDK` install, `RUMBLE_ROOT`

## 2. Copy AI_Train.dll to Mods folder

- Command: copy `mod\bin\Debug\net6.0\AI_Train.dll` into your RUMBLE `Mods` folder if the post-build step did not do it
- Expected output: `AI_Train.dll` present under `RUMBLE\Mods`
- Expected mod log line: none yet
- Failure signs: stale DLL in `Mods`, wrong game install, copy permission errors
- Likely fix area: `mod/AI_Train.csproj` post-build destination or local install layout

## 3. Start RUMBLE

- Command: launch the game normally through Steam
- Expected output: game reaches a scene where the mod can initialize
- Expected mod log line: `TrainingFoundation initialized.`
- Failure signs: game crashes on startup, mod loader never initializes
- Likely fix area: mod loader install, DLL compatibility, game environment

## 4. Open logs

- Command: open the MelonLoader or game log for the current session
- Expected output: log file updates live while the game runs
- Expected mod log line: `TrainingEnvironmentManager status[...]`
- Failure signs: no AI_Train lines at all
- Likely fix area: mod load failure, DLL copy failure

## 5. Confirm mod loaded

- Command: inspect the log after startup
- Expected output: AI_Train initialization messages appear
- Expected mod log line: `TrainingFoundation initialized.` and `Melon scene callback loaded: ...`
- Failure signs: AI_Train missing from the logs
- Likely fix area: `Mods` folder contents, MelonLoader load errors

## 6. Confirm training scene built

- Command: wait for the actor discovery and training scene setup to complete
- Expected output: the bridge sees a ready player root sourced from the Gym scene, and the spectator camera logs that it attached
- Expected mod log line: `Building training scene from source scene 'Gym' using gym candidate 'Player Controller(Clone)'`
- Expected mod log line: `Training scene ready: source='Gym', runtime='AI_Train_Training', actor='Player Controller(Clone)'.`
- Failure signs: repeated not-ready states, no player root found
- Likely fix area: `TrainingFoundation`, `TrainingActorLocator`, `TrainingEnvironmentManager`

## 7. Confirm bridge listening on loopback

- Command: inspect the log once the environment is ready
- Expected output: loopback listener starts on the configured port
- Expected mod log line: `TrainingBridgeServer listening on 127.0.0.1:8765.`
- Failure signs: host rejected for not being loopback, port already in use, no listener start
- Likely fix area: `TrainingBridgeServer`, environment variables, local port conflicts

## 8. Run probe_status.py

- Command: `cd trainer-client && python scripts/probe_status.py`
- Expected output: JSON with `sceneReady: true` and `playerRootFound: true`
- Expected mod log line: `TrainingBridgeServer served status request.`
- Failure signs: connection refused, protocol error, `sceneReady: false`
- Likely fix area: bridge startup, port config, training scene readiness

## 9. Run probe_observation.py

- Command: `cd trainer-client && python scripts/probe_observation.py`
- Expected output: JSON response with `type: "observation"` and an `observation` payload
- Expected mod log line: `TrainingBridgeServer served observation request.`
- Failure signs: missing observation fields, null payload, timeout
- Likely fix area: `ObservationBuilder`, bridge observation queue, scene readiness

## 10. Run probe_reset.py

- Command: `cd trainer-client && python scripts/probe_reset.py`
- Expected output: `reset_result` with `episodeStep: 0`
- Expected mod log line: `TrainingBridgeServer served reset request.`
- Failure signs: reset error, stale episode step, hand reset warnings every run
- Likely fix area: `ActionExecutor.ResetEpisodeState`, `TrainingEnvironmentManager.ResetEpisode`

## 11. Run probe_step_once.py

- Command: `cd trainer-client && python scripts/probe_step_once.py`
- Expected output: `step_result` with finite `reward` and a nontrivial `info.elapsedMs`
- Expected mod log line: `ActionExecutor step completed:` and `TrainingBridgeServer served step request.`
- Failure signs: step returns almost instantly, reward is NaN, no observation after step
- Likely fix area: `ActionExecutor`, `RewardCalculator`, `TrainingBridgeServer`

## 12. Run run_scripted_pose_sequence.py

- Command: `cd trainer-client && python scripts/run_scripted_pose_sequence.py --episode-length 5 --action-duration-ms 100`
- Expected output: script exits `0`, reports distinct left/right paths, and finishes with `failureCount=0`
- Expected mod log line: repeated step completion lines without protocol errors, while the spectator camera continues to follow the agent
- Failure signs: broken imports, bridge errors, no log output
- Likely fix area: `trainer-client/rumble_env_client`, script argument handling, step flow

For a human-visible camera check, use `python scripts/run_scripted_pose_sequence.py --episode-length 10 --action-duration-ms 500`. Press `F6` to toggle follow/free-fly camera mode.

## 13. Run run_random_policy.py

- Command: `cd trainer-client && python scripts/run_random_policy.py --episodes 5 --episode-length 3 --seed 42`
- Expected output: script exits `0` and writes a run folder with `steps.jsonl`
- Expected mod log line: repeated step completion lines with finite rewards
- Failure signs: random action crashes, timeouts, malformed logs
- Likely fix area: safe bounds config, step handling, JSONL logging

## 14. Run run_bridge_stability.py with 10 cycles

- Command: `cd trainer-client && python scripts/run_bridge_stability.py --cycles 10 --steps-per-cycle 3`
- Expected output: exit `0` and `cycles.jsonl` written
- Expected mod log line: reset and step requests continue serving without fatal bridge errors
- Failure signs: intermittent timeouts, reset hangs, socket failures
- Likely fix area: bridge queueing, reset cancellation, step serialization

## 15. Run run_bridge_stability.py with 100 cycles

- Command: `cd trainer-client && python scripts/run_bridge_stability.py --cycles 100 --steps-per-cycle 3`
- Expected output: exit `0` with no accumulated failures or timeouts
- Expected mod log line: periodic debug summaries with stable request handling
- Failure signs: late-cycle hangs, port exhaustion, memory growth, step time drift
- Likely fix area: `TrainingBridgeServer`, connection cleanup, action completion flow

## 16. Run run_full_validation.py

- Command: `cd trainer-client && python scripts/run_full_validation.py`
- Expected output: trailing `PASS` and a `validation_report.json` inside the run folder
- Expected mod log line: safe protocol errors for malformed requests plus normal reset/step servicing
- Failure signs: failure exit codes, missing report, raw protocol checks failing
- Likely fix area: error envelopes, request parsing, concurrent reset/step behavior

## 17. Inspect JSONL logs

- Command: inspect `trainer-client/runs/<run-id>/steps.jsonl` and `cycles.jsonl`
- Expected output: one valid JSON object per line
- Expected mod log line: none required
- Failure signs: truncated rows, mixed text and JSON, absolute user paths in metadata
- Likely fix area: `trainer-client/rumble_env_client/logging.py`, script writers

## 18. Confirm observations change after actions

- Command: compare pre-step and post-step hand positions from probe and rollout logs
- Expected output: hand positions move toward targets across successful steps
- Expected mod log line: `ActionExecutor step completed:` with realistic elapsed time
- Failure signs: positions stay frozen, only one-frame nudges, identical observations across long steps
- Likely fix area: `ActionExecutor.Pump`, step completion semantics, coordinate conversions

## 19. Confirm reward is finite

- Command: inspect step responses and JSONL logs
- Expected output: every reward is a finite number and `rewardBreakdown.totalReward` matches `reward`
- Expected mod log line: step completed with numeric reward
- Failure signs: NaN or Infinity in reward fields
- Likely fix area: `RewardCalculator`, missing-hand handling, distance sanitization

## 20. Confirm malformed requests do not crash the game

- Command: rely on `run_full_validation.py` or manually send malformed JSON over localhost
- Expected output: bridge returns `type: "error"` with a protocol-safe error code
- Expected mod log line: request rejected with `malformed_request`, `empty_request`, or `unknown_request_type`
- Failure signs: broken socket without error payload, game crash, listener stops responding
- Likely fix area: `TrainingBridgeServer.ReadRequestAsync`, request parsing, client handler exception flow

## 21. Confirm the game can close cleanly

- Command: close RUMBLE after validation
- Expected output: no hang during shutdown and no repeated dispose errors
- Expected mod log line: no fatal shutdown exceptions
- Failure signs: stuck process, repeated bridge errors during exit
- Likely fix area: `TrainingBridgeServer.Dispose`, resource cleanup, pending request cancellation
