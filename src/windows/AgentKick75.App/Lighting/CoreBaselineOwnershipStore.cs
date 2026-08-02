// SPDX-License-Identifier: MIT
using AgentKick75.Core.Baseline;

namespace AgentKick75.App.Lighting;

public sealed class FileBaselineOwnershipStore :
    IBaselineOwnershipStore
{
    private const string RuntimeOwnershipMarker = "agent-kick75:runtime-restore";
    private const string RuntimeTransportProfile = "kick75-usb";
    private readonly LightingRestoreStore store;

    public FileBaselineOwnershipStore(LightingRestoreStore store, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        _ = timeProvider;
    }

    public async ValueTask<BaselineOwnershipRecord?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            LightingRestoreRecord? record = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            return record is null ? null : FromCore(record);
        }
        catch (BaselineValidationException exception)
        {
            throw UnsafeBaseline(exception.Message);
        }
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

        await store.SaveAsync(
            new LightingRestoreRecord(
                record.DeviceIdentity,
                record.InterfaceFingerprint,
                record.CurrentMode,
                record.SideLightState),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MarkReleasedAsync(
        string ownershipMarker,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipMarker);
        LightingRestoreRecord? result = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw UnsafeBaseline("The owned baseline disappeared before release.");
        }

        await store.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static BaselineOwnershipRecord FromCore(LightingRestoreRecord record)
    {
        return new BaselineOwnershipRecord(
            BaselineRecord.CurrentSchemaVersion,
            record.DeviceIdentity,
            RuntimeTransportProfile,
            record.InterfaceFingerprint,
            record.OriginalSideLightBytes.ToArray(),
            record.CurrentMode,
            RuntimeOwnershipMarker,
            true,
            DateTimeOffset.UnixEpoch);
    }

    private static LightingTransportException UnsafeBaseline(string message)
    {
        return new LightingTransportException(
            LightingTransportFailureKind.BaselineMismatch,
            message);
    }
}
