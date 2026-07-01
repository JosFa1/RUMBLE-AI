from __future__ import annotations

import argparse
import json
import math
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, Tuple

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, create_run_logger, load_runtime_config


Action = Tuple[str, Dict[str, Any]]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a repeatable scripted pose sequence against the RUMBLE bridge.")
    add_common_args(parser, include_episode_length=True, include_action_duration=True)
    return parser.parse_args()


def build_sequence(duration_ms: int) -> Iterable[Action]:
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
        (
            "neutral",
            {
                "leftHandTargetLocal": [-0.2, 1.1, 0.35],
                "rightHandTargetLocal": [0.2, 1.1, 0.35],
                "durationMs": duration_ms,
            },
        ),
    ]


def maybe_distances(response: Dict[str, Any]) -> str:
    info = response.get("info")
    if not isinstance(info, dict):
        return ""

    breakdown = info.get("rewardBreakdown")
    if not isinstance(breakdown, dict):
        return ""

    left_after = breakdown.get("leftDistanceAfter")
    right_after = breakdown.get("rightDistanceAfter")
    if isinstance(left_after, (int, float)) and isinstance(right_after, (int, float)):
        return f"distances(left={left_after:.4f}, right={right_after:.4f})"

    return ""


def reward_value(response: Dict[str, Any]) -> float:
    reward = response.get("reward")
    if isinstance(reward, (int, float)):
        return float(reward)
    return 0.0


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    logger = create_run_logger("run_scripted_pose_sequence", config)

    total_reward = 0.0
    step_count = 0
    failure_count = 0
    step_time_ms_total = 0.0
    episode_id = 0

    try:
        reset_response = client.reset()
        print(json.dumps(reset_response, indent=2, sort_keys=True))
        episode_id = int(reset_response.get("episodeId", 0))
        print(f"Reset episodeId={episode_id} resetMode={reset_response.get('resetMode')} runDir={logger.run_dir}")

        sequence = list(build_sequence(config.action_duration_ms))
        repeats = max(1, math.ceil(config.episode_length / len(sequence)))

        for repeat_index in range(repeats):
            for label, action in sequence:
                if step_count >= config.episode_length:
                    break

                started_at = time.perf_counter()
                response = client.step(action)
                elapsed_ms = (time.perf_counter() - started_at) * 1000.0
                step_time_ms_total += elapsed_ms
                step_count += 1
                total_reward += reward_value(response)

                observation = response.get("observation")
                if isinstance(observation, dict):
                    episode_id = int(observation.get("episodeId", episode_id))
                    step_index = int(observation.get("episodeStep", step_count))
                else:
                    step_index = step_count

                if response.get("error"):
                    failure_count += 1

                logger.record_step(
                    episode_id=episode_id,
                    step_index=step_index,
                    timestamp=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                    action=action,
                    observation=observation,
                    reward=response.get("reward"),
                    terminated=bool(response.get("terminated", False)),
                    truncated=bool(response.get("truncated", False)),
                    info=response.get("info"),
                    error=response.get("error"),
                    step_time_ms=elapsed_ms,
                )

                print(
                    f"repeat={repeat_index + 1} step={step_count} pose={label} reward={reward_value(response):.6f} "
                    f"{maybe_distances(response)} stepTimeMs={elapsed_ms:.2f}"
                )

                if response.get("terminated") or response.get("truncated"):
                    break

            if step_count >= config.episode_length:
                break

    except BridgeError as exc:
        logger.finish(
            status="failed",
            error=str(exc),
            summary={
                "totalReward": total_reward,
                "stepCount": step_count,
                "failureCount": failure_count,
                "averageStepTimeMs": (step_time_ms_total / step_count) if step_count else None,
            },
        )
        print(str(exc), file=sys.stderr)
        return 1
    except Exception as exc:
        logger.finish(status="failed", error=str(exc))
        raise
    else:
        average_step_time_ms = (step_time_ms_total / step_count) if step_count else 0.0
        summary = {
            "totalReward": total_reward,
            "averageReward": (total_reward / step_count) if step_count else 0.0,
            "stepCount": step_count,
            "failureCount": failure_count,
            "averageStepTimeMs": average_step_time_ms,
            "episodeId": episode_id,
        }
        status = "success" if failure_count == 0 else "failed"
        logger.finish(status=status, summary=summary)
        print(
            f"\nSummary: totalReward={summary['totalReward']:.6f} averageReward={summary['averageReward']:.6f} "
            f"stepCount={summary['stepCount']} failureCount={summary['failureCount']} "
            f"averageStepTimeMs={summary['averageStepTimeMs']:.2f}"
        )
        print(f"Run log: {logger.run_dir}")
        return 0 if failure_count == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
