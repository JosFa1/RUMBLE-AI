from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Iterable, Tuple

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, load_runtime_config


Action = Tuple[str, dict]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a short action sequence against the RUMBLE training bridge.")
    add_common_args(parser, include_action_duration=True)
    return parser.parse_args()


def print_json(label: str, payload: dict) -> None:
    print(f"\n== {label} ==")
    print(json.dumps(payload, indent=2, sort_keys=True))


def build_actions(duration_ms: int) -> Iterable[Action]:
    return [
        (
            "neutral",
            {
                "leftHandTargetLocal": [-0.2, 1.1, 0.35],
                "rightHandTargetLocal": [0.2, 1.1, 0.35],
                "durationMs": duration_ms,
            },
        ),
        (
            "forward",
            {
                "leftHandTargetLocal": [-0.2, 1.05, 0.85],
                "rightHandTargetLocal": [0.2, 1.05, 0.85],
                "durationMs": duration_ms,
            },
        ),
        (
            "up",
            {
                "leftHandTargetLocal": [-0.2, 1.45, 0.35],
                "rightHandTargetLocal": [0.2, 1.45, 0.35],
                "durationMs": duration_ms,
            },
        ),
        (
            "apart",
            {
                "leftHandTargetLocal": [-0.55, 1.1, 0.35],
                "rightHandTargetLocal": [0.55, 1.1, 0.35],
                "durationMs": duration_ms,
            },
        ),
    ]


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    failure_count = 0

    try:
        print_json("status", client.status())
        print_json("observation", client.get_observation())

        for label, action in build_actions(config.action_duration_ms):
            response = client.step(action)
            print_json(f"step:{label}", response)
            info = response.get("info")
            if isinstance(info, dict):
                breakdown = info.get("rewardBreakdown")
                if isinstance(breakdown, dict):
                    left_after = breakdown.get("leftDistanceAfter")
                    right_after = breakdown.get("rightDistanceAfter")
                    if isinstance(left_after, (int, float)) and isinstance(right_after, (int, float)):
                        reward = response.get("reward")
                        reward_display = f"{reward:.6f}" if isinstance(reward, (int, float)) else "none"
                        print(f"{label}: distances(left={left_after:.4f}, right={right_after:.4f}) reward={reward_display}")
            if response.get("error"):
                failure_count += 1
    except BridgeError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    return 0 if failure_count == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
