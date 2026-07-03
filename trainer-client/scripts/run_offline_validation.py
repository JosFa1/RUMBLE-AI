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
    "**/__pycache__/",
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
    "*.tmp",
    "*.bak",
    "*.log",
    "*.jsonl",
    "training_status_*.json",
}
REQUEST_TYPES = {
    "status",
    "get_observation",
    "reset_episode",
    "step",
    "debug_probe",
    "get_bootstrap_report",
    "retry_bootstrap",
    "run_scene_inventory",
    "run_actor_discovery",
    "run_capability_discovery",
    "run_actor_completeness",
    "run_local_player_lifecycle_discovery",
    "run_summon_context_discovery",
    "run_single_actor_summon_probe",
    "run_move_probe",
    "run_multi_actor_probe",
    "run_actor_interaction_probe",
    "run_arena_rebuild",
}
BOOTSTRAP_STATUS_FIELDS = {
    "bootstrapStage",
    "actorMode",
    "bootstrapReady",
    "bootstrapFailed",
    "bootstrapFailureReason",
    "gymLoaded",
    "loaderRemoved",
    "loaderInert",
    "primaryActorFound",
    "arenaBuilt",
    "activeScene",
    "loadedScenes",
    "actorDiscoveryStatus",
    "capabilityDiscoveryStatus",
    "summonProbeStatus",
    "moveProbeStatus",
    "multiActorProbeStatus",
    "actorInteractionProbeStatus",
    "latestDumpPath",
    "latestDumpPaths",
    "actorCompletenessClassification",
    "hasVisibleModel",
    "rendererCount",
    "hasBody",
    "hasHead",
    "hasHands",
    "hasMovementSystem",
    "hasPhysicsOrGrounding",
    "hasHealth",
    "hasOwnership",
    "hasSummonContext",
    "realSummonConfirmed",
    "rootMotionConfirmed",
    "handMotionConfirmed",
    "onlyGhostHandsDetected",
    "currentBestActorPath",
    "currentBestActorScene",
    "latestActorCompletenessReport",
    "latestLocalPlayerLifecycleDiscoveryReport",
    "latestSummonContextReport",
    "latestRealSummonProbeReport",
    "latestPruningComparisonReport",
}
BOOTSTRAP_STAGES = {
    "Uninitialized",
    "InitialInventory",
    "RequestGymLoad",
    "WaitForGymLoaded",
    "RemoveLoaderScene",
    "GymInventory",
    "DiscoverPrimaryActor",
    "DiscoverActorCapabilities",
    "ProbeSingleActorSummons",
    "ProbeMoveModifiers",
    "ProbeMultiActorSupport",
    "ProbeActorInteraction",
    "BuildMinimalArena",
    "Ready",
    "Failed",
}
BOOTSTRAP_DUMP_DOC_TERMS = {
    "latest_scene_inventory.json",
    "latest_actor_discovery.json",
    "latest_capability_discovery.json",
    "latest_actor_completeness.json",
    "latest_local_player_lifecycle_discovery.json",
    "latest_summon_context_discovery.json",
    "latest_real_summon_probe.json",
    "latest_actor_pruning_comparison.json",
    "latest_arena_build_report.json",
    "bootstrap_stage_*.json",
}
BOOTSTRAP_CONFIG_GATES = {
    "UseStagedBootstrap",
    "EnableLegacyBootstrapFallback",
    "EnableFullSceneHierarchyDump",
    "EnableExplorationProbes",
    "EnableArenaPruning",
    "NoPruneActorValidationMode",
    "EnableActorCloneProbes",
    "EnableSummonProbes",
    "EnableMoveProbes",
    "EnableActorInteractionProbes",
}
BOOTSTRAP_DEFAULT_GATES = {
    "UseStagedBootstrap": "true",
    "EnableLegacyBootstrapFallback": "false",
    "NoPruneActorValidationMode": "false",
    "EnableFullSceneHierarchyDump": "false",
    "EnableActorCloneProbes": "false",
    "EnableSummonProbes": "false",
    "EnableMoveProbes": "false",
    "EnableActorInteractionProbes": "false",
}
ACTIVE_PROBE_CODE_TERMS = {
    "StartSingleActorSummonProbe",
    "StartMoveProbe",
    "StartMultiActorProbe",
    "StartActorInteractionProbe",
    "StructureSpawnerTypeName",
    "ReturnToPool",
    "TrainingProbeCollisionRecorder",
    "damageEvidence",
}
STAGED_ENGINEERING_CODE_TERMS = {
    "ResetAndRetry",
    "TrainingActorLocator.Resolve",
    "bootstrap-post-arena-inventory",
    "gym-not-confirmed-after-attempt",
    "loader-removal-confirmed-by-inventory",
    "loader-remained-active-after-cleanup",
    "LoaderInert",
    "GetArenaPreservationReason",
    "explicit_clutter_hint",
    "fallback_floor_created",
    "usableFloorConfirmed",
    "selected_collider_raycast_confirmed",
    "supportCollider.Raycast",
    "TryFindFloorSurfacePoint",
    "upward_surface_hit",
}
KNOWLEDGE_LABELS = {"confirmed", "likely", "unconfirmed", "failed", "unsafe"}
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


