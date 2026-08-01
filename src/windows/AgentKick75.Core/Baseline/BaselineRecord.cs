namespace AgentKick75.Core.Baseline;

public sealed record BaselineDeviceIdentity
{
    public BaselineDeviceIdentity(
        string deviceIdentity,
        string transportProfileId,
        string interfaceFingerprint)
    {
        DeviceIdentity = ValidateValue(deviceIdentity, nameof(deviceIdentity));
        TransportProfileId = ValidateValue(transportProfileId, nameof(transportProfileId));
        InterfaceFingerprint = ValidateValue(interfaceFingerprint, nameof(interfaceFingerprint));
    }

    public string DeviceIdentity { get; }

    public string TransportProfileId { get; }

    public string InterfaceFingerprint { get; }

    private static string ValidateValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 512 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Baseline identity fields must be at most 512 printable characters.",
                parameterName);
        }

        return value;
    }
}

public sealed record BaselineOwnership
{
    public const string MarkerPrefix = "agent-kick75:";

    public BaselineOwnership(
        string marker,
        bool isOwned,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset? releasedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        if (!marker.StartsWith(MarkerPrefix, StringComparison.Ordinal)
            || marker.Length > 128
            || marker.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid AgentKick75 ownership marker.", nameof(marker));
        }

        if (isOwned && releasedAtUtc is not null)
        {
            throw new ArgumentException(
                "An owned baseline cannot have a release timestamp.",
                nameof(releasedAtUtc));
        }

        if (!isOwned && releasedAtUtc is null)
        {
            throw new ArgumentException(
                "A released baseline must have a release timestamp.",
                nameof(releasedAtUtc));
        }

        if (releasedAtUtc is { } released && released < acquiredAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(releasedAtUtc),
                "The release timestamp cannot precede acquisition.");
        }

        Marker = marker;
        IsOwned = isOwned;
        AcquiredAtUtc = acquiredAtUtc;
        ReleasedAtUtc = releasedAtUtc;
    }

    public string Marker { get; }

    public bool IsOwned { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public DateTimeOffset? ReleasedAtUtc { get; }

    public BaselineOwnership Release(DateTimeOffset releasedAtUtc)
    {
        if (!IsOwned)
        {
            return this;
        }

        return new BaselineOwnership(Marker, false, AcquiredAtUtc, releasedAtUtc);
    }
}

public sealed record BaselineRecord
{
    public const int CurrentSchemaVersion = 2;
    public const int SideLightByteCount = 8;

    public BaselineRecord(
        BaselineDeviceIdentity device,
        IEnumerable<byte> originalSideLightBytes,
        byte currentMode,
        BaselineOwnership ownership,
        int schemaVersion = CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(originalSideLightBytes);
        ArgumentNullException.ThrowIfNull(ownership);

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new BaselineValidationException(
                BaselineValidationError.UnsupportedSchemaVersion,
                $"Unsupported baseline schema version {schemaVersion}.");
        }

        byte[] bytes = originalSideLightBytes.ToArray();
        if (bytes.Length != SideLightByteCount)
        {
            throw new BaselineValidationException(
                BaselineValidationError.InvalidSideLightBytes,
                $"A baseline must contain exactly {SideLightByteCount} side-light bytes.");
        }

        if (currentMode > 1)
        {
            throw new BaselineValidationException(
                BaselineValidationError.InvalidCurrentMode,
                "A baseline current mode must be either 0 or 1.");
        }

        SchemaVersion = schemaVersion;
        Device = device;
        OriginalSideLightBytes = Array.AsReadOnly(bytes);
        CurrentMode = currentMode;
        Ownership = ownership;
    }

    public int SchemaVersion { get; }

    public BaselineDeviceIdentity Device { get; }

    public IReadOnlyList<byte> OriginalSideLightBytes { get; }

    public byte CurrentMode { get; }

    public BaselineOwnership Ownership { get; }

    public static BaselineRecord Acquire(
        BaselineDeviceIdentity device,
        IEnumerable<byte> originalSideLightBytes,
        byte currentMode,
        DateTimeOffset acquiredAtUtc,
        Guid? ownershipId = null)
    {
        Guid id = ownershipId ?? Guid.NewGuid();
        var ownership = new BaselineOwnership(
            BaselineOwnership.MarkerPrefix + id.ToString("N"),
            true,
            acquiredAtUtc);
        return new BaselineRecord(device, originalSideLightBytes, currentMode, ownership);
    }

    public BaselineRecord Release(DateTimeOffset releasedAtUtc)
    {
        return new BaselineRecord(
            Device,
            OriginalSideLightBytes,
            CurrentMode,
            Ownership.Release(releasedAtUtc),
            SchemaVersion);
    }
}

public enum BaselineValidationError
{
    UnsupportedSchemaVersion,
    InvalidDocument,
    InvalidIdentity,
    InvalidOwnership,
    InvalidSideLightBytes,
    InvalidCurrentMode,
}

public sealed class BaselineValidationException : Exception
{
    public BaselineValidationException(
        BaselineValidationError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public BaselineValidationError Error { get; }
}
