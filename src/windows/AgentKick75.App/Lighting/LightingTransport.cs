// SPDX-License-Identifier: MIT

namespace AgentKick75.App.Lighting;

public enum LightingDeviceSupport
{
    Writable,
    DiagnosticOnly,
}

public enum LightingDeviceObservationKind
{
    None,
    Descriptor,
    RuntimeSession,
}

/// <summary>
/// Non-identifying HID descriptor display metadata. The numeric version comes
/// from HIDD_ATTRIBUTES.VersionNumber (USB bcdDevice convention); it is not a
/// NuPhyIO firmware-version claim.
/// </summary>
public sealed record LightingDeviceDescriptorMetadata
{
    private const int MaximumTextLength = 128;

    public LightingDeviceDescriptorMetadata(
        string? product,
        string? manufacturer,
        ushort? hidDescriptorVersionNumber)
    {
        Product = NormalizeText(product);
        Manufacturer = NormalizeText(manufacturer);
        HidDescriptorVersionNumber = hidDescriptorVersionNumber;
    }

    public string? Product { get; }

    public string? Manufacturer { get; }

    public ushort? HidDescriptorVersionNumber { get; }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length > MaximumTextLength ||
            normalized.Any(char.IsControl) ||
            normalized.Contains("path=", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("serial=", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalized.Contains("#vid_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
    }
}

/// <summary>
/// Strictly matched descriptor metadata observed without establishing a
/// protocol session. Implementations must not issue HID reports from this path.
/// </summary>
public sealed record LightingDeviceInspection(
    string DeviceIdentity,
    string TransportProfile,
    string InterfaceFingerprint,
    LightingDeviceSupport Support,
    LightingDeviceDescriptorMetadata? DescriptorMetadata = null);

public sealed record LightingDeviceSession(
    string DeviceIdentity,
    string TransportProfile,
    string InterfaceFingerprint,
    byte CurrentMode,
    LightingDeviceDescriptorMetadata? DescriptorMetadata = null);

public sealed record LightingConnectionRequest(string? RequiredTransportProfileId)
{
    public static LightingConnectionRequest Auto { get; } = new((string?)null);

    public static LightingConnectionRequest ForOwnedBaseline(string transportProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportProfileId);
        return new LightingConnectionRequest(transportProfileId);
    }
}

public enum LightingTransportFailureKind
{
    DeviceDisconnected,
    DeviceBusy,
    ReceiverUnavailable,
    KeyboardSleeping,
    Timeout,
    ProtocolViolation,
    BaselineMismatch,
}

public sealed class LightingTransportException : Exception
{
    public LightingTransportException(
        LightingTransportFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public LightingTransportFailureKind Kind { get; }
}

public interface ILightingTransport : IAsyncDisposable
{
    /// <summary>
    /// Inspects descriptor metadata only. This method must not create a protocol
    /// connection or issue read/write reports. A null result means that no
    /// strictly allowlisted descriptor was observed.
    /// </summary>
    ValueTask<LightingDeviceInspection?> InspectAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<LightingDeviceInspection?>(null);

