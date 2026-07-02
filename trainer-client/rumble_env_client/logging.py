from __future__ import annotations

import json
import uuid
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

from .config import TrainerClientConfig


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _short_run_id() -> str:
    return uuid.uuid4().hex[:10]


def _display_path(path: Path, base_dir: Path) -> str:
    try:
        return str(path.relative_to(base_dir))
    except ValueError:
        return str(path)


@dataclass
class RunLogger:
    script_name: str
    config: TrainerClientConfig
    run_id: str = field(default_factory=lambda: f"{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}-{_short_run_id()}")

    def __post_init__(self) -> None:
        base_dir = self.config.resolve_log_directory()
        base_dir.mkdir(parents=True, exist_ok=True)

        timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
        self.run_dir = base_dir / f"{timestamp}_{self.script_name}_{self.run_id[-10:]}"
        self.run_dir.mkdir(parents=True, exist_ok=True)

        self.metadata_path = self.run_dir / "metadata.json"
        self.steps_path = self.run_dir / "steps.jsonl"
        self._steps_handle = self.steps_path.open("a", encoding="utf-8", newline="\n")
        project_root = self.run_dir.parents[1]
        self._metadata: Dict[str, Any] = {
            "runId": self.run_id,
            "scriptName": self.script_name,
            "startedAt": _utc_now_iso(),
            "status": "running",
            "config": self.config.to_dict(),
            "paths": {
                "runDirectory": _display_path(self.run_dir, project_root),
                "metadata": _display_path(self.metadata_path, project_root),
                "steps": _display_path(self.steps_path, project_root),
            },
        }
        self._write_metadata()

    @property
    def log_path(self) -> Path:
        return self.steps_path

    def record_step(
        self,
        *,
        episode_id: int,
        step_index: int,
        timestamp: str,
        action: Dict[str, Any],
        observation: Any,
        reward: Any,
        terminated: bool,
        truncated: bool,
        info: Any,
        error: Any = None,
        step_time_ms: Optional[float] = None,
    ) -> Dict[str, Any]:
        row = {
            "runId": self.run_id,
            "episodeId": episode_id,
            "stepIndex": step_index,
            "timestamp": timestamp,
            "action": action,
            "observation": observation,
            "reward": reward,
            "terminated": terminated,
            "truncated": truncated,
            "info": info,
            "error": error,
        }
        if step_time_ms is not None:
            row["stepTimeMs"] = step_time_ms

        self._steps_handle.write(json.dumps(row, separators=(",", ":"), ensure_ascii=False) + "\n")
        self._steps_handle.flush()
        return row

    def finish(self, *, status: str, error: str | None = None, summary: Dict[str, Any] | None = None) -> None:
        self._metadata["status"] = status
        self._metadata["finishedAt"] = _utc_now_iso()
        if error is not None:
            self._metadata["error"] = error
        if summary is not None:
            self._metadata["summary"] = summary
        self._write_metadata()
        try:
            self._steps_handle.close()
        finally:
            self._steps_handle = None

    def _write_metadata(self) -> None:
        self.metadata_path.write_text(
            json.dumps(self._metadata, indent=2, sort_keys=True, ensure_ascii=False),
            encoding="utf-8",
        )


def create_run_logger(script_name: str, config: TrainerClientConfig) -> RunLogger:
    return RunLogger(script_name=script_name, config=config)
