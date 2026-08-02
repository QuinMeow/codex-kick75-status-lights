using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentKick75.Core.Storage;

namespace AgentKick75.Core.Installation;

public sealed record CodexNotificationRegistrationResult(
    bool Changed,
    bool Registered,
    string? BackupPath = null);

public sealed partial class CodexNotificationRegistrationManager
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider timeProvider;

    public CodexNotificationRegistrationManager(string configPath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ConfigPath = Path.GetFullPath(configPath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ConfigPath { get; }

    public async Task<CodexNotificationRegistrationResult> InstallAsync(
        string hookExecutablePath,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(hookExecutablePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string original = File.Exists(ConfigPath)
                ? await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            Match match = NotifyLineRegex().Match(original);
            string[] existing = match.Success ? ParseArray(match.Groups[1].Value) : [];
            string[] registered = BuildCommand(fullPath, existing);
            string notifyLine = $"notify = {JsonSerializer.Serialize(registered)}";
            string updated = match.Success
                ? original[..match.Index] + notifyLine + original[(match.Index + match.Length)..]
                : notifyLine + Environment.NewLine + original;
            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                return new CodexNotificationRegistrationResult(false, true);
            }

            string? backupPath = null;
            if (File.Exists(ConfigPath))
            {
                backupPath = CreateBackupPath();
                File.Copy(ConfigPath, backupPath, overwrite: false);
            }

            await AtomicFile.WriteUtf8Async(ConfigPath, updated, cancellationToken)
                .ConfigureAwait(false);
            return new CodexNotificationRegistrationResult(true, true, backupPath);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CodexNotificationRegistrationResult> UninstallAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new CodexNotificationRegistrationResult(false, false);
            }

            string original = await File.ReadAllTextAsync(ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            Match match = NotifyLineRegex().Match(original);
            if (!match.Success)
            {
                return new CodexNotificationRegistrationResult(false, false);
            }

            string[] existing = ParseArray(match.Groups[1].Value);
            if (!IsAgentKick75Notify(existing))
            {
                return new CodexNotificationRegistrationResult(false, false);
            }

            int forwardIndex = Array.IndexOf(existing, "--forward");
            string updated;
            if (forwardIndex >= 0 && forwardIndex + 1 < existing.Length)
            {
                string[] forwarded = existing[(forwardIndex + 1)..];
                string notifyLine = $"notify = {JsonSerializer.Serialize(forwarded)}";
                updated = original[..match.Index] + notifyLine + original[(match.Index + match.Length)..];
            }
            else
            {
                int removeLength = match.Length;
                if (match.Index + removeLength < original.Length &&
                    original.AsSpan(match.Index + removeLength).StartsWith("\r\n"))
                {
                    removeLength += 2;
                }
                else if (match.Index + removeLength < original.Length &&
                         original[match.Index + removeLength] == '\n')
                {
                    removeLength++;
                }

                updated = original.Remove(match.Index, removeLength);
            }

            string backupPath = CreateBackupPath();
            File.Copy(ConfigPath, backupPath, overwrite: false);
            await AtomicFile.WriteUtf8Async(ConfigPath, updated, cancellationToken)
                .ConfigureAwait(false);
            return new CodexNotificationRegistrationResult(true, false, backupPath);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string[] BuildCommand(string hookExecutablePath, string[] existing)
    {
        if (IsAgentKick75Notify(existing))
        {
            existing[0] = hookExecutablePath;
            return existing;
        }

        var command = new List<string> { hookExecutablePath, "notify", "codex" };
        if (existing.Length > 0)
        {
            command.Add("--forward");
            command.AddRange(existing);
        }

        return [.. command];
    }

    private static bool IsAgentKick75Notify(IReadOnlyList<string> command) =>
        command.Count >= 3
        && string.Equals(Path.GetFileName(command[0]), "AgentKick75.Hook.exe", StringComparison.OrdinalIgnoreCase)
        && command[1] == "notify"
        && command[2] == "codex";

    private static string[] ParseArray(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(value)
                ?? throw new InvalidDataException("Codex notify must be a string array.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The existing Codex notify command is not a supported string array.",
                exception);
        }
    }

    private string CreateBackupPath()
    {
        string directory = Path.GetDirectoryName(ConfigPath)
            ?? throw new InvalidOperationException("The Codex config path has no parent directory.");
        string timestamp = timeProvider.GetUtcNow().ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string basePath = Path.Combine(directory, $"{Path.GetFileName(ConfigPath)}.backup-{timestamp}");
        string candidate = basePath;
        for (int suffix = 1; File.Exists(candidate); suffix++)
        {
            candidate = $"{basePath}-{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    [GeneratedRegex(@"(?m)^[ \t]*notify[ \t]*=[ \t]*(\[[^\r\n]*\])[ \t]*(?=\r?$)")]
    private static partial Regex NotifyLineRegex();
}
