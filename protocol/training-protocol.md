# Training Protocol v0.3

The RUMBLE bridge exposes a localhost TCP protocol for validation and trainer bring-up. Each connection carries exactly one newline-delimited UTF-8 JSON request and one newline-delimited UTF-8 JSON response.

## Transport

- Host: `127.0.0.1` by default
- Port: `8765` by default
- Mod-side overrides:
  - `AI_TRAIN_BRIDGE_HOST`
  - `AI_TRAIN_BRIDGE_PORT`
- Trainer-side overrides:
  - `--host`
  - `--port`
  - `trainer-client/config.json`

The server only binds to loopback.

## Runtime scene behavior

- The mod creates a dedicated `AI_Train_Training` scene at runtime.
- In no-headset PC sessions, the staged bootstrap loads the game's built-in `Gym` scene additively after the normal Loader does not advance.
- The verified training actor is the Loader-created `BootLoaderPlayer`. It is preserved before Loader cleanup, validated through strong `PlayerController` plus distinct head/hand evidence, and then moved into the training scene.
- Required Gym geometry, lighting, probes, floor, and support roots remain in Gym. The arena builder moves the actor, preserves classified support/environment roots, and removes only explicit clutter.
- A PC spectator camera is managed by the mod for observation; it is not part of the wire protocol.

## Protocol version

- Current version: `0.3`
- Responses always include `protocolVersion`
- Requests do not need to include `protocolVersion`
- The Python client compares the observed response version to its configured expected version and warns on mismatch by default

Matching schemas:

- `protocol/schemas/action-v0.3.json`
- `protocol/schemas/observation-v0.3.json`
- `protocol/schemas/response-v0.3.json`

## Request types

Implemented requests:

- `status`
- `get_observation`
- `reset_episode`
- `step`
- `debug_probe`
- `get_bootstrap_report`
- `retry_bootstrap`
- `run_scene_inventory`
- `run_actor_discovery`
- `run_capability_discovery`
- `run_actor_completeness`
- `run_local_player_lifecycle_discovery`
- `run_summon_context_discovery`
- `run_single_actor_summon_probe`
- `run_move_probe`
- `run_multi_actor_probe`
- `run_actor_interaction_probe`
- `run_arena_rebuild`

Examples:

```json
{"type":"status"}
```

```json
{"type":"get_observation"}
```

```json
{"type":"reset_episode"}
```

```json
{
  "type": "step",
  "action": {
    "leftHandTargetLocal": [-0.18, 1.14, 0.42],
    "rightHandTargetLocal": [0.18, 1.14, 0.42],
    "durationMs": 100
  }
}
```

`leftHandTargetLocal` and `rightHandTargetLocal` are expressed in player-root-local coordinates. `durationMs` is clamped to a safe maximum of `1000`.

## Debug probe response

Request:

```json
{"type":"debug_probe"}
```

Successful response:

```json
{
  "type": "debug_probe_result",
  "protocolVersion": "0.3",
  "requestType": "debug_probe",
  "sceneReady": true,
  "playerRootFound": true,
  "trainingSceneName": "AI_Train_Training",
  "playerRootPath": "BootLoaderPlayer",
  "probeHostReady": true,
  "camera": {
    "freeFlyEnabled": true,
    "targetFound": true,
    "targetPath": "BootLoaderPlayer/Head",
    "cameraName": "AI_Train_MonitorCamera",
    "cameraPosition": { "x": 0.0, "y": 2.0, "z": -6.0 },
    "cameraRotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 }
  },
  "types": [],
  "warnings": [],
  "error": null
}
```

`debug_probe` is local-only and is intended for discovery work on the training scene. It returns a structured report of likely summon, kick, camera, and test-loop entry points, plus any coroutine-backed test sequence that was started.

Bootstrap diagnostic requests are local-only operator controls. `get_bootstrap_report` returns the cached staged bootstrap state. `retry_bootstrap` clears a non-ready staged failure and restarts at `InitialInventory`; it is a no-op with `already_ready` when the environment is already usable. `run_scene_inventory`, `run_actor_discovery`, and `run_capability_discovery` enqueue passive Unity-main-thread report generation and write the corresponding dump files. The summon, move, multi-actor, and interaction requests are config-gated and return `disabled_by_config` without side effects by default. When enabled, a request may return `running`; poll `status` until the corresponding probe status changes, then inspect the reported timestamped file and its `latest_*.json` alias. Summon success requires observed structure-like object evidence, move success requires measured actor displacement, and multi-actor feasibility currently confirms only a dummy target. Interaction distinguishes direct Unity collision/trigger callbacks from weaker paired collider-bounds overlap; none of these contact levels confirms damage or game combat. `run_arena_rebuild` is a manual arena build/rebuild request and should be used only during validation.

`run_actor_completeness` passively inventories renderers, head/hands/body-root evidence, rigidbodies, character controllers, colliders, floor support, and categorized gameplay-system candidates. `run_local_player_lifecycle_discovery` searches all loaded scenes and `DontDestroyOnLoad` roots for a better local-player lifecycle candidate. `run_summon_context_discovery` searches loaded components outside the selected actor for actor-bound, manager, pool, gesture, stack, and unsafe network summon paths. These three requests do not invoke gameplay, ownership, damage, or network methods.

