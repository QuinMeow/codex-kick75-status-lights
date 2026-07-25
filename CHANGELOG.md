# Changelog

All notable changes to this project will be documented in this file.

## 0.2.0 - 2026-07-25

### Added

- The first stable native menu bar configuration experience for status colors, brightness, live previews, and service visibility.
- Inline `#RRGGBB` validation that disables invalid previews and saves before they reach the daemon.
- Release checks for active-task preview restoration, invalid preview durations, and malformed preview overrides.

### Changed

- The app now reads its displayed version from the bundle metadata and distinguishes checking, connected, and unavailable service states.
- Action notices and service failures now use independent visual severity instead of sharing stale error styling.
- CLI configuration writes use private, uniquely named temporary files with an explicit flush before atomic replacement.
- Local app bundles now use stable version `0.2.0`, build number `3`, and retain ad-hoc signature verification.

## 0.2.0-alpha.2 - 2026-07-25

### Added

- A native macOS 13+ menu bar app for editing every status color and brightness with system controls.
- Three-second previews for unsaved colors, with automatic restoration of the active status or original side-light effect.
- A daemon `preview` socket command with validated color, brightness, and duration overrides.
- Reproducible local `.app` packaging, ad-hoc signing, and installation into `~/Applications`.
- Dependency-free Swift core checks that run with Xcode Command Line Tools alone.

### Changed

- The daemon snapshot now reports active previews and their remaining duration.
- Preview restoration now follows task changes, configuration reloads, reset, shutdown, and transient HID failures.
- The project version is now `0.2.0-alpha.2`.

## 0.2.0-alpha.1 - 2026-07-25

### Added

- Versioned `settings.json` configuration shared by the daemon, CLI, and future macOS app.
- Independent RGB and brightness settings for running, permission, failure, and completed states.
- A `config` CLI to inspect, update, or reset status-light settings.
- Automatic configuration hot reload without restarting Codex or the LaunchAgent.
- Validation, atomic writes, private file permissions, and last-known-good fallback for invalid edits.

### Changed

- The daemon now writes dynamically encoded side-light states instead of compiled red, yellow, and green constants.
- Permission requests and tool failures are separate configurable states, with failure taking global priority.
- Status output now reports the semantic state, active RGB value, brightness, and settings path.
- USB reconnect recovery validates the configured color and brightness rather than fixed defaults.

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
