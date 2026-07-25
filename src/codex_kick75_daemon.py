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

from codex_kick75_common import (
    APP_DIR,
    LIGHT_STATES,
    SETTINGS_PATH,
    VERSION,
    default_settings,
    load_settings,
    side_state_for,
    validate_settings,
)

SOCKET_PATH = APP_DIR / "status.sock"
STATE_PATH = APP_DIR / "state.json"
LOG_PATH = APP_DIR / "daemon.log"
LEDCTL_PATH = Path(os.environ.get("CODEX_KICK75_LEDCTL", str(APP_DIR / "kick75_ledctl")))
GREEN_HOLD_SECONDS = float(os.environ.get("CODEX_KICK75_GREEN_HOLD", "10"))
STALE_TASK_SECONDS = float(os.environ.get("CODEX_KICK75_STALE_TASK", str(12 * 60 * 60)))
HID_RETRY_SECONDS = float(os.environ.get("CODEX_KICK75_HID_RETRY", "10"))
HID_RECONNECT_CHECK_SECONDS = float(os.environ.get("CODEX_KICK75_RECONNECT_CHECK", "10"))
CLIENT_READ_TIMEOUT_SECONDS = float(os.environ.get("CODEX_KICK75_CLIENT_TIMEOUT", "0.5"))
DEFAULT_PREVIEW_SECONDS = 3.0
MAX_PREVIEW_SECONDS = 10.0
MAX_MESSAGE_SIZE = 1024 * 1024
MAX_LOG_SIZE = 1024 * 1024


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
            details = {
                task.get("detail")
                for task in self.tasks.values()
                if task.get("status") == "error"
            }
            if "tool_failure" in details:
                return "failure"
            if "permission" in details:
                return "permission"
            return "failure"
        if "running" in statuses:
            return "running"
        if "completed" in statuses:
            return "completed"
        return "idle"


class SettingsManager:
    def __init__(self, path: Path = SETTINGS_PATH) -> None:
        self.path = path
        self.settings = default_settings()
        self.signature = None
        self.error: Optional[str] = None
        self.reload(force=True)

    def _signature(self) -> tuple:
        try:
            stat = self.path.stat()
            return (stat.st_ino, stat.st_mtime_ns, stat.st_size)
        except FileNotFoundError:
            return ("missing",)

    def reload(self, force: bool = False) -> bool:
        signature = self._signature()
        if not force and signature == self.signature:
            return False
        self.signature = signature
        try:
            updated = load_settings(self.path)
        except (OSError, ValueError, json.JSONDecodeError) as error:
            self.error = str(error)
            return False
        changed = updated != self.settings
        self.settings = updated
        self.error = None
        return changed

    def light(self, state: str) -> Optional[Dict[str, Any]]:
        if state not in LIGHT_STATES:
            return None
        return dict(self.settings["states"][state])


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

    def apply(self, desired: str, settings: Dict[str, Any], force: bool = False) -> bool:
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
        self._run(["--set-side", side_state_for(settings, desired)])
        self.effective = desired
        self.last_health_check = time.time()
        return True

    def reapply(self, settings: Dict[str, Any]) -> bool:
        if self.effective not in LIGHT_STATES:
            return False
        self._run(["--set-side", side_state_for(settings, self.effective)])
        self.last_health_check = time.time()
        return True

    def preview(self, state: str, settings: Dict[str, Any]) -> None:
        if state not in LIGHT_STATES:
            raise ValueError("unknown preview state: {}".format(state))
        if self.baseline is None:
            self.baseline = self._read_baseline()
        self._run(["--set-side", side_state_for(settings, state)])

    def restore_preview(self, settings: Dict[str, Any]) -> bool:
        if self.effective in LIGHT_STATES:
            return self.reapply(settings)
        if self.baseline:
            self._run(["--set-side", self.baseline])
            self.baseline = None
            return True
        return False

    def health_check(self, settings: Dict[str, Any], force: bool = False) -> bool:
        """Reapply an active color when the keyboard has reset its side LEDs."""
        if self.effective not in LIGHT_STATES:
            return False
        expected = side_state_for(settings, self.effective)
        now = time.time()
        if not force and now - self.last_health_check < HID_RECONNECT_CHECK_SECONDS:
            return False
        self.last_health_check = now
        actual = self._read_side_state()
        if actual == expected:
            self.record_hardware_result()
            return False
        self._run(["--set-side", expected])
        self.record_hardware_result()
        return True


