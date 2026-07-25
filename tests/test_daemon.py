# SPDX-License-Identifier: MIT
import importlib.util
import pathlib
import unittest
import sys


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


if __name__ == "__main__":
    unittest.main()
