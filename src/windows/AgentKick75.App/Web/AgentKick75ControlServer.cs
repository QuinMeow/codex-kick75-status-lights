// SPDX-License-Identifier: MIT
using System.Net;
using AgentKick75.App.Diagnostics;
using AgentKick75.App.Hooks;
using AgentKick75.App.Ipc;
using AgentKick75.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentKick75.App.Web;

/// <summary>
/// Owns an isolated Kestrel instance bound to a random IPv4 loopback port.
/// </summary>
public sealed class AgentKick75ControlServer : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly string? hookEndpointPath;
    private int disposeState;

    private AgentKick75ControlServer(
        WebApplication application,
        Uri baseUri,
        string? hookEndpointPath)
    {
        this.application = application;
        this.hookEndpointPath = hookEndpointPath;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static async Task<AgentKick75ControlServer> StartAsync(
        IControlPlane controlPlane,
        ControlPageOptions? options = null,
        ISanitizedDiagnosticLog? diagnosticLog = null,
        Func<PipeEnvelope, CancellationToken, ValueTask<PipeEnvelope?>>? hookHandler = null,
        string? hookEndpointPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        options ??= ControlPageOptions.CreateWithRandomToken();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(AgentKick75ControlServer).Assembly.FullName,
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            kestrel.Limits.MaxConcurrentConnections = 32;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            kestrel.Limits.MaxRequestBufferSize = 64 * 1024;
            kestrel.Limits.MaxRequestHeaderCount = 32;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxRequestLineSize = 4 * 1024;
            kestrel.Limits.MinRequestBodyDataRate = new MinDataRate(
                bytesPerSecond: 1024,
                gracePeriod: TimeSpan.FromSeconds(5));
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });

        WebApplication application = builder.Build();
        application.UseControlPageSecurity(options);
        application.MapAgentKick75ControlPage(controlPlane, options, diagnosticLog, hookHandler);

        try
        {
            await application.StartAsync(cancellationToken);
            IServer server = application.Services.GetRequiredService<IServer>();
            IServerAddressesFeature? addresses = server.Features.Get<IServerAddressesFeature>();
            string address = addresses?.Addresses.SingleOrDefault()
                ?? throw new InvalidOperationException("Kestrel did not publish its loopback address.");

            var baseUri = new Uri(address.EndsWith('/') ? address : $"{address}/");
            if (!IPAddress.TryParse(baseUri.Host, out IPAddress? boundAddress) ||
                !IPAddress.IsLoopback(boundAddress) ||
                baseUri.Port == 0)
            {
                throw new InvalidOperationException("The control page did not bind to a random loopback port.");
            }

            string? publishedEndpointPath = null;
            if (hookHandler is not null && !string.IsNullOrWhiteSpace(hookEndpointPath))
            {
                publishedEndpointPath = Path.GetFullPath(hookEndpointPath);
                await AtomicFile.WriteUtf8Async(
                    publishedEndpointPath,
                    LoopbackHookEndpoint.Create(baseUri, options.HookToken).Serialize(),
                    cancellationToken);
            }

            return new AgentKick75ControlServer(application, baseUri, publishedEndpointPath);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await application.StopAsync(timeout.Token);
        }
        finally
        {
            try
            {
                await application.DisposeAsync();
            }
            finally
            {
                if (hookEndpointPath is not null)
                {
                    File.Delete(hookEndpointPath);
                }
            }
        }
    }
}
