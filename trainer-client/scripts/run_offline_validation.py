from __future__ import annotations

import argparse
import compileall
import importlib
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT_DIR = Path(__file__).resolve().parents[1]
if str(ROOT_DIR) not in sys.path:
    sys.path.insert(0, str(ROOT_DIR))

from rumble_env_client.config import load_config


REPO_DIR = ROOT_DIR.parent
PROTOCOL_DIR = REPO_DIR / "protocol"
MOD_DIR = REPO_DIR / "mod"
EXPECTED_IGNORE_ENTRIES = {
    "trainer-client/runs/*",
    "!trainer-client/runs/.gitkeep",
    "trainer-client/**/__pycache__/",
    "__pycache__/",
    "*.pyc",
    ".pytest_cache/",
    ".mypy_cache/",
    ".ruff_cache/",
    ".venv/",
    "venv/",
    "build/",
    "dist/",
    "mod/bin/",
    "mod/obj/",
    "bin/",
    "obj/",
    "*.user",
    "*.suo",
}
REQUEST_TYPES = {"status", "get_observation", "reset_episode", "step", "debug_probe"}
TRACKED_GENERATED_PREFIXES = ("trainer-client/runs/", "mod/bin/", "mod/obj/")
TEXT_SUFFIXES = {
    ".cs",
    ".csproj",
    ".gitignore",
    ".json",
    ".md",
    ".ps1",
    ".py",
    ".txt",
    ".xml",
    ".yml",
    ".yaml",
}
README_COMMAND_PATTERN = re.compile(r"python\s+scripts/([A-Za-z0-9_./-]+\.py)")
ABSOLUTE_USER_PATH_PATTERN = re.compile(r"(?:[A-Za-z]:\\+Users\\|/Users/)")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run offline validation for the trainer-client and protocol repo state.")
    parser.add_argument("--config", default=None, help="Optional path to trainer-client config.json.")
    return parser.parse_args()


def record(results: list[dict[str, object]], name: str, ok: bool, detail: str) -> None:
    results.append({"name": name, "ok": ok, "detail": detail})


def tracked_files() -> list[str]:
    completed = subprocess.run(
        ["git", "ls-files"],
        cwd=REPO_DIR,
        capture_output=True,
        text=True,
        check=True,
    )
    return [line.strip().replace("\\", "/") for line in completed.stdout.splitlines() if line.strip()]


def import_python_file(path: Path) -> None:
    relative_parts = path.relative_to(ROOT_DIR).with_suffix("").parts
    if not relative_parts:
        raise RuntimeError(f"Could not determine module path for {path}")

    if relative_parts[0] == "rumble_env_client":
        module_name = "rumble_env_client" if relative_parts[-1] == "__init__" else ".".join(relative_parts)
    elif relative_parts[0] == "scripts":
        module_name = ".".join(relative_parts)
    else:
        module_name = relative_parts[-1]

    if not module_name:
        raise RuntimeError(f"Could not determine importable module name for {path}")

    importlib.import_module(module_name)


