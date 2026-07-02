from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, load_runtime_config, safe_test_action


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Send one control step to the RUMBLE training bridge.")
    add_common_args(parser, include_action_duration=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    action = safe_test_action(config)

    try:
        status = client.status()
        print(json.dumps(status, indent=2, sort_keys=True))
        observation = client.get_observation()
        print(json.dumps(observation, indent=2, sort_keys=True))
        start = time.perf_counter()
        response = client.step(action)
        elapsed_ms = (time.perf_counter() - start) * 1000.0
    except BridgeError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    print(json.dumps(response, indent=2, sort_keys=True))
    info = response.get("info")
    if isinstance(info, dict):
        breakdown = info.get("rewardBreakdown")
        if isinstance(breakdown, dict):
            left_after = breakdown.get("leftDistanceAfter")
            right_after = breakdown.get("rightDistanceAfter")
            if isinstance(left_after, (int, float)) and isinstance(right_after, (int, float)):
                print(f"distances(left={left_after:.4f}, right={right_after:.4f}) reward={response.get('reward')} stepTimeMs={elapsed_ms:.2f}")
    return 2 if response.get("error") else 0


if __name__ == "__main__":
    raise SystemExit(main())
