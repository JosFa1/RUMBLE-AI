from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Dict, Iterable

from .config import TrainerClientConfig


def yes_no(value: Any) -> str:
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return "unknown"
    return str(value)


def error_message(response: Dict[str, Any] | None) -> str | None:
    if not isinstance(response, dict):
        return None
    error = response.get("error")
    if isinstance(error, dict):
        code = error.get("code")
        message = error.get("message")
        if code and message:
            return f"{code}: {message}"
        if message:
            return str(message)
        if code:
            return str(code)
    if isinstance(error, str) and error:
        return error
    return None


def format_vector(value: Any) -> str:
    if not isinstance(value, dict):
        return "missing"
    parts = []
    for axis in ("x", "y", "z"):
        item = value.get(axis)
        if isinstance(item, (int, float)):
            parts.append(f"{axis}={item:.3f}")
        else:
            parts.append(f"{axis}=?")
    return " ".join(parts)


def observation_payload(response_or_observation: Dict[str, Any]) -> Dict[str, Any] | None:
    if not isinstance(response_or_observation, dict):
        return None
    observation = response_or_observation.get("observation")
    if isinstance(observation, dict):
        return observation
    if "sceneReady" in response_or_observation or "episodeStep" in response_or_observation:
        return response_or_observation
    return None


