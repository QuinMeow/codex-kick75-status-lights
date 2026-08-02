// SPDX-License-Identifier: MIT
using AgentKick75.App.Lighting;
using AgentKick75.Core.Baseline;

namespace AgentKick75.Integration.Tests;

public sealed class CoreBaselineOwnershipStoreTests
{
    [Fact]
    public async Task SaveLoadAndRelease_PersistsOnlyRestoreFieldsThenDeletesRecord()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AgentKick75.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "lighting-restore.json");
        Directory.CreateDirectory(directory);
        try
        {
            var coreStore = new LightingRestoreStore(path);
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
            LightingRestoreRecord? coreLoaded = await coreStore.LoadAsync();

            Assert.Equal(expected.CurrentMode, loaded.CurrentMode);
            Assert.Equal(expected.SideLightState, loaded.SideLightState);
            Assert.Equal(expected.CurrentMode, coreLoaded?.CurrentMode);
            string json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("ownership", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("captured", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transportProfile", json, StringComparison.Ordinal);

            await adapter.MarkReleasedAsync(expected.OwnershipMarker);
            Assert.Null(await adapter.LoadAsync());
            Assert.False(File.Exists(path));
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
