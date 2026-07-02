from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, load_runtime_config, scripted_pose_sequence


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a short action sequence against the RUMBLE training bridge.")
    add_common_args(parser, include_action_duration=True)
    return parser.parse_args()


def print_json(label: str, payload: dict) -> None:
    print(f"\n== {label} ==")
    print(json.dumps(payload, indent=2, sort_keys=True))


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    failure_count = 0

    try:
        print_json("status", client.status())
        print_json("observation", client.get_observation())

        for label, action in scripted_pose_sequence(config.action_duration_ms)[:-1]:
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