def main() -> int:
    args = parse_args()
    results: list[dict[str, object]] = []

    config = load_config(args.config)
    record(results, "config_load", True, f"Loaded config from {config.source_path}")

    compile_ok = compileall.compile_dir(str(ROOT_DIR), quiet=1)
    record(results, "python_compile", compile_ok, "compileall over trainer-client")

    try:
        python_files = sorted(
            path for path in ROOT_DIR.rglob("*.py") if "__pycache__" not in path.parts
        )
        for path in python_files:
            import_python_file(path)
        record(results, "python_imports", True, f"Imported {len(python_files)} Python files")
    except Exception as exc:
        record(results, "python_imports", False, str(exc))

    schema_files = sorted((PROTOCOL_DIR / "schemas").glob("*.json"))
    try:
        parsed_schemas = {path.name: json.loads(path.read_text(encoding="utf-8")) for path in schema_files}
        record(results, "schema_parse", True, f"Parsed {len(parsed_schemas)} schema files")
    except Exception as exc:
        parsed_schemas = {}
        record(results, "schema_parse", False, str(exc))

    if parsed_schemas:
        action_schema = parsed_schemas.get("action-v0.3.json")
        sample_action = {
            "leftHandTargetLocal": [-0.2, 1.1, 0.35],
            "rightHandTargetLocal": [0.2, 1.1, 0.35],
            "durationMs": 100,
        }
        required = set(action_schema.get("required", [])) if isinstance(action_schema, dict) else set()
        sample_valid = required <= set(sample_action) and all(
            isinstance(sample_action[key], list) and len(sample_action[key]) == 3
            for key in ("leftHandTargetLocal", "rightHandTargetLocal")
        ) and isinstance(sample_action["durationMs"], int)
        record(results, "sample_action_schema", sample_valid, "Validated sample action against schema expectations")

    gitignore_path = REPO_DIR / ".gitignore"
    gitignore_text = gitignore_path.read_text(encoding="utf-8") if gitignore_path.exists() else ""
    missing_ignores = sorted(entry for entry in EXPECTED_IGNORE_ENTRIES if entry not in gitignore_text)
    record(
        results,
        "gitignore_entries",
        not missing_ignores,
        "All expected ignore entries present" if not missing_ignores else f"Missing: {', '.join(missing_ignores)}",
    )

    readme_files = [path for path in (REPO_DIR / "README.md", ROOT_DIR / "README.md") if path.exists()]
    missing_commands: list[str] = []
    for readme_path in readme_files:
        for match in README_COMMAND_PATTERN.finditer(readme_path.read_text(encoding="utf-8")):
            script_name = Path(match.group(1)).name
            if not (ROOT_DIR / "scripts" / script_name).exists():
                missing_commands.append(f"{readme_path.name}: {match.group(0)}")
    record(
        results,
        "readme_commands",
        not missing_commands,
        "All referenced script commands exist" if not missing_commands else "; ".join(missing_commands),
    )

    protocol_text = (PROTOCOL_DIR / "training-protocol.md").read_text(encoding="utf-8")
    missing_request_mentions = sorted(request_type for request_type in REQUEST_TYPES if request_type not in protocol_text)
    record(
        results,
        "protocol_request_mentions",
        not missing_request_mentions,
        "Protocol doc covers all implemented request types"
        if not missing_request_mentions
        else f"Missing: {', '.join(missing_request_mentions)}",
    )

    tracked = tracked_files()
    tracked_generated = [
        path for path in tracked
        if (
            any(path.startswith(prefix) for prefix in TRACKED_GENERATED_PREFIXES)
            and path != "trainer-client/runs/.gitkeep"
        ) or "__pycache__/" in path
    ]
    record(
        results,
        "tracked_generated_files",
        not tracked_generated,
        "No tracked generated files found" if not tracked_generated else ", ".join(tracked_generated[:20]),
    )

    absolute_user_paths: list[str] = []
    for relative_path in tracked:
        if relative_path == "trainer-client/scripts/run_offline_validation.py":
            continue
        suffix = Path(relative_path).suffix.lower()
        if suffix not in TEXT_SUFFIXES and Path(relative_path).name not in {"README", "LICENSE"}:
            continue
        full_path = REPO_DIR / relative_path
        try:
            text = full_path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if ABSOLUTE_USER_PATH_PATTERN.search(text):
            absolute_user_paths.append(relative_path)
    record(
        results,
        "absolute_user_paths",
        not absolute_user_paths,
        "No committed source files contain user-specific absolute paths"
        if not absolute_user_paths
        else ", ".join(absolute_user_paths[:20]),
    )

    requirements_text = (ROOT_DIR / "requirements.txt").read_text(encoding="utf-8")
    forbidden_requirements = [
        name for name in ("torch", "pytorch", "gymnasium") if name in requirements_text.lower()
    ]
    record(
        results,
        "forbidden_ml_dependencies",
        not forbidden_requirements,
        "No ML dependencies required"
        if not forbidden_requirements
        else f"Forbidden dependencies found: {', '.join(forbidden_requirements)}",
    )

    passed = all(result["ok"] for result in results)
    print(json.dumps({"passed": passed, "results": results}, indent=2))
    print("PASS" if passed else "FAIL")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
