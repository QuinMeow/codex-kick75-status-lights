// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using AgentKick75.App.Ipc;

namespace AgentKick75.Integration.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public async Task Framing_AnonymousPayload_RoundTripPreservesNestedValues()
    {
        PipeEnvelope expected = PipeEnvelope.Create("test", new
        {
            value = 42,
            label = "anonymous-payload",
            nested = new { accepted = true },
        });
        await using var stream = new MemoryStream();

        await PipeFraming.WriteAsync(stream, expected);
        stream.Position = 0;
        PipeEnvelope actual = await PipeFraming.ReadAsync(stream);

        Assert.Equal(PipeEnvelope.CurrentVersion, actual.Version);
        Assert.Equal("test", actual.Kind);
        Assert.Equal(42, actual.Payload.GetProperty("value").GetInt32());
        Assert.Equal("anonymous-payload", actual.Payload.GetProperty("label").GetString());
        Assert.True(actual.Payload.GetProperty("nested").GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task ReadAsync_OversizedLength_RejectsBeforeAllocatingPayload()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, PipeFraming.MaximumMessageBytes + 1);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<PipeProtocolException>(
            async () => await PipeFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_IncompatibleVersion_IsRejected()
    {
        var incompatible = new PipeEnvelope(
            PipeEnvelope.CurrentVersion + 1,
            "test",
            PipeEnvelope.Create("payload", new { }).Payload);
        await using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, incompatible);
        stream.Position = 0;

        await Assert.ThrowsAsync<PipeProtocolException>(
            async () => await PipeFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task NamedPipe_CurrentUserEndpoint_ExchangesOneRequest()
    {
        string pipeName = $"AgentKick75.tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeMessageServer(
            (request, cancellationToken) => ValueTask.FromResult<PipeEnvelope?>(
                PipeEnvelope.Create(PipeMessageKinds.StatusResponse, new { host = request.Kind })),
            pipeName);
        server.Start();
        var client = new NamedPipeRequestClient(pipeName);

        PipeEnvelope? response = await client.SendAsync(
            PipeEnvelope.Create(PipeMessageKinds.StatusRequest, new { }),
            expectResponse: true,
            TimeSpan.FromSeconds(2));

        Assert.NotNull(response);
        Assert.Equal(PipeMessageKinds.StatusResponse, response.Kind);
        Assert.Equal(PipeMessageKinds.StatusRequest, response.Payload.GetProperty("host").GetString());
    }

    [Fact]
    public async Task NamedPipe_OneWayRequest_IsHandledBeforeClientDisconnectIsObserved()
    {
        string pipeName = NewPipeName();
        var handled = new TaskCompletionSource<PipeEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new NamedPipeMessageServer(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                handled.TrySetResult(request);
                return ValueTask.FromResult<PipeEnvelope?>(null);
            },
            pipeName);
        server.Start();
        var client = new NamedPipeRequestClient(pipeName);

        await client.SendAsync(
            PipeEnvelope.Create(PipeMessageKinds.HookEvent, new { value = "public" }),
            expectResponse: false,
            TimeSpan.FromMilliseconds(250));

        PipeEnvelope request = await handled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PipeMessageKinds.HookEvent, request.Kind);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"unexpected\":true}")]
    public async Task NamedPipe_InvalidStatusPayload_IsRejectedAndNextClientSucceeds(
        string payloadJson)
    {
        string pipeName = NewPipeName();
        int handledRequests = 0;
        await using var server = CreateStatusServer(
            pipeName,
            onHandled: () => Interlocked.Increment(ref handledRequests));
        server.Start();
        var client = new NamedPipeRequestClient(pipeName);
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        var invalid = new PipeEnvelope(
            PipeEnvelope.CurrentVersion,
            PipeMessageKinds.StatusRequest,
            payload.RootElement.Clone());

        PipeEnvelope? rejected = await client.SendAsync(
            invalid,
            expectResponse: true,
            TimeSpan.FromSeconds(2));

        Assert.NotNull(rejected);
        Assert.Equal(PipeMessageKinds.Rejected, rejected.Kind);
        Assert.Equal("invalid-envelope", rejected.Payload.GetProperty("reason").GetString());
        Assert.Equal(0, Volatile.Read(ref handledRequests));

        PipeEnvelope response = await SendStatusAsync(pipeName);

        Assert.Equal(PipeMessageKinds.StatusResponse, response.Kind);
        Assert.Equal(1, Volatile.Read(ref handledRequests));
    }

    [Fact]
    public async Task NamedPipe_ClientDisconnectsMidHeader_NextClientSucceeds()
    {
        string pipeName = NewPipeName();
        await using var server = CreateStatusServer(pipeName);
        server.Start();

        await using (NamedPipeClientStream partialClient = CreateRawClient(pipeName))
        {
            await ConnectAsync(partialClient);
            byte[] header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, 32);
            await partialClient.WriteAsync(header.AsMemory(0, 2));
            await partialClient.FlushAsync();
        }

        PipeEnvelope response = await SendStatusAsync(pipeName);

        Assert.Equal(PipeMessageKinds.StatusResponse, response.Kind);
    }

    [Fact]
    public async Task NamedPipe_ClientStallsMidPayload_TimesOutAndNextClientSucceeds()
    {
        string pipeName = NewPipeName();
        await using var server = CreateStatusServer(
            pipeName,
            clientRequestTimeout: TimeSpan.FromMilliseconds(100));
        server.Start();

        await using var stalledClient = CreateRawClient(pipeName);
        await ConnectAsync(stalledClient);
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, 32);
        await stalledClient.WriteAsync(header);
        await stalledClient.WriteAsync(new byte[] { (byte)'{' });
        await stalledClient.FlushAsync();

        PipeEnvelope response = await SendStatusAsync(pipeName);

        Assert.Equal(PipeMessageKinds.StatusResponse, response.Kind);
    }

    [Fact]
    public async Task NamedPipe_HandlerIgnoresCancellation_TimesOutAndNextClientSucceeds()
    {
        string pipeName = NewPipeName();
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource<PipeEnvelope?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new NamedPipeMessageServer(
            HandleAsync,
            pipeName,
            TimeSpan.FromMilliseconds(100));
        server.Start();
        var firstClient = new NamedPipeRequestClient(pipeName);

        await firstClient.SendAsync(
            PipeEnvelope.Create("stall-handler", new { }),
            expectResponse: false,
            TimeSpan.FromSeconds(2));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        PipeEnvelope response = await SendStatusAsync(pipeName);
        releaseHandler.TrySetResult(null);

        Assert.Equal(PipeMessageKinds.StatusResponse, response.Kind);

        ValueTask<PipeEnvelope?> HandleAsync(
            PipeEnvelope request,
            CancellationToken cancellationToken)
        {
            if (request.Kind == "stall-handler")
            {
                handlerEntered.TrySetResult();
                // Deliberately ignore cancellation to prove the listener itself
                // enforces the bounded handler budget.
                return new ValueTask<PipeEnvelope?>(releaseHandler.Task);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PipeEnvelope?>(
                PipeEnvelope.Create(PipeMessageKinds.StatusResponse, new { host = "online" }));
        }
    }

    [Fact]
    public async Task NamedPipe_DisposeWhileClientStalls_CancelsListenerPromptly()
    {
        string pipeName = NewPipeName();
        var server = CreateStatusServer(
            pipeName,
            clientRequestTimeout: TimeSpan.FromSeconds(30));
        server.Start();
        await using var stalledClient = CreateRawClient(pipeName);
        await ConnectAsync(stalledClient);

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NamedPipe_DisposeWhileIdleAcceptIsPending_CancelsListenerPromptly()
    {
        string pipeName = NewPipeName();
        var server = CreateStatusServer(pipeName);
        server.Start();

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NamedPipe_DisposeImmediatelyAfterOneWayHandler_DoesNotSurfaceShutdownFailure()
    {
        string pipeName = NewPipeName();
        var handled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new NamedPipeMessageServer(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                handled.TrySetResult();
                return ValueTask.FromResult<PipeEnvelope?>(null);
            },
            pipeName);
        server.Start();
        var client = new NamedPipeRequestClient(pipeName);

        await client.SendAsync(
            PipeEnvelope.Create(PipeMessageKinds.HookEvent, new { value = "public" }),
            expectResponse: false,
            TimeSpan.FromSeconds(2));
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static NamedPipeMessageServer CreateStatusServer(
        string pipeName,
        TimeSpan? clientRequestTimeout = null,
        Action? onHandled = null)
    {
        return new NamedPipeMessageServer(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                onHandled?.Invoke();
                return ValueTask.FromResult<PipeEnvelope?>(
                    PipeEnvelope.Create(
                        PipeMessageKinds.StatusResponse,
                        new { host = request.Kind }));
            },
            pipeName,
            clientRequestTimeout);
    }

    private static async Task<PipeEnvelope> SendStatusAsync(string pipeName)
    {
        var client = new NamedPipeRequestClient(pipeName);
        PipeEnvelope? response = await client.SendAsync(
            PipeEnvelope.Create(PipeMessageKinds.StatusRequest, new { }),
            expectResponse: true,
            TimeSpan.FromSeconds(3));
        return Assert.IsType<PipeEnvelope>(response);
    }

    private static NamedPipeClientStream CreateRawClient(string pipeName)
    {
        return new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static async Task ConnectAsync(NamedPipeClientStream client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.ConnectAsync(timeout.Token);
    }

    private static string NewPipeName()
    {
        return $"AgentKick75.tests.{Guid.NewGuid():N}";
    }
}
