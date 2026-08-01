// SPDX-License-Identifier: MIT
using AgentKick75.App.Hosting;
using AgentKick75.App.Lighting;
using AgentKick75.Core.Baseline;
using AgentKick75.Core.State;

namespace AgentKick75.Integration.Tests;

public sealed class HostRuntimeStartupRecoveryTests
{
    private static readonly byte[] Baseline =
        [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];

    private static readonly byte[] InterruptedState =
        [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];

    [Fact]
    public async Task Start_OwnedMatchingBaselineWithoutHook_RestoresVerifiesAndMarksReleased()
    {
        var session = new LightingDeviceSession(
            "owned-device",
            "kick75-usb",
            "owned-interface",
            CurrentMode: 1);
        var transport = new MockLightingTransport(InterruptedState, session);
        var ownershipStore = new InMemoryBaselineOwnershipStore();
        await ownershipStore.SaveAsync(new BaselineOwnershipRecord(
            BaselineRecord.CurrentSchemaVersion,
            session.DeviceIdentity,
            session.TransportProfile,
            session.InterfaceFingerprint,
            Baseline,
            session.CurrentMode,
            BaselineOwnership.MarkerPrefix + "host-runtime-startup",
            IsOwned: true,
            DateTimeOffset.Parse("2026-07-31T00:00:00Z")));
        var worker = new HidLightingWorker(transport, ownershipStore);
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        var runtime = new HostRuntime(
            worker,
            coordinator,
            $"agent-kick75-startup-recovery-{Guid.NewGuid():N}");

        runtime.Start();
        try
        {
            BaselineOwnershipRecord released = await WaitForReleasedOwnershipAsync(
                ownershipStore,
                TimeSpan.FromSeconds(4));

            Assert.False(released.IsOwned);
            Assert.Equal(Baseline, Assert.Single(transport.Writes));
            Assert.Equal(
                new[] { "connect", "write", "read" },
                transport.Operations.Take(3).ToArray());
            Assert.Equal(
                session.TransportProfile,
                Assert.Single(transport.ConnectionRequests).RequiredTransportProfileId);
            Assert.Equal(TaskVisualState.Idle, coordinator.GetStatus().AggregateState);
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        Assert.Contains("disconnect", transport.Operations);
    }

    private static async Task<BaselineOwnershipRecord> WaitForReleasedOwnershipAsync(
        IBaselineOwnershipStore store,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (true)
        {
            BaselineOwnershipRecord? current = await store.LoadAsync(timeoutSource.Token);
            if (current?.IsOwned == false)
            {
                return current;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeoutSource.Token);
        }
    }
}
