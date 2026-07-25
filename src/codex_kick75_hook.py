#!/usr/bin/python3
# SPDX-License-Identifier: MIT
"""Non-blocking Codex hook client for the Kick75 status daemon."""

import json
import socket
import sys

from codex_kick75_common import APP_DIR, normalize_hook_event

SOCKET_PATH = APP_DIR / "status.sock"


def main() -> int:
    try:
        event = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        event = {}

    if isinstance(event, dict) and event:
        try:
            normalized = normalize_hook_event(event)
            payload = json.dumps(normalized, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
            client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            client.settimeout(0.25)
            try:
                client.connect(str(SOCKET_PATH))
                client.sendall(payload)
            finally:
                client.close()
        except OSError:
            pass

    # Stop hooks require valid JSON on stdout. An empty object has no steering effect.
    if isinstance(event, dict) and event.get("hook_event_name") == "Stop":
        print("{}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
