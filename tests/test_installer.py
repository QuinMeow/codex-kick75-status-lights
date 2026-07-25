# SPDX-License-Identifier: MIT
import importlib.util
import pathlib
import tempfile
import unittest
from unittest import mock


MODULE_PATH = pathlib.Path(__file__).parents[1] / "scripts" / "install.py"
SPEC = importlib.util.spec_from_file_location("kick75_installer", MODULE_PATH)
INSTALLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALLER)


class InstallerTests(unittest.TestCase):
    def test_app_plist_describes_menu_bar_bundle(self):
        info = INSTALLER.app_info_plist()
        self.assertEqual(info["CFBundleIdentifier"], "com.zzm.codex-kick75.app")
        self.assertEqual(info["CFBundleShortVersionString"], "0.2.0")
        self.assertEqual(info["CFBundleVersion"], "3")
        self.assertEqual(info["LSMinimumSystemVersion"], "13.0")
        self.assertTrue(info["LSUIElement"])

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

    def test_corrupt_runtime_state_is_reported_without_raising(self):
        with tempfile.TemporaryDirectory() as temporary:
            app_dir = pathlib.Path(temporary)
            (app_dir / "state.json").write_text("not-json", encoding="utf-8")
            with mock.patch.object(INSTALLER, "APP_DIR", app_dir):
                state, error = INSTALLER.load_runtime_state()
        self.assertIsNone(state)
        self.assertIsNotNone(error)

    def test_configure_writes_valid_custom_settings(self):
        with tempfile.TemporaryDirectory() as temporary:
            app_dir = pathlib.Path(temporary)
            settings_path = app_dir / "settings.json"
            socket_path = app_dir / "status.sock"
            socket_path.touch()
            with (
                mock.patch.object(INSTALLER, "SETTINGS_PATH", settings_path),
                mock.patch.object(INSTALLER, "SOCKET_PATH", socket_path),
                mock.patch.object(INSTALLER, "daemon_request") as request,
                mock.patch("builtins.print"),
            ):
                self.assertEqual(INSTALLER.configure("running", "#123abc", 42, False), 0)
            request.assert_called_once_with("reload", timeout=15.0)
            settings = INSTALLER.load_settings(settings_path)
            self.assertEqual(
                settings["states"]["running"],
                {"color": "#123ABC", "brightness": 42},
            )


if __name__ == "__main__":
    unittest.main()
