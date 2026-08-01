// SPDX-License-Identifier: MIT
using AgentKick75.Core.Baseline;

namespace AgentKick75.App.Lighting;

public sealed class FileBaselineOwnershipStore :
    IBaselineOwnershipStore,
    IBaselineMismatchDispositionStore
{
    private readonly BaselineStore store;
    private readonly TimeProvider timeProvider;

    public FileBaselineOwnershipStore(BaselineStore store, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<BaselineOwnershipRecord?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        BaselineLoadResult result = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            BaselineLoadStatus.Missing => null,
            BaselineLoadStatus.Loaded when result.Baseline is not null => FromCore(result.Baseline),
            BaselineLoadStatus.Corrupt => throw UnsafeBaseline(result.ErrorMessage ?? "Baseline is corrupt."),
            BaselineLoadStatus.UnsupportedVersion => throw UnsafeBaseline(
                result.ErrorMessage ?? "Baseline schema is unsupported."),
            _ => throw UnsafeBaseline("Baseline store returned an inconsistent result."),
        };
    }

    public async ValueTask SaveAsync(
        BaselineOwnershipRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsOwned)
        {
            throw new ArgumentException("A newly saved takeover baseline must be owned.", nameof(record));
        }

        var identity = new BaselineDeviceIdentity(
            record.DeviceIdentity,
            record.TransportProfile,
            record.InterfaceFingerprint);
        var ownership = new BaselineOwnership(
            record.OwnershipMarker,
            isOwned: true,
            record.CapturedAtUtc);
        await store.SaveAsync(
            new BaselineRecord(
                identity,
                record.SideLightState,
                record.CurrentMode,
                ownership,
                record.Version),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MarkReleasedAsync(
        string ownershipMarker,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipMarker);
        BaselineLoadResult result = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status != BaselineLoadStatus.Loaded || result.Baseline is null)
        {
            throw UnsafeBaseline("The owned baseline disappeared before release.");
        }

        if (!string.Equals(
                result.Baseline.Ownership.Marker,
                ownershipMarker,
                StringComparison.Ordinal))
        {
            throw UnsafeBaseline("The baseline ownership marker changed before release.");
        }

        await store.SaveAsync(
            result.Baseline.Release(timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BaselineMismatchDispositionResult> AbandonOwnedDeviceMismatchAsync(
        string expectedOwnershipMarker,
        string expectedBaselineDeviceIdentity,
        string observedDeviceIdentity,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return await store.AbandonOwnedDeviceMismatchAsync(
            expectedOwnershipMarker,
            expectedBaselineDeviceIdentity,
            observedDeviceIdentity,
            releasedAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private static BaselineOwnershipRecord FromCore(BaselineRecord record)
    {
        return new BaselineOwnershipRecord(
            record.SchemaVersion,
            record.Device.DeviceIdentity,
            record.Device.TransportProfileId,
            record.Device.InterfaceFingerprint,
            record.OriginalSideLightBytes.ToArray(),
            record.CurrentMode,
            record.Ownership.Marker,
            record.Ownership.IsOwned,
            record.Ownership.AcquiredAtUtc);
    }

    private static LightingTransportException UnsafeBaseline(string message)
    {
        return new LightingTransportException(
            LightingTransportFailureKind.BaselineMismatch,
            message);
    }
}