`bootstrapReady` and `sceneReady` mean that the staged scene and bridge workflow are usable. They do not mean the selected actor is a complete RUMBLE character. Actor usability is reported separately through `actorMode`, `actorCompletenessClassification`, `hasVisibleModel`, `rendererCount`, `hasBody`, `hasHead`, `hasHands`, `hasMovementSystem`, `hasPhysicsOrGrounding`, `hasHealth`, `hasOwnership`, `hasSummonContext`, `realSummonConfirmed`, `rootMotionConfirmed`, `handMotionConfirmed`, `onlyGhostHandsDetected`, `currentBestActorPath`, `currentBestActorScene`, `latestActorCompletenessReport`, `latestLocalPlayerLifecycleDiscoveryReport`, `latestSummonContextReport`, `latestRealSummonProbeReport`, and `latestPruningComparisonReport`.

The current expected incomplete state is `actorCompletenessClassification=partial_tracking_rig`: BootLoaderPlayer has head and hand transforms and ActionExecutor can move the hands, but no visible model, actor-bound movement system, actor-side physics/grounding, health, ownership, actor-bound summon context, root motion, or real summon has been confirmed. A blocked `latest_real_summon_probe.json` is valid when it explains the missing ownership/init context and reports `realSummonConfirmed=false`.

## Response shape

Successful responses use a consistent top-level shape:

- `type`
- `protocolVersion`
- `requestType`
- payload fields for that response
- `error: null`

Protocol failures use:

- `type: "error"`
- `protocolVersion`
- `requestType`
- `error.code`
- `error.message`
- `error.details` when available

## Required and optional fields

Required response fields are defined by `protocol/schemas/response-v0.3.json`. Required observation fields are defined by `protocol/schemas/observation-v0.3.json`. Required action fields are defined by `protocol/schemas/action-v0.3.json`.

The Python client and operator tolerate missing optional fields, but validation treats missing required fields as a protocol failure. Optional fields may be `null` when the game rig does not expose the corresponding data, such as health or hand transforms.

## Status response

Request:

```json
{"type":"status"}
```

Response:

```json
{
  "type": "status_result",
  "protocolVersion": "0.3",
  "requestType": "status",
  "bridgeRunning": true,
  "sceneReady": true,
  "sourceSceneName": "Gym",
  "trainingSceneName": "AI_Train_Training",
  "actorName": "Player Controller(Clone)",
  "playerRootPath": "Player Controller(Clone)",
  "playerRootFound": true,
  "bootstrapStage": "Ready",
  "bootstrapReady": true,
  "bootstrapFailed": false,
  "bootstrapFailureReason": null,
  "gymLoaded": true,
  "loaderRemoved": true,
  "loaderInert": false,
  "primaryActorFound": true,
  "arenaBuilt": true,
  "activeScene": "AI_Train_Training",
  "loadedScenes": ["Gym", "AI_Train_Training"],
  "actorDiscoveryStatus": "confirmed",
  "capabilityDiscoveryStatus": "complete",
  "summonProbeStatus": "not_run",
  "moveProbeStatus": "not_run",
  "multiActorProbeStatus": "not_run",
  "actorInteractionProbeStatus": "not_run",
  "latestDumpPath": "UserData\\AI_Train\\Dumps\\bootstrap_stage_20260703_120000_Ready.json",
  "latestDumpPaths": [
    "UserData\\AI_Train\\Dumps\\scene_bundle_20260703_115959.json",
    "UserData\\AI_Train\\Dumps\\bootstrap_stage_20260703_120000_Ready.json"
  ],
  "episodeId": 7,
  "episodeStep": 0,
  "tick": 12345,
  "timeSeconds": 12.3456,
  "lastRequestType": "status",
  "lastReward": null,
  "lastError": null,
  "error": null
}
```

`status` is a snapshot of cached bridge and environment state. It includes bridge telemetry for the most recent request and staged bootstrap telemetry such as the current stage, loaded scenes, Gym/load/actor/arena flags, passive discovery status, probe status, and recent dump paths. It does not touch Unity objects outside the manager's cached telemetry.

## Observation response

Request:

```json
{"type":"get_observation"}
```

Successful response:

```json
{
  "type": "observation",
  "protocolVersion": "0.3",
  "requestType": "get_observation",
  "observation": {
    "protocolVersion": "0.3",
    "tick": 12345,
    "timeSeconds": 12.3456,
    "observationSpace": "world",
    "actionTargetSpace": "player_root_local",
    "sceneReady": true,
    "episodeId": 7,
    "episodeStep": 0,
    "rootPosition": { "x": 0.0, "y": 0.0, "z": 0.0 },
    "rootRotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 },
    "headPosition": null,
    "headRotation": null,
    "leftHandPosition": null,
    "leftHandRotation": null,
    "rightHandPosition": null,
    "rightHandRotation": null,
    "health": null,
    "error": null,
    "warnings": []
  },
  "error": null
}
```

