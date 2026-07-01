from __future__ import annotations

import argparse
import json
import random
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, create_run_logger, load_runtime_config


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a random safe policy against the RUMBLE bridge.")
    add_common_args(parser, include_episode_length=True, include_action_duration=True, include_episodes=True)
    parser.add_argument("--seed", type=int, default=1337, help="Random seed for repeatable runs.")
    return parser.parse_args()


def reward_value(response: Dict[str, Any]) -> float:
    reward = response.get("reward")
    if isinstance(reward, (int, float)):
        return float(reward)
    return 0.0


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    logger = create_run_logger("run_random_policy", config)
    rng = random.Random(args.seed)

    total_reward = 0.0
    step_count = 0
    failure_count = 0
    episode_summaries: list[dict[str, Any]] = []
    step_time_ms_total = 0.0

    try:
        reset_response = client.reset()
        print(json.dumps(reset_response, indent=2, sort_keys=True))
        print(f"Reset episodeId={reset_response.get('episodeId')} resetMode={reset_response.get('resetMode')} runDir={logger.run_dir}")

        for episode_index in range(1, args.episodes + 1):
            if episode_index > 1:
                reset_response = client.reset()
                print(json.dumps(reset_response, indent=2, sort_keys=True))
                print(f"Reset episodeId={reset_response.get('episodeId')} resetMode={reset_response.get('resetMode')} runDir={logger.run_dir}")

            episode_reward = 0.0
            episode_steps = 0
            episode_failures = 0

            for _ in range(max(1, config.episode_length)):
                action = {
                    "leftHandTargetLocal": config.safe_hand_bounds.sample_left(rng),
                    "rightHandTargetLocal": config.safe_hand_bounds.sample_right(rng),
                    "durationMs": config.action_duration_ms,
                }

                started_at = time.perf_counter()
                response = client.step(action)
                elapsed_ms = (time.perf_counter() - started_at) * 1000.0
                step_time_ms_total += elapsed_ms

                step_count += 1
                episode_steps += 1
                reward = reward_value(response)
                total_reward += reward
                episode_reward += reward

                observation = response.get("observation")
                if response.get("error"):
                    failure_count += 1
                    episode_failures += 1

                episode_id = int(reset_response.get("episodeId", 0))
                step_index = step_count
                if isinstance(observation, dict):
                    episode_id = int(observation.get("episodeId", episode_id))
                    step_index = int(observation.get("episodeStep", step_index))

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
                    f"episode={episode_index} step={episode_steps} reward={reward:.6f} "
                    f"stepTimeMs={elapsed_ms:.2f} error={response.get('error')}"
                )

                if response.get("terminated") or response.get("truncated"):
                    break

            episode_summaries.append(
                {
                    "episodeIndex": episode_index,
                    "episodeReward": episode_reward,
                    "episodeSteps": episode_steps,
                    "episodeFailures": episode_failures,
                }
            )
            print(
                f"Episode {episode_index}: totalReward={episode_reward:.6f} "
                f"averageReward={(episode_reward / episode_steps) if episode_steps else 0.0:.6f} "
                f"stepCount={episode_steps} failureCount={episode_failures}"
            )

    except BridgeError as exc:
        logger.finish(
            status="failed",
            error=str(exc),
            summary={
                "totalReward": total_reward,
                "stepCount": step_count,
                "failureCount": failure_count,
                "averageStepTimeMs": (step_time_ms_total / step_count) if step_count else None,
                "episodes": episode_summaries,
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
            "episodes": episode_summaries,
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
