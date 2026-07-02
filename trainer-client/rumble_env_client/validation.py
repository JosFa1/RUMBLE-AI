from __future__ import annotations

import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable


ROOT_DIR = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class ValidationRunResult:
    name: str
    return_code: int
    command: list[str]
    stdout: str
    stderr: str

    @property
    def passed(self) -> bool:
        return self.return_code == 0

    @property
    def summary(self) -> str:
        status = "PASS" if self.passed else "FAIL"
        return f"{status} {self.name} exit={self.return_code}"


def run_python_script(
    script_name: str,
    args: Iterable[str] = (),
    *,
    timeout_seconds: int = 300,
    progress: Callable[[str], None] | None = None,
) -> ValidationRunResult:
    command = [sys.executable, f"scripts/{script_name}", *list(args)]
    if progress:
        progress(f"Running {' '.join(command)}")
    completed = subprocess.run(
        command,
        cwd=ROOT_DIR,
        capture_output=True,
        text=True,
        timeout=timeout_seconds,
    )
    return ValidationRunResult(
        name=script_name,
        return_code=completed.returncode,
        command=command,
        stdout=completed.stdout,
        stderr=completed.stderr,
    )


def run_offline_validation(progress: Callable[[str], None] | None = None) -> ValidationRunResult:
    return run_python_script("run_offline_validation.py", timeout_seconds=180, progress=progress)


def run_full_validation(progress: Callable[[str], None] | None = None) -> ValidationRunResult:
    return run_python_script("run_full_validation.py", timeout_seconds=600, progress=progress)
