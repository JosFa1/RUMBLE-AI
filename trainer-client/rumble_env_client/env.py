from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, Tuple

from .client import BridgeError, RumbleEnvClient
from .config import TrainerClientConfig, create_client


Observation = Dict[str, Any]
StepResult = Tuple[Observation, float, bool, bool, Dict[str, Any]]


def _error_message(response: Dict[str, Any], fallback: str) -> str:
    error = response.get("error")
    if isinstance(error, dict):
        code = error.get("code")
        message = error.get("message")
        if isinstance(code, str) and code and isinstance(message, str) and message:
            return f"{code}: {message}"
        if isinstance(message, str) and message:
            return message
        if isinstance(code, str) and code:
            return code
    if isinstance(error, str) and error:
        return error
    return fallback


def _ensure_observation(response: Dict[str, Any], fallback: str) -> Observation:
    observation = response.get("observation")
    if isinstance(observation, dict):
        return observation
    raise BridgeError(_error_message(response, fallback))


def _coerce_reward(response: Dict[str, Any]) -> float:
    reward = response.get("reward")
    if isinstance(reward, (int, float)):
        return float(reward)
    raise BridgeError("step() returned a response without a numeric reward.")


def _coerce_bool(value: Any, fallback: bool = False) -> bool:
    if isinstance(value, bool):
        return value
    return fallback


@dataclass
class RumbleEnv:
    config: TrainerClientConfig
    client: RumbleEnvClient = field(init=False)
    episode_id: int = 0
    episode_step: int = 0
    last_observation: Observation | None = None
    last_reset_response: Dict[str, Any] | None = None
    last_step_response: Dict[str, Any] | None = None
    closed: bool = False

    def __post_init__(self) -> None:
        self.client = create_client(self.config)

    def status(self) -> Dict[str, Any]:
        self._ensure_open()
        return self.client.status()

    def reset(self) -> Observation:
        self._ensure_open()
        response = self.client.reset()
        self.last_reset_response = response

        if response.get("type") == "error" or response.get("error") not in (None, ""):
            raise BridgeError(_error_message(response, "reset() failed."))

        observation = _ensure_observation(response, "reset() returned no observation.")
        self.last_observation = observation
        self.episode_id = int(response.get("episodeId", observation.get("episodeId", self.episode_id)))
        self.episode_step = int(observation.get("episodeStep", 0))
        return observation

    def step(self, action: Dict[str, Any]) -> StepResult:
        self._ensure_open()
        if not isinstance(action, dict):
            raise BridgeError("step(action) expects an action dictionary.")

        prepared_action = dict(action)
        prepared_action.setdefault("durationMs", self.config.action_duration_ms)

        response = self.client.step(prepared_action)
        self.last_step_response = response

        if response.get("type") == "error" or response.get("error") not in (None, ""):
            raise BridgeError(_error_message(response, "step() failed."))

        observation = _ensure_observation(response, "step() returned no observation.")
        reward = _coerce_reward(response)
        terminated = _coerce_bool(response.get("terminated"))
        truncated = _coerce_bool(response.get("truncated"))
        info = response.get("info")
        if not isinstance(info, dict):
            info = {}

        self.last_observation = observation
        self.episode_id = int(observation.get("episodeId", self.episode_id))
        self.episode_step = int(observation.get("episodeStep", self.episode_step))

        return observation, reward, terminated, truncated, info

    def close(self) -> None:
        self.closed = True

    def _ensure_open(self) -> None:
        if self.closed:
            raise BridgeError("RumbleEnv has already been closed.")

