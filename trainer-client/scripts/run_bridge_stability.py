from __future__ import annotations

import argparse
import json
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from rumble_env_client import BridgeError, add_common_args, create_client, create_run_logger, load_runtime_config, stability_cycle_actions


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run repeated bridge reset and step cycles to test stability.")
    add_common_args(parser, include_action_duration=True)
    parser.add_argument("--cycles", type=int, default=100, help="Number of reset/step cycles to run.")
    parser.add_argument("--steps-per-cycle", type=int, default=3, help="Number of steps to send after each reset.")
    return parser.parse_args()


def display_path(path: Path) -> str:
    try:
        return str(path.relative_to(Path(__file__).resolve().parents[1]))
    except ValueError:
        return str(path)


def json_dump_line(payload: Dict[str, Any]) -> str:
    return json.dumps(payload, separators=(",", ":"), ensure_ascii=False)


def timestamp() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def error_payload(code: str, message: str) -> Dict[str, Any]:
    return {"code": code, "message": message}


def response_error(response: Dict[str, Any]) -> Dict[str, Any] | None:
    error = response.get("error")
    if isinstance(error, dict):
        return error
    if isinstance(error, str):
        return {"code": error, "message": error}
    return None


def response_error_code(response: Dict[str, Any]) -> str | None:
    error = response_error(response)
    if error is None:
        return None
    code = error.get("code")
    if isinstance(code, str) and code:
        return code
    message = error.get("message")
    if isinstance(message, str) and message:
        return message
    return "bridge_error"


def response_error_message(response: Dict[str, Any]) -> str | None:
    error = response_error(response)
    if error is None:
        return None
    message = error.get("message")
    if isinstance(message, str) and message:
        return message
    code = error.get("code")
    if isinstance(code, str) and code:
        return code
    return "bridge_error"


def is_timeout_code(code: str | None) -> bool:
    if not code:
        return False
    return code.endswith("_timeout") or code == "timeout"


