from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, Tuple

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, RumbleEnv, add_common_args, create_run_logger, load_runtime_config


Action = Tuple[str, Dict[str, Any]]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run the Gym-style milestone demo against the RUMBLE bridge.")
    add_common_args(parser, include_action_duration=True)
    parser.add_argument("--steps", type=int, default=8, help="Number of scripted steps to run.")
    return parser.parse_args()


def build_demo_sequence(duration_ms: int) -> Iterable[Action]:
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
                "leftHandTargetLocal": [-0.2, 1.04, 0.82],
                "rightHandTargetLocal": [0.2, 1.04, 0.82],
                "durationMs": duration_ms,
            },
        ),
        (
            "up",
            {
                "leftHandTargetLocal": [-0.2, 1.42, 0.35],
                "rightHandTargetLocal": [0.2, 1.42, 0.35],
                "durationMs": duration_ms,
            },
        ),
        (
            "apart",
            {
                "leftHandTargetLocal": [-0.52, 1.1, 0.35],
                "rightHandTargetLocal": [0.52, 1.1, 0.35],
                "durationMs": duration_ms,
            },
        ),
    ]


def _format_vector(value: Any) -> str:
    if isinstance(value, dict):
        x = value.get("x")
        y = value.get("y")
        z = value.get("z")
        if all(isinstance(v, (int, float)) for v in (x, y, z)):
            return f"({x:.3f}, {y:.3f}, {z:.3f})"
    return str(value)


def _print_json(label: str, payload: Any) -> None:
    print(f"\n== {label} ==")
    print(json.dumps(payload, indent=2, sort_keys=True))


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    env = RumbleEnv(config)
    logger = create_run_logger("run_milestone_demo", config)

    total_reward = 0.0
    step_count = 0
    step_time_ms_total = 0.0
    sequence = list(build_demo_sequence(config.action_duration_ms))

    try:
        status = env.status()
        _print_json("status", status)

        observation = env.reset()
        _print_json("reset_observation", observation)
        if env.last_reset_response is not None:
            print(
                f"Reset episodeId={env.episode_id} resetMode={env.last_reset_response.get('resetMode')} "
                f"warnings={len(env.last_reset_response.get('warnings') or [])}"
            )

        for index in range(max(1, args.steps)):
            label, action = sequence[index % len(sequence)]
            started_at = time.perf_counter()
            observation, reward, terminated, truncated, info = env.step(action)
            elapsed_ms = (time.perf_counter() - started_at) * 1000.0
            step_time_ms_total += elapsed_ms
            step_count += 1
            total_reward += reward

            logger.record_step(
                episode_id=int(observation.get("episodeId", env.episode_id)),
                step_index=int(observation.get("episodeStep", step_count)),
                timestamp=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                action=action,
                observation=observation,
                reward=reward,
                terminated=terminated,
                truncated=truncated,
                info=info,
                error=None,
                step_time_ms=elapsed_ms,
            )

            root_position = _format_vector(observation.get("rootPosition"))
            left_hand = _format_vector(observation.get("leftHandPosition"))
            right_hand = _format_vector(observation.get("rightHandPosition"))
            tick = observation.get("tick")
            episode_step = observation.get("episodeStep")
            print(
                f"step={step_count} pose={label} reward={reward:.6f} "
                f"tick={tick} episodeStep={episode_step} "
                f"root={root_position} left={left_hand} right={right_hand} "
                f"stepTimeMs={elapsed_ms:.2f}"
            )

            if terminated or truncated:
                print(f"Episode ended early: terminated={terminated} truncated={truncated}")
                break

    except BridgeError as exc:
        logger.finish(
            status="failed",
            error=str(exc),
            summary={
                "totalReward": total_reward,
                "stepCount": step_count,
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
            "stepCount": step_count,
            "averageStepTimeMs": average_step_time_ms,
            "logPath": str(logger.log_path),
        }
        logger.finish(status="success", summary=summary)
        print(
            f"\nSummary: totalReward={total_reward:.6f} stepCount={step_count} "
            f"averageStepTimeMs={average_step_time_ms:.2f}"
        )
        print(f"Log path: {logger.log_path}")
        return 0
    finally:
        env.close()


if __name__ == "__main__":
    raise SystemExit(main())
