using System.Globalization;

namespace AgentKick75.Core.Lighting;

public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public static RgbColor Parse(string value)
    {
        if (!TryParse(value, out RgbColor color))
        {
            throw new FormatException("Color must use #RRGGBB format.");
        }

        return color;
    }

    public static bool TryParse(string? value, out RgbColor color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        if (!byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
        {
            return false;
        }

        color = new RgbColor(red, green, blue);
        return true;
    }

    public override string ToString()
    {
        return FormattableString.Invariant($"#{Red:X2}{Green:X2}{Blue:X2}");
    }
}
