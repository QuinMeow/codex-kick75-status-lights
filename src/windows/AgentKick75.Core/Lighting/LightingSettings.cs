namespace AgentKick75.Core.Lighting;

public sealed record LightStyle
{
    public LightStyle(RgbColor color, int brightness)
    {
        if (brightness is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(brightness),
                "Brightness must be between 0 and 100.");
        }

        Color = color;
        Brightness = (byte)brightness;
    }

    public RgbColor Color { get; }

    public byte Brightness { get; }
}

public sealed record LightingSettings
{
    public const int CurrentVersion = 1;

    public static LightingSettings Default { get; } = new(
        new LightStyle(RgbColor.Parse("#006BFF"), 100),
        new LightStyle(RgbColor.Parse("#FFB400"), 100),
        new LightStyle(RgbColor.Parse("#00FF00"), 100),
        TimeSpan.FromSeconds(10));

    public LightingSettings(
        LightStyle thinking,
        LightStyle requiresInput,
        LightStyle complete,
        TimeSpan completeTtl,
        int version = CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(thinking);
        ArgumentNullException.ThrowIfNull(requiresInput);
        ArgumentNullException.ThrowIfNull(complete);

        if (version != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Unsupported settings version.");
        }

        if (completeTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completeTtl), "Complete TTL must be positive.");
        }

        Version = version;
        Thinking = thinking;
        RequiresInput = requiresInput;
        Complete = complete;
        CompleteTtl = completeTtl;
    }

    public int Version { get; }

    public LightStyle Thinking { get; }

    public LightStyle RequiresInput { get; }

    public LightStyle Complete { get; }

    public TimeSpan CompleteTtl { get; }

    public void Validate()
    {
        if (Version != CurrentVersion
            || Thinking is null
            || RequiresInput is null
            || Complete is null
            || CompleteTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Lighting settings are invalid.");
        }
    }
}
