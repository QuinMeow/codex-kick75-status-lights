// SPDX-License-Identifier: MIT
using AgentKick75.App.Lighting;
using AgentKick75.Core.Baseline;
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests;

public sealed class CoreHardwareTestBaselineJournalTests
{
    private const byte CurrentMode = 1;
    private static readonly byte[] Baseline = Convert.FromHexString("0064010100E9FFFB");

    [Fact]
    public async Task AcquireAndRelease_DurableStoreTracksOwnershipBoundary()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AgentKick75.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "baseline.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new BaselineStore(path);
            var journal = new CoreHardwareTestBaselineJournal(store);
            HidInterfaceDescriptor device = UsbDevice();

            HardwareTestBaselineLease lease = await journal.AcquireAsync(
                device,
                HidTransportProfiles.Kick75Usb,
                Baseline,
                CurrentMode,
                CancellationToken.None);

            BaselineLoadResult owned = await store.LoadAsync();
            Assert.True(owned.Baseline?.Ownership.IsOwned);
            Assert.Equal(lease.OwnershipMarker, owned.Baseline?.Ownership.Marker);
            Assert.Equal(Baseline, owned.Baseline?.OriginalSideLightBytes);
            Assert.Equal(CurrentMode, owned.Baseline?.CurrentMode);
            Assert.Equal(CurrentMode, lease.CurrentMode);

            await journal.ReleaseAsync(lease, CancellationToken.None);

            BaselineLoadResult released = await store.LoadAsync();
            Assert.False(released.Baseline?.Ownership.IsOwned);
            Assert.NotNull(released.Baseline?.Ownership.ReleasedAtUtc);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReleaseAsync_LeaseCurrentModeChanged_RefusesToReleaseOwnership()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AgentKick75.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "baseline.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new BaselineStore(path);
            var journal = new CoreHardwareTestBaselineJournal(store);
            HardwareTestBaselineLease lease = await journal.AcquireAsync(
                UsbDevice(),
                HidTransportProfiles.Kick75Usb,
                Baseline,
                CurrentMode,
                CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await journal.ReleaseAsync(
                    lease with { CurrentMode = 0 },
                    CancellationToken.None));

            BaselineLoadResult current = await store.LoadAsync();
            Assert.True(current.Baseline?.Ownership.IsOwned);
            Assert.Equal(CurrentMode, current.Baseline?.CurrentMode);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AcquireAsync_ReleasedVersionOneJournal_AtomicallyReplacesWithObservedV2Mode()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AgentKick75.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "baseline.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "schemaVersion": 1,
                  "device": {
                    "identity": "legacy-device",
                    "transportProfile": "kick75-usb",
                    "interfaceFingerprint": "legacy-interface"
                  },
                  "originalSideLightBytes": [0, 100, 1, 1, 0, 233, 255, 251],
                  "ownership": {
                    "marker": "agent-kick75:f73e775c3dba4bffb040f18b2a504a28",
                    "isOwned": false,
                    "acquiredAtUtc": "2026-07-31T12:34:56.0000000+00:00",
                    "releasedAtUtc": "2026-07-31T12:35:56.0000000+00:00"
                  }
                }
                """);
            var store = new BaselineStore(path);
            var journal = new CoreHardwareTestBaselineJournal(store);

            HardwareTestBaselineLease lease = await journal.AcquireAsync(
                UsbDevice(),
                HidTransportProfiles.Kick75Usb,
                Baseline,
                CurrentMode,
                CancellationToken.None);

            BaselineLoadResult current = await store.LoadAsync();
            Assert.Equal(BaselineLoadStatus.Loaded, current.Status);
            Assert.Equal(BaselineRecord.CurrentSchemaVersion, current.Baseline?.SchemaVersion);
            Assert.Equal(CurrentMode, current.Baseline?.CurrentMode);
            Assert.True(current.Baseline?.Ownership.IsOwned);
            Assert.Equal(lease.OwnershipMarker, current.Baseline?.Ownership.Marker);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static HidInterfaceDescriptor UsbDevice() =>
        new("usb-path", 0x19F5, 0x1026, 0x0001, 0x0000, 65, 65);
}
