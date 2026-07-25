# SPDX-License-Identifier: MIT
import pathlib
import sys
import unittest


SRC = pathlib.Path(__file__).parents[1] / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

import codex_kick75_common


class CommonTests(unittest.TestCase):
    def test_normalization_drops_prompt_and_tool_payload(self):
        event = {
            "hook_event_name": "PostToolUse",
            "session_id": "session-a",
            "turn_id": "turn-a",
            "prompt": "private prompt",
            "tool_input": {"secret": "private input"},
            "tool_response": {"exit_code": 7, "output": "private output"},
        }
        normalized = codex_kick75_common.normalize_hook_event(event)
        self.assertEqual(
            normalized,
            {
                "hook_event_name": "PostToolUse",
                "session_id": "session-a",
                "turn_id": "turn-a",
                "tool_failed": True,
            },
        )

    def test_successful_nested_response_is_not_failure(self):
        self.assertFalse(
            codex_kick75_common.response_failed(
                {"content": [{"result": {"exit_code": 0, "ok": True}}]}
            )
        )


if __name__ == "__main__":
    unittest.main()
