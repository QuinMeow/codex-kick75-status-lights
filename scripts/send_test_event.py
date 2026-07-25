#!/usr/bin/python3
# SPDX-License-Identifier: MIT
"""Send a synthetic event through the installed Codex Kick75 Hook client."""

import argparse
import json
import subprocess
import sys
from pathlib import Path


HOOK = Path.home() / "Library" / "Application Support" / "CodexKick75" / "codex_kick75_hook.py"
EVENT_NAMES = {
    "start": "UserPromptSubmit",
    "permission": "PermissionRequest",
    "tool-ok": "PostToolUse",
    "tool-fail": "PostToolUse",
    "stop": "Stop",
    "end": "SessionEnd",
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("event", choices=sorted(EVENT_NAMES))
    parser.add_argument("session_id")
    arguments = parser.parse_args()

    event = {
        "hook_event_name": EVENT_NAMES[arguments.event],
        "session_id": arguments.session_id,
        "turn_id": "test-turn-{}".format(arguments.session_id),
    }
    if arguments.event == "start":
        event["prompt"] = "Synthetic status-light test"
    elif arguments.event in ("tool-ok", "tool-fail"):
        event["tool_response"] = {"exit_code": 0 if arguments.event == "tool-ok" else 1}

    result = subprocess.run(
        ["/usr/bin/python3", str(HOOK)],
        input=json.dumps(event),
        text=True,
        check=False,
    )
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