Coordinate semantics:

- observation positions and rotations are in world space
- action targets are in player-root-local space

## Reset response

Request:

```json
{"type":"reset_episode"}
```

Successful response:

```json
{
  "type": "reset_result",
  "protocolVersion": "0.3",
  "requestType": "reset_episode",
  "episodeId": 8,
  "observation": {
    "protocolVersion": "0.3",
    "tick": 12380,
    "timeSeconds": 12.8125,
    "observationSpace": "world",
    "actionTargetSpace": "player_root_local",
    "sceneReady": true,
    "episodeId": 8,
    "episodeStep": 0,
    "warnings": []
  },
  "sceneReady": true,
  "resetMode": "partial",
  "warnings": [
    "actor reset is partial; root transform preserved"
  ],
  "error": null
}
```

Reset behavior:

- increments the episode id
- resets `episodeStep` to `0`
- cancels an active step if one is in flight
- clears last reward state
- best-effort snaps hands back to neutral
- preserves the current player root transform

## Step response

Request:

```json
{
  "type": "step",
  "action": {
    "leftHandTargetLocal": [-0.18, 1.14, 0.42],
    "rightHandTargetLocal": [0.18, 1.14, 0.42],
    "durationMs": 100
  }
}
```

Successful response:

```json
{
  "type": "step_result",
  "protocolVersion": "0.3",
  "requestType": "step",
  "observation": {},
  "reward": 0.12,
  "terminated": false,
  "truncated": false,
  "info": {
    "actionApplied": true,
    "leftHandFound": true,
    "rightHandFound": true,
    "leftTargetClamped": false,
    "rightTargetClamped": false,
    "leftMovementBlocked": false,
    "rightMovementBlocked": false,
    "leftHandPath": "BootLoaderPlayer/Left Controller/IkTarget/InteractionHand",
    "rightHandPath": "BootLoaderPlayer/Right Controller/IkTarget/InteractionHand",
    "blockedReason": null,
    "durationMs": 100,
    "elapsedMs": 101.4,
    "leftDistanceBefore": 0.27,
    "leftDistanceAfter": 0.08,
    "rightDistanceBefore": 0.27,
    "rightDistanceAfter": 0.08,
    "leftTargetWorld": { "x": -0.18, "y": 1.14, "z": 0.42 },
    "rightTargetWorld": { "x": 0.18, "y": 1.14, "z": 0.42 },
    "leftTargetLocalClamped": { "x": -0.18, "y": 1.14, "z": 0.42 },
    "rightTargetLocalClamped": { "x": 0.18, "y": 1.14, "z": 0.42 },
    "reachedLeftTarget": false,
    "reachedRightTarget": false,
    "actionWindowCompleted": true,
    "activeStepReplaced": false,
    "reward": 0.12,
    "rewardBreakdown": {
      "leftDistanceBefore": 0.27,
      "leftDistanceAfter": 0.08,
      "rightDistanceBefore": 0.27,
      "rightDistanceAfter": 0.08,
      "leftProgress": 0.19,
      "rightProgress": 0.19,
      "leftReward": 0.19,
      "rightReward": 0.19,
      "bothHandsNearBonus": 0.0,
      "clampPenalty": 0.0,
      "noProgressPenalty": 0.0,
      "totalReward": 0.38,
      "bothHandsNearTarget": false,
      "noProgress": false
    },
    "notes": [
      "left hand moved",
      "right hand moved"
    ]
  },
  "error": null
}
```

Step semantics for this milestone:

- the request is accepted on the TCP background thread and executed on the Unity main thread
- the mod applies the action over the requested duration window
- the bridge does not reply until the action window completes or a safe error occurs
- the returned observation is the post-step observation after the action window
- `info.elapsedMs` is measured from Unity unscaled time, not wall-clock `DateTime`
- reward is computed from before/after distances across the full action window

## Error response

Example:

```json
{
  "type": "error",
  "protocolVersion": "0.3",
  "requestType": "step",
  "error": {
    "code": "malformed_step_request",
    "message": "Step request is missing a valid action.",
    "details": {
      "parseError": "missing_action"
    }
  }
}
```

Common error codes:

- `empty_request`
- `malformed_request`
- `request_timeout`
- `request_too_large`
- `unknown_request_type`
- `bridge_disposed`
- `scene_not_ready`
- `observation_unavailable`
- `observation_timeout`
- `observation_failed`
- `reset_timeout`
- `reset_failed`
- `reset_rejected`
- `action_executor_unavailable`
- `player_root_missing`
- `malformed_step_request`
- `step_timeout`
- `step_canceled_by_reset`
- `step_failed`
- `invalid_action`

Malformed, partial, oversized, unknown, or slow requests must never crash the game. They should return a safe protocol error or, in the timeout case, be canceled cleanly.

## Known limitations

- Single actor, single scene, single active step at a time
- Reset remains partial and preserves the player root transform
- Reward logic is validation-oriented, not final training reward design
- Live behavior still depends on the actual in-game actor rig and mod loader environment
