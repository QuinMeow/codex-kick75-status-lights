using System.Text.Json.Nodes;
using AgentKick75.Core.Installation;

namespace AgentKick75.Core.Tests;

public sealed class HookRegistrationTests
{
    private static readonly DateTimeOffset BackupTime = new(
        2026,
        7,
        31,
        1,
        2,
        3,
        456,
        TimeSpan.Zero);

    [Fact]
    public async Task InstallAsync_MissingFile_WritesSixOfficialCommandWindowsRegistrations()
    {
        using var directory = new HookTemporaryDirectory();
        string path = directory.File("hooks.json");
        var manager = Manager(path);

        HookRegistrationResult result = await manager.InstallAsync(Executable(directory));

        Assert.True(result.Changed);
        Assert.Equal(6, result.RegisteredHandlerCount);
        Assert.Null(result.BackupPath);
        JsonObject root = await ReadObject(path);
        JsonObject hooks = Assert.IsType<JsonObject>(root["hooks"]);
        Assert.Equal(
            new[]
            {
                "PermissionRequest",
                "PostToolUse",
                "PreToolUse",
                "SessionEnd",
                "Stop",
                "UserPromptSubmit",
            },
            hooks.Select(property => property.Key).Order(StringComparer.Ordinal));

        JsonObject preToolGroup = SingleGroup(hooks, "PreToolUse");
        Assert.Equal("^request_user_input$", preToolGroup["matcher"]!.GetValue<string>());
        JsonObject handler = SingleHandler(preToolGroup);
        Assert.Equal("command", handler["type"]!.GetValue<string>());
        Assert.EndsWith("\" hook codex", handler["command"]!.GetValue<string>());
        string expectedPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.StartsWith(
            $"{expectedPowerShell} -NoLogo -NoProfile -NonInteractive -Command \"& '",
            handler["commandWindows"]!.GetValue<string>());
        Assert.EndsWith("' hook codex\"", handler["commandWindows"]!.GetValue<string>());
        Assert.Contains("O''Brien", handler["commandWindows"]!.GetValue<string>());
        Assert.Equal(HookRegistrationManager.HookTimeoutSeconds, handler["timeout"]!.GetValue<int>());
    }

    [Fact]
    public async Task BuildWindowsCommand_PathWithSpaces_RunsFromPowerShellAndCmd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new HookTemporaryDirectory();
        string probe = directory.File("probe with spaces/O'Brien/AgentKick75.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
        await File.WriteAllTextAsync(
            probe,
            "@echo off\r\n" +
            "if not \"%~1\"==\"hook\" exit /B 7\r\n" +
            "if not \"%~2\"==\"codex\" exit /B 8\r\n" +
            "exit /B 0\r\n");
        string hooksPath = directory.File("hooks.json");
        await Manager(hooksPath).InstallAsync(probe);
        JsonObject root = await ReadObject(hooksPath);
        JsonObject hooks = Assert.IsType<JsonObject>(root["hooks"]);
        string command = SingleHandler(SingleGroup(hooks, "UserPromptSubmit"))
            ["commandWindows"]!.GetValue<string>();
        string powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        await AssertHookCommandSucceedsAsync(
            powerShell,
            ["-NoProfile", "-Command", command]);
        await AssertHookCommandSucceedsAsync(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            arguments: null,
            rawArguments: $"/C \"{command}\"");
    }

    [Fact]
    public async Task InstallAsync_RepeatedInstall_IsIdempotentAndDoesNotCreateAnotherBackup()
    {
        using var directory = new HookTemporaryDirectory();
        string path = directory.File("hooks.json");
        await File.WriteAllTextAsync(path, """{"description":"keep"}""");
        var manager = Manager(path);

        HookRegistrationResult first = await manager.InstallAsync(Executable(directory));
        string afterFirst = await File.ReadAllTextAsync(path);
        HookRegistrationResult second = await manager.InstallAsync(Executable(directory));

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Null(second.BackupPath);
        Assert.Equal(afterFirst, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFiles(directory.Path, "hooks.json.backup-*"));
    }