def status_lines(status: Dict[str, Any] | None, config: TrainerClientConfig, last_log: Path | None = None) -> list[str]:
    status = status if isinstance(status, dict) else {}
    return [
        f"Bridge: {'connected' if status else 'disconnected'}",
        f"Host: {config.host}",
        f"Port: {config.port}",
        f"Protocol: expected {config.protocol_version}, server {status.get('protocolVersion', 'unknown')}",
        f"Bridge running: {yes_no(status.get('bridgeRunning'))}",
        f"Scene ready: {yes_no(status.get('sceneReady'))}",
        f"Player root found: {yes_no(status.get('playerRootFound'))}",
        f"Bootstrap stage: {status.get('bootstrapStage') or 'unknown'}",
        f"Bootstrap ready: {yes_no(status.get('bootstrapReady'))}",
        f"Actor mode: {status.get('actorMode') or 'unknown'}",
        "Ready meaning: bridge/scene usable; actor completeness is reported separately",
        f"Bootstrap failed: {yes_no(status.get('bootstrapFailed'))}",
        f"Bootstrap failure: {status.get('bootstrapFailureReason') or 'none'}",
        f"Gym loaded: {yes_no(status.get('gymLoaded'))}",
        f"Loader removed: {yes_no(status.get('loaderRemoved'))}",
        f"Loader inert: {yes_no(status.get('loaderInert'))}",
        f"Primary actor found: {yes_no(status.get('primaryActorFound'))}",
        f"Arena built: {yes_no(status.get('arenaBuilt'))}",
        f"Active scene: {status.get('activeScene') or 'unknown'}",
        f"Loaded scenes: {format_loaded_scenes(status.get('loadedScenes'))}",
        f"Actor discovery: {status.get('actorDiscoveryStatus') or 'unknown'}",
        f"Capability discovery: {status.get('capabilityDiscoveryStatus') or 'unknown'}",
        f"Summon probe: {status.get('summonProbeStatus') or 'unknown'}",
        f"Move probe: {status.get('moveProbeStatus') or 'unknown'}",
        f"Multi-actor probe: {status.get('multiActorProbeStatus') or 'unknown'}",
        f"Interaction probe: {status.get('actorInteractionProbeStatus') or 'unknown'}",
        f"Actor completeness: {status.get('actorCompletenessClassification') or 'unknown'}",
        f"Visible model: {yes_no(status.get('hasVisibleModel'))} ({status.get('rendererCount', 0)} renderers)",
        f"Body/head/hands: {yes_no(status.get('hasBody'))}/{yes_no(status.get('hasHead'))}/{yes_no(status.get('hasHands'))}",
        f"Movement/physics: {yes_no(status.get('hasMovementSystem'))}/{yes_no(status.get('hasPhysicsOrGrounding'))}",
        f"Health/ownership: {yes_no(status.get('hasHealth'))}/{yes_no(status.get('hasOwnership'))}",
        f"Summon context/real summon: {yes_no(status.get('hasSummonContext'))}/{yes_no(status.get('realSummonConfirmed'))}",
        f"Root/hand motion confirmed: {yes_no(status.get('rootMotionConfirmed'))}/{yes_no(status.get('handMotionConfirmed'))}",
        f"Only ghost hands detected: {yes_no(status.get('onlyGhostHandsDetected'))}",
        f"Best actor: {status.get('currentBestActorPath') or 'unknown'} ({status.get('currentBestActorScene') or 'unknown'})",
        f"Complete actor found: {yes_no(status.get('completeActorFound'))}",
        f"Best complete actor: {status.get('bestCompleteActorPath') or 'none'}",
        f"Lifecycle mode: {status.get('lifecycleMode') or 'unknown'}",
        f"Lifecycle probe: {status.get('lifecycleProbeStatus') or 'unknown'}",
        f"Missing lifecycle dependency: {status.get('missingLifecycleDependency') or 'unknown'}",
        f"Actor completeness report: {status.get('latestActorCompletenessReport') or 'none'}",
        f"Lifecycle timeline report: {status.get('latestLifecycleTimelineReport') or 'none'}",
        f"Lifecycle report: {status.get('latestLocalPlayerLifecycleDiscoveryReport') or 'none'}",
        f"Lifecycle trigger discovery report: {status.get('latestLifecycleTriggerDiscoveryReport') or 'none'}",
        f"Lifecycle mode comparison report: {status.get('latestLifecycleModeComparisonReport') or 'none'}",
        f"Lifecycle trigger probe report: {status.get('latestLifecycleTriggerProbeReport') or 'none'}",
        f"Actor candidate ranking report: {status.get('latestActorCandidateRankingReport') or 'none'}",
        f"Missing dependency report: {status.get('latestMissingLifecycleDependencyReport') or 'none'}",
        f"Summon context report: {status.get('latestSummonContextReport') or 'none'}",
        f"Real summon report: {status.get('latestRealSummonProbeReport') or 'none'}",
        f"Pruning comparison report: {status.get('latestPruningComparisonReport') or 'none'}",
        f"Source scene: {status.get('sourceSceneName') or 'unknown'}",
        f"Training scene: {status.get('trainingSceneName') or 'unknown'}",
        f"Actor: {status.get('actorName') or status.get('playerRootPath') or 'unknown'}",
        f"Latest dump: {status.get('latestDumpPath') or latest_dump_path(status)}",
        f"Episode id: {status.get('episodeId', 'unknown')}",
        f"Episode step: {status.get('episodeStep', 'unknown')}",
        f"Tick: {status.get('tick', 'unknown')}",
        f"Time seconds: {status.get('timeSeconds', 'unknown')}",
        f"Last request: {status.get('lastRequestType', 'unknown')}",
        f"Last reward: {status.get('lastReward', 'unknown')}",
        f"Last error: {status.get('lastError') or error_message(status) or 'none'}",
        f"Last run log: {last_log if last_log else 'none'}",
    ]


def latest_dump_path(status: Dict[str, Any]) -> str:
    paths = status.get("latestDumpPaths")
    if isinstance(paths, list) and paths:
        return str(paths[-1])
    return "none"


def format_loaded_scenes(value: Any) -> str:
    if not isinstance(value, list) or not value:
        return "unknown"
    names = [str(item) for item in value[:6]]
    suffix = "" if len(value) <= 6 else f" (+{len(value) - 6} more)"
    return ", ".join(names) + suffix


