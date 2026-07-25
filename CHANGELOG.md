# Changelog

All notable changes to this project will be documented in this file.

## 0.1.1 - 2026-07-25

### Added

- Active side-light health checks that automatically reapply the current status color after a USB reconnect or keyboard-side reset.
- A daemon ping protocol used by `status` and acknowledged reset requests.
- Hardware availability and daemon version details in status output.
- Configurable reconnect check intervals with `--reconnect-check`.
- Protocol integration tests for stalled local clients and malformed state files.

### Fixed

- Prevented a partial or stalled Unix socket client from blocking the daemon indefinitely.
- Made daemon startup and the status command tolerate missing, malformed, or structurally invalid state files.
- Prevented stale socket files from being reported as a healthy running service.

## 0.1.0 - 2026-07-25

### Added

- USB HID control for the five Kick75 IO side LEDs on macOS.
- Red, yellow, green, and original-effect restoration states.
- Global aggregation across multiple Codex sessions.
- Codex lifecycle Hook integration.
- Per-user LaunchAgent with automatic restart.
- Automatic build, install, status, reset, hardware test, and uninstall commands.
- Privacy-preserving Hook event normalization.
- Unit tests and macOS GitHub Actions workflow.
- Detailed deployment, troubleshooting, privacy, and protocol documentation.
- MIT license and SPDX source identifiers.