    [Fact]
    public async Task InstallAsync_ExistingConfig_PreservesUnknownFieldsAndDeduplicatesOldProjectHandlers()
    {
        using var directory = new HookTemporaryDirectory();
        string path = directory.File("hooks.json");
        string oldCommand = $"\"{directory.File("old/AgentKick75.exe")}\" hook codex";
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "description": "user description",
              "unknownTopLevel": { "keep": true },
              "hooks": {
                "Stop": [
                  { "customGroup": 7, "hooks": [
                    { "type": "command", "command": "other-hook", "userField": "keep" },
                    { "type": "command", "commandWindows": {{JsonValue.Create(oldCommand)!.ToJsonString()}} }
                  ] },
                  { "hooks": [ { "type": "command", "commandWindows": {{JsonValue.Create(oldCommand)!.ToJsonString()}} } ] }
                ],
                "FutureEvent": { "unknownShape": true }
              }
            }
            """);
        var manager = Manager(path);

        HookRegistrationResult result = await manager.InstallAsync(Executable(directory));

        Assert.Equal(6, result.RegisteredHandlerCount);
        JsonObject root = await ReadObject(path);
        Assert.True(root["unknownTopLevel"]!["keep"]!.GetValue<bool>());
        JsonObject hooks = Assert.IsType<JsonObject>(root["hooks"]);
        Assert.True(hooks["FutureEvent"]!["unknownShape"]!.GetValue<bool>());
        JsonArray stopGroups = Assert.IsType<JsonArray>(hooks["Stop"]);
        Assert.Equal(2, stopGroups.Count);
        JsonObject userGroup = Assert.IsType<JsonObject>(stopGroups[0]);
        Assert.Equal(7, userGroup["customGroup"]!.GetValue<int>());
        JsonArray userHandlers = Assert.IsType<JsonArray>(userGroup["hooks"]);
        Assert.Single(userHandlers);
        Assert.Equal("other-hook", userHandlers[0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task UninstallAsync_SharedMatcherGroup_RemovesOnlyProjectHandlerAndKeepsUserData()
    {
        using var directory = new HookTemporaryDirectory();
        string path = directory.File("hooks.json");
        var manager = Manager(path);
        await manager.InstallAsync(Executable(directory));
        JsonObject root = await ReadObject(path);
        JsonObject hooks = Assert.IsType<JsonObject>(root["hooks"]);
        JsonObject stopGroup = SingleGroup(hooks, "Stop");
        JsonArray handlers = Assert.IsType<JsonArray>(stopGroup["hooks"]);
        handlers.Add(new JsonObject
        {
            ["type"] = "command",
            ["command"] = "user-stop-hook",
            ["unknown"] = 123,
        });
        stopGroup["userMetadata"] = "keep";
        root["anotherRootField"] = 42;
        await File.WriteAllTextAsync(path, root.ToJsonString(new() { WriteIndented = true }));

        HookRegistrationResult result = await manager.UninstallAsync();

        Assert.True(result.Changed);
        Assert.Equal(0, result.RegisteredHandlerCount);
        JsonObject after = await ReadObject(path);
        Assert.Equal(42, after["anotherRootField"]!.GetValue<int>());
        JsonObject remainingHooks = Assert.IsType<JsonObject>(after["hooks"]);
        JsonObject remainingStop = SingleGroup(remainingHooks, "Stop");
        Assert.Equal("keep", remainingStop["userMetadata"]!.GetValue<string>());
        JsonObject userHandler = SingleHandler(remainingStop);
        Assert.Equal("user-stop-hook", userHandler["command"]!.GetValue<string>());
        Assert.Equal(123, userHandler["unknown"]!.GetValue<int>());
    }

    [Fact]
    public async Task InstallAsync_ChangedExistingFile_CreatesExactTimestampedBackupBeforeWrite()
    {
        using var directory = new HookTemporaryDirectory();
        string path = directory.File("hooks.json");
        const string original = "{\"description\":\"original bytes\"}\r\n";
        await File.WriteAllTextAsync(path, original);
        var manager = Manager(path);

        HookRegistrationResult result = await manager.InstallAsync(Executable(directory));

        Assert.NotNull(result.BackupPath);
        Assert.EndsWith("hooks.json.backup-20260731T010203456Z", result.BackupPath);
        Assert.Equal(original, await File.ReadAllTextAsync(result.BackupPath));
        Assert.NotEqual(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InstallAsync_InvalidExistingJson_ThrowsAndDoesNotOverwriteOrBackup()
    {
        using var directory = new HookTemporaryDirectory();
        string path = directory.File("hooks.json");
        const string invalid = "not json";
        await File.WriteAllTextAsync(path, invalid);
        var manager = Manager(path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.InstallAsync(Executable(directory)));

        Assert.Equal(invalid, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "hooks.json.backup-*"));
    }

    [Fact]
    public async Task InstallAsync_MalformedTargetEvent_ThrowsWithoutChangingFile()
    {
        using var directory = new HookTemporaryDirectory();
        string path = directory.File("hooks.json");
        const string malformed = """{"hooks":{"Stop":{"not":"an array"}}}""";
        await File.WriteAllTextAsync(path, malformed);
        var manager = Manager(path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.InstallAsync(Executable(directory)));

        Assert.Equal(malformed, await File.ReadAllTextAsync(path));
    }

    private static HookRegistrationManager Manager(string path)
    {
        return new HookRegistrationManager(path, new HookFixedTimeProvider(BackupTime));
    }

    private static string Executable(HookTemporaryDirectory directory)
    {
        return directory.File("Program Files/O'Brien/AgentKick75/AgentKick75.exe");
    }

    private static async Task<JsonObject> ReadObject(string path)
    {
        return Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(path)));
    }

    private static async Task AssertHookCommandSucceedsAsync(
        string fileName,
        IReadOnlyList<string>? arguments,
        string? rawArguments = null)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = rawArguments ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (arguments is not null)
        {
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
        await process.StandardInput.WriteAsync("{}");
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string standardOutput = await process.StandardOutput.ReadToEndAsync();
        string standardError = await process.StandardError.ReadToEndAsync();

        Assert.True(
            process.ExitCode == 0,
            $"Hook shell exited with {process.ExitCode}; stdout={standardOutput}; stderr={standardError}");
    }

    private static JsonObject SingleGroup(JsonObject hooks, string eventName)
    {
        JsonArray groups = Assert.IsType<JsonArray>(hooks[eventName]);
        return Assert.IsType<JsonObject>(Assert.Single(groups));
    }

    private static JsonObject SingleHandler(JsonObject group)
    {
        JsonArray handlers = Assert.IsType<JsonArray>(group["hooks"]);
        return Assert.IsType<JsonObject>(Assert.Single(handlers));
    }

    private sealed class HookFixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class HookTemporaryDirectory : IDisposable
    {
        public HookTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AgentKick75.HookTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
