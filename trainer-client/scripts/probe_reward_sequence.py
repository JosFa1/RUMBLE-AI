from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import (
    BridgeError,
    add_common_args,
    create_client,
    create_run_logger,
    load_runtime_config,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a short reward comparison sequence.")
    add_common_args(parser, include_action_duration=True)
    return parser.parse_args()


def print_json(label: str, payload: dict) -> None:
    print(f"\n== {label} ==")
    print(json.dumps(payload, indent=2, sort_keys=True))


def extract_reward(response: Dict[str, Any]) -> float | None:
    reward = response.get("reward")
    if isinstance(reward, (int, float)):
        return float(reward)

    info = response.get("info")
    if isinstance(info, dict):
        breakdown = info.get("rewardBreakdown")
        if isinstance(breakdown, dict):
            value = breakdown.get("totalReward")
            if isinstance(value, (int, float)):
                return float(value)

    return None


def format_distances(response: Dict[str, Any]) -> str:
    info = response.get("info")
    if not isinstance(info, dict):
        return "distances=unavailable"

    breakdown = info.get("rewardBreakdown")
    if not isinstance(breakdown, dict):
        return "distances=unavailable"

    left_after = breakdown.get("leftDistanceAfter")
    right_after = breakdown.get("rightDistanceAfter")
    if isinstance(left_after, (int, float)) and isinstance(right_after, (int, float)):
        return f"distances(left={left_after:.4f}, right={right_after:.4f})"

    return "distances=unavailable"


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    logger = create_run_logger("probe_reward_sequence", config)

    try:
        reset_response = client.reset()
        print_json("reset", reset_response)

        rewards: list[tuple[str, float | None]] = []
        failure_count = 0
        actions = [
            (
                "easy",
                {
                    "leftHandTargetLocal": [-0.18, 1.14, 0.42],
                    "rightHandTargetLocal": [0.18, 1.14, 0.42],
                    "durationMs": config.action_duration_ms,
                },
            ),
            (
                "impossible",
                {
                    "leftHandTargetLocal": [3.0, 3.0, 3.0],
                    "rightHandTargetLocal": [3.0, 3.0, 3.0],
                    "durationMs": config.action_duration_ms,
                },
            ),
        ]

        for label, action in actions:
            start = time.perf_counter()
            response = client.step(action)
            elapsed_ms = (time.perf_counter() - start) * 1000.0
            print_json(f"step:{label}", response)
            reward = extract_reward(response)
            reward_display = f"{reward:.6f}" if reward is not None else "none"
            print(f"{label}: reward={reward_display} {format_distances(response)} stepTimeMs={elapsed_ms:.2f}")

            observation = response.get("observation")
            info = response.get("info")
            rewards.append((label, reward))
            if response.get("error"):
                failure_count += 1
            logger.record_step(
                episode_id=int(reset_response.get("episodeId", 0)),
                step_index=int(observation.get("episodeStep", 0)) if isinstance(observation, dict) else 0,
                timestamp=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                action=action,
                observation=observation,
                reward=reward,
                terminated=bool(response.get("terminated", False)),
                truncated=bool(response.get("truncated", False)),
                info=info,
                error=response.get("error"),
                step_time_ms=elapsed_ms,
            )

    except BridgeError as exc:
        logger.finish(status="failed", error=str(exc))
        print(str(exc), file=sys.stderr)
        return 1
    except Exception as exc:
        logger.finish(status="failed", error=str(exc))
        raise
    else:
        easy_reward = next((reward for label, reward in rewards if label == "easy"), None)
        impossible_reward = next((reward for label, reward in rewards if label == "impossible"), None)

        summary = {
            "easyReward": easy_reward,
            "impossibleReward": impossible_reward,
            "failureCount": failure_count,
        }

        if easy_reward is None or impossible_reward is None or failure_count > 0 or easy_reward <= impossible_reward:
            logger.finish(status="failed", summary=summary)
            if easy_reward is not None and impossible_reward is not None:
                print(f"\nReward comparison: easy={easy_reward:.4f} impossible={impossible_reward:.4f}")
                if easy_reward <= impossible_reward:
                    print("Easy target did not outperform the impossible target.", file=sys.stderr)
            else:
                print("\nReward comparison skipped because reward data was missing.")
            print(f"Run log: {logger.run_dir}")
            return 2

        logger.finish(status="success", summary=summary)
        print(f"\nReward comparison: easy={easy_reward:.4f} impossible={impossible_reward:.4f}")
        print(f"Run log: {logger.run_dir}")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
