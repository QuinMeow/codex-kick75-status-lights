#!/usr/bin/python3
# SPDX-License-Identifier: MIT
"""Aggregate Codex hook events and drive the Kick75 side LEDs."""

import json
import os
import re
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Callable, Dict, Optional

from codex_kick75_common import APP_DIR, VERSION

SOCKET_PATH = APP_DIR / "status.sock"
STATE_PATH = APP_DIR / "state.json"
LOG_PATH = APP_DIR / "daemon.log"
LEDCTL_PATH = Path(os.environ.get("CODEX_KICK75_LEDCTL", str(APP_DIR / "kick75_ledctl")))
GREEN_HOLD_SECONDS = float(os.environ.get("CODEX_KICK75_GREEN_HOLD", "10"))
STALE_TASK_SECONDS = float(os.environ.get("CODEX_KICK75_STALE_TASK", str(12 * 60 * 60)))
HID_RETRY_SECONDS = float(os.environ.get("CODEX_KICK75_HID_RETRY", "10"))
HID_RECONNECT_CHECK_SECONDS = float(os.environ.get("CODEX_KICK75_RECONNECT_CHECK", "10"))
CLIENT_READ_TIMEOUT_SECONDS = float(os.environ.get("CODEX_KICK75_CLIENT_TIMEOUT", "0.5"))
MAX_MESSAGE_SIZE = 1024 * 1024
MAX_LOG_SIZE = 1024 * 1024
COLOR_SIDE_STATES = {
    "red": "0264010000ff0000",
    "yellow": "0264010000ffb400",
    "green": "026401000000ff00",
}


class StatusAggregator:
    def __init__(
        self,
        tasks: Optional[Dict[str, Dict[str, Any]]] = None,
        clock: Callable[[], float] = time.time,
    ) -> None:
        self.tasks: Dict[str, Dict[str, Any]] = tasks or {}
        self.clock = clock

    def handle(self, event: Dict[str, Any]) -> None:
        event_name = event.get("hook_event_name")
        session_id = event.get("session_id")
        if not isinstance(event_name, str) or not isinstance(session_id, str) or not session_id:
            return

        now = self.clock()
        current = self.tasks.get(session_id, {})
        turn_id = event.get("turn_id")

        if event_name == "SessionEnd":
            self.tasks.pop(session_id, None)
            return
        if event_name == "UserPromptSubmit":
            status = "running"
            detail = None
        elif event_name == "PermissionRequest":
            status = "error"
            detail = "permission"
        elif event_name == "PostToolUse":
            if event.get("tool_failed") is True:
                status = "error"
                detail = "tool_failure"
            else:
                status = "running"
                detail = None
        elif event_name == "Stop":
            if current.get("status") == "error":
                status = "error"
                detail = current.get("detail")
            else:
                status = "completed"
                detail = None
        else:
            return

        self.tasks[session_id] = {
            "status": status,
            "detail": detail,
            "turn_id": turn_id if isinstance(turn_id, str) else current.get("turn_id"),
            "updated_at": now,
        }

    def expire(self) -> None:
        now = self.clock()
        expired = []
        for session_id, task in self.tasks.items():
            age = now - float(task.get("updated_at", 0))
            if task.get("status") == "completed" and age >= GREEN_HOLD_SECONDS:
                expired.append(session_id)
            elif age >= STALE_TASK_SECONDS:
                expired.append(session_id)
        for session_id in expired:
            self.tasks.pop(session_id, None)

    def effective(self) -> str:
        statuses = {task.get("status") for task in self.tasks.values()}
        if "error" in statuses:
            return "red"
        if "running" in statuses:
            return "yellow"
        if "completed" in statuses:
            return "green"
        return "idle"