def has_main_entry_point(path: Path) -> bool:
    text = path.read_text(encoding="utf-8")
    return 'if __name__ == "__main__"' in text and "raise SystemExit(main())" in text


def stale_protocol_mentions(path: Path) -> set[str]:
    text = path.read_text(encoding="utf-8", errors="ignore")
    return set(re.findall(r"(?:protocol version|Protocol v|Current version:|Version = \")\s*([0-9]+\.[0-9]+)", text, re.IGNORECASE))


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

        response_schema = parsed_schemas.get("response-v0.3.json")
        status_required = set()
        status_properties = set()
        if isinstance(response_schema, dict):
            status_schema = response_schema.get("$defs", {}).get("statusResponse", {})
            if isinstance(status_schema, dict):
                status_required = set(status_schema.get("required", []))
                status_properties = set(status_schema.get("properties", {}))
        missing_bootstrap_schema_fields = sorted(
            field for field in BOOTSTRAP_STATUS_FIELDS
            if field not in status_required or field not in status_properties
        )
        record(
            results,
            "bootstrap_status_schema",
            not missing_bootstrap_schema_fields,
            "Status schema includes staged bootstrap fields"
            if not missing_bootstrap_schema_fields
            else f"Missing bootstrap status fields: {', '.join(missing_bootstrap_schema_fields)}",
        )

    protocol_sources = {
        "config": {config.protocol_version},
        "mod": stale_protocol_mentions(MOD_DIR / "TrainingProtocol.cs"),
        "protocol_doc": stale_protocol_mentions(PROTOCOL_DIR / "training-protocol.md"),
        "response_schema": {"0.3"} if (PROTOCOL_DIR / "schemas" / "response-v0.3.json").exists() else set(),
        "observation_schema": {"0.3"} if (PROTOCOL_DIR / "schemas" / "observation-v0.3.json").exists() else set(),
        "action_schema": {"0.3"} if (PROTOCOL_DIR / "schemas" / "action-v0.3.json").exists() else set(),
    }
    version_ok = all("0.3" in versions for versions in protocol_sources.values())
    stale_versions = {
        name: sorted(version for version in versions if version != "0.3")
        for name, versions in protocol_sources.items()
        if any(version != "0.3" for version in versions)
    }
    record(
        results,
        "protocol_version_consistency",
        version_ok and not stale_versions,
        "All protocol sources use 0.3"
        if version_ok and not stale_versions
        else f"Protocol versions: {protocol_sources}; stale: {stale_versions}",
    )

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

    operator_script = ROOT_DIR / "scripts" / "operator_console.py"
    project_map = REPO_DIR / "docs" / "project-map.md"
    operator_docs = []
    for doc_path in [REPO_DIR / "README.md", ROOT_DIR / "README.md", project_map]:
        if doc_path.exists() and "operator_console.py" in doc_path.read_text(encoding="utf-8"):
            operator_docs.append(str(doc_path.relative_to(REPO_DIR)))
    record(
        results,
        "operator_entry_point",
        operator_script.exists() and len(operator_docs) == 3,
        f"operator_console.py exists and docs mention it: {', '.join(operator_docs)}",
    )

    script_files = sorted((ROOT_DIR / "scripts").glob("*.py"))
    scripts_without_main = [path.name for path in script_files if not has_main_entry_point(path)]
    record(
        results,
        "script_main_entry_points",
        not scripts_without_main,
        "All scripts have main entry points" if not scripts_without_main else ", ".join(scripts_without_main),
    )

    help_failures: list[str] = []
    for script_path in script_files:
        completed = subprocess.run(
            [sys.executable, str(script_path), "--help"],
            cwd=ROOT_DIR,
            capture_output=True,
            text=True,
            timeout=30,
        )
        if completed.returncode != 0:
            help_failures.append(f"{script_path.name}: {completed.returncode}")
    record(
        results,
        "script_help",
        not help_failures,
        "All scripts exit cleanly on --help" if not help_failures else "; ".join(help_failures),
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

    foundation_text = (MOD_DIR / "TrainingFoundation.cs").read_text(encoding="utf-8")
    missing_config_gates = sorted(gate for gate in BOOTSTRAP_CONFIG_GATES if gate not in foundation_text)
    record(
        results,
        "bootstrap_config_gates",
        not missing_config_gates,
        "TrainingFoundation declares the staged bootstrap config gates"
        if not missing_config_gates
        else f"Missing gates: {', '.join(missing_config_gates)}",
    )
    unexpected_default_gates = []
    for gate, expected_value in BOOTSTRAP_DEFAULT_GATES.items():
        pattern = rf"{re.escape(gate)}\s*=\s*{expected_value}\s*;"
        if not re.search(pattern, foundation_text, re.IGNORECASE):
            unexpected_default_gates.append(f"{gate}!={expected_value}")
    record(
        results,
        "bootstrap_default_gates",
        not unexpected_default_gates,
        "Staged bootstrap is default; full hierarchy logging and active probe gates are off by default"
        if not unexpected_default_gates
        else f"Unexpected defaults: {', '.join(unexpected_default_gates)}",
    )

    active_probe_path = MOD_DIR / "TrainingExplorationProbeService.cs"
    active_probe_text = active_probe_path.read_text(encoding="utf-8") if active_probe_path.exists() else ""
    runtime_tools_text = (MOD_DIR / "TrainingRuntimeTools.cs").read_text(encoding="utf-8")
    combined_probe_text = active_probe_text + "\n" + runtime_tools_text
    missing_active_probe_terms = sorted(
        term for term in ACTIVE_PROBE_CODE_TERMS if term not in combined_probe_text
    )
    stale_placeholder = "not_implemented" in foundation_text or "not_implemented" in active_probe_text
    record(
        results,
        "active_probe_implementation",
        active_probe_path.exists() and not missing_active_probe_terms and not stale_placeholder,
        "Bounded active probes include evidence, cleanup, and collision-only damage semantics"
        if active_probe_path.exists() and not missing_active_probe_terms and not stale_placeholder
        else (
            f"Missing terms: {', '.join(missing_active_probe_terms)}; "
            f"stale placeholder={stale_placeholder}"
        ),
    )

    orchestrator_text = (MOD_DIR / "TrainingBootstrapOrchestrator.cs").read_text(encoding="utf-8")
    staged_engineering_text = foundation_text + "\n" + orchestrator_text
    missing_staged_engineering_terms = sorted(
        term for term in STAGED_ENGINEERING_CODE_TERMS if term not in staged_engineering_text
    )
    blanket_prune_pattern = re.search(
        r"if\s*\(\s*EnableArenaPruning\s*\)\s*\{\s*UnityObject\.Destroy\(root\)",
        foundation_text,
        re.MULTILINE,
    )
    record(
        results,
        "staged_closed_loop_guards",
        not missing_staged_engineering_terms and blanket_prune_pattern is None,
        "Gym retries, Loader confirmation, retry control, and classified arena pruning are present"
        if not missing_staged_engineering_terms and blanket_prune_pattern is None
        else (
            f"Missing terms: {', '.join(missing_staged_engineering_terms)}; "
            f"blanket pruning={blanket_prune_pattern is not None}"
        ),
    )

    bootstrap_notes = REPO_DIR / "docs" / "ai-bootstrap-rework-notes.md"
    if bootstrap_notes.exists():
        bootstrap_text = bootstrap_notes.read_text(encoding="utf-8")
        missing_bootstrap_stages = sorted(stage for stage in BOOTSTRAP_STAGES if stage not in bootstrap_text)
        missing_bootstrap_dump_terms = sorted(term for term in BOOTSTRAP_DUMP_DOC_TERMS if term not in bootstrap_text)
        missing_bootstrap_config_terms = sorted(gate for gate in BOOTSTRAP_CONFIG_GATES if gate not in bootstrap_text)
        missing_bootstrap_docs = sorted(field for field in BOOTSTRAP_STATUS_FIELDS if field not in protocol_text)
        record(
            results,
            "bootstrap_rework_notes",
            not missing_bootstrap_stages and not missing_bootstrap_dump_terms and not missing_bootstrap_config_terms,
            "Bootstrap rework notes document all staged states, key dump files, and config gates"
            if not missing_bootstrap_stages and not missing_bootstrap_dump_terms and not missing_bootstrap_config_terms
            else (
                f"Missing stages: {', '.join(missing_bootstrap_stages)}; "
                f"missing dumps: {', '.join(missing_bootstrap_dump_terms)}; "
                f"missing config gates: {', '.join(missing_bootstrap_config_terms)}"
            ),
        )
        record(
            results,
            "bootstrap_protocol_docs",
            not missing_bootstrap_docs,
            "Protocol docs mention staged bootstrap status fields"
            if not missing_bootstrap_docs
            else f"Missing fields: {', '.join(missing_bootstrap_docs)}",
        )
    else:
        record(results, "bootstrap_rework_notes", False, "docs/ai-bootstrap-rework-notes.md is missing")
        record(results, "bootstrap_protocol_docs", False, "Cannot verify bootstrap protocol docs without notes file")

    bootstrap_knowledge = REPO_DIR / "docs" / "ai-bootstrap-knowledge.md"
    if bootstrap_knowledge.exists():
        knowledge_text = bootstrap_knowledge.read_text(encoding="utf-8")
        missing_labels = sorted(label for label in KNOWLEDGE_LABELS if f"`{label}`" not in knowledge_text)
        missing_knowledge_terms = sorted(
            term for term in [
                "latest_scene_inventory.json",
                "latest_actor_discovery.json",
                "latest_capability_discovery.json",
                "latest_arena_build_report.json",
                "run_full_validation.py",
            ]
            if term not in knowledge_text
        )
        record(
            results,
            "bootstrap_knowledge_doc",
            not missing_labels and not missing_knowledge_terms,
            "Bootstrap knowledge doc exists with claim labels and key artifacts"
            if not missing_labels and not missing_knowledge_terms
            else f"Missing labels: {', '.join(missing_labels)}; missing terms: {', '.join(missing_knowledge_terms)}",
        )
    else:
        record(results, "bootstrap_knowledge_doc", False, "docs/ai-bootstrap-knowledge.md is missing")

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
