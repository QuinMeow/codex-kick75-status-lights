using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKick75.Core.Storage;

namespace AgentKick75.Core.Installation;

public sealed record HookRegistrationResult(
    bool Changed,
    int RegisteredHandlerCount,
    string? BackupPath = null);

public sealed class HookRegistrationManager
{
    // Three seconds includes the Codex shell plus the explicit PowerShell
    // launcher cold start and is also the documented SessionEnd maximum.
    public const int HookTimeoutSeconds = 3;
    public const int RequiredHandlerCount = 6;

    private const string HookCommandSuffix = " hook codex";
    private const string PowerShellInvocationPrefix =
        " -NoLogo -NoProfile -NonInteractive -Command \"& '";
    private const string PowerShellCommandSuffix = "' hook codex\"";

    private static readonly (string EventName, string? Matcher)[] Registrations =
    [
        ("UserPromptSubmit", null),
        ("PreToolUse", "^request_user_input$"),
        ("PermissionRequest", null),
        ("PostToolUse", null),
        ("Stop", null),
        ("SessionEnd", null),
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider timeProvider;

    public HookRegistrationManager(string hooksPath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksPath);
        HooksPath = Path.GetFullPath(hooksPath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string HooksPath { get; }

    public async Task<HookRegistrationResult> InstallAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        string command = BuildCommand(executablePath);
        string windowsCommand = BuildWindowsCommand(executablePath);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject root = await LoadAsync(createIfMissing: true, cancellationToken)
                .ConfigureAwait(false);
            JsonNode before = root.DeepClone();
            JsonObject hooks = GetOrCreateHooks(root);

            ValidateTargetEvents(hooks);
            RemoveProjectHandlers(hooks);
            foreach ((string eventName, string? matcher) in Registrations)
            {
                var groups = hooks[eventName] as JsonArray ?? new JsonArray();
                hooks[eventName] = groups;
                groups.Add(CreateGroup(command, windowsCommand, matcher));
            }

            bool changed = !JsonNode.DeepEquals(before, root);
            string? backupPath = changed
                ? await SaveWithBackupAsync(root, cancellationToken).ConfigureAwait(false)
                : null;
            return new HookRegistrationResult(
                changed,
                CountProjectHandlers(hooks),
                backupPath);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HookRegistrationResult> UninstallAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(HooksPath))
            {
                return new HookRegistrationResult(false, 0);
            }

            JsonObject root = await LoadAsync(createIfMissing: false, cancellationToken)
                .ConfigureAwait(false);
            JsonNode before = root.DeepClone();
            if (root["hooks"] is JsonObject hooks)
            {
                RemoveProjectHandlers(hooks);
                if (hooks.Count == 0)
                {
                    root.Remove("hooks");
                }
            }

            bool changed = !JsonNode.DeepEquals(before, root);
            string? backupPath = changed
                ? await SaveWithBackupAsync(root, cancellationToken).ConfigureAwait(false)
                : null;
            int remaining = root["hooks"] is JsonObject remainingHooks
                ? CountProjectHandlers(remainingHooks)
                : 0;
            return new HookRegistrationResult(changed, remaining, backupPath);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string BuildCommand(string executablePath)
    {
        string fullPath = GetValidatedFullPath(executablePath);
        return $"\"{fullPath}\"{HookCommandSuffix}";
    }

    internal static string BuildWindowsCommand(string executablePath)
    {
        string fullPath = GetValidatedFullPath(executablePath);
        string powerShellLiteral = fullPath.Replace("'", "''", StringComparison.Ordinal);
        return $"{GetWindowsPowerShellPath()}{PowerShellInvocationPrefix}" +
            $"{powerShellLiteral}{PowerShellCommandSuffix}";
    }

    private static string GetWindowsPowerShellPath()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string powerShellPath = Path.GetFullPath(Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"));
        if (!OperatingSystem.IsWindows() || !File.Exists(powerShellPath))
        {
            throw new PlatformNotSupportedException(
                "Windows PowerShell 5.1 is required to register AgentKick75 Codex hooks.");
        }

        if (powerShellPath.Any(character =>
                !(char.IsLetterOrDigit(character) || character is ':' or '\\' or '.' or '-' or '_')))
        {
            throw new InvalidOperationException(
                "The Windows PowerShell path cannot be embedded safely in a shell command.");
        }

        return powerShellPath;
    }

    private static string GetValidatedFullPath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"')
            || executablePath.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The executable path contains characters that cannot be quoted safely.",
                nameof(executablePath));
        }

        return Path.GetFullPath(executablePath);
    }

    private static JsonObject CreateGroup(
        string command,
        string windowsCommand,
        string? matcher)
    {
        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            // Native Windows Codex runs hooks through the active shell. Invoke
            // PowerShell explicitly so this remains valid from PowerShell or cmd;
            // '&' performs the call and the single-quoted path cannot expand.
            ["commandWindows"] = windowsCommand,
            ["timeout"] = HookTimeoutSeconds,
        };
        var group = new JsonObject();
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }

        group["hooks"] = new JsonArray(handler);

        return group;
    }

    private static JsonObject GetOrCreateHooks(JsonObject root)
    {
        if (!root.TryGetPropertyValue("hooks", out JsonNode? hooksNode))
        {
            var hooks = new JsonObject();
            root["hooks"] = hooks;
            return hooks;
        }

        return hooksNode as JsonObject
            ?? throw new InvalidDataException("Top-level hooks must be a JSON object.");
    }

    private static void ValidateTargetEvents(JsonObject hooks)
    {
        foreach ((string eventName, _) in Registrations)
        {
            if (!hooks.TryGetPropertyValue(eventName, out JsonNode? eventNode))
            {
                hooks[eventName] = new JsonArray();
                continue;
            }

            if (eventNode is not JsonArray groups)
            {
                throw new InvalidDataException($"hooks.{eventName} must be an array.");
            }

            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonObject group || group["hooks"] is not JsonArray)
                {
                    throw new InvalidDataException(
                        $"Every hooks.{eventName} matcher group must contain a hooks array.");
                }
            }
        }
    }

    private static void RemoveProjectHandlers(JsonObject hooks)
    {
        foreach ((string eventName, JsonNode? eventNode) in hooks.ToArray())
        {
            if (eventNode is not JsonArray groups)
            {
                continue;
            }

            for (int groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                if (groups[groupIndex] is not JsonObject group
                    || group["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                for (int handlerIndex = handlers.Count - 1; handlerIndex >= 0; handlerIndex--)
                {
                    if (handlers[handlerIndex] is JsonObject handler && IsProjectHandler(handler))
                    {
                        handlers.RemoveAt(handlerIndex);
                    }
                }

                if (handlers.Count == 0 && HasOnlyCanonicalGroupProperties(group))
                {
                    groups.RemoveAt(groupIndex);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }
    }

    private static bool HasOnlyCanonicalGroupProperties(JsonObject group)
    {
        return group.All(property => property.Key is "matcher" or "hooks");
    }

    private static int CountProjectHandlers(JsonObject hooks)
    {
        int count = 0;
        foreach (KeyValuePair<string, JsonNode?> property in hooks)
        {
            JsonNode? eventNode = property.Value;
            if (eventNode is not JsonArray groups)
            {
                continue;
            }

            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonObject group || group["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                count += handlers.Count(
                    handler => handler is JsonObject handlerObject && IsProjectHandler(handlerObject));
            }
        }

        return count;
    }

    private static bool IsProjectHandler(JsonObject handler)
    {
        if (!string.Equals(TryGetString(handler["type"]), "command", StringComparison.Ordinal))
        {
            return false;
        }

        return IsProjectCommand(TryGetString(handler["commandWindows"]))
            || IsProjectCommand(TryGetString(handler["command"]));
    }

    private static bool IsProjectCommand(string? command)
    {
        if (command is null)
        {
            return false;
        }

        string executable;
        int powerShellInvocationIndex = command.IndexOf(
            PowerShellInvocationPrefix,
            StringComparison.OrdinalIgnoreCase);
        if (powerShellInvocationIndex > 0
            && command.EndsWith(PowerShellCommandSuffix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFileName(command[..powerShellInvocationIndex]),
                "powershell.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            executable = command[
                (powerShellInvocationIndex + PowerShellInvocationPrefix.Length)..^PowerShellCommandSuffix.Length]
                .Replace("''", "'", StringComparison.Ordinal);
        }
        else if (command.EndsWith(HookCommandSuffix, StringComparison.OrdinalIgnoreCase))
        {
            executable = command[..^HookCommandSuffix.Length].Trim();
        }
        else
        {
            return false;
        }

        if (executable.StartsWith('&'))
        {
            executable = executable[1..].TrimStart();
        }

        if (executable.Length >= 2
            && ((executable[0] == '"' && executable[^1] == '"')
                || (executable[0] == '\'' && executable[^1] == '\'')))
        {
            char quote = executable[0];
            executable = executable[1..^1];
            if (quote == '\'')
            {
                executable = executable.Replace("''", "'", StringComparison.Ordinal);
            }
        }

        string normalized = executable.Replace('\\', '/');
        string fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return string.Equals(fileName, "AgentKick75.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "AgentKick75.App.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "AgentKick75.Hook.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "AgentKick75", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? result)
            ? result
            : null;
    }

    private async Task<JsonObject> LoadAsync(
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(HooksPath))
        {
            if (!createIfMissing)
            {
                throw new FileNotFoundException("The hooks file does not exist.", HooksPath);
            }

            return new JsonObject
            {
                ["description"] = "Global personal Codex hooks.",
            };
        }

        string json = await File.ReadAllTextAsync(HooksPath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("hooks.json must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("hooks.json is not valid JSON.", exception);
        }
    }

    private async Task<string?> SaveWithBackupAsync(
        JsonObject root,
        CancellationToken cancellationToken)
    {
        string? backupPath = null;
        if (File.Exists(HooksPath))
        {
            backupPath = CreateBackupPath();
            File.Copy(HooksPath, backupPath, overwrite: false);
        }

        string json = root.ToJsonString(SerializerOptions) + Environment.NewLine;
        await AtomicFile.WriteUtf8Async(HooksPath, json, cancellationToken).ConfigureAwait(false);
        return backupPath;
    }

    private string CreateBackupPath()
    {
        string directory = Path.GetDirectoryName(HooksPath)
            ?? throw new InvalidOperationException("The hooks path has no parent directory.");
        string timestamp = timeProvider.GetUtcNow()
            .ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string basePath = Path.Combine(directory, $"{Path.GetFileName(HooksPath)}.backup-{timestamp}");
        string candidate = basePath;
        for (int suffix = 1; File.Exists(candidate); suffix++)
        {
            candidate = $"{basePath}-{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }
}
