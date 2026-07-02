from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, create_run_logger, load_runtime_config


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Probe the RUMBLE training bridge debug endpoint.")
    add_common_args(parser)
    return parser.parse_args()


def _error_message(response: dict) -> str | None:
    error = response.get("error")
    if isinstance(error, dict):
        message = error.get("message")
        if isinstance(message, str) and message:
            return message
        code = error.get("code")
        if isinstance(code, str) and code:
            return code
    if isinstance(error, str) and error:
        return error
    return None


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    logger = create_run_logger("probe_debug", config)
    client = create_client(config)
    output_path = logger.run_dir / "debug_probe.json"

    try:
        response = client.debug_probe()
    except BridgeError as exc:
        logger.finish(status="failed", error=str(exc), summary={"endpoint": "debug_probe"})
        print(str(exc), file=sys.stderr)
        return 1

    output_path.write_text(json.dumps(response, indent=2, sort_keys=True), encoding="utf-8")
    print(json.dumps(response, indent=2, sort_keys=True))
    print(f"Saved debug probe: {output_path}")

    error_message = _error_message(response)
    summary = {
        "endpoint": "debug_probe",
        "responseType": response.get("type"),
        "sceneReady": response.get("sceneReady"),
        "playerRootFound": response.get("playerRootFound"),
        "probeHostReady": response.get("probeHostReady"),
        "cameraFreeFlyEnabled": (response.get("camera") or {}).get("freeFlyEnabled") if isinstance(response.get("camera"), dict) else None,
        "typeCount": len(response.get("types") or []) if isinstance(response.get("types"), list) else 0,
        "warningCount": len(response.get("warnings") or []) if isinstance(response.get("warnings"), list) else 0,
        "outputPath": str(output_path.relative_to(ROOT)),
    }

    if error_message:
        logger.finish(status="failed", error=error_message, summary=summary)
        return 2

    logger.finish(status="success", summary=summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
