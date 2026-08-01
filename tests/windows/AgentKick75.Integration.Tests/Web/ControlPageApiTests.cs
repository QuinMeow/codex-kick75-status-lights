// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using AgentKick75.App.Hooks;
using AgentKick75.App.Ipc;
using AgentKick75.App.Web;

namespace AgentKick75.Integration.Tests.Web;

public sealed class ControlPageApiTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 7, 31, 10, 24, 31, TimeSpan.Zero);

    [Fact]
    public async Task HookIngress_PublishedLoopbackEndpoint_DeliversAuthenticatedEnvelopeAndCleansUp()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"AgentKick75-hook-{Guid.NewGuid():N}");
        string endpointPath = Path.Combine(directory, "hook-endpoint.json");
        Directory.CreateDirectory(directory);
        var received = new TaskCompletionSource<PipeEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        AgentKick75ControlServer? server = null;

        try
        {
            server = await AgentKick75ControlServer.StartAsync(
                new FakeControlPlane(),
                CreateOptions(),
                hookHandler: (envelope, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    received.TrySetResult(envelope);
                    return ValueTask.FromResult<PipeEnvelope?>(
                        PipeEnvelope.Create(PipeMessageKinds.Accepted, new { }));
                },
                hookEndpointPath: endpointPath);
            Assert.True(File.Exists(endpointPath));

            PipeEnvelope envelope = PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
            {
                kind = 0,
                sessionId = "session-1",
                turnId = "turn-1",
                toolName = (string?)null,
                toolUseId = (string?)null,
            });
            using HttpClient unauthorizedClient = CreateClient(server);
            using HttpResponseMessage unauthorized = await unauthorizedClient.PostAsJsonAsync(
                "/api/v1/hooks/codex",
                envelope);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);

            var client = new LoopbackHookRequestClient(endpointPath);
            await client.SendAsync(
                envelope,
                expectResponse: false,
                TimeSpan.FromSeconds(2));

            PipeEnvelope delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(PipeMessageKinds.HookEvent, delivered.Kind);
            Assert.Equal("session-1", delivered.Payload.GetProperty("sessionId").GetString());
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            Assert.False(File.Exists(endpointPath));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_DefaultConfiguration_BindsRandomIpv4LoopbackAndServesEmbeddedAssets()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);

        Assert.Equal(IPAddress.Loopback.ToString(), server.BaseUri.Host);
        Assert.InRange(server.BaseUri.Port, 1, ushort.MaxValue);

        using HttpResponseMessage response = await client.GetAsync("/");
        using HttpResponseMessage styleResponse = await client.GetAsync("/styles.css");
        using HttpResponseMessage scriptResponse = await client.GetAsync("/app.js");
        string html = await response.Content.ReadAsStringAsync();
        string styles = await styleResponse.Content.ReadAsStringAsync();
        string javaScript = await scriptResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, styleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, scriptResponse.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/css", styleResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/javascript", scriptResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore is true);
        Assert.True(styleResponse.Headers.CacheControl?.NoStore is true);
        Assert.True(scriptResponse.Headers.CacheControl?.NoStore is true);
        Assert.Contains("AgentKick75", html, StringComparison.Ordinal);
        Assert.Contains("五段侧灯预览", html, StringComparison.Ordinal);
        Assert.Contains("id=\"hardware-test-button\" type=\"button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"install-hooks-button\" type=\"button\"", html, StringComparison.Ordinal);
        Assert.Contains("首次启动会自动安装", html, StringComparison.Ordinal);
        Assert.Contains("/api/v1/hooks/install", javaScript, StringComparison.Ordinal);
        Assert.Contains("id=\"hardware-test-enabled\" type=\"checkbox\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"hardware-test-button\" type=\"button\" disabled", html, StringComparison.Ordinal);
        Assert.Contains("id=\"session-diagnostics-enabled\" type=\"checkbox\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"saved-diagnostics-enabled\" type=\"checkbox\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"clear-session-log\" type=\"button\" disabled", html, StringComparison.Ordinal);
        Assert.Contains("id=\"load-recent-diagnostics\" type=\"button\" disabled", html, StringComparison.Ordinal);
        Assert.Contains("id=\"baseline-recovery\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"baseline-recovery-confirmation\" type=\"checkbox\" autocomplete=\"off\"", html, StringComparison.Ordinal);
        Assert.Contains("放弃旧基线接管", html, StringComparison.Ordinal);
        Assert.Contains("会话诊断", html, StringComparison.Ordinal);
        Assert.Contains("仅内存", html, StringComparison.Ordinal);
        Assert.Contains("M4 安装后生效", html, StringComparison.Ordinal);
        Assert.Contains("设备标识", html, StringComparison.Ordinal);
        Assert.Contains("描述符 / 接口指纹", html, StringComparison.Ordinal);
        Assert.Contains("id=\"interface-fingerprint\">未知", html, StringComparison.Ordinal);
        Assert.Contains("value=\"dongle\" disabled", html, StringComparison.Ordinal);
        Assert.Contains("Dongle（仅诊断）", html, StringComparison.Ordinal);
        Assert.Contains(".hardware-panel", styles, StringComparison.Ordinal);
        Assert.Contains(".baseline-recovery", styles, StringComparison.Ordinal);
        Assert.Contains(".session-diagnostics", styles, StringComparison.Ordinal);
        Assert.Contains(options.WriteToken, html, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlPageAssetsTokenPlaceholder, html, StringComparison.Ordinal);
        Assert.DoesNotContain(options.WriteToken, javaScript, StringComparison.Ordinal);
        Assert.Contains("X-AgentKick75-Token", javaScript, StringComparison.Ordinal);
        Assert.Contains("safeDiagnosticToken", javaScript, StringComparison.Ordinal);
        Assert.Contains("event.diagnosticCode", javaScript, StringComparison.Ordinal);
        Assert.Contains("sessionDiagnosticStates.has", javaScript, StringComparison.Ordinal);
        Assert.Contains("sessionDiagnosticsEnabled.checked", javaScript, StringComparison.Ordinal);
        Assert.Contains("document.getElementById(\"event-log\").replaceChildren()", javaScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings saved and applied.", javaScript, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", javaScript, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", javaScript, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"pageshow\", resetBaselineRecoveryConfirmation)", javaScript, StringComparison.Ordinal);
        Assert.Contains("/api/v1/baseline-recovery/abandon", javaScript, StringComparison.Ordinal);
        Assert.Contains("device.interfaceFingerprint || \"未知\"", javaScript, StringComparison.Ordinal);
        Assert.Contains("setText(\"live-state\", deviceConnectionLabel(device))", javaScript, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task PutSettings_MissingOriginOrToken_RejectsRequestWithoutApplying()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);

        using var missingOrigin = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(controlPlane.Settings),
        };
        missingOrigin.Headers.Add(ControlPageOptions.TokenHeaderName, options.WriteToken);
        using HttpResponseMessage missingOriginResponse = await client.SendAsync(missingOrigin);

        using HttpRequestMessage missingToken = CreateWriteRequest(
            server,
            HttpMethod.Put,
            "/api/v1/settings",
            controlPlane.Settings,
            token: null);
        using HttpResponseMessage missingTokenResponse = await client.SendAsync(missingToken);

        using HttpRequestMessage badOrigin = CreateWriteRequest(
            server,
            HttpMethod.Put,
            "/api/v1/settings",
            controlPlane.Settings,
            options.WriteToken,
            origin: "https://example.test");
        using HttpResponseMessage badOriginResponse = await client.SendAsync(badOrigin);

        Assert.Equal(HttpStatusCode.Forbidden, missingOriginResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missingTokenResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, badOriginResponse.StatusCode);
        Assert.Equal(0, controlPlane.ApplySettingsCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task PutSettings_MissingOrNonSameOriginFetchMetadata_RejectsRequest(
        string? fetchSite)
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(controlPlane.Settings),
        };
        request.Headers.Add("Origin", server.BaseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.Add(ControlPageOptions.TokenHeaderName, options.WriteToken);
        if (fetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, controlPlane.ApplySettingsCallCount);
    }

    [Fact]
    public async Task PutSettings_ValidRequest_NormalizesAndPersistsBeforeResponding()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);
        var requested = controlPlane.Settings with
        {
            Thinking = new ControlLightStyleDto("  #a1b2c3  ", 72),
            CompleteHoldSeconds = 14,
            LaunchAtSignIn = true,
        };

        using HttpRequestMessage request = CreateWriteRequest(
            server,
            HttpMethod.Put,
            "/api/v1/settings",
            requested,
            options.WriteToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        ControlSettingsDto? applied = await response.Content.ReadFromJsonAsync<ControlSettingsDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(applied);
        Assert.Equal("#A1B2C3", applied.Thinking.Color);
        Assert.Equal(72, applied.Thinking.Brightness);
        Assert.Equal(14, applied.CompleteHoldSeconds);
        Assert.True(applied.LaunchAtSignIn);
        Assert.Equal(1, controlPlane.ApplySettingsCallCount);
        Assert.Equal(applied, controlPlane.Settings);
    }

    [Fact]
    public async Task PutSettings_InvalidColor_RejectsBeforeControlPlane()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);
        ControlSettingsDto invalid = controlPlane.Settings with
        {
            RequiresInput = new ControlLightStyleDto("amber", 100),
        };

        using HttpRequestMessage request = CreateWriteRequest(
            server,
            HttpMethod.Put,
            "/api/v1/settings",
            invalid,
            options.WriteToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, controlPlane.ApplySettingsCallCount);
    }

    [Fact]
    public async Task PostPreview_ValidState_AlwaysUsesThreeSecondRestoreWindow()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);

        using HttpRequestMessage request = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/preview/requires-input",
            new { },
            options.WriteToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ControlPreviewState.RequiresInput, controlPlane.LastPreviewState);
        Assert.Equal(TimeSpan.FromSeconds(3), controlPlane.LastPreviewDuration);
    }

    [Fact]
    public async Task StatusPauseAndRestore_ValidRequests_InvokeControlPlane()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);

        ControlStatusDto? initial = await client.GetFromJsonAsync<ControlStatusDto>("/api/v1/status");

        using HttpRequestMessage pauseRequest = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/pause",
            new PauseRequestDto(true),
            options.WriteToken);
        using HttpResponseMessage pauseResponse = await client.SendAsync(pauseRequest);
        ControlStatusDto? paused = await pauseResponse.Content.ReadFromJsonAsync<ControlStatusDto>();

        using HttpRequestMessage restoreRequest = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/restore",
            new { },
            options.WriteToken);
        using HttpResponseMessage restoreResponse = await client.SendAsync(restoreRequest);

        Assert.NotNull(initial);
        Assert.False(initial.IsPaused);
        Assert.Equal("19F5:1026", initial.Device.DeviceIdentity);
        Assert.Equal("mock-interface-fingerprint", initial.Device.InterfaceFingerprint);
        Assert.Equal("19F5:1026", initial.BaselineRecovery?.BaselineDeviceIdentity);
        Assert.Equal("19F5:1026", initial.BaselineRecovery?.ObservedDeviceIdentity);
        Assert.Equal(HttpStatusCode.OK, pauseResponse.StatusCode);
        Assert.NotNull(paused);
        Assert.True(paused.IsPaused);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        Assert.Equal(1, controlPlane.RestoreCallCount);
    }

    [Fact]
    public async Task PostHardwareTest_NormalizesTransport()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);

        using HttpRequestMessage request = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/hardware-test",
            new HardwareTestRequestDto(" USB "),
            options.WriteToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, controlPlane.HardwareTestCallCount);
        Assert.Equal("usb", controlPlane.LastHardwareTestRequest?.Transport);
    }

    [Fact]
    public async Task PostHookInstall_InvokesControlPlane()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);

        using HttpRequestMessage request = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/hooks/install",
            new { },
            options.WriteToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        HookInstallationResultDto? result = await response.Content
            .ReadFromJsonAsync<HookInstallationResultDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, controlPlane.HookInstallCallCount);
        Assert.True(result?.Succeeded);
        Assert.Equal(6, result?.RegisteredHandlerCount);
    }

    [Fact]
    public async Task PostBaselineRecoveryAbandon_RequiresTokenOriginFetchAndExplicitConfirmation()
    {
        const string confirmationId = "00112233445566778899aabbccddeeff";
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);
        var confirmed = new BaselineRecoveryDispositionRequestDto(confirmationId, true);

        using HttpRequestMessage missingToken = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/baseline-recovery/abandon",
            confirmed,
            token: null);
        using HttpResponseMessage missingTokenResponse = await client.SendAsync(missingToken);

        using HttpRequestMessage badOrigin = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/baseline-recovery/abandon",
            confirmed,
            options.WriteToken,
            origin: "https://example.test");
        using HttpResponseMessage badOriginResponse = await client.SendAsync(badOrigin);

        using var missingFetch = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/baseline-recovery/abandon")
        {
            Content = JsonContent.Create(confirmed),
        };
        missingFetch.Headers.Add("Origin", server.BaseUri.GetLeftPart(UriPartial.Authority));
        missingFetch.Headers.Add(ControlPageOptions.TokenHeaderName, options.WriteToken);
        using HttpResponseMessage missingFetchResponse = await client.SendAsync(missingFetch);

        using HttpRequestMessage unconfirmed = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/baseline-recovery/abandon",
            confirmed with { Confirmed = false },
            options.WriteToken);
        using HttpResponseMessage unconfirmedResponse = await client.SendAsync(unconfirmed);

        using HttpRequestMessage invalidChallenge = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/baseline-recovery/abandon",
            confirmed with { ConfirmationId = "stale" },
            options.WriteToken);
        using HttpResponseMessage invalidChallengeResponse = await client.SendAsync(invalidChallenge);

        using HttpRequestMessage valid = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/baseline-recovery/abandon",
            confirmed,
            options.WriteToken);
        using HttpResponseMessage validResponse = await client.SendAsync(valid);
        BaselineRecoveryDispositionDto? result = await validResponse.Content
            .ReadFromJsonAsync<BaselineRecoveryDispositionDto>();

        Assert.Equal(HttpStatusCode.Forbidden, missingTokenResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, badOriginResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missingFetchResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidChallengeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.True(result?.Succeeded);
        Assert.Equal(1, controlPlane.BaselineRecoveryCallCount);
        Assert.Equal(confirmed, controlPlane.LastBaselineRecoveryRequest);
    }

    [Fact]
    public async Task PostBaselineRecoveryAbandon_StaleOrMissingMismatch_ReturnsConflict()
    {
        var controlPlane = new FakeControlPlane
        {
            BaselineRecoveryResult = new BaselineRecoveryDispositionDto(
                false,
                "NoPendingMismatch",
                "There is no currently observed baseline identity mismatch to abandon."),
        };
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);
        using HttpRequestMessage request = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/baseline-recovery/abandon",
            new BaselineRecoveryDispositionRequestDto(
                "00112233445566778899aabbccddeeff",
                true),
            options.WriteToken);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, controlPlane.BaselineRecoveryCallCount);
    }

    [Fact]
    public async Task Events_ConnectionAndControlEvent_AreStreamedAsSse()
    {
        var controlPlane = new FakeControlPlane();
        controlPlane.Events =
        [
            new ControlEventDto(
                17,
                "devicechanged",
                FixedTimestamp,
                controlPlane.Status,
                DiagnosticCode: "DeviceBusy"),
        ];
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            CreateOptions());
        using HttpClient client = CreateClient(server);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/events",
            HttpCompletionOption.ResponseHeadersRead);
        string stream = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("retry: 3000", stream, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"connected\"", stream, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"devicechanged\"", stream, StringComparison.Ordinal);
        Assert.Contains("\"diagnosticCode\":\"DeviceBusy\"", stream, StringComparison.Ordinal);
        Assert.Contains("\"status\":", stream, StringComparison.Ordinal);
        Assert.Contains("\"deviceIdentity\":\"19F5:1026\"", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("private-hid-path", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("old-baseline-private-path", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("observed-device-private-path", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", stream, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("toolInput", stream, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", stream, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Events_SubscriptionStartsBeforeConnectedFrame()
    {
        var controlPlane = new FakeControlPlane
        {
            BlockWatchUntilReleased = true,
        };
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            CreateOptions());
        using HttpClient client = CreateClient(server);

        Task<HttpResponseMessage> responseTask = client.GetAsync(
            "/api/v1/events",
            HttpCompletionOption.ResponseHeadersRead);
        await controlPlane.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        bool responseStartedBeforeSubscription = responseTask.IsCompleted;
        controlPlane.ReleaseWatch.TrySetResult();

        using HttpResponseMessage response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
        string stream = await response.Content.ReadAsStringAsync();
        Assert.False(responseStartedBeforeSubscription);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"kind\":\"connected\"", stream, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_FifthConcurrentStreamIsRejectedAndCompletedStreamReleasesSlot()
    {
        var controlPlane = new FakeControlPlane
        {
            HoldEventStreamOpen = true,
        };
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            CreateOptions());
        var clients = new List<HttpClient>();
        var responses = new List<HttpResponseMessage>();

        try
        {
            for (int index = 0; index < 4; index++)
            {
                HttpClient client = CreateClient(server);
                clients.Add(client);
                responses.Add(await client.GetAsync(
                    "/api/v1/events",
                    HttpCompletionOption.ResponseHeadersRead));
            }

            await WaitForAsync(
                () => controlPlane.ActiveEventStreamCount == 4,
                TimeSpan.FromSeconds(2));

            using HttpClient rejectedClient = CreateClient(server);
            using HttpResponseMessage rejected = await rejectedClient.GetAsync(
                "/api/v1/events",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

            controlPlane.CompleteOneEventStream();
            string completedStream = await responses[0].Content
                .ReadAsStringAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("\"kind\":\"connected\"", completedStream, StringComparison.Ordinal);
            Assert.Equal(3, controlPlane.ActiveEventStreamCount);

            using HttpClient replacementClient = CreateClient(server);
            using HttpResponseMessage replacement = await replacementClient.GetAsync(
                "/api/v1/events",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
            await WaitForAsync(
                () => controlPlane.ActiveEventStreamCount == 4,
                TimeSpan.FromSeconds(2));
            controlPlane.CompleteAllEventStreams();
            await WaitForAsync(
                () => controlPlane.ActiveEventStreamCount == 0,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }

            foreach (HttpClient client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Request_MaliciousHostOrCorsPreflight_IsRejectedWithoutCorsHeaders()
    {
        var controlPlane = new FakeControlPlane();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            CreateOptions());
        using HttpClient client = CreateClient(server);

        using var maliciousHost = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        maliciousHost.Headers.Host = $"localhost:{server.BaseUri.Port}";
        using HttpResponseMessage maliciousHostResponse = await client.SendAsync(maliciousHost);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/settings");
        preflight.Headers.Add("Origin", "https://example.test");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        using HttpResponseMessage preflightResponse = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.Forbidden, maliciousHostResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preflightResponse.StatusCode);
        Assert.False(maliciousHostResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task WriteRequest_BodyLargerThan64KiB_IsRejected()
    {
        var controlPlane = new FakeControlPlane();
        ControlPageOptions options = CreateOptions();
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            controlPlane,
            options);
        using HttpClient client = CreateClient(server);
        string oversizedPadding = new('x', (64 * 1024) + 1024);

        using HttpRequestMessage request = CreateWriteRequest(
            server,
            HttpMethod.Post,
            "/api/v1/pause",
            new { paused = true, padding = oversizedPadding },
            options.WriteToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private const string ControlPageAssetsTokenPlaceholder = "__AGENT_KICK75_WRITE_TOKEN__";

    private static ControlPageOptions CreateOptions()
    {
        return ControlPageOptions.FromHostInstanceToken(new string('A', 43));
    }

    private static HttpClient CreateClient(AgentKick75ControlServer server)
    {
        return new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
        })
        {
            BaseAddress = server.BaseUri,
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static HttpRequestMessage CreateWriteRequest(
        AgentKick75ControlServer server,
        HttpMethod method,
        string path,
        object body,
        string? token,
        string? origin = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Origin", origin ?? server.BaseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        if (token is not null)
        {
            request.Headers.Add(ControlPageOptions.TokenHeaderName, token);
        }

        return request;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
        }
    }

    private sealed class FakeControlPlane : IControlPlane
    {
        private readonly ConcurrentQueue<TaskCompletionSource> eventStreamCompletions = new();
        private int activeEventStreamCount;

        public ControlStatusDto Status { get; private set; } = new(
            "Thinking",
            2,
            FixedTimestamp,
            IsPaused: false,
            IsPreviewActive: false,
            HookStatus: "Enabled",
            Device: new DeviceDiagnosticsDto(
                "Kick75 NuPhyIO",
                "USB",
                "Connected",
                "Ready",
                "Unverified",
                FirmwareVersion: null,
                DeviceIdentity: "19F5:1026/path=private-hid-path",
                LastErrorCode: null,
                InterfaceFingerprint: "mock-interface-fingerprint"),
            BaselineRecovery: new BaselineRecoveryRiskDto(
                "DeviceIdentityMismatch",
                "00112233445566778899aabbccddeeff",
                "Recovery blocked because the observed device does not match the saved baseline owner.",
                "19F5:1026/path=old-baseline-private-path",
                "19F5:1026/path=observed-device-private-path"));

        public ControlSettingsDto Settings { get; private set; } = new(
            new ControlLightStyleDto("#006BFF", 100),
            new ControlLightStyleDto("#FFB400", 100),
            new ControlLightStyleDto("#00FF00", 100),
            CompleteHoldSeconds: 10,
            LaunchAtSignIn: false);

        public IReadOnlyList<ControlEventDto> Events { get; set; } = [];

        public bool BlockWatchUntilReleased { get; set; }

        public bool HoldEventStreamOpen { get; set; }

        public TaskCompletionSource WatchStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseWatch { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveEventStreamCount => Volatile.Read(ref activeEventStreamCount);

        public void CompleteOneEventStream()
        {
            if (!eventStreamCompletions.TryDequeue(out TaskCompletionSource? completion))
            {
                throw new InvalidOperationException("No event stream is waiting for completion.");
            }

            completion.TrySetResult();
        }

        public void CompleteAllEventStreams()
        {
            while (eventStreamCompletions.TryDequeue(out TaskCompletionSource? completion))
            {
                completion.TrySetResult();
            }
        }

        public int ApplySettingsCallCount { get; private set; }

        public int HardwareTestCallCount { get; private set; }

        public int HookInstallCallCount { get; private set; }

        public int BaselineRecoveryCallCount { get; private set; }

        public int RestoreCallCount { get; private set; }

        public ControlPreviewState? LastPreviewState { get; private set; }

        public TimeSpan? LastPreviewDuration { get; private set; }

        public HardwareTestRequestDto? LastHardwareTestRequest { get; private set; }

        public BaselineRecoveryDispositionRequestDto? LastBaselineRecoveryRequest { get; private set; }

        public BaselineRecoveryDispositionDto BaselineRecoveryResult { get; set; } = new(
            true,
            "Released",
            "The old baseline ownership was abandoned without writing baseline bytes.");

        public ValueTask<ControlStatusDto> GetStatusAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Status);
        }

        public ValueTask<ControlSettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Settings);
        }

        public ValueTask<ControlSettingsDto> ApplySettingsAsync(
            ControlSettingsDto settings,
            CancellationToken cancellationToken)
        {
            ApplySettingsCallCount++;
            Settings = settings;
            return ValueTask.FromResult(Settings);
        }

        public ValueTask PreviewAsync(
            ControlPreviewState state,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            LastPreviewState = state;
            LastPreviewDuration = duration;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ControlStatusDto> SetPausedAsync(
            bool isPaused,
            CancellationToken cancellationToken)
        {
            Status = Status with { IsPaused = isPaused };
            return ValueTask.FromResult(Status);
        }

        public ValueTask RestoreOriginalLightingAsync(CancellationToken cancellationToken)
        {
            RestoreCallCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<HardwareTestResultDto> RunHardwareTestAsync(
            HardwareTestRequestDto request,
            CancellationToken cancellationToken)
        {
            HardwareTestCallCount++;
            LastHardwareTestRequest = request;
            return ValueTask.FromResult(new HardwareTestResultDto(
                Succeeded: true,
                Status: "passed",
                Message: "Baseline restored.",
                Transport: request.Transport));
        }

        public ValueTask<HookInstallationResultDto> InstallCodexHooksAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HookInstallCallCount++;
            return ValueTask.FromResult(new HookInstallationResultDto(
                true,
                false,
                6,
                "installed",
                "Codex Hook 已安装，无需修改。"));
        }

        public ValueTask<BaselineRecoveryDispositionDto> AbandonMismatchedBaselineAsync(
            BaselineRecoveryDispositionRequestDto request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BaselineRecoveryCallCount++;
            LastBaselineRecoveryRequest = request;
            return ValueTask.FromResult(BaselineRecoveryResult);
        }

        public IAsyncEnumerable<ControlEventDto> WatchEventsAsync(
            CancellationToken cancellationToken)
        {
            WatchStarted.TrySetResult();
            if (BlockWatchUntilReleased)
            {
                ReleaseWatch.Task.GetAwaiter().GetResult();
            }

            return EnumerateEventsAsync(cancellationToken);
        }

        private async IAsyncEnumerable<ControlEventDto> EnumerateEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            TaskCompletionSource? streamCompletion = null;
            if (HoldEventStreamOpen)
            {
                streamCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                eventStreamCompletions.Enqueue(streamCompletion);
            }

            Interlocked.Increment(ref activeEventStreamCount);
            try
            {
                await Task.Yield();
                foreach (ControlEventDto controlEvent in Events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return controlEvent;
                }

                if (HoldEventStreamOpen)
                {
                    await streamCompletion!.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeEventStreamCount);
            }
        }
    }
}
