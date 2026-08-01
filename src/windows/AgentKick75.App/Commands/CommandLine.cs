// SPDX-License-Identifier: MIT

using System.Globalization;

namespace AgentKick75.App.Commands;

public enum AppCommandKind
{
    Host,
    HookCodex,
    Status,
    HardwareTest,
    Help,
    Invalid,
}

public enum HardwareTransportChoice
{
    Auto,
    Usb,
    Dongle,
}

public sealed record HardwareTestArguments
{
    public HardwareTestArguments(
        HardwareTransportChoice transport,
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

        Transport = transport;
        Cycles = cycles;
        GreenDuration = effectiveDuration;
    }

    public HardwareTransportChoice Transport { get; }

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

        if (arguments.Count == 1 && arguments[0] is "--help" or "-h" or "help")
        {
            return new(AppCommandKind.Help);
        }

        if (arguments.Count == 2 && arguments[0] is "hook" && arguments[1] is "codex")
        {
            return new(AppCommandKind.HookCodex);
        }

        if (arguments[0] is "hardware-test")
        {
            return ParseHardwareTest(arguments);
        }

        return new(AppCommandKind.Invalid, Error: "Unknown command or arguments.");
    }

    public static string Usage =>
        $"AgentKick75 [status | hook codex | hardware-test --transport auto|usb|dongle " +
        $"[--cycles 1..100] [--green-seconds 0..60]]{Environment.NewLine}" +
        "The command reads the current side-light state, previews green, and restores it. " +
        "USB is currently the only writable profile; dongle remains diagnostic-only and write-blocked.";

    private static ParsedCommand ParseHardwareTest(IReadOnlyList<string> arguments)
    {
        HardwareTransportChoice? transport = null;
        int cycles = 1;
        bool cyclesSpecified = false;
        TimeSpan greenDuration = TimeSpan.FromSeconds(5);
        bool greenDurationSpecified = false;

        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--transport" && index + 1 < arguments.Count)
            {
                if (transport is not null || !TryParseTransport(arguments[++index], out HardwareTransportChoice parsed))
                {
                    return new(AppCommandKind.Invalid, Error: "Invalid or duplicate --transport value.");
                }

                transport = parsed;
                continue;
            }

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

        if (transport is null)
        {
            return new(AppCommandKind.Invalid, Error: "hardware-test requires --transport auto|usb|dongle.");
        }

        return new(
            AppCommandKind.HardwareTest,
            new HardwareTestArguments(transport.Value, cycles, greenDuration));
    }

    private static bool TryParseTransport(string value, out HardwareTransportChoice transport)
    {
        transport = value switch
        {
            "auto" => HardwareTransportChoice.Auto,
            "usb" => HardwareTransportChoice.Usb,
            "dongle" => HardwareTransportChoice.Dongle,
            _ => default,
        };

        return value is "auto" or "usb" or "dongle";
    }
}
