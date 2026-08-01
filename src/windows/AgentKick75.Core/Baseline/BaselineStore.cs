using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKick75.Core.Storage;

namespace AgentKick75.Core.Baseline;

public enum BaselineLoadStatus
{
    Loaded,
    Missing,
    Corrupt,
    UnsupportedVersion,
}

public sealed record BaselineLoadResult(
    BaselineLoadStatus Status,
    BaselineRecord? Baseline = null,
    string? ErrorMessage = null);

public enum BaselineMismatchDispositionStatus
{
    Released,
    Missing,
    InvalidBaseline,
    NotOwned,
    StaleOwnership,
    NoDeviceIdentityMismatch,
}

public sealed record BaselineMismatchDispositionResult(
    BaselineMismatchDispositionStatus Status)
{
    public bool Succeeded => Status == BaselineMismatchDispositionStatus.Released;
}

public sealed class BaselineStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    public BaselineStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public async Task<BaselineLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return new BaselineLoadResult(BaselineLoadStatus.Missing);
            }

            try
            {
                string json = await File.ReadAllTextAsync(Path, cancellationToken)
                    .ConfigureAwait(false);
                return new BaselineLoadResult(BaselineLoadStatus.Loaded, Parse(json));
            }
            catch (BaselineValidationException exception)
                when (exception.Error == BaselineValidationError.UnsupportedSchemaVersion)
            {
                return new BaselineLoadResult(
                    BaselineLoadStatus.UnsupportedVersion,
                    ErrorMessage: exception.Message);
            }
            catch (Exception exception) when (IsCorruptBaseline(exception))
            {
                return new BaselineLoadResult(
                    BaselineLoadStatus.Corrupt,
                    ErrorMessage: exception.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        BaselineRecord baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string json = Serialize(baseline);
            await AtomicFile.WriteUtf8Async(Path, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(Path);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Releases an owned journal only when it is still the exact ownership
    /// record that produced a confirmed device-identity mismatch. This method
    /// never opens a device and never writes baseline bytes to hardware.
    /// </summary>
    public async Task<BaselineMismatchDispositionResult> AbandonOwnedDeviceMismatchAsync(
        string expectedOwnershipMarker,
        string expectedBaselineDeviceIdentity,
        string observedDeviceIdentity,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnershipMarker);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBaselineDeviceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedDeviceIdentity);

        // A caller must prove an actual identity mismatch before this narrowly
        // scoped disposition can release ownership.
        if (string.Equals(
                expectedBaselineDeviceIdentity,
                observedDeviceIdentity,
                StringComparison.Ordinal))
        {
            return new(BaselineMismatchDispositionStatus.NoDeviceIdentityMismatch);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return new(BaselineMismatchDispositionStatus.Missing);
            }

            BaselineRecord baseline;
            try
            {
                string json = await File.ReadAllTextAsync(Path, cancellationToken)
                    .ConfigureAwait(false);
                baseline = Parse(json);
            }
            catch (Exception exception) when (IsCorruptBaseline(exception))
            {
                return new(BaselineMismatchDispositionStatus.InvalidBaseline);
            }

            if (!baseline.Ownership.IsOwned)
            {
                return new(BaselineMismatchDispositionStatus.NotOwned);
            }

            if (!string.Equals(
                    baseline.Ownership.Marker,
                    expectedOwnershipMarker,
                    StringComparison.Ordinal)
                || !string.Equals(
                    baseline.Device.DeviceIdentity,
                    expectedBaselineDeviceIdentity,
                    StringComparison.Ordinal))
            {
                return new(BaselineMismatchDispositionStatus.StaleOwnership);
            }

            string releasedJson = Serialize(baseline.Release(releasedAtUtc));
            await AtomicFile.WriteUtf8Async(Path, releasedJson, cancellationToken).ConfigureAwait(false);
            return new(BaselineMismatchDispositionStatus.Released);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static BaselineRecord Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw InvalidDocument("Baseline root must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw InvalidDocument("Baseline is not valid JSON.", exception);
        }

        int schemaVersion = ReadRequiredInteger(root, "schemaVersion");
        if (schemaVersion is not 1 && schemaVersion != BaselineRecord.CurrentSchemaVersion)
        {
            throw new BaselineValidationException(
                BaselineValidationError.UnsupportedSchemaVersion,
                $"Unsupported baseline schema version {schemaVersion}.");
        }

        try
        {
            JsonObject device = ReadRequiredObject(root, "device");
            var identity = new BaselineDeviceIdentity(
                ReadRequiredString(device, "identity"),
                ReadRequiredString(device, "transportProfile"),
                ReadRequiredString(device, "interfaceFingerprint"));

            byte[] bytes = ReadSideLightBytes(root);
            JsonObject ownershipNode = ReadRequiredObject(root, "ownership");
            var ownership = new BaselineOwnership(
                ReadRequiredString(ownershipNode, "marker"),
                ReadRequiredBoolean(ownershipNode, "isOwned"),
                ReadRequiredTimestamp(ownershipNode, "acquiredAtUtc"),
                ReadOptionalTimestamp(ownershipNode, "releasedAtUtc"));

            if (schemaVersion == 1)
            {
                if (ownership.IsOwned)
                {
                    throw new BaselineValidationException(
                        BaselineValidationError.UnsupportedSchemaVersion,
                        "An owned schema version 1 baseline has no currentMode and cannot be restored safely.");
                }

                // A released v1 marker can never authorize a device write. It is
                // therefore safe to normalize it in memory; the next acquisition
                // atomically overwrites it with a freshly observed v2 mode.
                return new BaselineRecord(
                    identity,
                    bytes,
                    currentMode: 0,
                    ownership,
                    BaselineRecord.CurrentSchemaVersion);
            }

            int currentMode = ReadRequiredInteger(root, "currentMode");
            if (currentMode is < 0 or > 1)
            {
                throw new BaselineValidationException(
                    BaselineValidationError.InvalidCurrentMode,
                    "currentMode must be either 0 or 1.");
            }

            return new BaselineRecord(identity, bytes, (byte)currentMode, ownership, schemaVersion);
        }
        catch (BaselineValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw InvalidDocument(exception.Message, exception);
        }
    }

    internal static string Serialize(BaselineRecord baseline)
    {
        _ = new BaselineRecord(
            baseline.Device,
            baseline.OriginalSideLightBytes,
            baseline.CurrentMode,
            baseline.Ownership,
            baseline.SchemaVersion);

        var bytes = new JsonArray();
        foreach (byte value in baseline.OriginalSideLightBytes)
        {
            bytes.Add(value);
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = baseline.SchemaVersion,
            ["device"] = new JsonObject
            {
                ["identity"] = baseline.Device.DeviceIdentity,
                ["transportProfile"] = baseline.Device.TransportProfileId,
                ["interfaceFingerprint"] = baseline.Device.InterfaceFingerprint,
            },
            ["originalSideLightBytes"] = bytes,
            ["currentMode"] = baseline.CurrentMode,
            ["ownership"] = new JsonObject
            {
                ["marker"] = baseline.Ownership.Marker,
                ["isOwned"] = baseline.Ownership.IsOwned,
                ["acquiredAtUtc"] = FormatTimestamp(baseline.Ownership.AcquiredAtUtc),
                ["releasedAtUtc"] = baseline.Ownership.ReleasedAtUtc is { } releasedAtUtc
                    ? FormatTimestamp(releasedAtUtc)
                    : null,
            },
        };

        return root.ToJsonString(SerializerOptions) + Environment.NewLine;
    }

    private static byte[] ReadSideLightBytes(JsonObject root)
    {
        JsonArray array = root["originalSideLightBytes"] as JsonArray
            ?? throw InvalidDocument("originalSideLightBytes must be an array.");
        if (array.Count != BaselineRecord.SideLightByteCount)
        {
            throw new BaselineValidationException(
                BaselineValidationError.InvalidSideLightBytes,
                $"A baseline must contain exactly {BaselineRecord.SideLightByteCount} side-light bytes.");
        }

        var result = new byte[array.Count];
        for (int index = 0; index < array.Count; index++)
        {
            int value = array[index] is { } node
                ? ReadInteger(node, $"originalSideLightBytes[{index}]")
                : -1;
            if (value is < byte.MinValue or > byte.MaxValue)
            {
                throw new BaselineValidationException(
                    BaselineValidationError.InvalidSideLightBytes,
                    $"originalSideLightBytes[{index}] must be between 0 and 255.");
            }

            result[index] = (byte)value;
        }

        return result;
    }

    private static JsonObject ReadRequiredObject(JsonObject parent, string propertyName)
    {
        return parent[propertyName] as JsonObject
            ?? throw InvalidDocument($"{propertyName} must be a JSON object.");
    }

    private static string ReadRequiredString(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonValue value
            && value.TryGetValue(out string? result)
            && result is not null)
        {
            return result;
        }

        throw InvalidDocument($"{propertyName} must be a string.");
    }

    private static bool ReadRequiredBoolean(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonValue value && value.TryGetValue(out bool result))
        {
            return result;
        }

        throw InvalidDocument($"{propertyName} must be a boolean.");
    }

    private static int ReadRequiredInteger(JsonObject parent, string propertyName)
    {
        JsonNode node = parent[propertyName]
            ?? throw InvalidDocument($"{propertyName} is required.");
        return ReadInteger(node, propertyName);
    }

    private static int ReadInteger(JsonNode node, string propertyName)
    {
        if (node is JsonValue value && value.TryGetValue(out int result))
        {
            return result;
        }

        throw InvalidDocument($"{propertyName} must be an integer.");
    }

    private static DateTimeOffset ReadRequiredTimestamp(JsonObject parent, string propertyName)
    {
        string text = ReadRequiredString(parent, propertyName);
        return ParseTimestamp(text, propertyName);
    }

    private static DateTimeOffset? ReadOptionalTimestamp(JsonObject parent, string propertyName)
    {
        JsonNode? node = parent[propertyName];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value
            && value.TryGetValue(out string? text)
            && text is not null)
        {
            return ParseTimestamp(text, propertyName);
        }

        throw InvalidDocument($"{propertyName} must be a timestamp or null.");
    }

    private static DateTimeOffset ParseTimestamp(string text, string propertyName)
    {
        if (DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            return timestamp;
        }

        throw InvalidDocument($"{propertyName} must use the round-trip timestamp format.");
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static BaselineValidationException InvalidDocument(
        string message,
        Exception? innerException = null)
    {
        return new BaselineValidationException(
            BaselineValidationError.InvalidDocument,
            message,
            innerException);
    }

    private static bool IsCorruptBaseline(Exception exception)
    {
        return exception is JsonException
            or BaselineValidationException
            or FormatException
            or OverflowException;
    }
}
