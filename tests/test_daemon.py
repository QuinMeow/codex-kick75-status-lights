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
        self.assertEqual(self.status.effective(), "yellow")
        self.event("Stop", "a")
        self.assertEqual(self.status.effective(), "green")
        self.event("UserPromptSubmit", "b")
        self.assertEqual(self.status.effective(), "yellow")
        self.event("PermissionRequest", "a")
        self.assertEqual(self.status.effective(), "red")

    def test_error_is_sticky_at_stop_but_successful_tool_clears_it(self):
        self.event("UserPromptSubmit", "a")
        self.event("PermissionRequest", "a")
        self.event("Stop", "a")
        self.assertEqual(self.status.effective(), "red")
        self.event("PostToolUse", "a", {"exit_code": 0})
        self.assertEqual(self.status.effective(), "yellow")
        self.event("Stop", "a")
        self.assertEqual(self.status.effective(), "green")

    def test_expired_green_returns_idle(self):
        self.event("UserPromptSubmit", "a")
        self.event("Stop", "a")
        self.clock.value += MODULE.GREEN_HOLD_SECONDS + 0.1
        self.status.expire()
        self.assertEqual(self.status.effective(), "idle")

    def test_failed_tool_response_is_red(self):
        self.event("UserPromptSubmit", "a")
        self.event("PostToolUse", "a", {"result": {"exit_code": 2}})
        self.assertEqual(self.status.effective(), "red")

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
        controller = MODULE.LedController(baseline="0064010100e9fffb", effective="yellow")
        calls = []

        def run(arguments):
            calls.append(arguments)
            if arguments == ["--get-side"]:
                return "SIDE_STATE=0064010100e9fffb\n"
            return ""

        controller._run = run
        self.assertTrue(controller.health_check(force=True))
        self.assertEqual(calls, [["--get-side"], ["--color", "yellow"]])

    def test_health_check_does_not_rewrite_matching_color(self):
        controller = MODULE.LedController(baseline="0064010100e9fffb", effective="green")
        calls = []

        def run(arguments):
            calls.append(arguments)
            return "SIDE_STATE=026401000000ff00\n"

        controller._run = run
        self.assertFalse(controller.health_check(force=True))
        self.assertEqual(calls, [["--get-side"]])


class DaemonProtocolIntegrationTests(unittest.TestCase):
    def test_stalled_client_times_out_and_ping_response_is_valid(self):
        daemon = object.__new__(MODULE.CodexKick75Daemon)
        daemon.aggregator = MODULE.StatusAggregator()
        daemon.controller = MODULE.LedController(effective="idle")

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
        self.assertEqual(response["version"], "0.1.1")
        self.assertEqual(response["light"], "idle")
        self.assertEqual(response["tasks"], 0)

    def test_corrupt_state_is_ignored_on_startup(self):
        with tempfile.TemporaryDirectory() as temporary:
            state_path = pathlib.Path(temporary) / "state.json"
            state_path.write_text("not-json", encoding="utf-8")
            with mock.patch.object(MODULE, "STATE_PATH", state_path):
                self.assertEqual(MODULE.CodexKick75Daemon._load_state(), {})


if __name__ == "__main__":
    unittest.main()
