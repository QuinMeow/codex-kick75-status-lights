// SPDX-License-Identifier: MIT
using System.Net;
using System.Text.Json;
using AgentKick75.App.Hosting;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.App.Web;
using AgentKick75.Core.Hooks;
using AgentKick75.Core.State;

namespace AgentKick75.Integration.Tests.Web;

public sealed class ControlPageHostIsolationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly byte[] Baseline =
        [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];

    private static readonly byte[] Thinking =
        [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];

    [Fact]
    public async Task EventStreamPages_CloseAndReleaseSubscription_HostContinuesHookAndRestoreOverPipe()
    {
        string pipeName = $"agent-kick75-m3-isolation-{Guid.NewGuid():N}";
        var transport = new MockLightingTransport(Baseline);
        var ownershipStore = new InMemoryBaselineOwnershipStore();
        var worker = new HidLightingWorker(transport, ownershipStore);
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        await using var runtime = new HostRuntime(worker, coordinator, pipeName);
        using var controlPlane = new HostControlPlaneAdapter(coordinator);
        runtime.Start();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            ControlPageOptions.FromHostInstanceToken(new string('A', 43)));
        var pages = new List<EventStreamPage>();

        try
        {
            for (int index = 0; index < 4; index++)
            {
                pages.Add(await OpenEventStreamPageAsync(server.BaseUri));
            }

            using (EventStreamPage rejected = await OpenEventStreamPageAsync(
                server.BaseUri,
                HttpStatusCode.TooManyRequests))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, rejected.Response.StatusCode);
            }

            pages[0].Dispose();
            EventStreamPage replacement = await WaitForReleasedEventStreamSlotAsync(
                server.BaseUri,
                TimeSpan.FromSeconds(5));
            pages.Add(replacement);
            Assert.Equal(HttpStatusCode.OK, replacement.Response.StatusCode);

            foreach (EventStreamPage page in pages)
            {
                page.Dispose();
            }

            var pipeClient = new NamedPipeRequestClient(pipeName);
            await pipeClient.SendAsync(
                HookEnvelope(CodexHookEventKind.UserPromptSubmit, "session", "turn"),
                expectResponse: false,
                TimeSpan.FromSeconds(2));
            await WaitForAsync(
                () => transport.Writes.Any(write => write.AsSpan().SequenceEqual(Thinking)),
                TimeSpan.FromSeconds(3));

            await pipeClient.SendAsync(
                HookEnvelope(CodexHookEventKind.SessionEnd, "session"),
                expectResponse: false,
                TimeSpan.FromSeconds(2));
            await WaitForAsync(
                () => transport.Writes.Count >= 2 &&
                    transport.Writes[^1].AsSpan().SequenceEqual(Baseline),
                TimeSpan.FromSeconds(3));

            Assert.Equal(TaskVisualState.Idle, coordinator.GetStatus().AggregateState);
            Assert.Equal(Thinking, transport.Writes[0]);
            Assert.Equal(Baseline, transport.Writes[^1]);
            BaselineOwnershipRecord persisted = Assert.IsType<BaselineOwnershipRecord>(
                await ownershipStore.LoadAsync());
            Assert.False(persisted.IsOwned);
        }
        finally
        {
            foreach (EventStreamPage page in pages)
            {
                page.Dispose();
            }
        }
    }

    [Fact]
    public async Task DeviceBusy_ProductionChain_StreamsReconnectDiagnosticAndConvergesAfterRetry()
    {
        var session = new LightingDeviceSession(
            "19F5:1026/path=private-hid-path",
            "kick75-usb",
            "mock-interface-fingerprint",
            CurrentMode: 0);
        var transport = new MockLightingTransport(Baseline, session);
        transport.FailNext(MockLightingOperation.Connect, LightingTransportFailureKind.DeviceBusy);
        var reconnectDelay = new ControlledReconnectDelay();
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore(),
            reconnectDelay: reconnectDelay);
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var controlPlane = new HostControlPlaneAdapter(coordinator);
        worker.Start();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            ControlPageOptions.FromHostInstanceToken(new string('A', 43)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new HttpClient
        {
            BaseAddress = server.BaseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var eventRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events");
        using HttpResponseMessage eventResponse = await client.SendAsync(
            eventRequest,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await using Stream eventStream = await eventResponse.Content.ReadAsStreamAsync(timeout.Token);
        using var eventReader = new StreamReader(eventStream);

        Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);
        ControlEventDto connected = await ReadSseEventAsync(eventReader, timeout.Token);
        Assert.Equal("connected", connected.Kind);

        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "device-busy-session",
            "device-busy-turn"), timeout.Token);

        ControlEventDto busyEvent = await ReadSseEventAsync(eventReader, timeout.Token);
        ControlStatusDto busyStatus = Assert.IsType<ControlStatusDto>(busyEvent.Status);
        Assert.Equal("status", busyEvent.Kind);
        Assert.Equal("DeviceBusy", busyEvent.DiagnosticCode);
        Assert.Equal("Thinking", busyStatus.AggregateState);
        Assert.Equal("DeviceBusy", busyStatus.Device.KeyboardStatus);
        Assert.Equal("DeviceBusy", busyStatus.Device.LastErrorCode);
        Assert.Equal(LightingWorkerState.Reconnecting, worker.Snapshot.State);
        Assert.Equal(1, worker.Snapshot.ReconnectAttempt);
        Assert.Equal(1, reconnectDelay.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(2), reconnectDelay.RequestedDelay);

        ControlStatusDto busyApi = await ReadStatusAsync(client, timeout.Token);
        Assert.Equal("DeviceBusy", busyApi.Device.KeyboardStatus);
        Assert.Equal("DeviceBusy", busyApi.Device.LastErrorCode);

        reconnectDelay.Release();
        await WaitForAsync(
            () => worker.Snapshot.State == LightingWorkerState.Active,
            TimeSpan.FromSeconds(2));
        await coordinator.CleanupAsync(timeout.Token);

        ControlEventDto recoveredEvent = await ReadSseEventAsync(eventReader, timeout.Token);
        ControlStatusDto recoveredStatus = Assert.IsType<ControlStatusDto>(recoveredEvent.Status);
        Assert.Equal("status", recoveredEvent.Kind);
        Assert.Null(recoveredEvent.DiagnosticCode);
        Assert.Equal("Ready", recoveredStatus.Device.KeyboardStatus);
        Assert.Null(recoveredStatus.Device.LastErrorCode);
        Assert.Equal("19F5:1026", recoveredStatus.Device.DeviceIdentity);
        Assert.Equal("USB allowlisted; runtime session observed", recoveredStatus.Device.SupportStatus);
        Assert.Equal(LightingWorkerState.Active, worker.Snapshot.State);
        Assert.Equal(0, worker.Snapshot.ReconnectAttempt);

        ControlStatusDto recoveredApi = await ReadStatusAsync(client, timeout.Token);
        Assert.Equal(recoveredStatus, recoveredApi);
        Assert.Single(transport.Writes);
        Assert.Equal(Thinking, transport.Writes[0]);
    }

    private static PipeEnvelope HookEnvelope(
        CodexHookEventKind kind,
        string sessionId,
        string? turnId = null)
    {
        return PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
        {
            kind = (int)kind,
            sessionId,
            turnId,
        });
    }

    private static async Task<EventStreamPage> OpenEventStreamPageAsync(
        Uri baseUri,
        HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        var cancellation = new CancellationTokenSource();
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events");

        try
        {
            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expectedStatus, response.StatusCode);
            return new EventStreamPage(client, response, request, cancellation);
        }
        catch
        {
            cancellation.Cancel();
            request.Dispose();
            cancellation.Dispose();
            client.Dispose();
            throw;
        }
    }

    private static async Task<EventStreamPage> WaitForReleasedEventStreamSlotAsync(
        Uri baseUri,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (true)
        {
            timeoutSource.Token.ThrowIfCancellationRequested();
            EventStreamPage candidate = await OpenEventStreamPageAsync(
                baseUri,
                expectedStatus: HttpStatusCode.OK,
                allowTooManyRequests: true);
            if (candidate.Response.StatusCode == HttpStatusCode.OK)
            {
                return candidate;
            }

            candidate.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeoutSource.Token);
        }
    }

    private static async Task<EventStreamPage> OpenEventStreamPageAsync(
        Uri baseUri,
        HttpStatusCode expectedStatus,
        bool allowTooManyRequests)
    {
        var cancellation = new CancellationTokenSource();
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events");

        try
        {
            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            if (!allowTooManyRequests || response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                Assert.Equal(expectedStatus, response.StatusCode);
            }

            return new EventStreamPage(client, response, request, cancellation);
        }
        catch
        {
            cancellation.Cancel();
            request.Dispose();
            cancellation.Dispose();
            client.Dispose();
            throw;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeoutSource.Token);
        }
    }

    private static async Task<ControlEventDto> ReadSseEventAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            ControlEventDto? controlEvent = JsonSerializer.Deserialize<ControlEventDto>(
                line.AsSpan("data: ".Length),
                WebJsonOptions);
            return Assert.IsType<ControlEventDto>(controlEvent);
        }
    }

    private static async Task<ControlStatusDto> ReadStatusAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        string json = await client.GetStringAsync("/api/v1/status", cancellationToken);
        ControlStatusDto? status = JsonSerializer.Deserialize<ControlStatusDto>(json, WebJsonOptions);
        return Assert.IsType<ControlStatusDto>(status);
    }

    private sealed class ControlledReconnectDelay : IReconnectDelay
    {
        private readonly TaskCompletionSource released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;
        private TimeSpan requestedDelay;

        public int CallCount => Volatile.Read(ref callCount);

        public TimeSpan RequestedDelay => requestedDelay;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            requestedDelay = delay;
            Interlocked.Increment(ref callCount);
            return new ValueTask(released.Task.WaitAsync(cancellationToken));
        }

        public void Release()
        {
            released.TrySetResult();
        }
    }

    private sealed class EventStreamPage(
        HttpClient client,
        HttpResponseMessage response,
        HttpRequestMessage request,
        CancellationTokenSource cancellation) : IDisposable
    {
        private int disposed;

        public HttpResponseMessage Response => response;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            cancellation.Cancel();
            response.Dispose();
            request.Dispose();
            client.Dispose();
            cancellation.Dispose();
        }
    }
}
