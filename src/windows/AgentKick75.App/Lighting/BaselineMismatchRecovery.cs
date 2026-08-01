// SPDX-License-Identifier: MIT
using AgentKick75.Core.Baseline;

namespace AgentKick75.App.Lighting;

public enum BaselineMismatchRecoveryStatus
{
    Released,
    NotConfirmed,
    NoPendingMismatch,
    StaleConfirmation,
    CurrentDeviceChanged,
    UnsafeJournal,
}

public sealed record BaselineMismatchRecoveryResult(
    BaselineMismatchRecoveryStatus Status,
    string Message)
{
    public bool Succeeded => Status == BaselineMismatchRecoveryStatus.Released;
}

public sealed record BaselineIdentityMismatchNotice(
    string ConfirmationId,
    string BaselineDeviceIdentity,
    string ObservedDeviceIdentity,
    string TransportProfile,
    string InterfaceFingerprint,
    DateTimeOffset ObservedAtUtc);

public interface IBaselineMismatchDispositionStore
{
    ValueTask<BaselineMismatchDispositionResult> AbandonOwnedDeviceMismatchAsync(
        string expectedOwnershipMarker,
        string expectedBaselineDeviceIdentity,
        string observedDeviceIdentity,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken = default);
}

internal sealed class RefusingBaselineMismatchDispositionStore : IBaselineMismatchDispositionStore
{
    public static RefusingBaselineMismatchDispositionStore Instance { get; } = new();

    public ValueTask<BaselineMismatchDispositionResult> AbandonOwnedDeviceMismatchAsync(
        string expectedOwnershipMarker,
        string expectedBaselineDeviceIdentity,
        string observedDeviceIdentity,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new BaselineMismatchDispositionResult(
                BaselineMismatchDispositionStatus.InvalidBaseline));
    }
}

/// <summary>
/// Owns the memory-only confirmation challenge for one automatically refused
/// device-identity mismatch. Disposition only releases the journal marker; it
/// has no HID dependency and cannot write the saved bytes to any device.
/// </summary>
public sealed class BaselineMismatchRecoveryService
{
    private readonly IBaselineMismatchDispositionStore store;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private PendingMismatch? pending;

    public BaselineMismatchRecoveryService(
        IBaselineMismatchDispositionStore store,
        TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BaselineIdentityMismatchNotice? Current => Volatile.Read(ref pending)?.Notice;

    public async ValueTask ReportAsync(
        BaselineOwnershipRecord baseline,
        LightingDeviceSession observedDevice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(observedDevice);
        if (!baseline.IsOwned
            || string.Equals(
                baseline.DeviceIdentity,
                observedDevice.DeviceIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                baseline.TransportProfile,
                observedDevice.TransportProfile,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A recovery challenge requires an owned baseline and a different observed identity on the same transport profile.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingMismatch? current = pending;
            if (current is not null
                && string.Equals(current.OwnershipMarker, baseline.OwnershipMarker, StringComparison.Ordinal)
                && string.Equals(current.Notice.BaselineDeviceIdentity, baseline.DeviceIdentity, StringComparison.Ordinal)
                && string.Equals(current.Notice.ObservedDeviceIdentity, observedDevice.DeviceIdentity, StringComparison.Ordinal)
                && string.Equals(current.Notice.TransportProfile, observedDevice.TransportProfile, StringComparison.Ordinal)
                && string.Equals(current.Notice.InterfaceFingerprint, observedDevice.InterfaceFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var notice = new BaselineIdentityMismatchNotice(
                Guid.NewGuid().ToString("N"),
                baseline.DeviceIdentity,
                observedDevice.DeviceIdentity,
                observedDevice.TransportProfile,
                observedDevice.InterfaceFingerprint,
                timeProvider.GetUtcNow());
            Volatile.Write(ref pending, new PendingMismatch(baseline.OwnershipMarker, notice));
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref pending, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<BaselineMismatchRecoveryResult> AbandonAsync(
        string confirmationId,
        bool confirmed,
        LightingDeviceInspection? currentDevice,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return Result(
                BaselineMismatchRecoveryStatus.NotConfirmed,
                "Explicit confirmation is required before abandoning baseline ownership.");
        }

        if (!Guid.TryParseExact(confirmationId, "N", out _))
        {
            return Result(
                BaselineMismatchRecoveryStatus.StaleConfirmation,
                "The baseline recovery confirmation is invalid or stale.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingMismatch? current = pending;
            if (current is null)
            {
                return Result(
                    BaselineMismatchRecoveryStatus.NoPendingMismatch,
                    "There is no currently observed baseline identity mismatch to abandon.");
            }

            if (!string.Equals(current.Notice.ConfirmationId, confirmationId, StringComparison.Ordinal))
            {
                return Result(
                    BaselineMismatchRecoveryStatus.StaleConfirmation,
                    "The baseline recovery confirmation is invalid or stale.");
            }

            if (currentDevice is not
                {
                    Support: LightingDeviceSupport.Writable,
                }
                || !string.Equals(
                    currentDevice.DeviceIdentity,
                    current.Notice.ObservedDeviceIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    currentDevice.TransportProfile,
                    current.Notice.TransportProfile,
                    StringComparison.Ordinal)
                || !string.Equals(
                    currentDevice.InterfaceFingerprint,
                    current.Notice.InterfaceFingerprint,
                    StringComparison.Ordinal))
            {
                Volatile.Write(ref pending, null);
                return Result(
                    BaselineMismatchRecoveryStatus.CurrentDeviceChanged,
                    "The currently observed device changed; automatic recovery must refuse it again before a new confirmation is available.");
            }

            BaselineMismatchDispositionResult disposition = await store
                .AbandonOwnedDeviceMismatchAsync(
                    current.OwnershipMarker,
                    current.Notice.BaselineDeviceIdentity,
                    current.Notice.ObservedDeviceIdentity,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref pending, null);

            return disposition.Status switch
            {
                BaselineMismatchDispositionStatus.Released => Result(
                    BaselineMismatchRecoveryStatus.Released,
                    "The old baseline ownership was abandoned without writing baseline bytes. Lighting control is paused until explicitly resumed."),
                BaselineMismatchDispositionStatus.Missing or
                BaselineMismatchDispositionStatus.NotOwned or
                BaselineMismatchDispositionStatus.StaleOwnership => Result(
                    BaselineMismatchRecoveryStatus.StaleConfirmation,
                    "The baseline journal changed; no ownership was abandoned."),
                _ => Result(
                    BaselineMismatchRecoveryStatus.UnsafeJournal,
                    "The baseline journal is not eligible for device-mismatch disposition."),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private static BaselineMismatchRecoveryResult Result(
        BaselineMismatchRecoveryStatus status,
        string message) => new(status, message);

    private sealed record PendingMismatch(
        string OwnershipMarker,
        BaselineIdentityMismatchNotice Notice);
}