class CodexKick75Daemon:
    def __init__(self) -> None:
        saved = self._load_state()
        self.aggregator = StatusAggregator(saved.get("tasks", {}))
        self.settings = SettingsManager()
        # The keyboard may have reset while the daemon was down, so force the
        # persisted aggregate state to be replayed on startup.
        self.controller = LedController(saved.get("baseline"), "unknown")
        self.stopping = False
        self.logged_settings_error: Optional[str] = None
        self.preview_status: Optional[str] = None
        self.preview_until = 0.0

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
        if self.preview_status is not None:
            self._cancel_preview(restore=desired == self.controller.effective)
        if not force and desired == self.controller.effective:
            return
        try:
            changed = self.controller.apply(desired, self.settings.settings, force=force)
            if changed:
                self._log("effective={}; tasks={}".format(desired, len(self.aggregator.tasks)))
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            self.controller.record_hardware_result(error)
            self._log("HID update failed for {}: {}".format(desired, error))
        self._save_state()

    def _reload_settings(self, force: bool = False) -> bool:
        changed = self.settings.reload(force=force)
        if self.settings.error:
            if self.settings.error != self.logged_settings_error:
                self._log("settings reload failed: {}".format(self.settings.error))
                self.logged_settings_error = self.settings.error
            return False
        self.logged_settings_error = None
        if not changed:
            return False
        if self.preview_status is not None:
            self._cancel_preview(restore=True)
        try:
            if self.controller.reapply(self.settings.settings):
                self._log("reapplied {} after settings reload".format(self.controller.effective))
                self._save_state()
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            self.controller.record_hardware_result(error)
            self._log("HID update failed after settings reload: {}".format(error))
            self._save_state()
        return True

    def _check_hardware(self) -> None:
        if self.preview_status is not None:
            return
        if self.aggregator.effective() != self.controller.effective:
            return
        try:
            if self.controller.health_check(self.settings.settings):
                self._log("reapplied {} after side-light reset".format(self.controller.effective))
                self._save_state()
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            was_available = self.controller.hardware_available
            self.controller.record_hardware_result(error)
            if was_available is not False:
                self._log("HID health check failed: {}".format(error))
            self._save_state()

    def _start_preview(
        self,
        state: str,
        seconds: float,
        color: Optional[str] = None,
        brightness: Optional[int] = None,
    ) -> None:
        if state not in LIGHT_STATES:
            raise ValueError("preview state must be one of: {}".format(", ".join(LIGHT_STATES)))
        if not 0.5 <= seconds <= MAX_PREVIEW_SECONDS:
            raise ValueError("preview seconds must be between 0.5 and 10")
        preview_settings = {
            "version": self.settings.settings["version"],
            "states": {
                key: dict(value)
                for key, value in self.settings.settings["states"].items()
            },
        }
        if color is not None:
            preview_settings["states"][state]["color"] = color
        if brightness is not None:
            preview_settings["states"][state]["brightness"] = brightness
        preview_settings = validate_settings(preview_settings)
        self.controller.preview(state, preview_settings)
        self.preview_status = state
        self.preview_until = time.monotonic() + seconds

    def _cancel_preview(self, restore: bool) -> None:
        if self.preview_status is None:
            return
        preview_status = self.preview_status
        if not restore:
            self.preview_status = None
            self.preview_until = 0.0
            return
        try:
            if self.controller.restore_preview(self.settings.settings):
                self._log("restored light after {} preview".format(preview_status))
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            self.controller.record_hardware_result(error)
            self.preview_until = time.monotonic() + HID_RETRY_SECONDS
            self._log("HID restore failed after preview: {}".format(error))
            return
        self.preview_status = None
        self.preview_until = 0.0

    def _finish_preview_if_needed(self) -> None:
        if self.preview_status is None or time.monotonic() < self.preview_until:
            return
        self._cancel_preview(restore=True)
        self._save_state()

    def _snapshot(self) -> Dict[str, Any]:
        status = self.controller.effective
        preview = None
        if self.preview_status is not None:
            preview = {
                "status": self.preview_status,
                "remaining_seconds": max(0.0, self.preview_until - time.monotonic()),
            }
        return {
            "ok": True,
            "version": VERSION,
            "status": status,
            "desired_status": self.aggregator.effective(),
            "light": self.settings.light(status),
            "tasks": len(self.aggregator.tasks),
            "settings": str(self.settings.path),
            "settings_error": self.settings.error,
            "preview": preview,
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
        if self.settings.error:
            self._log("settings load failed; using defaults: {}".format(self.settings.error))
            self.logged_settings_error = self.settings.error
        self._sync_lights(force=True)
        self._log("daemon started")

        try:
            while not self.stopping:
                try:
                    connection, _ = server.accept()
                except socket.timeout:
                    self._finish_preview_if_needed()
                    self._reload_settings()
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
                        if command == "reload":
                            self._cancel_preview(restore=True)
                            self._reload_settings(force=True)
                            if self.settings.error:
                                self._respond(
                                    connection,
                                    {"ok": False, "error": self.settings.error},
                                )
                            else:
                                self._respond(connection, self._snapshot())
                            continue
                        if command == "preview":
                            preview_state = event.get("state")
                            preview_seconds = event.get("seconds", DEFAULT_PREVIEW_SECONDS)
                            try:
                                if isinstance(preview_seconds, bool):
                                    raise ValueError("preview seconds must be a number")
                                self._start_preview(
                                    preview_state,
                                    float(preview_seconds),
                                    event.get("color"),
                                    event.get("brightness"),
                                )
                                self._respond(connection, self._snapshot())
                            except (TypeError, ValueError, OSError, RuntimeError) as error:
                                self._respond(connection, {"ok": False, "error": str(error)})
                            continue
                        if command == "reset":
                            self.aggregator.tasks.clear()
                            self.aggregator.expire()
                            self._sync_lights(force=True)
                            self._respond(connection, {"ok": True})
                            continue
                        elif command is None:
                            self.aggregator.handle(event)
                        self._reload_settings()
                        self.aggregator.expire()
                        self._sync_lights(force=True)
                        self._check_hardware()
        finally:
            try:
                self._cancel_preview(restore=True)
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
