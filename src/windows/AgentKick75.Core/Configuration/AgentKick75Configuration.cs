using AgentKick75.Core.Lighting;

namespace AgentKick75.Core.Configuration;

public sealed record AgentKick75Configuration
{
    public const int CurrentSchemaVersion = 1;

    public static readonly TimeSpan MinimumCompleteTtl = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumCompleteTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinimumStaleSessionTtl = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumStaleSessionTtl = TimeSpan.FromDays(7);

    public static AgentKick75Configuration Default { get; } = new(
        LightingSettings.Default,
        TimeSpan.FromMinutes(30));

    public AgentKick75Configuration(
        LightingSettings lighting,
        TimeSpan staleSessionTtl,
        bool startAtLogin = false,
        int schemaVersion = CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(lighting);

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ConfigurationValidationException(
                ConfigurationValidationError.UnsupportedSchemaVersion,
                $"Unsupported configuration schema version {schemaVersion}.");
        }

        ValidateDuration(
            lighting.CompleteTtl,
            MinimumCompleteTtl,
            MaximumCompleteTtl,
            ConfigurationValidationError.InvalidCompleteTtl,
            "Complete TTL");
        ValidateDuration(
            staleSessionTtl,
            MinimumStaleSessionTtl,
            MaximumStaleSessionTtl,
            ConfigurationValidationError.InvalidStaleSessionTtl,
            "Stale session TTL");

        SchemaVersion = schemaVersion;
        Lighting = lighting;
        StaleSessionTtl = staleSessionTtl;
        StartAtLogin = startAtLogin;
    }

    public int SchemaVersion { get; }

    public LightingSettings Lighting { get; }

    public TimeSpan StaleSessionTtl { get; }

    public bool StartAtLogin { get; }

    private static void ValidateDuration(
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum,
        ConfigurationValidationError error,
        string label)
    {
        if (value < minimum || value > maximum)
        {
            throw new ConfigurationValidationException(
                error,
                $"{label} must be between {minimum} and {maximum}.");
        }
    }
}

public enum ConfigurationValidationError
{
    UnsupportedSchemaVersion,
    InvalidColor,
    InvalidBrightness,
    InvalidCompleteTtl,
    InvalidStaleSessionTtl,
    InvalidDocument,
}

public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(
        ConfigurationValidationError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public ConfigurationValidationError Error { get; }
}
