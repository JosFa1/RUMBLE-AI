# Training Protocol v0.3

This protocol exposes a small localhost bridge for trainer tooling. The bridge
uses plain TCP with one JSON request per connection and one JSON response per
connection. Each payload is newline-delimited UTF-8 JSON.

## Transport

- Host: `127.0.0.1` by default
- Port: `8765` by default
- Override with environment variables on the mod side:
  - `AI_TRAIN_BRIDGE_HOST`
  - `AI_TRAIN_BRIDGE_PORT`
- Override from the trainer side with the Python client flags:
  - `--host`
  - `--port`

## Request format

Each request is a UTF-8 JSON object followed by a newline.

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

The bridge currently accepts four request types: `status`, `get_observation`,
`reset_episode`, and `step`.

Every bridge response includes `protocolVersion`. Normal responses may also
carry `error: null`, while protocol-level failures use the dedicated error
envelope.

## Status request

Request:

```json
{"type":"status"}
```

Response:

```json
{
  "protocolVersion": "0.3",
  "sceneReady": true,
  "trainingSceneName": "AI_Train_Training",
  "playerRootFound": true,
  "episodeId": 2,
  "episodeStep": 0,
  "tick": 12345,
  "timeSeconds": 12.3456,
  "lastError": null
}
```

The status response is a direct snapshot of the current training environment.

## Observation request

Request:

```json
{"type":"get_observation"}
```

Successful response:

```json
{
  "type": "observation",
  "protocolVersion": "0.3",
  "observation": {
    "protocolVersion": "0.3",
    "tick": 12345,
    "timeSeconds": 12.3456,
    "sceneReady": true,
    "episodeId": 2,
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
  }
}
```

If observation building fails, the bridge returns:

```json
{
  "type": "error",
  "protocolVersion": "0.3",
  "requestType": "get_observation",
  "error": {
    "code": "scene_not_ready",
    "message": "Training scene is not ready."
  }
}
```

## Reset request

Request:

```json
{"type":"reset_episode"}
```

Successful response:

```json
{
  "type": "reset_result",
  "protocolVersion": "0.3",
  "episodeId": 2,
  "observation": {
    "protocolVersion": "0.3",
    "tick": 12345,
    "timeSeconds": 12.3456,
    "sceneReady": true,
    "episodeId": 2,
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

The bridge increments the episode id, clears the step count, clears pending
action state, and best-effort snaps the hands back to a neutral pose. The
current implementation returns `partial` because the root transform is
preserved.

## Step request

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
  "observation": {},
  "reward": 0.12,
  "terminated": false,
  "truncated": false,
  "info": {
    "reward": 0.12,
    "rewardBreakdown": {
      "leftDistanceBefore": 0.27,
      "leftDistanceAfter": 0.21,
      "rightDistanceBefore": 0.27,
      "rightDistanceAfter": 0.21,
      "leftProgress": 0.06,
      "rightProgress": 0.06,
      "leftReward": 0.06,
      "rightReward": 0.06,
      "bothHandsNearBonus": 0.0,
      "clampPenalty": 0.0,
      "noProgressPenalty": 0.0,
      "totalReward": 0.12,
      "bothHandsNearTarget": false,
      "noProgress": false
    }
  },
  "error": null
}
```

The `info` object records whether the hands were found, whether movement was
clamped or blocked, and the reward breakdown for the step. Reward increases
when the hands move closer to their targets and includes a small bonus when
both hands end up near target.

## Error response format

Malformed JSON, unknown request types, and other bridge-level failures return a
safe error envelope:

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

The `error` object always includes:

- `code`
- `message`
- `details` when extra context is available

Common error codes include:

- `empty_request`
- `unknown_request_type`
- `scene_not_ready`
- `observation_unavailable`
- `observation_timeout`
- `serialization_failed`
- `observation_failed`
- `bridge_disposed`
- `internal_error`

Reset-specific failures may also report:

- `reset_timeout`
- `reset_failed`
- `reset_rejected`
- `action_executor_unavailable`
- `player_root_missing`

Step-specific failures may also report:

- `malformed_step_request`
- `step_timeout`
- `step_failed`
- `action_executor_unavailable`
- `player_root_missing`
- `invalid_action`

## Known limitations

- The bridge currently targets a single training actor, not multi-agent
  matchmaking or concurrent episodes.
- Reset is partial: the current implementation preserves the root transform.
- Observation and step handling happen on the Unity main thread.
- Requests are one-at-a-time over a short-lived TCP connection.
- Reward logic is heuristic and is meant for trainer bring-up, not final
  learning quality.
