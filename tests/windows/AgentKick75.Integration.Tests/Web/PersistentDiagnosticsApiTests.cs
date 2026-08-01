// SPDX-License-Identifier: MIT
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentKick75.App.Diagnostics;
using AgentKick75.App.Lighting;
using AgentKick75.App.Web;
using AgentKick75.Core.State;

namespace AgentKick75.Integration.Tests.Web;

public sealed class PersistentDiagnosticsApiTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 8, 1, 10, 20, 30, TimeSpan.Zero);

    [Fact]
    public async Task Diagnostics_ValidLimit_ReturnsOnlyPageAllowlistWithoutSessionHash()
    {
        const string sessionHashSecret = "session-hash-must-not-escape";
        var log = new StubDiagnosticLog(
        [
            new SanitizedDiagnosticEntry(
                FixedTimestamp,
                SanitizedDiagnosticEventType.HookReceived,
                sessionHashSecret,
                TaskVisualState.RequiresInput,
                LatencyMilliseconds: 42,
                LightingTransportFailureKind.DeviceBusy,
                SanitizedDiagnosticCode.Succeeded),
            new SanitizedDiagnosticEntry(
                FixedTimestamp.AddSeconds(-1),
                SanitizedDiagnosticEventType.HostStarted,
                "another-secret"),
        ]);
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            new NoOpControlPlane(),
            CreateOptions(),
            diagnosticLog: log);
        using HttpClient client = CreateClient(server);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/diagnostics?limit=1");
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, log.LastMaximumEntries);
        JsonElement entry = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal(
            new[]
            {
                "code",
                "eventType",
                "latencyMilliseconds",
                "timestamp",
                "transportFailure",
                "visualState",
            },
            entry.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal("hookReceived", entry.GetProperty("eventType").GetString());
        Assert.Equal("requiresInput", entry.GetProperty("visualState").GetString());
        Assert.Equal(42, entry.GetProperty("latencyMilliseconds").GetInt64());
        Assert.Equal("deviceBusy", entry.GetProperty("transportFailure").GetString());
        Assert.Equal("succeeded", entry.GetProperty("code").GetString());
        Assert.DoesNotContain(sessionHashSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ControlPageOptions.TokenHeaderName, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnostics_UpperBound_NeverReturnsMoreThanOneHundredEntries()
    {
        SanitizedDiagnosticEntry[] entries = Enumerable.Range(0, 101)
            .Select(index => new SanitizedDiagnosticEntry(
                FixedTimestamp.AddMilliseconds(-index),
                SanitizedDiagnosticEventType.StateChanged,
                VisualState: TaskVisualState.Thinking))
            .ToArray();
        var log = new StubDiagnosticLog(entries);
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            new NoOpControlPlane(),
            CreateOptions(),
            diagnosticLog: log);
        using HttpClient client = CreateClient(server);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/diagnostics?limit=100");
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, log.LastMaximumEntries);
        Assert.Equal(100, document.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=not-a-number")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?limit=1&unexpected=prompt-secret")]
    public async Task Diagnostics_InvalidQuery_IsRejectedBeforeReader(
        string query)
    {
        var log = new StubDiagnosticLog([]);
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            new NoOpControlPlane(),
            CreateOptions(),
            diagnosticLog: log);
        using HttpClient client = CreateClient(server);

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/diagnostics{query}");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, log.ReadCallCount);
        Assert.DoesNotContain("prompt-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_NoReader_ReturnsSafeServiceUnavailable()
    {
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            new NoOpControlPlane(),
            CreateOptions());
        using HttpClient client = CreateClient(server);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/diagnostics?limit=10");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("path", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Page_PersistentDiagnosticsEntry_IsSeparateFromLiveMemoryList()
    {
        await using AgentKick75ControlServer server = await AgentKick75ControlServer.StartAsync(
            new NoOpControlPlane(),
            CreateOptions());
        using HttpClient client = CreateClient(server);

        string html = await client.GetStringAsync("/");
        string script = await client.GetStringAsync("/app.js");

        Assert.Contains("id=\"event-log\"", html, StringComparison.Ordinal);
        Assert.Contains("events · memory only", html, StringComparison.Ordinal);
        Assert.Contains("id=\"persisted-event-log\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"load-recent-diagnostics\"", html, StringComparison.Ordinal);
        Assert.Contains("Load recent logs", html, StringComparison.Ordinal);
        Assert.Contains("Loaded separately from live session events", html, StringComparison.Ordinal);
        Assert.Contains("HID descriptor version", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Firmware</dt>", html, StringComparison.Ordinal);
        Assert.Contains("/api/v1/diagnostics?limit=50", script, StringComparison.Ordinal);
        Assert.Contains("persisted-event-log", script, StringComparison.Ordinal);
    }

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

    private sealed class StubDiagnosticLog(
        IReadOnlyList<SanitizedDiagnosticEntry> entries) : ISanitizedDiagnosticLog
    {
        public int ReadCallCount { get; private set; }

        public int? LastMaximumEntries { get; private set; }

        public ValueTask WriteAsync(
            SanitizedDiagnosticEventType eventType,
            string? sessionId = null,
            TaskVisualState? visualState = null,
            long? latencyMilliseconds = null,
            LightingTransportFailureKind? transportFailure = null,
            SanitizedDiagnosticCode? code = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The read-only endpoint must never write diagnostics.");
        }

        public ValueTask<IReadOnlyList<SanitizedDiagnosticEntry>> ReadRecentAsync(
            int maxEntries,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            LastMaximumEntries = maxEntries;
            return ValueTask.FromResult(entries);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpControlPlane : IControlPlane
    {
        private static readonly ControlStatusDto Status = new(
            "Idle",
            ActiveSessionCount: 0,
            LastEventAt: null,
            IsPaused: false,
            IsPreviewActive: false,
            HookStatus: "Unconfirmed",
            Device: new DeviceDiagnosticsDto(
                "Unknown device",
                "None",
                "NotDetected",
                "NotDetected",
                "Unknown",
                FirmwareVersion: null,
                DeviceIdentity: null,
                LastErrorCode: null));

        private static readonly ControlSettingsDto Settings = new(
            new ControlLightStyleDto("#006BFF", 100),
            new ControlLightStyleDto("#FFB400", 100),
            new ControlLightStyleDto("#00FF00", 100),
            CompleteHoldSeconds: 10,
            LaunchAtSignIn: false);

        public ValueTask<ControlStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Status);

        public ValueTask<ControlSettingsDto> GetSettingsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Settings);

        public ValueTask<ControlSettingsDto> ApplySettingsAsync(
            ControlSettingsDto settings,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(settings);

        public ValueTask PreviewAsync(
            ControlPreviewState state,
            TimeSpan duration,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<ControlStatusDto> SetPausedAsync(
            bool isPaused,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Status with { IsPaused = isPaused });

        public ValueTask RestoreOriginalLightingAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<HardwareTestResultDto> RunHardwareTestAsync(
            HardwareTestRequestDto request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new HardwareTestResultDto(
                false,
                "unavailable",
                "Hardware tests are unavailable in this fixture.",
                request.Transport));

        public IAsyncEnumerable<ControlEventDto> WatchEventsAsync(
            CancellationToken cancellationToken) =>
            EmptyEventsAsync(cancellationToken);

        private static async IAsyncEnumerable<ControlEventDto> EmptyEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
