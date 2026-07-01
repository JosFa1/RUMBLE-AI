from __future__ import annotations

import argparse
import json
import sys

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, load_runtime_config


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Probe the RUMBLE training bridge observation endpoint.")
    add_common_args(parser)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    try:
        response = client.get_observation()
    except BridgeError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    print(json.dumps(response, indent=2, sort_keys=True))
    if response.get("error"):
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
