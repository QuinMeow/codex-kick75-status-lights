using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKick75.Core.Lighting;
using AgentKick75.Core.Storage;

namespace AgentKick75.Core.Configuration;

public enum ConfigurationLoadStatus
{
    Loaded,
    MissingUsingDefaults,
    InvalidUsingDefaults,
    UnsupportedVersionUsingDefaults,
}

public sealed record ConfigurationLoadResult(
    AgentKick75Configuration Configuration,
    ConfigurationLoadStatus Status,
    string? ErrorMessage = null);

public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    public ConfigurationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public async Task<ConfigurationLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return new ConfigurationLoadResult(
                    AgentKick75Configuration.Default,
                    ConfigurationLoadStatus.MissingUsingDefaults);
            }

            try
            {
                string json = await File.ReadAllTextAsync(Path, cancellationToken)
                    .ConfigureAwait(false);
                AgentKick75Configuration configuration = Parse(json);
                return new ConfigurationLoadResult(configuration, ConfigurationLoadStatus.Loaded);
            }
            catch (ConfigurationValidationException exception)
                when (exception.Error == ConfigurationValidationError.UnsupportedSchemaVersion)
            {
                return new ConfigurationLoadResult(
                    AgentKick75Configuration.Default,
                    ConfigurationLoadStatus.UnsupportedVersionUsingDefaults,
                    exception.Message);
            }
            catch (Exception exception) when (IsInvalidConfiguration(exception))
            {
                return new ConfigurationLoadResult(
                    AgentKick75Configuration.Default,
                    ConfigurationLoadStatus.InvalidUsingDefaults,
                    exception.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        AgentKick75Configuration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string json = Serialize(configuration);
            await AtomicFile.WriteUtf8Async(Path, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static AgentKick75Configuration Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw InvalidDocument("Configuration root must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw InvalidDocument("Configuration is not valid JSON.", exception);
        }

        int schemaVersion = ReadSchemaVersion(root);
        if (schemaVersion != AgentKick75Configuration.CurrentSchemaVersion)
        {
            throw new ConfigurationValidationException(
                ConfigurationValidationError.UnsupportedSchemaVersion,
                $"Unsupported configuration schema version {schemaVersion}.");
        }

        bool usesLegacyStateNames = root["schemaVersion"] is null && root["version"] is not null;
        JsonObject states = ReadOptionalObject(root, "states") ?? [];
        LightingSettings defaults = LightingSettings.Default;

        LightStyle thinking = ReadStyle(
            states,
            usesLegacyStateNames ? "running" : "thinking",
            defaults.Thinking);
        LightStyle requiresInput = ReadStyle(
            states,
            usesLegacyStateNames ? "permission" : "requiresInput",
            defaults.RequiresInput);
        LightStyle complete = ReadStyle(
            states,
            usesLegacyStateNames ? "completed" : "complete",
            defaults.Complete);

        TimeSpan completeTtl = TimeSpan.FromSeconds(ReadOptionalNumber(
            root,
            "completeTtlSeconds",
            defaults.CompleteTtl.TotalSeconds));
        TimeSpan staleSessionTtl = TimeSpan.FromMinutes(ReadOptionalNumber(
            root,
            "staleSessionTtlMinutes",
            AgentKick75Configuration.Default.StaleSessionTtl.TotalMinutes));
        bool startAtLogin = ReadOptionalBoolean(root, "startAtLogin", defaultValue: false);

        try
        {
            var lighting = new LightingSettings(thinking, requiresInput, complete, completeTtl);
            return new AgentKick75Configuration(
                lighting,
                staleSessionTtl,
                startAtLogin,
                schemaVersion);
        }
        catch (ConfigurationValidationException)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw InvalidDocument(exception.Message, exception);
        }
    }

    internal static string Serialize(AgentKick75Configuration configuration)
    {
        Validate(configuration);

        var root = new JsonObject
        {
            ["schemaVersion"] = configuration.SchemaVersion,
            ["states"] = new JsonObject
            {
                ["thinking"] = SerializeStyle(configuration.Lighting.Thinking),
                ["requiresInput"] = SerializeStyle(configuration.Lighting.RequiresInput),
                ["complete"] = SerializeStyle(configuration.Lighting.Complete),
            },
            ["completeTtlSeconds"] = configuration.Lighting.CompleteTtl.TotalSeconds,
            ["staleSessionTtlMinutes"] = configuration.StaleSessionTtl.TotalMinutes,
            ["startAtLogin"] = configuration.StartAtLogin,
        };

        return root.ToJsonString(SerializerOptions) + Environment.NewLine;
    }

    private static void Validate(AgentKick75Configuration configuration)
    {
        _ = new AgentKick75Configuration(
            configuration.Lighting,
            configuration.StaleSessionTtl,
            configuration.StartAtLogin,
            configuration.SchemaVersion);
    }

    private static int ReadSchemaVersion(JsonObject root)
    {
        JsonNode? value = root["schemaVersion"] ?? root["version"];
        if (value is null)
        {
            return AgentKick75Configuration.CurrentSchemaVersion;
        }

        return ReadInteger(value, "schemaVersion");
    }

    private static LightStyle ReadStyle(
        JsonObject states,
        string propertyName,
        LightStyle defaultValue)
    {
        JsonObject? style = ReadOptionalObject(states, propertyName);
        if (style is null)
        {
            return defaultValue;
        }

        string colorText = ReadOptionalString(style, "color", defaultValue.Color.ToString());
        int brightness = style["brightness"] is { } brightnessNode
            ? ReadInteger(brightnessNode, $"states.{propertyName}.brightness")
            : defaultValue.Brightness;

        if (!RgbColor.TryParse(colorText, out RgbColor color))
        {
            throw new ConfigurationValidationException(
                ConfigurationValidationError.InvalidColor,
                $"states.{propertyName}.color must use #RRGGBB format.");
        }

        try
        {
            return new LightStyle(color, brightness);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ConfigurationValidationException(
                ConfigurationValidationError.InvalidBrightness,
                $"states.{propertyName}.brightness must be between 0 and 100.",
                exception);
        }
    }

    private static JsonObject? ReadOptionalObject(JsonObject parent, string propertyName)
    {
        JsonNode? node = parent[propertyName];
        if (node is null)
        {
            return null;
        }

        return node as JsonObject
            ?? throw InvalidDocument($"{propertyName} must be a JSON object.");
    }

    private static string ReadOptionalString(
        JsonObject parent,
        string propertyName,
        string defaultValue)
    {
        JsonNode? node = parent[propertyName];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value && value.TryGetValue(out string? result) && result is not null)
        {
            return result;
        }

        throw InvalidDocument($"{propertyName} must be a string.");
    }

    private static bool ReadOptionalBoolean(
        JsonObject parent,
        string propertyName,
        bool defaultValue)
    {
        JsonNode? node = parent[propertyName];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value && value.TryGetValue(out bool result))
        {
            return result;
        }

        throw InvalidDocument($"{propertyName} must be a boolean.");
    }

    private static double ReadOptionalNumber(
        JsonObject parent,
        string propertyName,
        double defaultValue)
    {
        JsonNode? node = parent[propertyName];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value
            && value.TryGetValue(out double result)
            && double.IsFinite(result))
        {
            return result;
        }

        throw InvalidDocument($"{propertyName} must be a finite number.");
    }

    private static int ReadInteger(JsonNode node, string propertyName)
    {
        if (node is JsonValue value
            && value.TryGetValue(out int result))
        {
            return result;
        }

        throw InvalidDocument($"{propertyName} must be an integer.");
    }

    private static JsonObject SerializeStyle(LightStyle style)
    {
        return new JsonObject
        {
            ["color"] = style.Color.ToString(),
            ["brightness"] = style.Brightness,
        };
    }

    private static ConfigurationValidationException InvalidDocument(
        string message,
        Exception? innerException = null)
    {
        return new ConfigurationValidationException(
            ConfigurationValidationError.InvalidDocument,
            message,
            innerException);
    }

    private static bool IsInvalidConfiguration(Exception exception)
    {
        return exception is JsonException
            or ConfigurationValidationException
            or FormatException
            or OverflowException;
    }
}
