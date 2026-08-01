// SPDX-License-Identifier: MIT
using AgentKick75.Core.Baseline;
using AgentKick75.Hid.Windows;

namespace AgentKick75.App.Lighting;

/// <summary>
/// Uses the same durable baseline journal as the Host worker so an interrupted
/// hardware test can be recovered on the next Host start.
/// </summary>
public sealed class CoreHardwareTestBaselineJournal : IHardwareTestBaselineJournal
{
    private readonly BaselineStore store;
    private readonly TimeProvider timeProvider;

    public CoreHardwareTestBaselineJournal(
        BaselineStore store,
        TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<HardwareTestBaselineLease> AcquireAsync(
        HidInterfaceDescriptor device,
        HidTransportProfile profile,
        ReadOnlyMemory<byte> baseline,
        byte currentMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);
        if (!HidTransportProfiles.IsWritableAllowlisted(profile) ||
            !profile.MatchesWritableDescriptor(device))
        {
            throw new InvalidOperationException(
                "A hardware-test baseline can only be journaled for one descriptor-validated write profile.");
        }

        InMemoryBaselineOwnershipStore.ValidateSideLight(baseline.Span);
        InMemoryBaselineOwnershipStore.ValidateCurrentMode(currentMode);
        BaselineLoadResult existing = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Status is BaselineLoadStatus.Corrupt or BaselineLoadStatus.UnsupportedVersion)
        {
            throw new InvalidDataException(
                existing.ErrorMessage ?? "The existing baseline journal is unsafe.");
        }

        if (existing.Baseline?.Ownership.IsOwned == true)
        {
            throw new InvalidOperationException(
                "An unreleased baseline already exists; restore it before starting another hardware test.");
        }

        var identity = new BaselineDeviceIdentity(
            device.DeviceIdentity,
            profile.Id,
            device.InterfaceFingerprint);
        BaselineRecord record = BaselineRecord.Acquire(
            identity,
            baseline.ToArray(),
            currentMode,
            timeProvider.GetUtcNow());
        await store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return new HardwareTestBaselineLease(record.Ownership.Marker, record.CurrentMode);
    }

    public async ValueTask ReleaseAsync(
        HardwareTestBaselineLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        BaselineLoadResult current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status != BaselineLoadStatus.Loaded || current.Baseline is null)
        {
            throw new InvalidDataException("The hardware-test baseline disappeared before release.");
        }

        if (!current.Baseline.Ownership.IsOwned ||
            !string.Equals(
                current.Baseline.Ownership.Marker,
                lease.OwnershipMarker,
                StringComparison.Ordinal) ||
            current.Baseline.CurrentMode != lease.CurrentMode)
        {
            throw new InvalidDataException(
                "The hardware-test baseline ownership marker or current mode changed before release.");
        }

        await store.SaveAsync(
            current.Baseline.Release(timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }
}
