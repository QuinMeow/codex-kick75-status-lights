namespace AgentKick75.Core.Lighting;

public enum SideLightEffect : byte
{
    Flowing = 0x00,
    Static = 0x02,
    Breathing = 0x03,
}

public enum KeepAwakePolicy
{
    Disabled,
    WhileCodexActive,
    WhileHostRunning,
}

public enum KeepAwakeRegion
{
    SideLightsOnly,
}

public sealed record KeepAwakeSettings
{
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumRefreshInterval = TimeSpan.FromMinutes(5);

    public static KeepAwakeSettings Default { get; } = new(
        KeepAwakePolicy.Disabled,
        KeepAwakeRegion.SideLightsOnly,
        TimeSpan.FromSeconds(60));

    public KeepAwakeSettings(
        KeepAwakePolicy policy,
        KeepAwakeRegion region,
        TimeSpan refreshInterval)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (!Enum.IsDefined(region))
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        if (refreshInterval < MinimumRefreshInterval ||
            refreshInterval > MaximumRefreshInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refreshInterval),
                $"Refresh interval must be between {MinimumRefreshInterval} and {MaximumRefreshInterval}.");
        }

        Policy = policy;
        Region = region;
        RefreshInterval = refreshInterval;
    }

    public KeepAwakePolicy Policy { get; }

    public KeepAwakeRegion Region { get; }

    public TimeSpan RefreshInterval { get; }
}

public sealed record LightStyle
{
    public const int MinimumSpeed = 1;
    public const int MaximumSpeed = 5;

    public LightStyle(
        RgbColor color,
        int brightness,
        SideLightEffect effect = SideLightEffect.Static,
        int speed = MinimumSpeed)
    {
        if (brightness is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(brightness),
                "Brightness must be between 0 and 100.");
        }

        if (!Enum.IsDefined(effect))
        {
            throw new ArgumentOutOfRangeException(nameof(effect), "Unsupported side-light effect.");
        }

        if (speed is < MinimumSpeed or > MaximumSpeed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speed),
                $"Speed must be between {MinimumSpeed} and {MaximumSpeed}.");
        }

        Color = color;
        Brightness = (byte)brightness;
        Effect = effect;
        Speed = (byte)speed;
    }

    public RgbColor Color { get; }

    public byte Brightness { get; }

    public SideLightEffect Effect { get; }

    public byte Speed { get; }
}

public sealed record LightingSettings
{
    public const int CurrentVersion = 1;

    public static LightingSettings Default { get; } = new(
        new LightStyle(RgbColor.Parse("#006BFF"), 100),
        new LightStyle(RgbColor.Parse("#FFB400"), 100),
        new LightStyle(RgbColor.Parse("#00FF00"), 100),
        new LightStyle(RgbColor.Parse("#FF3B30"), 100),
        TimeSpan.FromSeconds(10));

    public LightingSettings(
        LightStyle thinking,
        LightStyle requiresInput,
        LightStyle complete,
        TimeSpan completeTtl,
        int version = CurrentVersion)
        : this(
            thinking,
            requiresInput,
            complete,
            Default.Interrupted,
            completeTtl,
            version)
    {
    }

    public LightingSettings(
        LightStyle thinking,
        LightStyle requiresInput,
        LightStyle complete,
        LightStyle interrupted,
        TimeSpan completeTtl,
        int version = CurrentVersion)
        : this(
            thinking,
            requiresInput,
            complete,
            interrupted,
            completeTtl,
            KeepAwakeSettings.Default,
            version)
    {
    }

    public LightingSettings(
        LightStyle thinking,
        LightStyle requiresInput,
        LightStyle complete,
        LightStyle interrupted,
        TimeSpan completeTtl,
        KeepAwakeSettings keepAwake,
        int version = CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(thinking);
        ArgumentNullException.ThrowIfNull(requiresInput);
        ArgumentNullException.ThrowIfNull(complete);
        ArgumentNullException.ThrowIfNull(interrupted);
        ArgumentNullException.ThrowIfNull(keepAwake);

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
        Interrupted = interrupted;
        CompleteTtl = completeTtl;
        KeepAwake = keepAwake;
    }

    public int Version { get; }

    public LightStyle Thinking { get; }

    public LightStyle RequiresInput { get; }

    public LightStyle Complete { get; }

    public LightStyle Interrupted { get; }

    public TimeSpan CompleteTtl { get; }

    public KeepAwakeSettings KeepAwake { get; }

    public void Validate()
    {
        if (Version != CurrentVersion
            || Thinking is null
            || RequiresInput is null
            || Complete is null
            || Interrupted is null
            || KeepAwake is null
            || CompleteTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Lighting settings are invalid.");
        }
    }
}
