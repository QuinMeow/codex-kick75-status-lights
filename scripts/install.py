#!/usr/bin/python3
# SPDX-License-Identifier: MIT
"""Build, install, inspect, test, or uninstall Codex Kick75 status lights."""

import argparse
import json
import os
import plistlib
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Dict, Tuple


ROOT = Path(__file__).resolve().parents[1]
HOME = Path.home()
BUILD_DIR = ROOT / "build"
LEDCTL_BUILD = BUILD_DIR / "kick75_ledctl"
APP_DIR = HOME / "Library" / "Application Support" / "CodexKick75"
LAUNCH_AGENTS = HOME / "Library" / "LaunchAgents"
LABEL = "com.zzm.codex-kick75"
PLIST_PATH = LAUNCH_AGENTS / "{}.plist".format(LABEL)
HOOKS_PATH = HOME / ".codex" / "hooks.json"
HOOK_SCRIPT = APP_DIR / "codex_kick75_hook.py"
HOOK_EVENTS = (
    "UserPromptSubmit",
    "PermissionRequest",
    "PostToolUse",
    "Stop",
    "SessionEnd",
)


def hook_group() -> Dict[str, Any]:
    return {
        "hooks": [
            {
                "type": "command",
                "command": '/usr/bin/python3 "{}"'.format(HOOK_SCRIPT),
                "timeout": 3,
            }
        ]
    }


def merge_hooks(config: Dict[str, Any]) -> bool:
    before = json.dumps(config, sort_keys=True, ensure_ascii=False)
    hooks = config.setdefault("hooks", {})
    if not isinstance(hooks, dict):
        raise ValueError("top-level 'hooks' in hooks.json must be an object")
    for event_name in HOOK_EVENTS:
        groups = hooks.setdefault(event_name, [])
        if not isinstance(groups, list):
            raise ValueError("hooks.{} must be an array".format(event_name))
        groups[:] = [
            group
            for group in groups
            if "codex_kick75_hook.py" not in json.dumps(group, ensure_ascii=False)
        ]
        groups.append(hook_group())
    return before != json.dumps(config, sort_keys=True, ensure_ascii=False)


def remove_hooks(config: Dict[str, Any]) -> bool:
    before = json.dumps(config, sort_keys=True, ensure_ascii=False)
    hooks = config.get("hooks")
    if not isinstance(hooks, dict):
        return False
    for event_name in list(hooks):
        groups = hooks.get(event_name)
        if not isinstance(groups, list):
            continue
        groups[:] = [
            group
            for group in groups
            if "codex_kick75_hook.py" not in json.dumps(group, ensure_ascii=False)
        ]
        if not groups:
            hooks.pop(event_name, None)
    if not hooks:
        config.pop("hooks", None)
    return before != json.dumps(config, sort_keys=True, ensure_ascii=False)


def load_hooks() -> Dict[str, Any]:
    if not HOOKS_PATH.exists():
        return {"description": "Global personal Codex hooks."}
    with HOOKS_PATH.open("r", encoding="utf-8") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise ValueError("{} must contain a JSON object".format(HOOKS_PATH))
    return value


