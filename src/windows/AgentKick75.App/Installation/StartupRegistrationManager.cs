// SPDX-License-Identifier: MIT
using Microsoft.Win32;

namespace AgentKick75.App.Installation;

public sealed class StartupRegistrationManager
{
    public const string ValueName = "AgentKick75";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled(string executablePath)
    {
        string expected = BuildCommand(executablePath);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return string.Equals(
            key?.GetValue(ValueName) as string,
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        string command = BuildCommand(executablePath);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the current-user Run registry key.");
        if (enabled)
        {
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Contains('"') || fullPath.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The executable path cannot be safely registered for startup.",
                nameof(executablePath));
        }

        return $"\"{fullPath}\"";
    }
}
