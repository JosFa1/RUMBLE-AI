from __future__ import annotations

import argparse
import json
import random
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from .actions import random_safe_action, safe_test_action, scripted_pose_sequence
from .client import BridgeError
from .config import TrainerClientConfig, create_client, load_config, save_config
from .config import SafeHandBounds
from .logging import RunLogger, create_run_logger
from .summaries import bootstrap_result_lines, config_lines, error_message, observation_lines, print_lines, status_lines, step_lines
from .validation import run_full_validation, run_offline_validation


class OperatorConsole:
    def __init__(self, config: TrainerClientConfig, *, verbose: bool = False):
        self.config = config
        self.verbose = verbose
        self.client = create_client(config)
        self.last_status: dict[str, Any] | None = None
        self.last_error: str | None = None
        self.last_log_dir: Path | None = None

    def run(self) -> int:
        while True:
            self.print_screen()
            try:
                choice = input("Select command: ").strip().lower()
                if choice == "0":
                    return 0
                if choice == "1":
                    self.connect_and_status()
                elif choice == "2":
                    self.get_observation()
                elif choice == "3":
                    self.reset_episode()
                elif choice == "4":
                    self.send_safe_step()
                elif choice == "5":
                    self.run_scripted_sequence()
                elif choice == "6":
                    self.run_random_policy()
                elif choice == "7":
                    self.run_short_stability()
                elif choice == "8":
                    self.run_validation_menu()
                elif choice == "9":
                    self.show_config()
                elif choice == "10":
                    self.edit_config()
                elif choice == "11":
                    self.print_latest_run_folder()
                elif choice == "12":
                    self.print_troubleshooting()
                elif choice == "13":
                    self.run_bootstrap_diagnostics_menu()
                else:
                    print("Unknown selection.")
            except EOFError:
                print()
                return 0
            except KeyboardInterrupt:
                print("\nInterrupted.")
            self.pause()

    def print_screen(self) -> None:
        print()
        print("RUMBLE AI Trainer Operator")
        print("=" * 28)
        print_lines(status_lines(self.last_status, self.config, self.last_log_dir))
        if self.last_error:
            print(f"Console last error: {self.last_error}")
        print()
        print("Menu:")
        print("1. Connect and status check")
        print("2. Get observation")
        print("3. Reset episode")
        print("4. Send safe test step")
        print("5. Run scripted pose sequence")
        print("6. Run random policy test")
        print("7. Run short bridge stability test")
        print("8. Run validation")
        print("9. Show config")
        print("10. Edit basic config")
        print("11. Open latest run folder path")
        print("12. Print troubleshooting help")
        print("13. Bootstrap diagnostics")
        print("0. Exit")
        print()

    def connect_and_status(self) -> None:
        self.with_bridge("status", lambda: self.client.status(), self.print_status_result)

    def get_observation(self) -> None:
        self.with_bridge("get observation", lambda: self.client.get_observation(), self.print_observation_result)

    def reset_episode(self) -> None:
        self.with_bridge("reset", lambda: self.client.reset(), self.print_reset_result)

    def send_safe_step(self) -> None:
        action = safe_test_action(self.config)

        def request() -> dict[str, Any]:
            return self.client.step(action)

        self.with_bridge("safe step", request, lambda response: self.print_step_result(response, "safe test step"))

    def run_scripted_sequence(self) -> None:
        logger = create_run_logger("operator_scripted_pose_sequence", self.config)
        self.last_log_dir = logger.run_dir
        total_reward = 0.0
        failures = 0
        try:
            reset = self.client.reset()
            self.print_reset_result(reset)
            for index, (label, action) in enumerate(scripted_pose_sequence(self.config.action_duration_ms), start=1):
                started = time.perf_counter()
                response = self.client.step(action)
                elapsed_ms = (time.perf_counter() - started) * 1000.0
                reward = response.get("reward")
                if isinstance(reward, (int, float)):
                    total_reward += float(reward)
                error = error_message(response)
                if error:
                    failures += 1
                observation = response.get("observation")
                logger.record_step(
                    episode_id=int(observation.get("episodeId", 0)) if isinstance(observation, dict) else 0,
                    step_index=int(observation.get("episodeStep", index)) if isinstance(observation, dict) else index,
                    timestamp=utc_now(),
                    action=action,
                    observation=observation,
                    reward=reward,
                    terminated=bool(response.get("terminated", False)),
                    truncated=bool(response.get("truncated", False)),
                    info=response.get("info"),
                    error=error,
                    step_time_ms=elapsed_ms,
                )
                print(f"[{index}/5] {label}")
                print_lines(step_lines(response, label))
                if response.get("terminated") or response.get("truncated"):
                    break
            status = "success" if failures == 0 else "failed"
            logger.finish(status=status, summary={"totalReward": total_reward, "failureCount": failures})
            print(f"Scripted sequence {status}. Run log: {logger.run_dir}")
        except Exception as exc:
            finish_logger(logger, exc)
            self.handle_exception(exc)

    def run_random_policy(self) -> None:
        episodes = self.ask_int("Episodes", default=1, minimum=1, maximum=100)
        steps = self.ask_int("Steps per episode", default=min(self.config.episode_length, 10), minimum=1, maximum=1000)
        rng = random.Random(self.ask_int("Seed", default=1337, minimum=0, maximum=2_147_483_647))
        logger = create_run_logger("operator_random_policy", self.config)
        self.last_log_dir = logger.run_dir
        total_reward = 0.0
        failures = 0
        step_count = 0
        try:
            for episode in range(1, episodes + 1):
                reset = self.client.reset()
                print(f"Episode {episode} reset: episodeId={reset.get('episodeId')} sceneReady={reset.get('sceneReady')}")
                for step in range(1, steps + 1):
                    action = random_safe_action(self.config, rng)
                    started = time.perf_counter()
                    response = self.client.step(action)
                    elapsed_ms = (time.perf_counter() - started) * 1000.0
                    reward = response.get("reward")
                    if isinstance(reward, (int, float)):
                        total_reward += float(reward)
                    error = error_message(response)
                    if error:
                        failures += 1
                    step_count += 1
                    observation = response.get("observation")
                    logger.record_step(
                        episode_id=int(observation.get("episodeId", episode)) if isinstance(observation, dict) else episode,
                        step_index=int(observation.get("episodeStep", step)) if isinstance(observation, dict) else step,
                        timestamp=utc_now(),
                        action=action,
                        observation=observation,
                        reward=reward,
                        terminated=bool(response.get("terminated", False)),
                        truncated=bool(response.get("truncated", False)),
                        info=response.get("info"),
                        error=error,
                        step_time_ms=elapsed_ms,
                    )
                    print(f"episode={episode} step={step} reward={reward} error={error or 'none'}")
                    if response.get("terminated") or response.get("truncated"):
                        break
            status = "success" if failures == 0 else "failed"
            logger.finish(status=status, summary={"totalReward": total_reward, "stepCount": step_count, "failureCount": failures})
            print(f"Random policy {status}. Run log: {logger.run_dir}")
        except Exception as exc:
            finish_logger(logger, exc)
            self.handle_exception(exc)

    def run_short_stability(self) -> None:
        from .validation import run_python_script

        cycles = self.ask_int("Cycles", default=10, minimum=1, maximum=1000)
        result = run_python_script(
            "run_bridge_stability.py",
            ["--cycles", str(cycles), "--steps-per-cycle", "3", "--action-duration-ms", str(self.config.action_duration_ms)],
            timeout_seconds=600,
            progress=print,
        )
        print(result.summary)
        print_tail(result.stdout)
        if result.stderr:
            print_tail(result.stderr)

    def run_validation_menu(self) -> None:
        print("1. Offline validation")
        print("2. Full live validation")
        selection = input("Select validation: ").strip()
        if selection == "1":
            result = run_offline_validation(progress=print)
        elif selection == "2":
            result = run_full_validation(progress=print)
        else:
            print("Validation canceled.")
            return
        print(result.summary)
        print_tail(result.stdout)
        if result.stderr:
            print_tail(result.stderr)

    def run_bootstrap_diagnostics_menu(self) -> None:
        options = {
            "1": ("get_bootstrap_report", "Get bootstrap report"),
            "2": ("run_scene_inventory", "Run scene inventory"),
            "3": ("run_actor_discovery", "Run actor discovery"),
            "4": ("run_capability_discovery", "Run capability discovery"),
            "5": ("run_single_actor_summon_probe", "Run gated summon probe"),
            "6": ("run_move_probe", "Run gated move/modifier probe"),
            "7": ("run_multi_actor_probe", "Run gated multi-actor feasibility probe"),
            "8": ("run_actor_interaction_probe", "Run gated collision interaction probe"),
            "9": ("run_arena_rebuild", "Run arena build/rebuild"),
            "10": ("retry_bootstrap", "Reset and retry failed bootstrap"),
            "11": ("run_actor_completeness", "Run actor completeness report"),
            "12": ("run_lifecycle_timeline", "Write lifecycle timeline snapshot"),
            "13": ("run_local_player_lifecycle_discovery", "Discover complete local-player lifecycle candidates"),
            "14": ("run_lifecycle_trigger_discovery", "Discover lifecycle trigger candidates"),
            "15": ("run_lifecycle_mode_comparison", "Compare lifecycle modes"),
            "16": ("run_lifecycle_trigger_probe", "Run safe lifecycle trigger probe report"),
            "17": ("run_actor_candidate_ranking", "Rank actor candidates"),
            "18": ("run_missing_lifecycle_dependency_report", "Write missing lifecycle dependency report"),
            "19": ("run_summon_context_discovery", "Discover wider summon context"),
        }
        for key, (_, label) in options.items():
            print(f"{key}. {label}")
        selection = input("Select bootstrap action: ").strip()
        selected = options.get(selection)
        if selected is None:
            print("Bootstrap action canceled.")
            return

        request_type, label = selected
        self.with_bridge(label, lambda: self.client.bootstrap_request(request_type), self.print_bootstrap_result)

    def show_config(self) -> None:
        print_lines(config_lines(self.config))

    def edit_config(self) -> None:
        print("Edit config. Press Enter to keep a value.")
        updates: dict[str, Any] = {}
        updates["host"] = self.ask_string("host", self.config.host)
        updates["port"] = self.ask_int("port", self.config.port, 1, 65535)
        updates["timeout_ms"] = self.ask_int("timeoutMs", self.config.timeout_ms, 100, 120000)
        updates["episode_length"] = self.ask_int("episodeLength", self.config.episode_length, 1, 100000)
        updates["action_duration_ms"] = self.ask_int("actionDurationMs", self.config.action_duration_ms, 1, 1000)
        updates["strict_protocol_version"] = self.ask_bool("strictProtocolVersion", self.config.strict_protocol_version)
        next_bounds = self.ask_safe_bounds(self.config.safe_hand_bounds)
        if next_bounds is not None:
            updates["safe_hand_bounds"] = next_bounds
        next_config = self.config.with_overrides(**updates)
        print("New config preview:")
        print_lines(config_lines(next_config))
        if input("Write config? [y/N]: ").strip().lower() == "y":
            path = save_config(next_config)
            self.config = load_config(path)
            self.client = create_client(self.config)
            print(f"Saved {path}. A timestamped backup was created if the file already existed.")
        else:
            print("Config unchanged.")

    def print_latest_run_folder(self) -> None:
        log_dir = self.config.resolve_log_directory()
        candidates = [path for path in log_dir.glob("*") if path.is_dir()]
        latest = max(candidates, key=lambda path: path.stat().st_mtime, default=None)
        if latest is None:
            print(f"No run folders found in {log_dir}")
            return
        self.last_log_dir = latest
        print(latest)

    def print_troubleshooting(self) -> None:
        print("Troubleshooting:")
        print(f"- Start RUMBLE with the mod loaded before live commands.")
        print(f"- Confirm the mod log says TrainingBridgeServer listening on {self.config.host}:{self.config.port}.")
        print("- If sceneReady=false, wait for Gym scene setup or inspect TrainingEnvironmentManager log lines.")
        print("- If playerRootFound=false, inspect TrainingActorLocator and the current scene.")
        print("- If protocol versions differ, align mod, protocol docs, schemas, and config.json on 0.3.")
        print("- Generated run logs live under trainer-client/runs.")

    def with_bridge(self, label: str, call: Callable[[], dict[str, Any]], printer: Callable[[dict[str, Any]], None]) -> None:
        try:
            response = call()
            self.last_error = error_message(response)
            if response.get("type") == "status_result":
                self.last_status = response
            printer(response)
            if self.verbose:
                print_raw(response)
        except Exception as exc:
            self.handle_exception(exc)

    def print_status_result(self, response: dict[str, Any]) -> None:
        self.last_status = response
        print_lines(status_lines(response, self.config, self.last_log_dir))

    def print_observation_result(self, response: dict[str, Any]) -> None:
        print_lines(observation_lines(response))

    def print_reset_result(self, response: dict[str, Any]) -> None:
        print(f"Reset result: episodeId={response.get('episodeId')} sceneReady={response.get('sceneReady')} resetMode={response.get('resetMode')}")
        print_lines(observation_lines(response))

    def print_step_result(self, response: dict[str, Any], label: str) -> None:
        print_lines(step_lines(response, label))
        observation = response.get("observation")
        if isinstance(observation, dict):
            print("Observation after step:")
            print_lines(observation_lines(observation))

    def print_bootstrap_result(self, response: dict[str, Any]) -> None:
        print_lines(bootstrap_result_lines(response))
        bootstrap_status = response.get("bootstrapStatus")
        if isinstance(bootstrap_status, dict):
            self.last_status = bootstrap_status

    def handle_exception(self, exc: Exception) -> None:
        if isinstance(exc, BridgeError):
            message = str(exc)
        else:
            message = f"{type(exc).__name__}: {exc}"
        self.last_error = message
        print(message, file=sys.stderr)

    @staticmethod
    def pause() -> None:
        try:
            input("\nPress Enter to continue...")
        except EOFError:
            pass

    @staticmethod
    def ask_string(label: str, current: str) -> str:
        value = input(f"{label} [{current}]: ").strip()
        return value or current

    @staticmethod
    def ask_int(label: str, default: int, minimum: int, maximum: int) -> int:
        while True:
            value = input(f"{label} [{default}]: ").strip()
            if not value:
                return default
            try:
                parsed = int(value)
            except ValueError:
                print("Enter a whole number.")
                continue
            if parsed < minimum or parsed > maximum:
                print(f"Enter a value from {minimum} to {maximum}.")
                continue
            return parsed

    @staticmethod
    def ask_bool(label: str, current: bool) -> bool:
        suffix = "Y/n" if current else "y/N"
        value = input(f"{label} [{suffix}]: ").strip().lower()
        if not value:
            return current
        return value in {"y", "yes", "true", "1"}

    @staticmethod
    def ask_safe_bounds(current: SafeHandBounds) -> SafeHandBounds | None:
        print("safeHandBounds current JSON:")
        print(json.dumps(current.to_dict(), indent=2, sort_keys=True))
        value = input("Paste new safeHandBounds JSON, or press Enter to keep: ").strip()
        if not value:
            return None
        try:
            parsed = json.loads(value)
            return SafeHandBounds.from_mapping(parsed, current)
        except Exception as exc:
            print(f"Could not parse safeHandBounds, keeping current value: {exc}")
            return None


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def print_raw(payload: dict[str, Any]) -> None:
    print(json.dumps(payload, indent=2, sort_keys=True))


def print_tail(text: str, lines: int = 20) -> None:
    if not text:
        return
    payload = text.strip().splitlines()
    for line in payload[-lines:]:
        print(line)


def finish_logger(logger: RunLogger, exc: Exception) -> None:
    try:
        logger.finish(status="failed", error=str(exc))
    except Exception:
        pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Human-facing operator console for the RUMBLE trainer bridge.")
    parser.add_argument("--config", default=None, help="Path to config.json, default: trainer-client/config.json")
    parser.add_argument("--verbose", action="store_true", help="Print raw JSON responses after concise summaries.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    config = load_config(args.config)
    return OperatorConsole(config, verbose=args.verbose).run()
