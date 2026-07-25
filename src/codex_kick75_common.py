# SPDX-License-Identifier: MIT
"""Shared, dependency-free helpers for the Codex Kick75 integration."""

import json
import os
import re
import tempfile
from pathlib import Path
from typing import Any, Dict


VERSION = "0.2.0"
APP_DIR = Path(
    os.environ.get(
        "CODEX_KICK75_DATA_DIR",
        str(Path.home() / "Library" / "Application Support" / "CodexKick75"),
    )
)
SETTINGS_PATH = APP_DIR / "settings.json"
LIGHT_STATES = ("running", "permission", "failure", "completed")
DEFAULT_LIGHTS = {
    "running": {"color": "#FFB400", "brightness": 100},
    "permission": {"color": "#FF0000", "brightness": 100},
    "failure": {"color": "#FF0000", "brightness": 100},
    "completed": {"color": "#00FF00", "brightness": 100},
}


def default_settings() -> Dict[str, Any]:
    return {
        "version": 1,
        "states": {state: dict(DEFAULT_LIGHTS[state]) for state in LIGHT_STATES},
    }


def normalize_color(value: Any) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"#[0-9a-fA-F]{6}", value):
        raise ValueError("color must use #RRGGBB format")
    return value.upper()


def validate_settings(value: Any) -> Dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError("settings must contain a JSON object")
    version = value.get("version", 1)
    if version != 1:
        raise ValueError("unsupported settings version: {}".format(version))
    raw_states = value.get("states", {})
    if not isinstance(raw_states, dict):
        raise ValueError("settings.states must contain a JSON object")

    normalized = default_settings()
    for state in LIGHT_STATES:
        raw_light = raw_states.get(state)
        if raw_light is None:
            continue
        if not isinstance(raw_light, dict):
            raise ValueError("settings.states.{} must contain a JSON object".format(state))
        color = normalize_color(raw_light.get("color", DEFAULT_LIGHTS[state]["color"]))
        brightness = raw_light.get("brightness", DEFAULT_LIGHTS[state]["brightness"])
        if isinstance(brightness, bool) or not isinstance(brightness, int):
            raise ValueError("settings.states.{}.brightness must be an integer".format(state))
        if not 0 <= brightness <= 100:
            raise ValueError("settings.states.{}.brightness must be between 0 and 100".format(state))
        normalized["states"][state] = {"color": color, "brightness": brightness}
    return normalized


def load_settings(path: Path = SETTINGS_PATH) -> Dict[str, Any]:
    if not path.exists():
        return default_settings()
    with path.open("r", encoding="utf-8") as handle:
        return validate_settings(json.load(handle))


def save_settings(settings: Dict[str, Any], path: Path = SETTINGS_PATH) -> None:
    normalized = validate_settings(settings)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.parent == APP_DIR:
        os.chmod(str(path.parent), 0o700)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=str(path.parent),
            prefix=".{}-".format(path.name),
            suffix=".tmp",
            delete=False,
        ) as handle:
            temporary = Path(handle.name)
            os.chmod(handle.name, 0o600)
            json.dump(normalized, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(str(temporary), str(path))
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()
    os.chmod(str(path), 0o600)


def side_state_for(settings: Dict[str, Any], state: str) -> str:
    if state not in LIGHT_STATES:
        raise ValueError("unknown light state: {}".format(state))
    light = settings["states"][state]
    color = normalize_color(light["color"])[1:].lower()
    return "02{:02x}010000{}".format(light["brightness"], color)


def response_failed(value: Any) -> bool:
    """Recognize explicit tool failure fields without guessing from prose."""
    if isinstance(value, dict):
        for key in ("isError", "is_error", "failed"):
            if value.get(key) is True:
                return True
        for key in ("exit_code", "exitCode", "status_code", "statusCode"):
            code = value.get(key)
            if isinstance(code, int) and code != 0:
                return True
        if value.get("success") is False or value.get("ok") is False:
            return True
        return any(response_failed(item) for item in value.values())
    if isinstance(value, list):
        return any(response_failed(item) for item in value)
    if isinstance(value, str):
        stripped = value.strip()
        if stripped.startswith(("{", "[")):
            try:
                return response_failed(json.loads(stripped))
            except json.JSONDecodeError:
                pass
        return bool(
            re.search(r'"(?:exit_code|exitCode)"\s*:\s*[1-9]\d*', value)
            or re.search(r"\bProcess exited with (?:code|status) [1-9]\d*\b", value)
        )
    return False


def normalize_hook_event(event: Dict[str, Any]) -> Dict[str, Any]:
    """Keep only fields needed by the daemon; prompts and tool data stay private."""
    normalized = {
        "hook_event_name": event.get("hook_event_name"),
        "session_id": event.get("session_id"),
        "turn_id": event.get("turn_id"),
    }
    if event.get("hook_event_name") == "PostToolUse":
        normalized["tool_failed"] = response_failed(event.get("tool_response"))
    return normalized
