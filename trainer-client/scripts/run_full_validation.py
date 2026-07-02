from __future__ import annotations

import argparse
import json
import math
import socket
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict

ROOT_DIR = Path(__file__).resolve().parents[1]
if str(ROOT_DIR) not in sys.path:
    sys.path.insert(0, str(ROOT_DIR))

from rumble_env_client.client import BridgeError
from rumble_env_client.actions import clamped_test_action
from rumble_env_client.config import load_runtime_config, add_common_args, sample_safe_action
from rumble_env_client.logging import create_run_logger


EXIT_CONNECTION_FAILURE = 10
EXIT_SCENE_NOT_READY = 11
EXIT_PROTOCOL_FAILURE = 12
EXIT_OBSERVATION_FAILURE = 13
EXIT_ACTION_FAILURE = 14
EXIT_REWARD_FAILURE = 15
EXIT_RESET_FAILURE = 16
EXIT_STABILITY_FAILURE = 17


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run full live validation against an already-running RUMBLE bridge.")
    add_common_args(parser, include_episode_length=True, include_action_duration=True)
    parser.add_argument("--stability-cycles", type=int, default=20, help="Cycles for the stability pass.")
    parser.add_argument("--random-episodes", type=int, default=2, help="Episodes for the random policy pass.")
    parser.add_argument("--scripted-episode-length", type=int, default=4, help="Episode length for scripted validation.")
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def print_json(label: str, payload: Dict[str, Any]) -> None:
    print(f"{label}:")
    print(json.dumps(payload, indent=2, sort_keys=True))


def response_error(response: Dict[str, Any]) -> Dict[str, Any] | str | None:
    value = response.get("error")
    if isinstance(value, dict) and value:
        return value
    if isinstance(value, str) and value:
        return value
    return None


def response_error_code(response: Dict[str, Any]) -> str | None:
    error = response_error(response)
    if isinstance(error, dict):
        code = error.get("code")
        return code if isinstance(code, str) and code else None
    if isinstance(error, str):
        return error
    return None


def response_error_message(response: Dict[str, Any]) -> str | None:
    error = response_error(response)
    if isinstance(error, dict):
        message = error.get("message")
        if isinstance(message, str) and message:
            return message
        code = error.get("code")
        return code if isinstance(code, str) and code else None
    if isinstance(error, str):
        return error
    return None


def raw_request(host: str, port: int, timeout_seconds: float, payload: str) -> Dict[str, Any] | None:
    with socket.create_connection((host, port), timeout=timeout_seconds) as sock:
        sock.settimeout(timeout_seconds)
        sock.sendall(payload.encode("utf-8"))
        buffer = bytearray()
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            buffer.extend(chunk)
            if b"\n" in chunk:
                break
        if not buffer:
            return None
        line = buffer.split(b"\n", 1)[0].decode("utf-8", errors="replace").strip()
        if not line:
            return None
        return json.loads(line)


def run_script(command: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=ROOT_DIR,
        capture_output=True,
        text=True,
        timeout=300,
    )


def write_report(path: Path, report: Dict[str, Any]) -> None:
    path.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")


