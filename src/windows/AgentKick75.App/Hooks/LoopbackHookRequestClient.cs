// SPDX-License-Identifier: MIT
using System.Net.Http.Json;
using AgentKick75.App.Ipc;
using AgentKick75.App.Web;

namespace AgentKick75.App.Hooks;

public sealed class LoopbackHookRequestClient : IPipeRequestClient
{
    private static readonly HttpClient Client = CreateClient();
    private readonly string endpointPath;

    public LoopbackHookRequestClient(string? endpointPath = null)
    {
        this.endpointPath = endpointPath ?? LoopbackHookEndpoint.DefaultPath;
    }

    public async ValueTask<PipeEnvelope?> SendAsync(
        PipeEnvelope request,
        bool expectResponse,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (expectResponse || request.Kind != PipeMessageKinds.HookEvent)
        {
            throw new InvalidOperationException("The loopback Hook client only sends one-way Hook events.");
        }

        if (!LoopbackHookEndpoint.TryLoad(endpointPath, out LoopbackHookEndpoint? endpoint))
        {
            throw new IOException("The AgentKick75 Host endpoint is unavailable.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(endpoint!.BaseUri), "api/v1/hooks/codex"))
        {
            Content = JsonContent.Create(request, options: PipeJson.Options),
        };
        message.Headers.Add(ControlPageOptions.HookTokenHeaderName, endpoint.Token);

        using HttpResponseMessage response = await Client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutSource.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return null;
    }

    private static HttpClient CreateClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
