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
from typing import Any, Dict, Optional, Tuple


ROOT = Path(__file__).resolve().parents[1]
SRC_DIR = ROOT / "src"
if str(SRC_DIR) not in sys.path:
    sys.path.insert(0, str(SRC_DIR))

from codex_kick75_common import (  # noqa: E402
    LIGHT_STATES,
    VERSION,
    default_settings,
    load_settings,
    normalize_color,
    save_settings,
)

HOME = Path.home()
BUILD_DIR = ROOT / "build"
LEDCTL_BUILD = BUILD_DIR / "kick75_ledctl"
MACOS_PACKAGE_DIR = ROOT / "macos-app"
MACOS_APP_NAME = "Codex Kick75.app"
MACOS_APP_BUILD = BUILD_DIR / MACOS_APP_NAME
MACOS_APP_INSTALL = HOME / "Applications" / MACOS_APP_NAME
MACOS_APP_EXECUTABLE = "CodexKick75"
MACOS_APP_BUNDLE_ID = "com.zzm.codex-kick75.app"
MACOS_APP_BUILD_NUMBER = "3"
APP_DIR = HOME / "Library" / "Application Support" / "CodexKick75"
LAUNCH_AGENTS = HOME / "Library" / "LaunchAgents"
LABEL = "com.zzm.codex-kick75"
PLIST_PATH = LAUNCH_AGENTS / "{}.plist".format(LABEL)
HOOKS_PATH = HOME / ".codex" / "hooks.json"
HOOK_SCRIPT = APP_DIR / "codex_kick75_hook.py"
SOCKET_PATH = APP_DIR / "status.sock"
SETTINGS_PATH = APP_DIR / "settings.json"
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


def app_info_plist() -> Dict[str, Any]:
    return {
        "CFBundleDevelopmentRegion": "zh_CN",
        "CFBundleDisplayName": "Codex Kick75",
        "CFBundleExecutable": MACOS_APP_EXECUTABLE,
        "CFBundleIdentifier": MACOS_APP_BUNDLE_ID,
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleName": "Codex Kick75",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": VERSION,
        "CFBundleVersion": MACOS_APP_BUILD_NUMBER,
        "LSMinimumSystemVersion": "13.0",
        "LSUIElement": True,
        "NSHighResolutionCapable": True,
    }


def build_app() -> Path:
    swift = shutil.which("swift")
    if not swift:
        raise RuntimeError(
            "swift not found; install Xcode Command Line Tools with: xcode-select --install"
        )
    command = [
        swift,
        "build",
        "-c",
        "release",
        "--product",
        MACOS_APP_EXECUTABLE,
        "--package-path",
        str(MACOS_PACKAGE_DIR),
    ]
    subprocess.run(command, check=True)
    result = subprocess.run(
        command + ["--show-bin-path"],
        check=True,
        capture_output=True,
        text=True,
    )
    executable = Path(result.stdout.strip()) / MACOS_APP_EXECUTABLE
    if not executable.is_file():
        raise RuntimeError("Swift build did not produce {}".format(executable))

    if MACOS_APP_BUILD.exists():
        shutil.rmtree(str(MACOS_APP_BUILD))
    contents = MACOS_APP_BUILD / "Contents"
    macos = contents / "MacOS"
    macos.mkdir(parents=True)
    destination = macos / MACOS_APP_EXECUTABLE
    shutil.copy2(str(executable), str(destination))
    destination.chmod(0o755)
    with (contents / "Info.plist").open("wb") as handle:
        plistlib.dump(app_info_plist(), handle, sort_keys=True)
    # Non-APFS workspaces may materialize extended attributes as AppleDouble
    # files. They are not part of the bundle and can make codesign reject it.
    for sidecar in MACOS_APP_BUILD.rglob("._*"):
        sidecar.unlink()
    codesign = shutil.which("codesign")
    if not codesign:
        raise RuntimeError("codesign not found; install Xcode Command Line Tools")
    subprocess.run(
        [codesign, "--force", "--deep", "--sign", "-", str(MACOS_APP_BUILD)],
        check=True,
    )
    for sidecar in MACOS_APP_BUILD.rglob("._*"):
        sidecar.unlink()
    subprocess.run(
        [codesign, "--verify", "--deep", "--strict", str(MACOS_APP_BUILD)],
        check=True,
    )
    return MACOS_APP_BUILD


def install_app() -> Path:
    application = build_app()
    MACOS_APP_INSTALL.parent.mkdir(parents=True, exist_ok=True)
    if MACOS_APP_INSTALL.exists():
        shutil.rmtree(str(MACOS_APP_INSTALL))
    shutil.copytree(str(application), str(MACOS_APP_INSTALL), copy_function=shutil.copy2)
    return MACOS_APP_INSTALL


def service_domain() -> str:
    return "gui/{}".format(os.getuid())