def finalize_failure(
    logger,
    report_path: Path,
    report: Dict[str, Any],
    exit_code: int,
    category: str,
    message: str,
) -> int:
    report["passed"] = False
    report["failureCategory"] = category
    report["failureMessage"] = message
    write_report(report_path, report)
    logger.finish(status="failed", error=message, summary={"report": str(report_path.relative_to(ROOT_DIR)), "failureCategory": category})
    print(message, file=sys.stderr)
    return exit_code


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = None
    logger = create_run_logger("run_full_validation", config)
    report_path = logger.run_dir / "validation_report.json"
    report: Dict[str, Any] = {
        "startedAt": utc_now(),
        "config": config.to_dict(),
        "checks": [],
    }

    from rumble_env_client.config import create_client

    try:
        client = create_client(config)
        status = client.status()
        report["checks"].append({"name": "status", "response": status})
    except BridgeError as exc:
        return finalize_failure(logger, report_path, report, EXIT_CONNECTION_FAILURE, "connection_failure", str(exc))

    if response_error(status):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_PROTOCOL_FAILURE,
            "protocol_failure",
            response_error_message(status) or "Status request returned an error.",
        )

    print_json("status", status)
    if not status.get("sceneReady"):
        return finalize_failure(logger, report_path, report, EXIT_SCENE_NOT_READY, "scene_not_ready", "Bridge reported sceneReady=false.")
    if not status.get("playerRootFound"):
        return finalize_failure(logger, report_path, report, EXIT_SCENE_NOT_READY, "scene_not_ready", "Bridge reported playerRootFound=false.")

    try:
        observation_response = client.get_observation()
        report["checks"].append({"name": "get_observation", "response": observation_response})
    except BridgeError as exc:
        return finalize_failure(logger, report_path, report, EXIT_OBSERVATION_FAILURE, "observation_failure", str(exc))

    if response_error(observation_response):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_OBSERVATION_FAILURE,
            "observation_failure",
            response_error_message(observation_response) or "Observation request returned an error.",
        )

    observation = observation_response.get("observation")
    required_observation_fields = {"protocolVersion", "tick", "timeSeconds", "sceneReady", "episodeId", "episodeStep"}
    if not isinstance(observation, dict) or not required_observation_fields.issubset(observation):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_OBSERVATION_FAILURE,
            "observation_failure",
            "Observation payload was missing required fields.",
        )

    try:
        malformed = raw_request(config.host, config.port, config.timeout_seconds, "{\"type\":\n")
        unknown = raw_request(config.host, config.port, config.timeout_seconds, "{\"type\":\"bogus\"}\n")
        empty = raw_request(config.host, config.port, config.timeout_seconds, "\n")
        report["checks"].append({"name": "raw_protocol", "malformed": malformed, "unknown": unknown, "empty": empty})
    except Exception as exc:
        return finalize_failure(logger, report_path, report, EXIT_PROTOCOL_FAILURE, "protocol_failure", f"Raw protocol checks failed: {exc}")

    expected_protocol_errors = {
        "malformed": {"malformed_request"},
        "unknown": {"unknown_request_type"},
        "empty": {"empty_request"},
    }
    raw_results = {"malformed": malformed, "unknown": unknown, "empty": empty}
    for label, codes in expected_protocol_errors.items():
        response = raw_results[label]
        code = response_error_code(response or {})
        if code not in codes:
            return finalize_failure(
                logger,
                report_path,
                report,
                EXIT_PROTOCOL_FAILURE,
                "protocol_failure",
                f"{label} request returned {code!r} instead of one of {sorted(codes)}.",
            )

    try:
        reset_response = client.reset()
        report["checks"].append({"name": "reset_episode", "response": reset_response})
    except BridgeError as exc:
        return finalize_failure(logger, report_path, report, EXIT_RESET_FAILURE, "reset_failure", str(exc))

    if response_error(reset_response):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_RESET_FAILURE,
            "reset_failure",
            response_error_message(reset_response) or "Reset returned an error.",
        )

    reset_observation = reset_response.get("observation")
    if not isinstance(reset_observation, dict) or reset_observation.get("episodeStep") != 0:
        return finalize_failure(logger, report_path, report, EXIT_RESET_FAILURE, "reset_failure", "Reset did not produce episodeStep=0.")

    safe_action = sample_safe_action(config, __import__("random").Random(42))
    safe_action["durationMs"] = config.action_duration_ms

    try:
        step_response = client.step(safe_action)
    except BridgeError as exc:
        return finalize_failure(logger, report_path, report, EXIT_ACTION_FAILURE, "action_failure", str(exc))

    report["checks"].append({"name": "safe_step", "response": step_response})
    if response_error(step_response):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            response_error_message(step_response) or "Safe step returned an error.",
        )

    observation_after = step_response.get("observation")
    reward = step_response.get("reward")
    terminated = bool(step_response.get("terminated", False))
    truncated = bool(step_response.get("truncated", False))
    info = step_response.get("info") if isinstance(step_response.get("info"), dict) else {}
    logger.record_step(
        episode_id=int(observation_after.get("episodeId", 0)) if isinstance(observation_after, dict) else 0,
        step_index=int(observation_after.get("episodeStep", 0)) if isinstance(observation_after, dict) else 0,
        timestamp=utc_now(),
        action=safe_action,
        observation=observation_after,
        reward=reward,
        terminated=terminated,
        truncated=truncated,
        info=info,
        error=None,
        step_time_ms=float(info.get("elapsedMs", 0.0)) if isinstance(info, dict) else None,
    )

    if not isinstance(observation_after, dict):
        return finalize_failure(logger, report_path, report, EXIT_ACTION_FAILURE, "action_failure", "Safe step returned no observation.")

    if not isinstance(reward, (int, float)) or not math.isfinite(float(reward)):
        return finalize_failure(logger, report_path, report, EXIT_REWARD_FAILURE, "reward_failure", "Safe step reward was not finite.")

    elapsed_ms = float(info.get("elapsedMs", 0.0)) if isinstance(info, dict) else 0.0
    min_expected_elapsed = max(10.0, config.action_duration_ms * 0.5)
    max_expected_elapsed = config.action_duration_ms + 1000.0
    if elapsed_ms < min_expected_elapsed or elapsed_ms > max_expected_elapsed:
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            f"Safe step elapsedMs={elapsed_ms:.2f} was outside the expected range.",
        )

    if info.get("leftHandFound") and "leftDistanceBefore" not in info:
        return finalize_failure(logger, report_path, report, EXIT_ACTION_FAILURE, "action_failure", "Safe step info was missing leftDistanceBefore.")
    if info.get("rightHandFound") and "rightDistanceAfter" not in info:
        return finalize_failure(logger, report_path, report, EXIT_ACTION_FAILURE, "action_failure", "Safe step info was missing rightDistanceAfter.")

    clamped_action = clamped_test_action(config)
    try:
        clamped_response = client.step(clamped_action)
    except BridgeError as exc:
        return finalize_failure(logger, report_path, report, EXIT_ACTION_FAILURE, "action_failure", f"Clamped step failed: {exc}")

    if response_error(clamped_response):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            response_error_message(clamped_response) or "Clamped step returned an error.",
        )

    clamped_info = clamped_response.get("info") if isinstance(clamped_response.get("info"), dict) else {}
    report["checks"].append({"name": "clamped_step", "response": clamped_response})
    if not (clamped_info.get("leftTargetClamped") or clamped_info.get("rightTargetClamped") or clamped_info.get("blockedReason")):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            "Clamped/impossible target did not report clamping or a blocked reason.",
        )

    long_step_action = {
        "type": "step",
        "action": {
            "leftHandTargetLocal": [-0.2, 1.2, 0.5],
            "rightHandTargetLocal": [0.2, 1.2, 0.5],
            "durationMs": 800,
        },
    }
    concurrent_result: Dict[str, Any] = {}

    def run_concurrent_step() -> None:
        try:
            concurrent_result["response"] = raw_request(
                config.host,
                config.port,
                max(config.timeout_seconds, 5.0),
                json.dumps(long_step_action, separators=(",", ":")) + "\n",
            )
        except Exception as exc:
            concurrent_result["error"] = str(exc)

    step_thread = threading.Thread(target=run_concurrent_step, daemon=True)
    step_thread.start()
    time.sleep(0.15)

    try:
        concurrent_reset = client.reset()
    except BridgeError as exc:
        return finalize_failure(logger, report_path, report, EXIT_RESET_FAILURE, "reset_failure", f"Reset during active step failed: {exc}")

    step_thread.join(timeout=10.0)
    report["checks"].append({"name": "reset_during_active_step", "reset": concurrent_reset, "step": concurrent_result})
    if response_error(concurrent_reset):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_RESET_FAILURE,
            "reset_failure",
            response_error_message(concurrent_reset) or "Reset during active step returned an error.",
        )

    concurrent_step_response = concurrent_result.get("response")
    concurrent_step_code = response_error_code(concurrent_step_response or {})
    if concurrent_result.get("error"):
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            f"Concurrent step thread failed: {concurrent_result['error']}",
        )
    if concurrent_step_response is None:
        return finalize_failure(logger, report_path, report, EXIT_ACTION_FAILURE, "action_failure", "Concurrent step did not return a response.")
    if concurrent_step_code not in {None, "step_canceled_by_reset"} and concurrent_step_response.get("type") != "step_result":
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            f"Concurrent step returned unexpected result: {concurrent_step_code}",
        )

    scripted = run_script(
        [
            sys.executable,
            "scripts/run_scripted_pose_sequence.py",
            "--episode-length",
            str(args.scripted_episode_length),
            "--action-duration-ms",
            str(config.action_duration_ms),
        ]
    )
    report["checks"].append({"name": "scripted_sequence", "returncode": scripted.returncode})
    if scripted.returncode != 0:
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            f"run_scripted_pose_sequence.py failed with exit code {scripted.returncode}.",
        )

    random_policy = run_script(
        [
            sys.executable,
            "scripts/run_random_policy.py",
            "--episodes",
            str(args.random_episodes),
            "--episode-length",
            str(config.episode_length),
            "--seed",
            "42",
        ]
    )
    report["checks"].append({"name": "random_policy", "returncode": random_policy.returncode})
    if random_policy.returncode != 0:
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_ACTION_FAILURE,
            "action_failure",
            f"run_random_policy.py failed with exit code {random_policy.returncode}.",
        )

    stability = run_script(
        [
            sys.executable,
            "scripts/run_bridge_stability.py",
            "--cycles",
            str(args.stability_cycles),
            "--steps-per-cycle",
            "3",
        ]
    )
    report["checks"].append({"name": "stability", "returncode": stability.returncode})
    if stability.returncode != 0:
        return finalize_failure(
            logger,
            report_path,
            report,
            EXIT_STABILITY_FAILURE,
            "stability_failure",
            f"run_bridge_stability.py failed with exit code {stability.returncode}.",
        )

    report["passed"] = True
    report["finishedAt"] = utc_now()
    write_report(report_path, report)
    logger.finish(
        status="success",
        summary={
            "report": str(report_path.relative_to(ROOT_DIR)),
            "scriptedReturnCode": scripted.returncode,
            "randomPolicyReturnCode": random_policy.returncode,
            "stabilityReturnCode": stability.returncode,
        },
    )
    print(f"Validation report: {report_path}")
    print("PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
