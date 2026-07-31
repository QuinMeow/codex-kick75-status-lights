# M0 Windows Baseline

> Date: 2026-07-31
> Status: Complete; no HID write or hardware test was performed

## Repository Baseline

- Fork: [QuinMeow/codex-kick75-status-lights](https://github.com/QuinMeow/codex-kick75-status-lights)
- Working branch: `agent/windows-m0`
- Primary upstream: Pixelmoss `v0.2.0` at `e32648ee86a8a729734060ac09bd7f8a1213876f`
- Reference upstream: alvis at `bf2dcb48f2c87c1794d524b9194d9aae96827cc4`
- Remotes: `origin` = QuinMeow, `upstream` = Pixelmoss, `alvis` = alvis-HaoH

The original Git history, `LICENSE`, and copyright notice remain intact. M0
copies no alvis implementation code; it records only provenance and reviewed
safety constraints.

## Development Baseline

| Component | Verified version |
| --- | --- |
| Windows | NT `10.0.26200`, x64 |
| .NET SDK | `10.0.302` |
| .NET runtime | `10.0.10` |
| Python | `3.11.7` via `C:\Users\nicop\anaconda3\python.exe` |
| Git | `2.45.2.windows.1` |

The solution contains Core, Windows HID, WinForms host, and three xUnit
projects. The host is intentionally non-persistent in M0: it initializes
WinForms and exits. No HID transport or lighting command exists yet.

## Verification Record

| Check | Result |
| --- | --- |
| Unmodified upstream Python tests on Windows | 26/27; only the POSIX `0600` mode-bit assertion failed |
| Cross-platform Python tests after conditional mode-bit check | 27/27 passed |
| `dotnet format --verify-no-changes` | Passed |
| Release build | 6/6 projects built; 0 warnings, 0 errors |
| Protocol fixture tests | 4/4 passed |
| Core and integration test projects | Compile-only M0 skeletons; no tests discovered |

The POSIX permission assertion remains active on non-Windows systems. Windows
ACL behavior belongs to the future Windows settings-store tests; the round-trip
and temporary-file cleanup assertions still run on every platform.

## Protocol Evidence Boundary

The 64-byte request reports in `protocol-v1.json` are derived deterministically
from the pinned Pixelmoss source using fixed session key `0x5A`; they are not
new M0 device captures. The `baseline-example` payload reuses an upstream
documented device-read example and must never be used as a universal restore
value. Tests lock the frame length, checksum, `0xD5`/`0xD6` commands, and
side-light address `9`.

The alvis review adds M1 safety constraints: never emit persistent-off mode
`0x04`; use static mode `0x02` with zero brightness for an off animation
phase; preserve the original eight side-light bytes; and restore them on exit
or failure. These constraints are documented but not exercised against hardware
in M0.
