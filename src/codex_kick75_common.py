# SPDX-License-Identifier: MIT
"""Shared, dependency-free helpers for the Codex Kick75 integration."""

import json
import os
import re
from pathlib import Path
from typing import Any, Dict


APP_DIR = Path(
    os.environ.get(
        "CODEX_KICK75_DATA_DIR",
        str(Path.home() / "Library" / "Application Support" / "CodexKick75"),
    )
)


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
