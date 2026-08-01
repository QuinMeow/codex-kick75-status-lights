// SPDX-License-Identifier: MIT
using AgentKick75.App.Lighting;
using AgentKick75.Core.Baseline;

namespace AgentKick75.Integration.Tests;

public sealed class CoreBaselineOwnershipStoreTests
{
    [Fact]
    public async Task SaveLoadAndRelease_CurrentMode_RoundTripsWithoutFieldLoss()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AgentKick75.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "baseline.json");
        Directory.CreateDirectory(directory);
        try
        {
            var coreStore = new BaselineStore(path);
            var adapter = new FileBaselineOwnershipStore(coreStore);
            var expected = new BaselineOwnershipRecord(
                BaselineRecord.CurrentSchemaVersion,
                "device",
                "kick75-usb",
                "interface",
                Convert.FromHexString("0064010100E9FFFB"),
                CurrentMode: 1,
                BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
                IsOwned: true,
                DateTimeOffset.UtcNow.AddMinutes(-1));

            await adapter.SaveAsync(expected);
            BaselineOwnershipRecord loaded = Assert.IsType<BaselineOwnershipRecord>(
                await adapter.LoadAsync());
            BaselineLoadResult coreLoaded = await coreStore.LoadAsync();

            Assert.Equal(expected.CurrentMode, loaded.CurrentMode);
            Assert.Equal(expected.SideLightState, loaded.SideLightState);
            Assert.Equal(expected.CurrentMode, coreLoaded.Baseline?.CurrentMode);

            await adapter.MarkReleasedAsync(expected.OwnershipMarker);
            BaselineOwnershipRecord released = Assert.IsType<BaselineOwnershipRecord>(
                await adapter.LoadAsync());
            Assert.False(released.IsOwned);
            Assert.Equal(expected.CurrentMode, released.CurrentMode);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
