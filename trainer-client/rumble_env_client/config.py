from __future__ import annotations

import json
import random
import argparse
import shutil
from datetime import datetime, timezone
from dataclasses import asdict, dataclass, field, replace
from pathlib import Path
from typing import Any, Dict, Iterable, Mapping, Optional, Tuple

ROOT_DIR = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG_PATH = ROOT_DIR / "config.json"


@dataclass(frozen=True)
class AxisBounds:
    minimum: float
    maximum: float

    def clamp(self, value: float) -> float:
        return max(self.minimum, min(self.maximum, value))

    def sample(self, rng: random.Random) -> float:
        return rng.uniform(self.minimum, self.maximum)

    def to_dict(self) -> Dict[str, float]:
        return {"min": self.minimum, "max": self.maximum}

    @classmethod
    def from_value(cls, value: Any, default: "AxisBounds") -> "AxisBounds":
        if isinstance(value, cls):
            return value

        if isinstance(value, Mapping):
            if "min" in value and "max" in value:
                return cls(float(value["min"]), float(value["max"]))
            if "minimum" in value and "maximum" in value:
                return cls(float(value["minimum"]), float(value["maximum"]))

        if isinstance(value, (list, tuple)) and len(value) == 2:
            return cls(float(value[0]), float(value[1]))

        return default


@dataclass(frozen=True)
class HandBounds:
    x: AxisBounds
    y: AxisBounds
    z: AxisBounds

    def sample(self, rng: random.Random) -> list[float]:
        return [self.x.sample(rng), self.y.sample(rng), self.z.sample(rng)]

    def clamp(self, values: Iterable[float]) -> list[float]:
        values = list(values)
        if len(values) != 3:
            raise ValueError("Expected three target coordinates.")
        return [self.x.clamp(values[0]), self.y.clamp(values[1]), self.z.clamp(values[2])]

    def to_dict(self) -> Dict[str, Dict[str, float]]:
        return {
            "x": self.x.to_dict(),
            "y": self.y.to_dict(),
            "z": self.z.to_dict(),
        }

    @classmethod
    def from_mapping(cls, value: Any, default: "HandBounds") -> "HandBounds":
        if isinstance(value, cls):
            return value

        if not isinstance(value, Mapping):
            return default

        return cls(
            x=AxisBounds.from_value(value.get("x"), default.x),
            y=AxisBounds.from_value(value.get("y"), default.y),
            z=AxisBounds.from_value(value.get("z"), default.z),
        )


@dataclass(frozen=True)
class SafeHandBounds:
    left: HandBounds
    right: HandBounds

    def sample_left(self, rng: random.Random) -> list[float]:
        return self.left.sample(rng)

    def sample_right(self, rng: random.Random) -> list[float]:
        return self.right.sample(rng)

    def sample_action(self, rng: random.Random) -> Dict[str, list[float]]:
        return {
            "leftHandTargetLocal": self.sample_left(rng),
            "rightHandTargetLocal": self.sample_right(rng),
        }

    def to_dict(self) -> Dict[str, Dict[str, Dict[str, float]]]:
        return {
            "left": self.left.to_dict(),
            "right": self.right.to_dict(),
        }

    @classmethod
    def from_mapping(cls, value: Any, default: "SafeHandBounds") -> "SafeHandBounds":
        if isinstance(value, cls):
            return value

        if not isinstance(value, Mapping):
            return default

        return cls(
            left=HandBounds.from_mapping(value.get("left"), default.left),
            right=HandBounds.from_mapping(value.get("right"), default.right),
        )


DEFAULT_LEFT_BOUNDS = HandBounds(
    x=AxisBounds(-0.6, -0.05),
    y=AxisBounds(0.85, 1.45),
    z=AxisBounds(0.2, 0.7),
)
DEFAULT_RIGHT_BOUNDS = HandBounds(
    x=AxisBounds(0.05, 0.6),
    y=AxisBounds(0.85, 1.45),
    z=AxisBounds(0.2, 0.7),
)
DEFAULT_SAFE_HAND_BOUNDS = SafeHandBounds(left=DEFAULT_LEFT_BOUNDS, right=DEFAULT_RIGHT_BOUNDS)


@dataclass(frozen=True)
class TrainerClientConfig:
    host: str = "127.0.0.1"
    port: int = 8765
    timeout_ms: int = 5000
    episode_length: int = 20
    action_duration_ms: int = 100
    log_directory: str = "runs"
    safe_hand_bounds: SafeHandBounds = DEFAULT_SAFE_HAND_BOUNDS
    protocol_version: str = "0.3"
    strict_protocol_version: bool = False
    source_path: Optional[str] = None

    @property
    def timeout_seconds(self) -> float:
        return self.timeout_ms / 1000.0

    def with_overrides(self, **overrides: Any) -> "TrainerClientConfig":
        return replace(self, **overrides)

    def resolve_log_directory(self) -> Path:
        log_dir = Path(self.log_directory)
        if not log_dir.is_absolute():
            log_dir = ROOT_DIR / log_dir
        return log_dir

    def to_dict(self) -> Dict[str, Any]:
        payload = asdict(self)
        payload["safeHandBounds"] = payload.pop("safe_hand_bounds")
        payload["timeoutMs"] = payload.pop("timeout_ms")
        payload["episodeLength"] = payload.pop("episode_length")
        payload["actionDurationMs"] = payload.pop("action_duration_ms")
        payload["logDirectory"] = payload.pop("log_directory")
        payload["protocolVersion"] = payload.pop("protocol_version")
        payload["strictProtocolVersion"] = payload.pop("strict_protocol_version")
        payload["host"] = payload.pop("host")
        payload["port"] = payload.pop("port")
        payload.pop("source_path", None)
        return payload

    @classmethod
    def from_mapping(cls, value: Any) -> "TrainerClientConfig":
        if not isinstance(value, Mapping):
            return cls()

        default = cls()
        return cls(
            host=str(value.get("host", default.host)),
            port=int(value.get("port", default.port)),
            timeout_ms=int(value.get("timeoutMs", default.timeout_ms)),
            episode_length=int(value.get("episodeLength", default.episode_length)),
            action_duration_ms=int(value.get("actionDurationMs", default.action_duration_ms)),
            log_directory=str(value.get("logDirectory", default.log_directory)),
            safe_hand_bounds=SafeHandBounds.from_mapping(value.get("safeHandBounds"), default.safe_hand_bounds),
            protocol_version=str(value.get("protocolVersion", default.protocol_version)),
            strict_protocol_version=bool(value.get("strictProtocolVersion", default.strict_protocol_version)),
            source_path=None,
        )