class LedController:
    def __init__(self, baseline: Optional[str] = None, effective: str = "idle") -> None:
        self.baseline = baseline
        self.effective = effective
        self.last_attempt = 0.0
        self.last_health_check = 0.0
        self.hardware_available: Optional[bool] = None
        self.hardware_error: Optional[str] = None
        self.hardware_checked_at: Optional[float] = None

    def record_hardware_result(self, error: Optional[BaseException] = None) -> None:
        self.hardware_available = error is None
        self.hardware_error = None if error is None else str(error)
        self.hardware_checked_at = time.time()

    def _run(self, arguments: list) -> str:
        result = subprocess.run(
            [str(LEDCTL_PATH)] + arguments,
            check=False,
            capture_output=True,
            text=True,
            timeout=12,
        )
        if result.returncode != 0:
            message = result.stderr.strip() or result.stdout.strip() or "unknown HID error"
            raise RuntimeError(message)
        self.record_hardware_result()
        return result.stdout

    def _read_side_state(self) -> str:
        output = self._run(["--get-side"])
        match = re.search(r"^SIDE_STATE=([0-9a-fA-F]{16})$", output, re.MULTILINE)
        if not match:
            raise RuntimeError("kick75_ledctl returned no SIDE_STATE")
        return match.group(1).lower()

    def _read_baseline(self) -> str:
        return self._read_side_state()

    def apply(self, desired: str, force: bool = False) -> bool:
        now = time.time()
        if not force and desired != self.effective and now - self.last_attempt < HID_RETRY_SECONDS:
            return False
        if desired == self.effective:
            return False
        self.last_attempt = now

        if desired == "idle":
            if self.baseline:
                self._run(["--set-side", self.baseline])
            self.baseline = None
            self.effective = "idle"
            return True

        if self.baseline is None:
            self.baseline = self._read_baseline()
        self._run(["--color", desired])
        self.effective = desired
        self.last_health_check = time.time()
        return True

    def health_check(self, force: bool = False) -> bool:
        """Reapply an active color when the keyboard has reset its side LEDs."""
        expected = COLOR_SIDE_STATES.get(self.effective)
        if expected is None:
            return False
        now = time.time()
        if not force and now - self.last_health_check < HID_RECONNECT_CHECK_SECONDS:
            return False
        self.last_health_check = now
        actual = self._read_side_state()
        if actual == expected:
            self.record_hardware_result()
            return False
        self._run(["--color", self.effective])
        self.record_hardware_result()
        return True