    ValueTask<LightingDeviceSession> ConnectAsync(
        LightingConnectionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReadSideLightAsync(CancellationToken cancellationToken = default);

    ValueTask WriteSideLightAsync(
        ReadOnlyMemory<byte> sideLightState,
        CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed record BaselineOwnershipRecord(
    int Version,
    string DeviceIdentity,
    string TransportProfile,
    string InterfaceFingerprint,
    byte[] SideLightState,
    byte CurrentMode,
    string OwnershipMarker,
    bool IsOwned,
    DateTimeOffset CapturedAtUtc);

public interface IBaselineOwnershipStore
{
    ValueTask<BaselineOwnershipRecord?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        BaselineOwnershipRecord record,
        CancellationToken cancellationToken = default);

    ValueTask MarkReleasedAsync(
        string ownershipMarker,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryBaselineOwnershipStore :
    IBaselineOwnershipStore,
    IBaselineMismatchDispositionStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private BaselineOwnershipRecord? record;

    public async ValueTask<BaselineOwnershipRecord?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Clone(record);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        BaselineOwnershipRecord newRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newRecord);
        ValidateSideLight(newRecord.SideLightState);
        ValidateCurrentMode(newRecord.CurrentMode);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            record = Clone(newRecord);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask MarkReleasedAsync(
        string ownershipMarker,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipMarker);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (record?.OwnershipMarker == ownershipMarker)
            {
                record = record with { IsOwned = false };
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<AgentKick75.Core.Baseline.BaselineMismatchDispositionResult>
        AbandonOwnedDeviceMismatchAsync(
            string expectedOwnershipMarker,
            string expectedBaselineDeviceIdentity,
            string observedDeviceIdentity,
            DateTimeOffset releasedAtUtc,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnershipMarker);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBaselineDeviceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedDeviceIdentity);
        _ = releasedAtUtc;

        if (string.Equals(
                expectedBaselineDeviceIdentity,
                observedDeviceIdentity,
                StringComparison.Ordinal))
        {
            return new(AgentKick75.Core.Baseline.BaselineMismatchDispositionStatus.NoDeviceIdentityMismatch);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (record is null)
            {
                return new(AgentKick75.Core.Baseline.BaselineMismatchDispositionStatus.Missing);
            }

            if (!record.IsOwned)
            {
                return new(AgentKick75.Core.Baseline.BaselineMismatchDispositionStatus.NotOwned);
            }

            if (!string.Equals(record.OwnershipMarker, expectedOwnershipMarker, StringComparison.Ordinal)
                || !string.Equals(record.DeviceIdentity, expectedBaselineDeviceIdentity, StringComparison.Ordinal))
            {
                return new(AgentKick75.Core.Baseline.BaselineMismatchDispositionStatus.StaleOwnership);
            }

            record = record with { IsOwned = false };
            return new(AgentKick75.Core.Baseline.BaselineMismatchDispositionStatus.Released);
        }
        finally
        {
            gate.Release();
        }
    }

    private static BaselineOwnershipRecord? Clone(BaselineOwnershipRecord? value)
    {
        return value is null
            ? null
            : value with { SideLightState = value.SideLightState.ToArray() };
    }

    internal static void ValidateSideLight(ReadOnlySpan<byte> state)
    {
        if (state.Length != HidLightingWorker.SideLightStateLength)
        {
            throw new ArgumentException(
                $"Side-light state must be exactly {HidLightingWorker.SideLightStateLength} bytes.",
                nameof(state));
        }
    }

    internal static void ValidateCurrentMode(byte currentMode)
    {
        if (currentMode > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentMode),
                "The current mode must be either 0 or 1.");
        }
    }
}

public interface IReconnectDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemReconnectDelay : IReconnectDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return new(Task.Delay(delay, cancellationToken));
    }
}

public sealed class LayeredReconnectPolicy
{
    public TimeSpan GetDelay(LightingTransportFailureKind failureKind, int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        return failureKind switch
        {
            LightingTransportFailureKind.DeviceBusy => Exponential(attempt, 2_000, 5_000),
            LightingTransportFailureKind.KeyboardSleeping => Exponential(attempt, 2_000, 5_000),
            LightingTransportFailureKind.ReceiverUnavailable => Exponential(attempt, 1_000, 5_000),
            LightingTransportFailureKind.DeviceDisconnected => Exponential(attempt, 250, 5_000),
            LightingTransportFailureKind.Timeout => Exponential(attempt, 500, 5_000),
            _ => Timeout.InfiniteTimeSpan,
        };
    }

    public bool IsTransient(LightingTransportFailureKind failureKind)
    {
        return failureKind is
            LightingTransportFailureKind.DeviceDisconnected or
            LightingTransportFailureKind.DeviceBusy or
            LightingTransportFailureKind.ReceiverUnavailable or
            LightingTransportFailureKind.KeyboardSleeping or
            LightingTransportFailureKind.Timeout;
    }

    private static TimeSpan Exponential(int attempt, int initialMilliseconds, int maximumMilliseconds)
    {
        int shift = Math.Min(attempt - 1, 10);
        long milliseconds = Math.Min((long)initialMilliseconds << shift, maximumMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
