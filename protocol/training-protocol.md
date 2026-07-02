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
- In no-headset PC sessions, the mod directly loads the game's built-in `Gym` scene after the normal loader does not advance.
- The training actor is the Gym-spawned player-controller root. The Loader actor is never accepted as the Gym training actor.
- Gym scene geometry, lighting, probes, and VFX are moved with the actor into the stripped runtime scene.
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
  "sceneReady": true,
  "trainingSceneName": "AI_Train_Training",
  "playerRootFound": true,
  "episodeId": 7,
  "episodeStep": 0,
  "tick": 12345,
  "timeSeconds": 12.3456,
  "lastError": null,
  "error": null
}
```

`status` is a snapshot of cached bridge and environment state. It does not touch Unity objects outside the manager’s cached telemetry.

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
