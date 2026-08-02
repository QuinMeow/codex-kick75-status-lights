// SPDX-License-Identifier: MIT

using System.Globalization;

namespace AgentKick75.App.Commands;

public enum AppCommandKind
{
    Host,
    Status,
    Install,
    Uninstall,
    HardwareTest,
    Help,
    Invalid,
}

public sealed record HardwareTestArguments
{
    public HardwareTestArguments(
        int cycles = 1,
        TimeSpan? greenDuration = null)
    {
        if (cycles is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles));
        }

        TimeSpan effectiveDuration = greenDuration ?? TimeSpan.FromSeconds(5);
        if (effectiveDuration < TimeSpan.Zero || effectiveDuration > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(greenDuration));
        }

        Cycles = cycles;
        GreenDuration = effectiveDuration;
    }

    public int Cycles { get; }

    public TimeSpan GreenDuration { get; }
}

public sealed record ParsedCommand(
    AppCommandKind Kind,
    HardwareTestArguments? HardwareTest = null,
    string? Error = null);

public static class CommandLine
{
    public static ParsedCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return new(AppCommandKind.Host);
        }

        if (arguments.Count == 1 && arguments[0] is "status")
        {
            return new(AppCommandKind.Status);
        }

        if (arguments.Count == 1 && arguments[0] is "install")
        {
            return new(AppCommandKind.Install);
        }

        if (arguments.Count == 1 && arguments[0] is "uninstall")
        {
            return new(AppCommandKind.Uninstall);
        }

        if (arguments.Count == 1 && arguments[0] is "--help" or "-h" or "help")
        {
            return new(AppCommandKind.Help);
        }

        if (arguments[0] is "hardware-test")
        {
            return ParseHardwareTest(arguments);
        }

        return new(AppCommandKind.Invalid, Error: "Unknown command or arguments.");
    }

    public static string Usage =>
        $"AgentKick75 [status | install | uninstall | hardware-test " +
        $"[--cycles 1..100] [--green-seconds 0..60]]{Environment.NewLine}" +
        "The command reads the current side-light state, previews green, and restores it. " +
        "Only the USB transport is supported.";

    private static ParsedCommand ParseHardwareTest(IReadOnlyList<string> arguments)
    {
        int cycles = 1;
        bool cyclesSpecified = false;
        TimeSpan greenDuration = TimeSpan.FromSeconds(5);
        bool greenDurationSpecified = false;

        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--cycles" && index + 1 < arguments.Count)
            {
                if (cyclesSpecified ||
                    !int.TryParse(arguments[++index], NumberStyles.None, CultureInfo.InvariantCulture, out cycles) ||
                    cycles is < 1 or > 100)
                {
                    return new(AppCommandKind.Invalid, Error: "--cycles must be an integer from 1 to 100.");
                }

                cyclesSpecified = true;
                continue;
            }

            if (argument == "--green-seconds" && index + 1 < arguments.Count)
            {
                if (greenDurationSpecified ||
                    !double.TryParse(
                        arguments[++index],
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out double seconds) ||
                    !double.IsFinite(seconds) ||
                    seconds is < 0 or > 60)
                {
                    return new(AppCommandKind.Invalid, Error: "--green-seconds must be from 0 to 60.");
                }

                greenDuration = TimeSpan.FromSeconds(seconds);
                greenDurationSpecified = true;
                continue;
            }

            return new(AppCommandKind.Invalid, Error: $"Unknown hardware-test option: {argument}");
        }

        return new(
            AppCommandKind.HardwareTest,
            new HardwareTestArguments(cycles, greenDuration));
    }
}
