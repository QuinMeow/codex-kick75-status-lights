# Repository Guidelines

## Project Structure & Module Organization

Treat `docs/WINDOWS_CODEX_MVP_PLAN.md` as the authoritative scope and architecture. The planned Windows layout is:

- `src/windows/AgentKick75.Core/`: protocol codec, Codex event normalization, state reduction, lighting, and configuration.
- `src/windows/AgentKick75.Hid.Windows/`: Win32 HID/SetupAPI transport, device filtering, timeouts, and reconnect behavior.
- `src/windows/AgentKick75.App/`: tray host, named pipe, localhost API, commands, and `wwwroot/` assets.
- `tests/windows/`: xUnit unit, protocol, and integration tests.
- `docs/`: implementation plans, protocol notes, and the hardware test matrix.

Preserve upstream Python, C, macOS, license, and attribution files when the Pixelmoss fork is imported.

## Build, Test, and Development Commands

Run these from the repository root after M0 creates the solution:

```powershell
dotnet restore
dotnet build
dotnet test --no-restore
dotnet format --verify-no-changes
dotnet run --project src/windows/AgentKick75.App
dotnet publish src/windows/AgentKick75.App -c Release -r win-x64 --self-contained true
```

Do not run `hardware-test --transport usb|dongle` casually. It writes keyboard lighting and requires M1 allowlists, response validation, baseline capture, and guaranteed restoration.

## Coding Style & Naming Conventions

Use four spaces in C# and two in HTML, CSS, and JavaScript. Enable nullable reference types and keep warnings actionable. Use `PascalCase` for public members and types, `camelCase` for locals and parameters, `IName` for interfaces, and an `Async` suffix for asynchronous methods. Keep platform-independent logic in `Core`; isolate P/Invoke and device handles in `Hid.Windows`. Format with `dotnet format`.

## Testing Guidelines

Use xUnit. Name tests `Method_Scenario_ExpectedResult`. Add golden-vector tests for every protocol change and reducer tests for every Codex event sequence. CI tests must use mocked transports; real USB/U1 tests belong in the documented hardware matrix. A transport is not “verified” until it completes 20 read-write-restore cycles without altering non-side-light state.

## Commit & Pull Request Guidelines

Preserve the imported upstream history and use concise Conventional Commit subjects, for example `feat(hid): add U1 transport profile`. Pull requests should describe scope, tests run, device/firmware/transport used, security implications, and documentation changes. Include screenshots only for dashboard changes and link relevant issues.

## Security & Agent Instructions

Never log prompts, tool payloads, assistant messages, or transcripts. Keep HID PID, usage, opcode, and address allowlists explicit. Do not claim hardware support from enumeration alone. Preserve user-edited plans and unrelated changes; ask before expanding scope beyond Codex and Kick75 NuPhyIO.
