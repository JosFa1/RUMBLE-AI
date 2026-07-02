from __future__ import annotations

import random
from typing import Any, Dict, Iterable, Tuple

from .config import TrainerClientConfig


Action = Dict[str, Any]
NamedAction = Tuple[str, Action]


def neutral_action(duration_ms: int) -> Action:
    return {
        "leftHandTargetLocal": [-0.2, 1.1, 0.35],
        "rightHandTargetLocal": [0.2, 1.1, 0.35],
        "durationMs": duration_ms,
    }


def safe_test_action(config: TrainerClientConfig) -> Action:
    return neutral_action(config.action_duration_ms)


def clamped_test_action(config: TrainerClientConfig) -> Action:
    return {
        "leftHandTargetLocal": [3.0, 3.0, 3.0],
        "rightHandTargetLocal": [3.0, 3.0, 3.0],
        "durationMs": config.action_duration_ms,
    }


def easy_reward_action(config: TrainerClientConfig) -> Action:
    return {
        "leftHandTargetLocal": [-0.18, 1.14, 0.42],
        "rightHandTargetLocal": [0.18, 1.14, 0.42],
        "durationMs": config.action_duration_ms,
    }


def scripted_pose_sequence(duration_ms: int) -> list[NamedAction]:
    return [
        ("neutral", neutral_action(duration_ms)),
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
        ("neutral", neutral_action(duration_ms)),
    ]


def stability_cycle_actions(duration_ms: int, cycle_index: int) -> list[NamedAction]:
    patterns = [
        [
            ("neutral", neutral_action(duration_ms)),
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
        ],
        [
            ("neutral", neutral_action(duration_ms)),
            (
                "apart",
                {
                    "leftHandTargetLocal": [-0.55, 1.1, 0.35],
                    "rightHandTargetLocal": [0.55, 1.1, 0.35],
                    "durationMs": duration_ms,
                },
            ),
            ("neutral", neutral_action(duration_ms)),
        ],
    ]
    return patterns[cycle_index % len(patterns)]


def milestone_pose_sequence(duration_ms: int) -> list[NamedAction]:
    return [
        ("neutral", neutral_action(duration_ms)),
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


def random_safe_action(config: TrainerClientConfig, rng: random.Random) -> Action:
    action = config.safe_hand_bounds.sample_action(rng)
    action["durationMs"] = config.action_duration_ms
    return action


def clamp_action_to_config(config: TrainerClientConfig, action: Action) -> Action:
    left = action.get("leftHandTargetLocal")
    right = action.get("rightHandTargetLocal")
    clamped = dict(action)
    if isinstance(left, Iterable) and not isinstance(left, (str, bytes)):
        clamped["leftHandTargetLocal"] = config.safe_hand_bounds.left.clamp(left)
    if isinstance(right, Iterable) and not isinstance(right, (str, bytes)):
        clamped["rightHandTargetLocal"] = config.safe_hand_bounds.right.clamp(right)
    clamped.setdefault("durationMs", config.action_duration_ms)
    return clamped