def load_config(config_path: str | Path | None = None) -> TrainerClientConfig:
    path = Path(config_path) if config_path is not None else DEFAULT_CONFIG_PATH
    if not path.is_absolute():
        path = (ROOT_DIR / path).resolve()

    if not path.exists():
        return TrainerClientConfig(source_path=str(path))

    with path.open("r", encoding="utf-8") as handle:
        raw = json.load(handle)

    config = TrainerClientConfig.from_mapping(raw)
    return config.with_overrides(source_path=str(path))


def save_config(config: TrainerClientConfig, config_path: str | Path | None = None) -> Path:
    path = Path(config_path or config.source_path or DEFAULT_CONFIG_PATH)
    if not path.is_absolute():
        path = (ROOT_DIR / path).resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        stamp = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
        backup_path = path.with_name(f"{path.name}.{stamp}.bak")
        shutil.copy2(path, backup_path)
    path.write_text(json.dumps(config.to_dict(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return path


def apply_overrides(
    config: TrainerClientConfig,
    *,
    host: str | None = None,
    port: int | None = None,
    timeout_ms: int | None = None,
    timeout_seconds: float | None = None,
    episode_length: int | None = None,
    action_duration_ms: int | None = None,
    log_directory: str | None = None,
    protocol_version: str | None = None,
    strict_protocol_version: bool | None = None,
) -> TrainerClientConfig:
    overrides: Dict[str, Any] = {}
    if host is not None:
        overrides["host"] = host
    if port is not None:
        overrides["port"] = int(port)
    if timeout_ms is not None:
        overrides["timeout_ms"] = int(timeout_ms)
    elif timeout_seconds is not None:
        overrides["timeout_ms"] = int(round(timeout_seconds * 1000))
    if episode_length is not None:
        overrides["episode_length"] = int(episode_length)
    if action_duration_ms is not None:
        overrides["action_duration_ms"] = int(action_duration_ms)
    if log_directory is not None:
        overrides["log_directory"] = log_directory
    if protocol_version is not None:
        overrides["protocol_version"] = protocol_version
    if strict_protocol_version is not None:
        overrides["strict_protocol_version"] = bool(strict_protocol_version)

    return config.with_overrides(**overrides) if overrides else config


def make_client(config: TrainerClientConfig):
    from .client import RumbleEnvClient

    return RumbleEnvClient(
        host=config.host,
        port=config.port,
        timeout_seconds=config.timeout_seconds,
        protocol_version=config.protocol_version,
        strict_protocol_version=config.strict_protocol_version,
    )


def sample_safe_action(config: TrainerClientConfig, rng: random.Random) -> Dict[str, list[float]]:
    return config.safe_hand_bounds.sample_action(rng)


def summarize_bounds(config: TrainerClientConfig) -> Dict[str, Any]:
    return config.safe_hand_bounds.to_dict()


def add_common_args(
    parser: argparse.ArgumentParser,
    *,
    include_episode_length: bool = False,
    include_action_duration: bool = False,
    include_episodes: bool = False,
) -> argparse.ArgumentParser:
    parser.add_argument("--config", default=None, help="Path to config.json, default: trainer-client/config.json")
    parser.add_argument("--host", default=None, help="Override bridge host from config.")
    parser.add_argument("--port", type=int, default=None, help="Override bridge port from config.")
    parser.add_argument("--timeout-ms", dest="timeout_ms", type=int, default=None, help="Override timeoutMs from config.")
    parser.add_argument("--timeout", dest="timeout_seconds", type=float, default=None, help="Legacy timeout in seconds.")
    if include_episode_length:
        parser.add_argument("--episode-length", dest="episode_length", type=int, default=None, help="Override episodeLength from config.")
    if include_action_duration:
        parser.add_argument("--action-duration-ms", dest="action_duration_ms", type=int, default=None, help="Override actionDurationMs from config.")
    parser.add_argument("--log-directory", dest="log_directory", default=None, help="Override logDirectory from config.")
    parser.add_argument("--protocol-version", dest="protocol_version", default=None, help="Override protocolVersion from config.")
    if include_episodes:
        parser.add_argument("--episodes", type=int, default=5, help="Number of episodes for the run script.")
    return parser


def load_runtime_config(args: argparse.Namespace) -> TrainerClientConfig:
    config = load_config(getattr(args, "config", None))
    return apply_overrides(
        config,
        host=getattr(args, "host", None),
        port=getattr(args, "port", None),
        timeout_ms=getattr(args, "timeout_ms", None),
        timeout_seconds=getattr(args, "timeout_seconds", None),
        episode_length=getattr(args, "episode_length", None),
        action_duration_ms=getattr(args, "action_duration_ms", None),
        log_directory=getattr(args, "log_directory", None),
        protocol_version=getattr(args, "protocol_version", None),
    )


def create_client(config: TrainerClientConfig):
    return make_client(config)
