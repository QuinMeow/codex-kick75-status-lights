// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Hosting;
using AgentKick75.App.Lighting;
using AgentKick75.App.Web;
using AgentKick75.Core.Hooks;
using AgentKick75.Core.State;

namespace AgentKick75.Integration.Tests.Web;

public sealed class HostControlPlaneAdapterTests
{
    private static readonly byte[] Baseline =
        [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];

    [Fact]
    public async Task WatchEventsAsync_TwoSubscribers_ReceiveSameStatusEvent()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<ControlEventDto> first = adapter
            .WatchEventsAsync(timeout.Token)
            .GetAsyncEnumerator();
        await using IAsyncEnumerator<ControlEventDto> second = adapter
            .WatchEventsAsync(timeout.Token)
            .GetAsyncEnumerator();

        Task<bool> firstRead = first.MoveNextAsync().AsTask();
        Task<bool> secondRead = second.MoveNextAsync().AsTask();
        coordinator.SetHookEnablement(HookEnablementState.Enabled);

        Assert.True(await firstRead.WaitAsync(timeout.Token));
        Assert.True(await secondRead.WaitAsync(timeout.Token));
        Assert.Equal("status", first.Current.Kind);
        Assert.Equal(first.Current.Sequence, second.Current.Sequence);
        Assert.Equal(first.Current.Status, second.Current.Status);
    }

    [Fact]
    public async Task WatchEventsAsync_CallRegistersBeforeFirstMoveNext()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        IAsyncEnumerable<ControlEventDto> stream = adapter.WatchEventsAsync(timeout.Token);
        coordinator.SetHookEnablement(HookEnablementState.Enabled);

        await using IAsyncEnumerator<ControlEventDto> subscriber =
            stream.GetAsyncEnumerator(timeout.Token);
        Assert.True(await subscriber.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
        Assert.Equal(1, subscriber.Current.Sequence);
        Assert.Equal("Enabled", subscriber.Current.Status?.HookStatus);
    }

    [Fact]
    public async Task WatchEventsAsync_ConcurrentBroadcasts_RemainMonotonicAndConverge()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        IAsyncEnumerable<ControlEventDto> firstStream = adapter.WatchEventsAsync(timeout.Token);
        IAsyncEnumerable<ControlEventDto> secondStream = adapter.WatchEventsAsync(timeout.Token);

        const int producerCount = 8;
        const int eventsPerProducer = 250;
        await Task.WhenAll(Enumerable.Range(0, producerCount).Select(_ => Task.Run(() =>
        {
            for (int index = 0; index < eventsPerProducer; index++)
            {
                coordinator.SetHookEnablement(HookEnablementState.Enabled);
            }
        })));

        long expectedFinalSequence = producerCount * eventsPerProducer;
        long[] first = await ReadBufferedSequencesAsync(firstStream, 64, timeout.Token);
        long[] second = await ReadBufferedSequencesAsync(secondStream, 64, timeout.Token);

        Assert.Equal(first, second);
        Assert.True(first.SequenceEqual(first.OrderBy(sequence => sequence)));
        Assert.Equal(first.Length, first.Distinct().Count());
        Assert.Equal(expectedFinalSequence, first[^1]);
    }

    [Fact]
    public async Task WatchEventsAsync_DelayedOldCallback_CannotPublishStateRollback()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        var reducer = new TaskStateReducer();
        var coordinator = new HostCoordinator(reducer, worker);
        var firstCallbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        coordinator.StatusChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref callbackCount) == 1)
            {
                firstCallbackEntered.TrySetResult();
                releaseFirstCallback.Task.GetAwaiter().GetResult();
            }
        };

        using var adapter = new HostControlPlaneAdapter(coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        IAsyncEnumerable<ControlEventDto> stream = adapter.WatchEventsAsync(timeout.Token);

        Task oldCallback = Task.Run(() =>
            coordinator.SetHookEnablement(HookEnablementState.Disabled));
        await firstCallbackEntered.Task.WaitAsync(timeout.Token);
        reducer.Apply(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));
        await Task.Run(() => coordinator.SetHookEnablement(HookEnablementState.Enabled))
            .WaitAsync(timeout.Token);

        releaseFirstCallback.TrySetResult();
        await oldCallback.WaitAsync(timeout.Token);

        await using IAsyncEnumerator<ControlEventDto> subscriber =
            stream.GetAsyncEnumerator(timeout.Token);
        Assert.True(await subscriber.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
        Assert.Equal("Thinking", subscriber.Current.Status?.AggregateState);
        Assert.Equal("Enabled", subscriber.Current.Status?.HookStatus);
        Assert.True(await subscriber.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
        Assert.Equal("Thinking", subscriber.Current.Status?.AggregateState);
        Assert.Equal("Enabled", subscriber.Current.Status?.HookStatus);
        Assert.Equal(2, subscriber.Current.Sequence);
    }

    [Fact]
    public async Task WatchEventsAsync_SlowSubscriber_DropsOldestAndKeepsBoundedNewestWindow()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<ControlEventDto> subscriber = adapter
            .WatchEventsAsync(timeout.Token)
            .GetAsyncEnumerator();

        Task<bool> initialRead = subscriber.MoveNextAsync().AsTask();
        coordinator.SetHookEnablement(HookEnablementState.Enabled);
        Assert.True(await initialRead.WaitAsync(timeout.Token));
        Assert.Equal(1, subscriber.Current.Sequence);

        for (int index = 0; index < 70; index++)
        {
            coordinator.SetHookEnablement(HookEnablementState.Enabled);
        }

        var bufferedSequences = new List<long>(capacity: 64);
        for (int index = 0; index < 64; index++)
        {
            Assert.True(await subscriber.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            bufferedSequences.Add(subscriber.Current.Sequence);
        }

        Assert.Equal(64, bufferedSequences.Count);
        Assert.Equal(8, bufferedSequences[0]);
        Assert.Equal(71, bufferedSequences[^1]);
    }

    [Fact]
    public async Task Dispose_PendingSubscriber_CompletesWithoutAnEvent()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<ControlEventDto> subscriber = adapter
            .WatchEventsAsync(timeout.Token)
            .GetAsyncEnumerator();

        Task<bool> pendingRead = subscriber.MoveNextAsync().AsTask();
        adapter.Dispose();

        Assert.False(await pendingRead.WaitAsync(timeout.Token));
    }

    [Theory]
    [InlineData("kick75-usb", "NotApplicable", "USB allowlisted; runtime session observed")]
    public async Task GetStatusAsync_KnownTransport_ReportsReceiverSemanticsAndDeviceIdentity(
        string transportProfile,
        string expectedReceiverStatus,
        string expectedSupportStatus)
    {
        const string deviceIdentity = "19F5:1026/path=private-hid-path";
        var session = new LightingDeviceSession(
            deviceIdentity,
            transportProfile,
            "mock-interface-fingerprint",
            CurrentMode: 0,
            DescriptorMetadata: new LightingDeviceDescriptorMetadata(
                "Kick75 IO",
                "NuPhy",
                0x0418));
        var transport = new MockLightingTransport(Baseline, session);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        await worker.SetSideLightAsync(Baseline);
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);

        ControlStatusDto status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal(expectedReceiverStatus, status.Device.ReceiverStatus);
        Assert.Equal("19F5:1026", status.Device.DeviceIdentity);
        Assert.Equal(transportProfile, status.Device.Transport);
        Assert.Equal("mock-interface-fingerprint", status.Device.InterfaceFingerprint);
        Assert.Equal(expectedSupportStatus, status.Device.SupportStatus);
        Assert.Equal("NuPhy Kick75 IO", status.Device.Model);
        Assert.Equal("HID descriptor bcdDevice 0x0418", status.Device.FirmwareVersion);
    }

    [Theory]
    [InlineData(
        "kick75-usb",
        LightingDeviceSupport.Writable,
        "NotApplicable",
        "USB allowlisted; descriptor observed",
        "NuPhy Kick75 IO")]
    [InlineData(
        "kick75-u1-dongle",
        LightingDeviceSupport.DiagnosticOnly,
        "Present",
        "DiagnosticOnly",
        "NuPhy Kick75 U1 Receiver")]
    [InlineData(
        "kick75-high-diagnostic",
        LightingDeviceSupport.DiagnosticOnly,
        "NotApplicable",
        "DiagnosticOnly",
        "NuPhy Kick75 High")]
    public async Task GetStatusAsync_StrictDescriptorObservation_IsReachableWithoutConnectOrWrite(
        string transportProfile,
        LightingDeviceSupport support,
        string expectedReceiverStatus,
        string expectedSupportStatus,
        string expectedModel)
    {
        string product = transportProfile switch
        {
            "kick75-usb" => "Kick75 IO",
            "kick75-u1-dongle" => "Kick75 U1 Receiver",
            "kick75-high-diagnostic" => "Kick75 High",
            _ => throw new ArgumentOutOfRangeException(nameof(transportProfile)),
        };
        var transport = new MockLightingTransport(Baseline)
        {
            Inspection = new LightingDeviceInspection(
                "19F5:2620/path=private-hid-path",
                transportProfile,
                "strict-interface-fingerprint",
                support,
                new LightingDeviceDescriptorMetadata(
                    product,
                    "NuPhy",
                    0x0418)),
        };
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        await worker.ProbeAsync();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);

        ControlStatusDto status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal(expectedReceiverStatus, status.Device.ReceiverStatus);
        Assert.Equal(expectedSupportStatus, status.Device.SupportStatus);
        Assert.Equal(transportProfile, status.Device.Transport);
        Assert.Equal("19F5:2620", status.Device.DeviceIdentity);
        Assert.Equal("strict-interface-fingerprint", status.Device.InterfaceFingerprint);
        Assert.Equal(expectedModel, status.Device.Model);
        Assert.Equal("HID descriptor bcdDevice 0x0418", status.Device.FirmwareVersion);
        Assert.Equal(new[] { "inspect" }, transport.Operations);
        Assert.Empty(transport.ConnectionRequests);
        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData("kick75-usb", LightingDeviceSupport.Writable, "Kick75 USB HID device")]
    [InlineData("kick75-u1-dongle", LightingDeviceSupport.DiagnosticOnly, "Kick75 U1 receiver")]
    [InlineData("kick75-high-diagnostic", LightingDeviceSupport.DiagnosticOnly, "Kick75 High HID device")]
    public async Task GetStatusAsync_MissingDescriptorNames_UsesProfileSpecificFallback(
        string transportProfile,
        LightingDeviceSupport support,
        string expectedModel)
    {
        var transport = new MockLightingTransport(Baseline)
        {
            Inspection = new LightingDeviceInspection(
                "19F5:1026/path=private-hid-path",
                transportProfile,
                "19F5:1026/0001:0000/in=65/out=65",
                support),
        };
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        await worker.ProbeAsync();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);

        ControlStatusDto status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal(expectedModel, status.Device.Model);
        Assert.Null(status.Device.FirmwareVersion);
    }

    [Fact]
    public async Task GetStatusAsync_FreshIdle_DoesNotOpenDeviceOrClaimSupport()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);

        ControlStatusDto status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal("none", status.Device.Transport);
        Assert.Equal("Unknown", status.Device.SupportStatus);
        Assert.Null(status.Device.InterfaceFingerprint);
        Assert.Empty(transport.ConnectionRequests);
    }

    [Fact]
    public async Task GetStatusAsync_IdentityLikeDescriptorText_FallsBackWithoutApiLeak()
    {
        var transport = new MockLightingTransport(Baseline)
        {
            Inspection = new LightingDeviceInspection(
                "19F5:1026/path=private-hid-path",
                "kick75-usb",
                "19F5:1026/0001:0000/in=65/out=65",
                LightingDeviceSupport.Writable,
                new LightingDeviceDescriptorMetadata(
                    @"\\?\hid#vid_19f5&pid_1026#private-product-path",
                    "serial=private-manufacturer-serial",
                    0x0418)),
        };
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        await worker.ProbeAsync();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var adapter = new HostControlPlaneAdapter(coordinator);

        ControlStatusDto status = await adapter.GetStatusAsync(CancellationToken.None);
        string json = JsonSerializer.Serialize(status);

        Assert.Equal("Kick75 USB HID device", status.Device.Model);
        Assert.Equal("HID descriptor bcdDevice 0x0418", status.Device.FirmwareVersion);
        Assert.DoesNotContain("path=", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial=", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long[]> ReadBufferedSequencesAsync(
        IAsyncEnumerable<ControlEventDto> stream,
        int count,
        CancellationToken cancellationToken)
    {
        var sequences = new long[count];
        await using IAsyncEnumerator<ControlEventDto> subscriber =
            stream.GetAsyncEnumerator(cancellationToken);
        for (int index = 0; index < count; index++)
        {
            Assert.True(await subscriber.MoveNextAsync().AsTask().WaitAsync(cancellationToken));
            sequences[index] = subscriber.Current.Sequence;
        }

        return sequences;
    }
}