def observation_lines(response_or_observation: Dict[str, Any]) -> list[str]:
    observation = observation_payload(response_or_observation)
    if observation is None:
        return ["Observation: missing"]
    required = ["protocolVersion", "tick", "timeSeconds", "sceneReady", "episodeId", "episodeStep"]
    missing = [name for name in required if name not in observation]
    warnings = observation.get("warnings")
    if not isinstance(warnings, list):
        warnings = []
    return [
        f"Protocol: {observation.get('protocolVersion', 'unknown')}",
        f"Tick: {observation.get('tick', 'unknown')}",
        f"Time seconds: {observation.get('timeSeconds', 'unknown')}",
        f"Scene ready: {yes_no(observation.get('sceneReady'))}",
        f"Episode id: {observation.get('episodeId', 'unknown')}",
        f"Episode step: {observation.get('episodeStep', 'unknown')}",
        f"Root position: {format_vector(observation.get('rootPosition'))}",
        f"Head position: {format_vector(observation.get('headPosition'))}",
        f"Left hand position: {format_vector(observation.get('leftHandPosition'))}",
        f"Right hand position: {format_vector(observation.get('rightHandPosition'))}",
        f"Health: {observation.get('health', 'unknown')}",
        f"Warnings: {len(warnings)}" + (f" - {'; '.join(str(item) for item in warnings[:3])}" if warnings else ""),
        f"Missing required fields: {', '.join(missing) if missing else 'none'}",
    ]


def step_lines(response: Dict[str, Any], action_label: str = "step") -> list[str]:
    info = response.get("info") if isinstance(response, dict) else None
    if not isinstance(info, dict):
        info = {}
    lines = [
        f"Action: {action_label}",
        f"Reward: {response.get('reward', 'missing')}",
        f"Elapsed ms: {info.get('elapsedMs', 'missing')}",
        f"Action applied: {yes_no(info.get('actionApplied'))}",
        f"Left hand found: {yes_no(info.get('leftHandFound'))}",
        f"Right hand found: {yes_no(info.get('rightHandFound'))}",
        f"Target clamping: left={yes_no(info.get('leftTargetClamped'))} right={yes_no(info.get('rightTargetClamped'))}",
        f"Distances: left {info.get('leftDistanceBefore', 'missing')} -> {info.get('leftDistanceAfter', 'missing')}; right {info.get('rightDistanceBefore', 'missing')} -> {info.get('rightDistanceAfter', 'missing')}",
        f"Blocked reason: {info.get('blockedReason') or 'none'}",
        f"Hand positions changed: {hand_positions_changed(response)}",
        f"Terminated: {yes_no(response.get('terminated'))}",
        f"Truncated: {yes_no(response.get('truncated'))}",
        f"Error: {error_message(response) or 'none'}",
    ]
    return lines


def bootstrap_result_lines(response: Dict[str, Any]) -> list[str]:
    if not isinstance(response, dict):
        return ["Bootstrap request: missing response"]
    return [
        f"Request: {response.get('requestType', 'unknown')}",
        f"Succeeded: {yes_no(response.get('succeeded'))}",
        f"Status: {response.get('status') or 'unknown'}",
        f"Bootstrap stage: {response.get('bootstrapStage') or 'unknown'}",
        f"Bootstrap ready: {yes_no(response.get('bootstrapReady'))}",
        f"Bootstrap failed: {yes_no(response.get('bootstrapFailed'))}",
        f"Report path: {response.get('reportPath') or latest_dump_path(response) or 'none'}",
        f"Message: {response.get('message') or error_message(response) or 'none'}",
    ]


def hand_positions_changed(response: Dict[str, Any]) -> str:
    info = response.get("info") if isinstance(response, dict) else None
    if not isinstance(info, dict):
        return "unknown"
    changed = []
    for side in ("left", "right"):
        before = info.get(f"{side}DistanceBefore")
        after = info.get(f"{side}DistanceAfter")
        if isinstance(before, (int, float)) and isinstance(after, (int, float)):
            changed.append(after < before)
    if not changed:
        return "unknown"
    return "true" if any(changed) else "false"


def config_lines(config: TrainerClientConfig) -> list[str]:
    return [
        f"Config path: {config.source_path}",
        f"host: {config.host}",
        f"port: {config.port}",
        f"timeoutMs: {config.timeout_ms}",
        f"episodeLength: {config.episode_length}",
        f"actionDurationMs: {config.action_duration_ms}",
        f"logDirectory: {config.log_directory}",
        f"protocolVersion: {config.protocol_version}",
        f"strictProtocolVersion: {config.strict_protocol_version}",
        f"safeHandBounds: {json.dumps(config.safe_hand_bounds.to_dict(), separators=(',', ':'))}",
    ]


def print_lines(lines: Iterable[str]) -> None:
    for line in lines:
        print(line)
