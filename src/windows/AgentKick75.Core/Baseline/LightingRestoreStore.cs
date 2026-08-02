// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKick75.Core.Storage;

namespace AgentKick75.Core.Baseline;

public sealed record LightingRestoreRecord
{
    public const int CurrentSchemaVersion = 1;
    public const int SideLightByteCount = 8;

    public LightingRestoreRecord(
        string deviceIdentity,
        string interfaceFingerprint,
        byte currentMode,
        IEnumerable<byte> originalSideLightBytes,
        int schemaVersion = CurrentSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceFingerprint);
        ArgumentNullException.ThrowIfNull(originalSideLightBytes);

        byte[] bytes = originalSideLightBytes.ToArray();
        if (schemaVersion != CurrentSchemaVersion || bytes.Length != SideLightByteCount || currentMode > 1)
        {
            throw new BaselineValidationException(
                schemaVersion != CurrentSchemaVersion
                    ? BaselineValidationError.UnsupportedSchemaVersion
                    : currentMode > 1
                        ? BaselineValidationError.InvalidCurrentMode
                        : BaselineValidationError.InvalidSideLightBytes,
                "The lighting restore record is invalid.");
        }

        SchemaVersion = schemaVersion;
        DeviceIdentity = deviceIdentity;
        InterfaceFingerprint = interfaceFingerprint;
        CurrentMode = currentMode;
        OriginalSideLightBytes = Array.AsReadOnly(bytes);
    }

    public int SchemaVersion { get; }
    public string DeviceIdentity { get; }
    public string InterfaceFingerprint { get; }
    public byte CurrentMode { get; }
    public IReadOnlyList<byte> OriginalSideLightBytes { get; }
}

public sealed class LightingRestoreStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);

    public LightingRestoreStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public async Task<LightingRestoreRecord?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            return Parse(json);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(LightingRestoreRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = new JsonArray(record.OriginalSideLightBytes.Select(value => (JsonNode?)value).ToArray());
            var root = new JsonObject
            {
                ["schemaVersion"] = record.SchemaVersion,
                ["deviceIdentity"] = record.DeviceIdentity,
                ["interfaceFingerprint"] = record.InterfaceFingerprint,
                ["currentMode"] = record.CurrentMode,
                ["originalSideLightBytes"] = bytes,
            };
            await AtomicFile.WriteUtf8Async(
                Path,
                root.ToJsonString(SerializerOptions) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
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

    private static LightingRestoreRecord Parse(string json)
    {
        try
        {
            JsonObject root = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("Restore record root must be an object.");
            int schemaVersion = root["schemaVersion"]?.GetValue<int>()
                ?? throw new JsonException("schemaVersion is required.");
            string deviceIdentity = root["deviceIdentity"]?.GetValue<string>()
                ?? throw new JsonException("deviceIdentity is required.");
            string interfaceFingerprint = root["interfaceFingerprint"]?.GetValue<string>()
                ?? throw new JsonException("interfaceFingerprint is required.");
            int mode = root["currentMode"]?.GetValue<int>()
                ?? throw new JsonException("currentMode is required.");
            JsonArray bytesNode = root["originalSideLightBytes"] as JsonArray
                ?? throw new JsonException("originalSideLightBytes is required.");
            byte[] bytes = bytesNode.Select(node => checked((byte)(node?.GetValue<int>() ?? -1))).ToArray();
            return new LightingRestoreRecord(
                deviceIdentity,
                interfaceFingerprint,
                checked((byte)mode),
                bytes,
                schemaVersion);
        }
        catch (BaselineValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new BaselineValidationException(
                BaselineValidationError.InvalidDocument,
                "The lighting restore record is corrupt.",
                exception);
        }
    }
}