class CodexKick75Daemon:
    def __init__(self) -> None:
        saved = self._load_state()
        self.aggregator = StatusAggregator(saved.get("tasks", {}))
        # The keyboard may have reset while the daemon was down, so force the
        # persisted aggregate state to be replayed on startup.
        self.controller = LedController(saved.get("baseline"), "unknown")
        self.stopping = False

    @staticmethod
    def _load_state() -> Dict[str, Any]:
        try:
            with STATE_PATH.open("r", encoding="utf-8") as handle:
                value = json.load(handle)
            if not isinstance(value, dict):
                return {}
            tasks = value.get("tasks")
            sanitized_tasks = {}
            if isinstance(tasks, dict):
                for session_id, task in tasks.items():
                    if not isinstance(session_id, str) or not isinstance(task, dict):
                        continue
                    status = task.get("status")
                    updated_at = task.get("updated_at")
                    if status not in ("running", "error", "completed"):
                        continue
                    if not isinstance(updated_at, (int, float)):
                        continue
                    sanitized_tasks[session_id] = task
            baseline = value.get("baseline")
            if not isinstance(baseline, str) or not re.fullmatch(r"[0-9a-fA-F]{16}", baseline):
                baseline = None
            return {"tasks": sanitized_tasks, "baseline": baseline}
        except (OSError, json.JSONDecodeError):
            return {}

    @staticmethod
    def _log(message: str) -> None:
        APP_DIR.mkdir(parents=True, exist_ok=True)
        try:
            if LOG_PATH.stat().st_size >= MAX_LOG_SIZE:
                backup = LOG_PATH.with_suffix(".log.1")
                os.replace(str(LOG_PATH), str(backup))
        except FileNotFoundError:
            pass
        timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
        with LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write("{} {}\n".format(timestamp, message.replace("\n", " | ")))

    def _save_state(self) -> None:
        state = {
            "tasks": self.aggregator.tasks,
            "effective": self.controller.effective,
            "baseline": self.controller.baseline,
            "hardware": {
                "available": self.controller.hardware_available,
                "error": self.controller.hardware_error,
                "checked_at": self.controller.hardware_checked_at,
            },
            "updated_at": time.time(),
        }
        temporary = STATE_PATH.with_suffix(".tmp")
        with temporary.open("w", encoding="utf-8") as handle:
            json.dump(state, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
        os.replace(str(temporary), str(STATE_PATH))
        os.chmod(str(STATE_PATH), 0o600)

    def _sync_lights(self, force: bool = False) -> None:
        desired = self.aggregator.effective()
        if not force and desired == self.controller.effective:
            return
        try:
            changed = self.controller.apply(desired, force=force)
            if changed:
                self._log("effective={}; tasks={}".format(desired, len(self.aggregator.tasks)))
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            self.controller.record_hardware_result(error)
            self._log("HID update failed for {}: {}".format(desired, error))
        self._save_state()

    def _check_hardware(self) -> None:
        if self.aggregator.effective() != self.controller.effective:
            return
        try:
            if self.controller.health_check():
                self._log("reapplied {} after side-light reset".format(self.controller.effective))
                self._save_state()
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            was_available = self.controller.hardware_available
            self.controller.record_hardware_result(error)
            if was_available is not False:
                self._log("HID health check failed: {}".format(error))
            self._save_state()

    def _snapshot(self) -> Dict[str, Any]:
        return {
            "ok": True,
            "version": VERSION,
            "light": self.controller.effective,
            "desired_light": self.aggregator.effective(),
            "tasks": len(self.aggregator.tasks),
            "hardware": {
                "available": self.controller.hardware_available,
                "error": self.controller.hardware_error,
                "checked_at": self.controller.hardware_checked_at,
            },
        }

    def _receive(self, connection: socket.socket) -> Optional[Dict[str, Any]]:
        chunks = []
        length = 0
        try:
            while length < MAX_MESSAGE_SIZE:
                chunk = connection.recv(min(65536, MAX_MESSAGE_SIZE - length))
                if not chunk:
                    break
                chunks.append(chunk)
                length += len(chunk)
                if b"\n" in chunk:
                    break
        except socket.timeout:
            return None
        if not chunks:
            return None
        try:
            value = json.loads(b"".join(chunks).split(b"\n", 1)[0].decode("utf-8"))
            return value if isinstance(value, dict) else None
        except (UnicodeDecodeError, json.JSONDecodeError):
            return None

    @staticmethod
    def _respond(connection: socket.socket, response: Dict[str, Any]) -> bool:
        payload = json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        try:
            connection.sendall(payload + b"\n")
            return True
        except OSError:
            return False

    def run(self) -> int:
        APP_DIR.mkdir(parents=True, exist_ok=True)
        os.chmod(str(APP_DIR), 0o700)
        if SOCKET_PATH.exists():
            SOCKET_PATH.unlink()
        server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        server.bind(str(SOCKET_PATH))
        os.chmod(str(SOCKET_PATH), 0o600)
        server.listen(16)
        server.settimeout(1.0)

        def request_stop(_signum: int, _frame: Any) -> None:
            self.stopping = True

        signal.signal(signal.SIGTERM, request_stop)
        signal.signal(signal.SIGINT, request_stop)
        self.aggregator.expire()
        self._sync_lights(force=True)
        self._log("daemon started")

        try:
            while not self.stopping:
                try:
                    connection, _ = server.accept()
                except socket.timeout:
                    before = self.aggregator.effective()
                    self.aggregator.expire()
                    self._sync_lights(force=before != self.aggregator.effective())
                    self._check_hardware()
                    continue
                connection.settimeout(CLIENT_READ_TIMEOUT_SECONDS)
                with connection:
                    event = self._receive(connection)
                    if event:
                        command = event.get("command")
                        if command == "ping":
                            self._respond(connection, self._snapshot())
                            continue
                        if command == "reset":
                            self.aggregator.tasks.clear()
                            self.aggregator.expire()
                            self._sync_lights(force=True)
                            self._respond(connection, {"ok": True})
                            continue
                        elif command is None:
                            self.aggregator.handle(event)
                        self.aggregator.expire()
                        self._sync_lights(force=True)
                        self._check_hardware()
        finally:
            try:
                self.aggregator.tasks.clear()
                self._sync_lights(force=True)
            finally:
                server.close()
                if SOCKET_PATH.exists():
                    SOCKET_PATH.unlink()
                self._log("daemon stopped")
        return 0


if __name__ == "__main__":
    sys.exit(CodexKick75Daemon().run())
