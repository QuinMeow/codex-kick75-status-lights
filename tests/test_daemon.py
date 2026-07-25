# SPDX-License-Identifier: MIT
import importlib.util
import json
import pathlib
import socket
import tempfile
import unittest
import sys
from unittest import mock


MODULE_PATH = pathlib.Path(__file__).parents[1] / "src" / "codex_kick75_daemon.py"
sys_path = str(MODULE_PATH.parent)
if sys_path not in sys.path:
    sys.path.insert(0, sys_path)
import codex_kick75_common
SPEC = importlib.util.spec_from_file_location("codex_kick75_daemon", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class FakeClock:
    def __init__(self):
        self.value = 1000.0

    def __call__(self):
        return self.value


class StatusAggregatorTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.status = MODULE.StatusAggregator(clock=self.clock)

    def event(self, name, session, response=None):
        value = {
            "hook_event_name": name,
            "session_id": session,
            "turn_id": "turn-{}".format(session),
        }
        if response is not None:
            value["tool_failed"] = codex_kick75_common.response_failed(response)
        self.status.handle(value)

    def test_global_priority(self):
        self.event("UserPromptSubmit", "a")
        self.assertEqual(self.status.effective(), "running")
        self.event("Stop", "a")
        self.assertEqual(self.status.effective(), "completed")
        self.event("UserPromptSubmit", "b")
        self.assertEqual(self.status.effective(), "running")
        self.event("PermissionRequest", "a")
        self.assertEqual(self.status.effective(), "permission")

    def test_error_is_sticky_at_stop_but_successful_tool_clears_it(self):
        self.event("UserPromptSubmit", "a")
        self.event("PermissionRequest", "a")
        self.event("Stop", "a")
        self.assertEqual(self.status.effective(), "permission")
        self.event("PostToolUse", "a", {"exit_code": 0})
        self.assertEqual(self.status.effective(), "running")
        self.event("Stop", "a")
        self.assertEqual(self.status.effective(), "completed")

    def test_expired_completed_returns_idle(self):
        self.event("UserPromptSubmit", "a")
        self.event("Stop", "a")
        self.clock.value += MODULE.GREEN_HOLD_SECONDS + 0.1
        self.status.expire()
        self.assertEqual(self.status.effective(), "idle")

    def test_failed_tool_response_is_failure(self):
        self.event("UserPromptSubmit", "a")
        self.event("PostToolUse", "a", {"result": {"exit_code": 2}})
        self.assertEqual(self.status.effective(), "failure")

    def test_failure_has_priority_over_permission(self):
        self.event("PermissionRequest", "a")
        self.event("PostToolUse", "b", {"exit_code": 2})
        self.assertEqual(self.status.effective(), "failure")

    def test_session_end_removes_task(self):
        self.event("UserPromptSubmit", "a")
        self.event("SessionEnd", "a")
        self.assertEqual(self.status.effective(), "idle")


class LedControllerTests(unittest.TestCase):
    def test_reads_machine_side_state(self):
        controller = MODULE.LedController()
        controller._run = lambda _arguments: "debug output\nSIDE_STATE=0064010100e9fffb\n"
        self.assertEqual(controller._read_baseline(), "0064010100e9fffb")

    def test_health_check_reapplies_color_after_keyboard_reset(self):
        controller = MODULE.LedController(baseline="0064010100e9fffb", effective="running")
        settings = codex_kick75_common.default_settings()
        calls = []

        def run(arguments):
            calls.append(arguments)
            if arguments == ["--get-side"]:
                return "SIDE_STATE=0064010100e9fffb\n"
            return ""

        controller._run = run
        self.assertTrue(controller.health_check(settings, force=True))
        self.assertEqual(
            calls,
            [["--get-side"], ["--set-side", "0264010000ffb400"]],
        )

    def test_health_check_does_not_rewrite_matching_color(self):
        controller = MODULE.LedController(baseline="0064010100e9fffb", effective="completed")
        settings = codex_kick75_common.default_settings()
        calls = []

        def run(arguments):
            calls.append(arguments)
            return "SIDE_STATE=026401000000ff00\n"

        controller._run = run
        self.assertFalse(controller.health_check(settings, force=True))
        self.assertEqual(calls, [["--get-side"]])

    def test_idle_preview_saves_and_restores_original_light(self):
        controller = MODULE.LedController(effective="idle")
        settings = codex_kick75_common.default_settings()
        calls = []

        def run(arguments):
            calls.append(arguments)
            if arguments == ["--get-side"]:
                return "SIDE_STATE=0064010100e9fffb\n"
            return ""

        controller._run = run
        controller.preview("permission", settings)
        self.assertEqual(controller.baseline, "0064010100e9fffb")
        self.assertTrue(controller.restore_preview(settings))
        self.assertIsNone(controller.baseline)
        self.assertEqual(
            calls,
            [
                ["--get-side"],
                ["--set-side", "0264010000ff0000"],
                ["--set-side", "0064010100e9fffb"],
            ],
        )

    def test_active_preview_restores_current_status_color(self):
        controller = MODULE.LedController(
            baseline="0064010100e9fffb",
            effective="running",
        )
        settings = codex_kick75_common.default_settings()
        calls = []
        controller._run = lambda arguments: calls.append(arguments) or ""

        controller.preview("completed", settings)
        self.assertTrue(controller.restore_preview(settings))
        self.assertEqual(
            calls,
            [
                ["--set-side", "026401000000ff00"],
                ["--set-side", "0264010000ffb400"],
            ],
        )


class SettingsManagerTests(unittest.TestCase):
    def test_hot_reload_keeps_last_valid_settings_after_invalid_edit(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = pathlib.Path(temporary) / "settings.json"
            manager = MODULE.SettingsManager(path)
            settings = codex_kick75_common.default_settings()
            settings["states"]["running"]["color"] = "#123456"
            codex_kick75_common.save_settings(settings, path)
            self.assertTrue(manager.reload())
            self.assertEqual(manager.light("running")["color"], "#123456")

            path.write_text('{"states":{"running":{"color":"bad"}}}', encoding="utf-8")
            self.assertFalse(manager.reload())
            self.assertIsNotNone(manager.error)
            self.assertEqual(manager.light("running")["color"], "#123456")

    def test_daemon_reapplies_active_light_after_settings_reload(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = pathlib.Path(temporary) / "settings.json"
            daemon = object.__new__(MODULE.CodexKick75Daemon)
            daemon.settings = MODULE.SettingsManager(path)
            daemon.controller = MODULE.LedController(
                baseline="0064010100e9fffb",
                effective="running",
            )
            daemon.logged_settings_error = None
            daemon.preview_status = None
            daemon.preview_until = 0.0
            daemon._log = lambda _message: None
            daemon._save_state = lambda: None
            calls = []
            daemon.controller._run = lambda arguments: calls.append(arguments) or ""

            settings = codex_kick75_common.default_settings()
            settings["states"]["running"] = {"color": "#123456", "brightness": 42}
            codex_kick75_common.save_settings(settings, path)
            self.assertTrue(daemon._reload_settings())
            self.assertEqual(calls, [["--set-side", "022a010000123456"]])

    def test_preview_accepts_unsaved_color_override(self):
        with tempfile.TemporaryDirectory() as temporary:
            daemon = object.__new__(MODULE.CodexKick75Daemon)
            daemon.settings = MODULE.SettingsManager(
                pathlib.Path(temporary) / "settings.json"
            )
            daemon.controller = MODULE.LedController(
                baseline="0064010100e9fffb",
                effective="idle",
            )
            daemon.preview_status = None
            daemon.preview_until = 0.0
            calls = []
            daemon.controller._run = lambda arguments: calls.append(arguments) or ""

            daemon._start_preview("running", 3.0, "#123456", 42)
            self.assertEqual(calls, [["--set-side", "022a010000123456"]])
            self.assertEqual(daemon.preview_status, "running")

    def test_preview_rejects_invalid_duration_and_overrides(self):
        with tempfile.TemporaryDirectory() as temporary:
            daemon = object.__new__(MODULE.CodexKick75Daemon)
            daemon.settings = MODULE.SettingsManager(
                pathlib.Path(temporary) / "settings.json"
            )
            daemon.controller = MODULE.LedController(effective="idle")
            daemon.preview_status = None
            daemon.preview_until = 0.0
            daemon.controller._run = mock.Mock()

            for seconds in (0.49, 10.01):
                with self.assertRaises(ValueError):
                    daemon._start_preview("running", seconds)
            with self.assertRaises(ValueError):
                daemon._start_preview("running", 3.0, "invalid", 50)
            with self.assertRaises(ValueError):
                daemon._start_preview("running", 3.0, "#123456", True)
            daemon.controller._run.assert_not_called()


class DaemonProtocolIntegrationTests(unittest.TestCase):
    def test_stalled_client_times_out_and_ping_response_is_valid(self):
        with tempfile.TemporaryDirectory() as temporary:
            daemon = object.__new__(MODULE.CodexKick75Daemon)
            daemon.aggregator = MODULE.StatusAggregator()
            daemon.controller = MODULE.LedController(effective="idle")
            daemon.settings = MODULE.SettingsManager(pathlib.Path(temporary) / "settings.json")
            daemon.preview_status = None
            daemon.preview_until = 0.0

            server, stalled = socket.socketpair()
            server.settimeout(0.01)
            try:
                stalled.sendall(b'{"command"')
                self.assertIsNone(daemon._receive(server))
            finally:
                server.close()
                stalled.close()

            server, client = socket.socketpair()
            client.settimeout(1.0)
            try:
                self.assertTrue(daemon._respond(server, daemon._snapshot()))
                response = json.loads(client.recv(65536).decode("utf-8"))
            finally:
                server.close()
                client.close()
        self.assertTrue(response["ok"])
        self.assertEqual(response["version"], "0.2.0")
        self.assertEqual(response["status"], "idle")
        self.assertIsNone(response["light"])
        self.assertEqual(response["tasks"], 0)

    def test_corrupt_state_is_ignored_on_startup(self):
        with tempfile.TemporaryDirectory() as temporary:
            state_path = pathlib.Path(temporary) / "state.json"
            state_path.write_text("not-json", encoding="utf-8")
            with mock.patch.object(MODULE, "STATE_PATH", state_path):
                self.assertEqual(MODULE.CodexKick75Daemon._load_state(), {})


if __name__ == "__main__":
    unittest.main()
