# SPDX-License-Identifier: MIT
import importlib.util
import pathlib
import unittest


MODULE_PATH = pathlib.Path(__file__).parents[1] / "scripts" / "install.py"
SPEC = importlib.util.spec_from_file_location("kick75_installer", MODULE_PATH)
INSTALLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALLER)


class InstallerTests(unittest.TestCase):
    def test_merge_preserves_other_hooks_and_is_idempotent(self):
        config = {
            "description": "existing",
            "hooks": {
                "Stop": [{"hooks": [{"type": "command", "command": "other-hook"}]}],
            },
        }
        self.assertTrue(INSTALLER.merge_hooks(config))
        self.assertEqual(len(config["hooks"]["Stop"]), 2)
        self.assertFalse(INSTALLER.merge_hooks(config))
        self.assertEqual(len(config["hooks"]["Stop"]), 2)

    def test_remove_only_deletes_project_hooks(self):
        config = {
            "hooks": {
                "Stop": [
                    {"hooks": [{"command": "other-hook"}]},
                    INSTALLER.hook_group(),
                ],
                "UserPromptSubmit": [INSTALLER.hook_group()],
            }
        }
        self.assertTrue(INSTALLER.remove_hooks(config))
        self.assertEqual(config["hooks"], {"Stop": [{"hooks": [{"command": "other-hook"}]}]})


if __name__ == "__main__":
    unittest.main()
