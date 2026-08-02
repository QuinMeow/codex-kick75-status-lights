// SPDX-License-Identifier: MIT
using System.IO.Pipes;
using AgentKick75.App.Infrastructure;

namespace AgentKick75.App.Ipc;

public interface IPipeRequestClient
{
    ValueTask<PipeEnvelope?> SendAsync(
        PipeEnvelope request,
        bool expectResponse,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class NamedPipeRequestClient : IPipeRequestClient
{
    private readonly string pipeName;

    public NamedPipeRequestClient(string? pipeName = null)
    {
        this.pipeName = pipeName ?? UserScope.PipeName;
    }

    public async ValueTask<PipeEnvelope?> SendAsync(
        PipeEnvelope request,
        bool expectResponse,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        await PipeFraming.WriteAsync(client, request, timeoutSource.Token).ConfigureAwait(false);
        if (!expectResponse)
        {
            return null;
        }

        return await PipeFraming.ReadAsync(client, timeoutSource.Token).ConfigureAwait(false);
    }
}

public sealed class NamedPipeMessageServer : IAsyncDisposable
{
    public static TimeSpan DefaultClientRequestTimeout { get; } = TimeSpan.FromSeconds(1);

    public static TimeSpan MaximumClientRequestTimeout { get; } = TimeSpan.FromMinutes(1);

    private readonly string pipeName;
    private readonly Func<PipeEnvelope, CancellationToken, ValueTask<PipeEnvelope?>> handler;
    private readonly Func<PipeEnvelope, PipeEnvelope, CancellationToken, ValueTask>? responseFlushed;
    private readonly TimeSpan clientRequestTimeout;
    private readonly CancellationTokenSource stopSource = new();
    private Task? listeningTask;

    public NamedPipeMessageServer(
        Func<PipeEnvelope, CancellationToken, ValueTask<PipeEnvelope?>> handler,
        string? pipeName = null,
        TimeSpan? clientRequestTimeout = null,
        Func<PipeEnvelope, PipeEnvelope, CancellationToken, ValueTask>? responseFlushed = null)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.pipeName = pipeName ?? UserScope.PipeName;
        this.responseFlushed = responseFlushed;
        this.clientRequestTimeout = clientRequestTimeout ?? DefaultClientRequestTimeout;
        if (this.clientRequestTimeout <= TimeSpan.Zero ||
            this.clientRequestTimeout > MaximumClientRequestTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientRequestTimeout),
                "The client request timeout must be between 1 tick and 60 seconds.");
        }
    }

    public void Start()
    {
        if (listeningTask is not null)
        {
            throw new InvalidOperationException("The pipe server has already been started.");
        }

        listeningTask = ListenAsync(stopSource.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await stopSource.CancelAsync().ConfigureAwait(false);
        if (listeningTask is not null)
        {
            try
            {
                await listeningTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
            }
        }

        stopSource.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream server = CreateServer();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var clientTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                clientTimeoutSource.CancelAfter(clientRequestTimeout);
                try
                {
                    await HandleConnectionAsync(server, clientTimeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A connected same-user client must not monopolize the listener by
                    // stalling a frame or handler. Disposing this server instance lets
                    // the next loop iteration accept a fresh client.
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A fail-open hook is allowed to disconnect immediately after its
                    // one-way write. One broken client must not stop the Host listener.
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        // CurrentUserOnly asks the runtime to create a pipe ACL restricted to the
        // process owner. The SID-derived pipe name is defense in depth, not the ACL.
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            PipeFraming.MaximumMessageBytes,
            PipeFraming.MaximumMessageBytes);
    }

    private async ValueTask HandleConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        PipeEnvelope? request = null;
        PipeEnvelope? response;
        try
        {
            request = await PipeFraming.ReadAsync(server, cancellationToken).ConfigureAwait(false);
            response = await handler(request, cancellationToken)
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PipeProtocolException)
        {
            response = PipeEnvelope.Create(PipeMessageKinds.Rejected, new { reason = "invalid-envelope" });
        }
        catch (EndOfStreamException)
        {
            return;
        }

        if (response is not null && server.IsConnected)
        {
            await PipeFraming.WriteAsync(server, response, cancellationToken).ConfigureAwait(false);
            if (request is not null && responseFlushed is not null)
            {
                await responseFlushed(request, response, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