def main() -> int:
    args = parse_args()
    config = load_runtime_config(args)
    client = create_client(config)
    logger = create_run_logger("run_bridge_stability", config)
    cycles_path = logger.run_dir / "cycles.jsonl"
    cycles_handle = cycles_path.open("a", encoding="utf-8", newline="\n")

    success_cycles = 0
    failure_cycles = 0
    timeout_cycles = 0
    success_steps = 0
    failure_steps = 0
    timeout_steps = 0
    total_step_time_ms = 0.0
    max_step_time_ms = 0.0
    error_counter: Counter[str] = Counter()
    cycle_rows: list[dict[str, Any]] = []

    try:
        status = client.status()
        status_error = response_error(status)
        if status_error:
            code = response_error_code(status)
            message = response_error_message(status) or "Status request returned an error."
            error_counter[code or message] += 1
            logger.finish(
                status="failed",
                error=message,
                summary={"cyclesRequested": args.cycles, "statusError": status_error},
            )
            print(json.dumps(status, indent=2, sort_keys=True))
            print(message, file=sys.stderr)
            return 1

        print(json.dumps(status, indent=2, sort_keys=True))
        print(f"Starting stability run: cycles={args.cycles} stepsPerCycle={args.steps_per_cycle} runDir={logger.run_dir}")

        for cycle_index in range(1, args.cycles + 1):
            cycle_started = time.perf_counter()
            cycle_record: Dict[str, Any] = {
                "cycleIndex": cycle_index,
                "startedAt": timestamp(),
                "reset": None,
                "stepResults": [],
            }
            cycle_failed = False
            cycle_timed_out = False
            cycle_step_times: list[float] = []

            try:
                reset_response = client.reset()
                reset_error = response_error(reset_response)
                cycle_record["reset"] = reset_response
                if reset_error:
                    cycle_failed = True
                    code = response_error_code(reset_response) or "reset_error"
                    message = response_error_message(reset_response) or code
                    error_counter[code] += 1
                    if is_timeout_code(code):
                        cycle_timed_out = True
                    print(f"cycle={cycle_index} reset_error={code} message={message}")
                    cycles_handle.write(
                        json_dump_line(
                            {
                                "cycleIndex": cycle_index,
                                "startedAt": cycle_record["startedAt"],
                                "endedAt": timestamp(),
                                "status": "reset_error",
                                "error": reset_error,
                            }
                        )
                        + "\n"
                    )
                    cycles_handle.flush()
                    cycle_record["status"] = "reset_error"
                    cycle_record["error"] = reset_error
                    cycle_rows.append(cycle_record)
                    if cycle_timed_out:
                        timeout_cycles += 1
                    else:
                        failure_cycles += 1
                    continue

                episode_id = reset_response.get("episodeId")
                print(
                    f"cycle={cycle_index} reset episodeId={episode_id} "
                    f"resetMode={reset_response.get('resetMode')} sceneReady={reset_response.get('sceneReady')}"
                )

                for step_index, (label, action) in enumerate(stability_cycle_actions(config.action_duration_ms, cycle_index), start=1):
                    if step_index > max(1, args.steps_per_cycle):
                        break

                    started_at = time.perf_counter()
                    try:
                        response = client.step(action)
                        elapsed_ms = (time.perf_counter() - started_at) * 1000.0
                        cycle_step_times.append(elapsed_ms)
                        total_step_time_ms += elapsed_ms
                        max_step_time_ms = max(max_step_time_ms, elapsed_ms)

                        error = response_error(response)
                        if error:
                            cycle_failed = True
                            code = response_error_code(response) or "step_error"
                            message = response_error_message(response) or code
                            error_counter[code] += 1
                            if is_timeout_code(code):
                                cycle_timed_out = True
                                timeout_steps += 1
                            failure_steps += 1
                        else:
                            code = None
                            message = None
                            success_steps += 1

                        observation = response.get("observation")
                        info = response.get("info")
                        logger.record_step(
                            episode_id=int(episode_id or 0),
                            step_index=int(observation.get("episodeStep", step_index)) if isinstance(observation, dict) else step_index,
                            timestamp=timestamp(),
                            action=action,
                            observation=observation,
                            reward=response.get("reward"),
                            terminated=bool(response.get("terminated", False)),
                            truncated=bool(response.get("truncated", False)),
                            info=info,
                            error=error,
                            step_time_ms=elapsed_ms,
                        )

                        cycle_record["stepResults"].append(
                            {
                                "label": label,
                                "stepIndex": step_index,
                                "reward": response.get("reward"),
                                "error": error,
                                "stepTimeMs": elapsed_ms,
                            }
                        )
                        print(
                            f"cycle={cycle_index} step={step_index} label={label} "
                            f"reward={response.get('reward')} stepTimeMs={elapsed_ms:.2f} "
                            f"error={code or 'none'}"
                        )
                    except BridgeError as exc:
                        elapsed_ms = (time.perf_counter() - started_at) * 1000.0
                        cycle_step_times.append(elapsed_ms)
                        total_step_time_ms += elapsed_ms
                        max_step_time_ms = max(max_step_time_ms, elapsed_ms)
                        cycle_failed = True
                        failure_steps += 1
                        message = str(exc)
                        code = "bridge_timeout" if "timed out" in message.lower() else "bridge_error"
                        if is_timeout_code(code):
                            cycle_timed_out = True
                            timeout_steps += 1
                        error_counter[code] += 1
                        logger.record_step(
                            episode_id=int(episode_id or 0),
                            step_index=step_index,
                            timestamp=timestamp(),
                            action=action,
                            observation=None,
                            reward=None,
                            terminated=False,
                            truncated=False,
                            info=None,
                            error=error_payload(code, message),
                            step_time_ms=elapsed_ms,
                        )
                        cycle_record["stepResults"].append(
                            {
                                "label": label,
                                "stepIndex": step_index,
                                "reward": None,
                                "error": error_payload(code, message),
                                "stepTimeMs": elapsed_ms,
                            }
                        )
                        print(f"cycle={cycle_index} step={step_index} label={label} error={code} message={message}")

                if cycle_failed:
                    if cycle_timed_out:
                        timeout_cycles += 1
                    else:
                        failure_cycles += 1
                else:
                    success_cycles += 1

                cycle_record["status"] = "success" if not cycle_failed else ("timeout" if cycle_timed_out else "failed")
                cycle_record["durationMs"] = (time.perf_counter() - cycle_started) * 1000.0
                cycle_record["stepCount"] = len(cycle_record["stepResults"])
                cycle_record["averageStepTimeMs"] = (
                    sum(cycle_step_times) / len(cycle_step_times) if cycle_step_times else None
                )
                cycle_record["maxStepTimeMs"] = max(cycle_step_times) if cycle_step_times else None
                cycle_record["errorCounts"] = dict(error_counter)
                cycle_rows.append(cycle_record)
                cycles_handle.write(json_dump_line(cycle_record) + "\n")
                cycles_handle.flush()
            except BridgeError as exc:
                cycle_failed = True
                message = str(exc)
                code = "bridge_timeout" if "timed out" in message.lower() else "bridge_error"
                if is_timeout_code(code):
                    cycle_timed_out = True
                error_counter[code] += 1
                cycle_record["status"] = "timeout" if cycle_timed_out else "bridge_error"
                cycle_record["error"] = error_payload(code, message)
                cycle_record["durationMs"] = (time.perf_counter() - cycle_started) * 1000.0
                cycle_rows.append(cycle_record)
                cycles_handle.write(json_dump_line(cycle_record) + "\n")
                cycles_handle.flush()
                if cycle_timed_out:
                    timeout_cycles += 1
                else:
                    failure_cycles += 1
                print(f"cycle={cycle_index} bridge_error={code} message={message}")

        average_step_time_ms = (total_step_time_ms / (success_steps + failure_steps)) if (success_steps + failure_steps) else 0.0
        summary = {
            "cyclesRequested": args.cycles,
            "cycleSuccessCount": success_cycles,
            "cycleFailureCount": failure_cycles,
            "cycleTimeoutCount": timeout_cycles,
            "stepSuccessCount": success_steps,
            "stepFailureCount": failure_steps,
            "stepTimeoutCount": timeout_steps,
            "averageStepTimeMs": average_step_time_ms,
            "maxStepTimeMs": max_step_time_ms,
            "repeatedErrors": error_counter.most_common(10),
            "cyclesObserved": len(cycle_rows),
            "stepsObserved": success_steps + failure_steps,
            "cyclesLog": display_path(cycles_path),
        }
        status = "success" if failure_cycles == 0 and timeout_cycles == 0 and failure_steps == 0 else "failed"
        logger.finish(status=status, summary=summary)

        print(
            "\nSummary: "
            f"cycleSuccessCount={success_cycles} cycleFailureCount={failure_cycles} cycleTimeoutCount={timeout_cycles} "
            f"stepSuccessCount={success_steps} stepFailureCount={failure_steps} stepTimeoutCount={timeout_steps} "
            f"averageStepTimeMs={average_step_time_ms:.2f} maxStepTimeMs={max_step_time_ms:.2f}"
        )
        if error_counter:
            print("Repeated errors:")
            for code, count in error_counter.most_common(10):
                print(f"  {code}: {count}")
        print(f"Run log: {logger.run_dir}")
        print(f"Cycles log: {cycles_path}")
        return 0 if status == "success" else 2

    except BridgeError as exc:
        logger.finish(status="failed", error=str(exc))
        print(str(exc), file=sys.stderr)
        return 1
    except Exception as exc:
        logger.finish(status="failed", error=str(exc))
        raise
    finally:
        try:
            cycles_handle.close()
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
