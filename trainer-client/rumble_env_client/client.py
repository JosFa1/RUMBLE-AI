from __future__ import annotations

import json
import socket
import warnings
from dataclasses import dataclass
from typing import Any, Dict


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8765
DEFAULT_TIMEOUT_SECONDS = 5.0


class BridgeError(RuntimeError):
    pass


@dataclass
class RumbleEnvClient:
    host: str = DEFAULT_HOST
    port: int = DEFAULT_PORT
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS
    protocol_version: str | None = None
    strict_protocol_version: bool = False
    _warned_protocol_mismatch: bool = False

    def request(self, payload: Dict[str, Any]) -> Dict[str, Any]:
        raw_request = json.dumps(payload, separators=(",", ":")) + "\n"

        try:
            with socket.create_connection((self.host, self.port), timeout=self.timeout_seconds) as sock:
                sock.settimeout(self.timeout_seconds)
                sock.sendall(raw_request.encode("utf-8"))
                raw_response = self._read_line(sock)
        except ConnectionRefusedError as exc:
            raise BridgeError(
                f"Could not connect to {self.host}:{self.port}. Is RUMBLE running with the mod loaded?"
            ) from exc
        except socket.timeout as exc:
            raise BridgeError(f"Timed out talking to {self.host}:{self.port}.") from exc
        except OSError as exc:
            raise BridgeError(f"Socket error talking to {self.host}:{self.port}: {exc}") from exc

        try:
            response = json.loads(raw_response)
        except json.JSONDecodeError as exc:
            raise BridgeError(f"Bridge returned invalid JSON: {raw_response!r}") from exc

        if not isinstance(response, dict):
            raise BridgeError(f"Bridge returned a non-object JSON response: {response!r}")

        expected_version = self.protocol_version
        if expected_version:
            observed_version = response.get("protocolVersion")
            if observed_version != expected_version:
                message = f"Protocol mismatch: expected {expected_version}, got {observed_version}."
                if self.strict_protocol_version:
                    raise BridgeError(message)
                if not self._warned_protocol_mismatch:
                    warnings.warn(message, RuntimeWarning, stacklevel=2)
                    self._warned_protocol_mismatch = True

        return response

    def status(self) -> Dict[str, Any]:
        return self.request({"type": "status"})

    def get_observation(self) -> Dict[str, Any]:
        return self.request({"type": "get_observation"})

    def reset(self) -> Dict[str, Any]:
        return self.request({"type": "reset_episode"})

    def step(self, action: Dict[str, Any]) -> Dict[str, Any]:
        if not isinstance(action, dict):
            raise BridgeError("step(action) expects an action dictionary.")

        return self.request({"type": "step", "action": action})

    @staticmethod
    def _read_line(sock: socket.socket) -> str:
        buffer = bytearray()

        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break

            buffer.extend(chunk)
            if b"\n" in chunk or len(buffer) > 65536:
                break

        if not buffer:
            raise BridgeError("Bridge returned an empty response.")

        line = buffer.split(b"\n", 1)[0].decode("utf-8", errors="replace").strip()
        if not line:
            raise BridgeError("Bridge returned an empty response line.")

        return line