def save_hooks(config: Dict[str, Any], backup: bool = True) -> None:
    HOOKS_PATH.parent.mkdir(parents=True, exist_ok=True)
    if backup and HOOKS_PATH.exists():
        backup_path = HOOKS_PATH.with_name("hooks.json.backup-{}".format(int(time.time())))
        shutil.copy2(str(HOOKS_PATH), str(backup_path))
    temporary = HOOKS_PATH.with_suffix(".tmp")
    with temporary.open("w", encoding="utf-8") as handle:
        json.dump(config, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    os.replace(str(temporary), str(HOOKS_PATH))


def build_ledctl() -> Path:
    clang = shutil.which("clang")
    if not clang:
        raise RuntimeError("clang not found; install Xcode Command Line Tools with: xcode-select --install")
    BUILD_DIR.mkdir(parents=True, exist_ok=True)
    command = [
        clang,
        "-Wall",
        "-Wextra",
        "-Werror",
        "-O2",
        "-framework",
        "IOKit",
        "-framework",
        "CoreFoundation",
        str(ROOT / "src" / "kick75_ledctl.c"),
        "-o",
        str(LEDCTL_BUILD),
    ]
    subprocess.run(command, check=True)
    return LEDCTL_BUILD


def service_domain() -> str:
    return "gui/{}".format(os.getuid())


def stop_service() -> None:
    subprocess.run(
        ["/bin/launchctl", "bootout", "{}/{}".format(service_domain(), LABEL)],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def install_launch_agent(green_hold: float, stale_task_hours: float) -> None:
    plist = {
        "Label": LABEL,
        "ProgramArguments": ["/usr/bin/python3", str(APP_DIR / "codex_kick75_daemon.py")],
        "RunAtLoad": True,
        "KeepAlive": True,
        "ProcessType": "Background",
        "StandardOutPath": str(APP_DIR / "launchd.stdout.log"),
        "StandardErrorPath": str(APP_DIR / "launchd.stderr.log"),
        "EnvironmentVariables": {
            "PYTHONUNBUFFERED": "1",
            "CODEX_KICK75_GREEN_HOLD": str(green_hold),
            "CODEX_KICK75_STALE_TASK": str(stale_task_hours * 60 * 60),
        },
    }
    LAUNCH_AGENTS.mkdir(parents=True, exist_ok=True)
    with PLIST_PATH.open("wb") as handle:
        plistlib.dump(plist, handle, sort_keys=True)

    stop_service()
    result = None
    for attempt in range(3):
        if attempt:
            time.sleep(1.0)
        result = subprocess.run(
            ["/bin/launchctl", "bootstrap", service_domain(), str(PLIST_PATH)],
            check=False,
            capture_output=True,
            text=True,
        )
        if result.returncode == 0:
            break
    if result is None or result.returncode != 0:
        detail = (result.stderr or result.stdout).strip() if result else "no launchctl result"
        raise RuntimeError(
            "launchctl bootstrap failed with status {}: {}".format(
                result.returncode if result else "unknown",
                detail,
            )
        )
    subprocess.run(
        ["/bin/launchctl", "kickstart", "{}/{}".format(service_domain(), LABEL)],
        check=True,
    )


def install(green_hold: float, stale_task_hours: float) -> None:
    ledctl = build_ledctl()
    APP_DIR.mkdir(parents=True, exist_ok=True)
    APP_DIR.chmod(0o700)
    for source, name in (
        (ledctl, "kick75_ledctl"),
        (ROOT / "src" / "codex_kick75_common.py", "codex_kick75_common.py"),
        (ROOT / "src" / "codex_kick75_daemon.py", "codex_kick75_daemon.py"),
        (ROOT / "src" / "codex_kick75_hook.py", "codex_kick75_hook.py"),
    ):
        destination = APP_DIR / name
        shutil.copy2(str(source), str(destination))
        destination.chmod(0o755 if source.suffix != ".py" or "hook" in name or "daemon" in name else 0o644)

    install_launch_agent(green_hold, stale_task_hours)
    config = load_hooks()
    if merge_hooks(config):
        save_hooks(config)

    print("Installed Codex Kick75 status lights.")
    print("  hooks:   {}".format(HOOKS_PATH))
    print("  service: {}".format(PLIST_PATH))
    print("  data:    {}".format(APP_DIR))
    print("Restart Codex, then run /hooks to verify Installed=1 and Active=1.")


def hook_status() -> Tuple[int, int]:
    try:
        config = load_hooks()
    except (OSError, ValueError, json.JSONDecodeError):
        return 0, len(HOOK_EVENTS)
    hooks = config.get("hooks", {})
    installed = 0
    for event_name in HOOK_EVENTS:
        groups = hooks.get(event_name, []) if isinstance(hooks, dict) else []
        if any("codex_kick75_hook.py" in json.dumps(group) for group in groups):
            installed += 1
    return installed, len(HOOK_EVENTS)


def status() -> int:
    service = subprocess.run(
        ["/bin/launchctl", "print", "{}/{}".format(service_domain(), LABEL)],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    socket_ready = (APP_DIR / "status.sock").exists()
    installed, expected = hook_status()
    if service.returncode == 0 and socket_ready:
        service_status = "running"
    elif service.returncode == 0:
        service_status = "registered but socket unavailable"
    else:
        service_status = "not running"
    print("service: {}".format(service_status))
    print("hooks:   {}/{} installed".format(installed, expected))
    state_path = APP_DIR / "state.json"
    if state_path.exists():
        with state_path.open("r", encoding="utf-8") as handle:
            state = json.load(handle)
        print("light:   {}".format(state.get("effective", "unknown")))
        print("tasks:   {}".format(len(state.get("tasks", {}))))
        print("state:   {}".format(state_path))
    else:
        print("state:   unavailable")
    return 0 if service.returncode == 0 and socket_ready and installed == expected else 1


def test_hid() -> int:
    ledctl = build_ledctl()
    print("The five side LEDs should turn green for 5 seconds, then restore.")
    return subprocess.run([str(ledctl), "--test-green"], check=False).returncode


def reset() -> int:
    socket_path = APP_DIR / "status.sock"
    try:
        client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        client.settimeout(1.0)
        try:
            client.connect(str(socket_path))
            client.sendall(b'{"command":"reset"}\n')
        finally:
            client.close()
    except OSError as error:
        print("error: could not contact daemon: {}".format(error), file=sys.stderr)
        return 1
    print("Cleared tracked tasks; the original side-light effect will be restored.")
    return 0


def uninstall() -> None:
    baseline = None
    state_path = APP_DIR / "state.json"
    try:
        with state_path.open("r", encoding="utf-8") as handle:
            state = json.load(handle)
        candidate = state.get("baseline") if isinstance(state, dict) else None
        if isinstance(candidate, str) and len(candidate) == 16:
            baseline = candidate
    except (OSError, json.JSONDecodeError):
        pass

    config = load_hooks()
    if remove_hooks(config):
        save_hooks(config)
    stop_service()
    installed_ledctl = APP_DIR / "kick75_ledctl"
    if baseline and installed_ledctl.exists():
        subprocess.run([str(installed_ledctl), "--set-side", baseline], check=False)
    if PLIST_PATH.exists():
        PLIST_PATH.unlink()
    if APP_DIR.exists():
        shutil.rmtree(str(APP_DIR))
    print("Uninstalled Codex Kick75 status lights.")
    print("A timestamped hooks.json backup was kept in ~/.codex when applicable.")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "command",
        nargs="?",
        default="install",
        choices=("build", "install", "status", "reset", "test-hid", "uninstall"),
    )
    parser.add_argument("--green-hold", type=float, default=10.0, help="seconds to keep green")
    parser.add_argument(
        "--stale-task-hours",
        type=float,
        default=12.0,
        help="hours before abandoned task state expires",
    )
    arguments = parser.parse_args()
    if arguments.green_hold <= 0 or arguments.stale_task_hours <= 0:
        parser.error("timings must be positive")
    return arguments


def main() -> int:
    arguments = parse_arguments()
    try:
        if arguments.command == "build":
            print(build_ledctl())
        elif arguments.command == "install":
            install(arguments.green_hold, arguments.stale_task_hours)
        elif arguments.command == "status":
            return status()
        elif arguments.command == "reset":
            return reset()
        elif arguments.command == "test-hid":
            return test_hid()
        elif arguments.command == "uninstall":
            uninstall()
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError, subprocess.CalledProcessError) as error:
        print("error: {}".format(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