def stop_service() -> None:
    subprocess.run(
        ["/bin/launchctl", "bootout", "{}/{}".format(service_domain(), LABEL)],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def install_launch_agent(
    green_hold: float,
    stale_task_hours: float,
    reconnect_check: float,
) -> None:
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
            "CODEX_KICK75_RECONNECT_CHECK": str(reconnect_check),
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


def install(green_hold: float, stale_task_hours: float, reconnect_check: float) -> None:
    ledctl = build_ledctl()
    APP_DIR.mkdir(parents=True, exist_ok=True)
    APP_DIR.chmod(0o700)
    if SETTINGS_PATH.exists():
        load_settings(SETTINGS_PATH)
    else:
        save_settings(default_settings(), SETTINGS_PATH)
    for source, name in (
        (ledctl, "kick75_ledctl"),
        (ROOT / "src" / "codex_kick75_common.py", "codex_kick75_common.py"),
        (ROOT / "src" / "codex_kick75_daemon.py", "codex_kick75_daemon.py"),
        (ROOT / "src" / "codex_kick75_hook.py", "codex_kick75_hook.py"),
    ):
        destination = APP_DIR / name
        shutil.copy2(str(source), str(destination))
        destination.chmod(0o755 if source.suffix != ".py" or "hook" in name or "daemon" in name else 0o644)

    install_launch_agent(green_hold, stale_task_hours, reconnect_check)
    config = load_hooks()
    if merge_hooks(config):
        save_hooks(config)

    print("Installed Codex Kick75 status lights {}.".format(VERSION))
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


def daemon_request(command: str, timeout: float = 1.0) -> Dict[str, Any]:
    client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    client.settimeout(timeout)
    try:
        client.connect(str(SOCKET_PATH))
        request = json.dumps({"command": command}, separators=(",", ":")).encode("utf-8")
        client.sendall(request + b"\n")
        chunks = []
        length = 0
        while length < 65536:
            chunk = client.recv(min(4096, 65536 - length))
            if not chunk:
                break
            chunks.append(chunk)
            length += len(chunk)
            if b"\n" in chunk:
                break
    finally:
        client.close()
    if not chunks:
        raise RuntimeError("daemon returned no response")
    response = json.loads(b"".join(chunks).split(b"\n", 1)[0].decode("utf-8"))
    if isinstance(response, dict) and response.get("ok") is False:
        raise RuntimeError(str(response.get("error", "daemon rejected the request")))
    if not isinstance(response, dict) or response.get("ok") is not True:
        raise RuntimeError("daemon returned an invalid response")
    return response


def load_runtime_state() -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    state_path = APP_DIR / "state.json"
    if not state_path.exists():
        return None, None
    try:
        with state_path.open("r", encoding="utf-8") as handle:
            value = json.load(handle)
        if not isinstance(value, dict):
            return None, "state.json does not contain an object"
        return value, None
    except (OSError, json.JSONDecodeError) as error:
        return None, str(error)


def status() -> int:
    service = subprocess.run(
        ["/bin/launchctl", "print", "{}/{}".format(service_domain(), LABEL)],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    snapshot = None
    ping_error = None
    if SOCKET_PATH.exists():
        try:
            snapshot = daemon_request("ping")
        except (OSError, RuntimeError, UnicodeDecodeError, json.JSONDecodeError) as error:
            ping_error = str(error)
    installed, expected = hook_status()
    if service.returncode == 0 and snapshot is not None:
        service_status = "running (version {})".format(snapshot.get("version", "unknown"))
    elif service.returncode == 0 and SOCKET_PATH.exists():
        service_status = "registered but unresponsive"
    elif service.returncode == 0:
        service_status = "registered but socket unavailable"
    else:
        service_status = "not running"
    print("service: {}".format(service_status))
    if ping_error:
        print("ping:    failed ({})".format(ping_error))
    print("hooks:   {}/{} installed".format(installed, expected))
    state, state_error = load_runtime_state()
    if snapshot is not None:
        status_name = snapshot.get("status", snapshot.get("light", "unknown"))
        print("status:  {}".format(status_name))
        light = snapshot.get("light")
        if isinstance(light, dict):
            print(
                "light:   {} at {}%".format(
                    light.get("color", "unknown"),
                    light.get("brightness", "unknown"),
                )
            )
        print("tasks:   {}".format(snapshot.get("tasks", "unknown")))
        hardware = snapshot.get("hardware", {})
        available = hardware.get("available") if isinstance(hardware, dict) else None
        hardware_status = "connected" if available is True else "unavailable" if available is False else "unknown"
        print("hardware: {}".format(hardware_status))
        print("settings: {}".format(snapshot.get("settings", SETTINGS_PATH)))
        if snapshot.get("settings_error"):
            print("config:  invalid ({})".format(snapshot["settings_error"]))
    elif state is not None:
        print("status:  {} (last saved)".format(state.get("effective", "unknown")))
        tasks = state.get("tasks", {})
        print("tasks:   {} (last saved)".format(len(tasks) if isinstance(tasks, dict) else "unknown"))
    if state_error:
        print("state:   corrupted ({})".format(state_error))
    elif state is not None:
        print("state:   {}".format(APP_DIR / "state.json"))
    else:
        print("state:   unavailable")
    return 0 if service.returncode == 0 and snapshot is not None and installed == expected else 1


def test_hid() -> int:
    ledctl = build_ledctl()
    print("The five side LEDs should turn green for 5 seconds, then restore.")
    return subprocess.run([str(ledctl), "--test-green"], check=False).returncode


def reset() -> int:
    try:
        daemon_request("reset", timeout=15.0)
    except (OSError, RuntimeError, UnicodeDecodeError, json.JSONDecodeError) as error:
        print("error: could not contact daemon: {}".format(error), file=sys.stderr)
        return 1
    print("Cleared tracked tasks; the original side-light effect will be restored.")
    return 0


def configure(
    state_name: Optional[str],
    color: Optional[str],
    brightness: Optional[int],
    reset_settings: bool,
) -> int:
    settings = default_settings() if reset_settings else load_settings(SETTINGS_PATH)
    changed = reset_settings
    if state_name is not None:
        if color is not None:
            settings["states"][state_name]["color"] = normalize_color(color)
            changed = True
        if brightness is not None:
            settings["states"][state_name]["brightness"] = brightness
            changed = True
    if changed:
        save_settings(settings, SETTINGS_PATH)
        if SOCKET_PATH.exists():
            try:
                daemon_request("reload", timeout=15.0)
            except (OSError, RuntimeError, UnicodeDecodeError, json.JSONDecodeError) as error:
                print(
                    "warning: settings were saved but the daemon could not reload them: {}".format(
                        error
                    ),
                    file=sys.stderr,
                )
        print("Saved settings to {}.".format(SETTINGS_PATH))
    print(json.dumps(settings, ensure_ascii=False, indent=2))
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
    if MACOS_APP_INSTALL.exists():
        shutil.rmtree(str(MACOS_APP_INSTALL))
    print("Uninstalled Codex Kick75 status lights.")
    print("A timestamped hooks.json backup was kept in ~/.codex when applicable.")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "command",
        nargs="?",
        default="install",
        choices=(
            "build",
            "build-app",
            "install",
            "install-app",
            "status",
            "reset",
            "config",
            "test-hid",
            "uninstall",
        ),
    )
    parser.add_argument(
        "--completed-hold",
        "--green-hold",
        dest="green_hold",
        type=float,
        default=10.0,
        help="seconds to keep the completed state",
    )
    parser.add_argument(
        "--stale-task-hours",
        type=float,
        default=12.0,
        help="hours before abandoned task state expires",
    )
    parser.add_argument(
        "--reconnect-check",
        type=float,
        default=10.0,
        help="seconds between active side-light health checks",
    )
    parser.add_argument("--state", choices=LIGHT_STATES, help="status light to configure")
    parser.add_argument("--color", help="custom color in #RRGGBB format")
    parser.add_argument("--brightness", type=int, help="custom brightness from 0 to 100")
    parser.add_argument(
        "--reset",
        action="store_true",
        dest="reset_settings",
        help="restore all status-light settings to defaults",
    )
    arguments = parser.parse_args()
    if (
        arguments.green_hold <= 0
        or arguments.stale_task_hours <= 0
        or arguments.reconnect_check <= 0
    ):
        parser.error("timings must be positive")
    config_values = (arguments.state, arguments.color, arguments.brightness)
    if arguments.command != "config" and (
        any(value is not None for value in config_values) or arguments.reset_settings
    ):
        parser.error("--state, --color, --brightness, and --reset require the config command")
    if arguments.command == "config":
        if (
            arguments.color is not None or arguments.brightness is not None
        ) and arguments.state is None:
            parser.error("--state is required when changing color or brightness")
        if arguments.reset_settings and any(value is not None for value in config_values):
            parser.error("--reset cannot be combined with state settings")
    return arguments


def main() -> int:
    arguments = parse_arguments()
    try:
        if arguments.command == "build":
            print(build_ledctl())
        elif arguments.command == "build-app":
            print(build_app())
        elif arguments.command == "install":
            install(arguments.green_hold, arguments.stale_task_hours, arguments.reconnect_check)
        elif arguments.command == "install-app":
            print(install_app())
        elif arguments.command == "status":
            return status()
        elif arguments.command == "reset":
            return reset()
        elif arguments.command == "config":
            return configure(
                arguments.state,
                arguments.color,
                arguments.brightness,
                arguments.reset_settings,
            )
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
