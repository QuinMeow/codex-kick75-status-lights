// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using AgentKick75.App.Hooks;
using AgentKick75.App.Ipc;
using AgentKick75.Core.Hooks;

namespace AgentKick75.Integration.Tests;

public sealed class HookCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ValidHook_SendsOnlyNormalizedFieldsAndWritesNothing()
    {
        const string secret = "do not transmit this prompt";
        string input = $$"""
            {
              "hook_event_name": "UserPromptSubmit",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "prompt": "{{secret}}",
              "tool_input": { "also": "private" }
            }
            """;
        var client = new CapturingPipeClient();

        int exitCode = await HookCommand.ExecuteAsync(new StringReader(input), client);

        Assert.Equal(0, exitCode);
        PipeEnvelope envelope = Assert.IsType<PipeEnvelope>(client.Request);
        Assert.Equal(PipeMessageKinds.HookEvent, envelope.Kind);
        Assert.DoesNotContain(secret, envelope.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.Equal((int)CodexHookEventKind.UserPromptSubmit, envelope.Payload.GetProperty("kind").GetInt32());
        Assert.Equal("session-1", envelope.Payload.GetProperty("sessionId").GetString());
        Assert.Equal("turn-1", envelope.Payload.GetProperty("turnId").GetString());
        Assert.Equal(JsonValueKind.Null, envelope.Payload.GetProperty("toolName").ValueKind);
        Assert.Equal(JsonValueKind.Null, envelope.Payload.GetProperty("toolUseId").ValueKind);
        Assert.Equal(
            ["kind", "sessionId", "toolName", "toolUseId", "turnId"],
            envelope.Payload
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.False(envelope.Payload.TryGetProperty("isTurnScoped", out _));
    }

    [Fact]
    public async Task ExecuteAsync_PostToolUse_SendsToolUseIdButNoToolPayload()
    {
        const string secret = "private tool response";
        string input = $$"""
            {
              "hook_event_name": "PostToolUse",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "tool_name": "request_user_input",
              "tool_use_id": "tool-call-1",
              "tool_response": { "answer": "{{secret}}" }
            }
            """;
        var client = new CapturingPipeClient();

        int exitCode = await HookCommand.ExecuteAsync(new StringReader(input), client);

        Assert.Equal(0, exitCode);
        PipeEnvelope envelope = Assert.IsType<PipeEnvelope>(client.Request);
        Assert.Equal("tool-call-1", envelope.Payload.GetProperty("toolUseId").GetString());
        Assert.Equal(
            ["kind", "sessionId", "toolName", "toolUseId", "turnId"],
            envelope.Payload
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(secret, envelope.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("tool_response", envelope.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_HostOffline_FailsOpenWithZeroExitCode()
    {
        const string input = """
            {"hook_event_name":"Stop","session_id":"s","turn_id":"t"}
            """;
        var client = new ThrowingPipeClient();

        int exitCode = await HookCommand.ExecuteAsync(new StringReader(input), client);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ExecuteAsync_Stop_WritesUpstreamCompatibleEmptyJson()
    {
        const string input = """
            {"hook_event_name":"Stop","session_id":"s","turn_id":"t"}
            """;
        var output = new StringWriter();
        var client = new CapturingPipeClient();

        int exitCode = await HookCommand.ExecuteAsync(
            new StringReader(input),
            output,
            client);

        Assert.Equal(0, exitCode);
        Assert.Equal("{}" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_MissingRealPipe_FailsOpenWithinOfflineBudget()
    {
        const string input = """
            {"hook_event_name":"Stop","session_id":"s","turn_id":"t"}
            """;
        var client = new NamedPipeRequestClient(
            $"AgentKick75.missing.{Guid.NewGuid():N}");
        Stopwatch elapsed = Stopwatch.StartNew();

        int exitCode = await HookCommand.ExecuteAsync(new StringReader(input), client);

        elapsed.Stop();
        Assert.Equal(0, exitCode);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Offline Hook took {elapsed.Elapsed.TotalMilliseconds:F0} ms; budget is under 500 ms.");
    }

    [Fact]
    public async Task ExecuteAsync_RealPipe_OnlineP95StaysBelowBudget()
    {
        const int sampleCount = 20;
        const string input = """
            {"hook_event_name":"Stop","session_id":"s","turn_id":"t"}
            """;
        string pipeName = $"AgentKick75.hook-latency.{Guid.NewGuid():N}";
        int handledCount = 0;
        var allHandled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new NamedPipeMessageServer(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref handledCount) == sampleCount)
                {
                    allHandled.TrySetResult();
                }

                return ValueTask.FromResult<PipeEnvelope?>(null);
            },
            pipeName);
        server.Start();
        var client = new NamedPipeRequestClient(pipeName);
        var samples = new TimeSpan[sampleCount];

        for (int index = 0; index < sampleCount; index++)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int exitCode = await HookCommand.ExecuteAsync(new StringReader(input), client);
            elapsed.Stop();
            Assert.Equal(0, exitCode);
            samples[index] = elapsed.Elapsed;
        }

        await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        TimeSpan p95 = Percentile95(samples);
        Assert.True(
            p95 < TimeSpan.FromMilliseconds(300),
            $"Online Hook P95 was {p95.TotalMilliseconds:F0} ms; budget is under 300 ms.");
    }

    [Fact]
    public async Task ExecuteAsync_MissingRealPipe_OfflineP95StaysBelowBudget()
    {
        const int sampleCount = 20;
        const string input = """
            {"hook_event_name":"Stop","session_id":"s","turn_id":"t"}
            """;
        var client = new NamedPipeRequestClient(
            $"AgentKick75.missing-latency.{Guid.NewGuid():N}");
        var samples = new TimeSpan[sampleCount];

        for (int index = 0; index < sampleCount; index++)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int exitCode = await HookCommand.ExecuteAsync(new StringReader(input), client);
            elapsed.Stop();
            Assert.Equal(0, exitCode);
            samples[index] = elapsed.Elapsed;
        }

        TimeSpan p95 = Percentile95(samples);
        Assert.True(
            p95 < TimeSpan.FromMilliseconds(500),
            $"Offline Hook P95 was {p95.TotalMilliseconds:F0} ms; budget is under 500 ms.");
    }

    [Fact]
    public async Task ExecuteAsync_OversizedOrIncompleteInput_DoesNotContactHost()
    {
        var client = new CapturingPipeClient();
        string oversized = new('x', HookCommand.MaximumInputBytes + 1);

        Assert.Equal(0, await HookCommand.ExecuteAsync(new StringReader(oversized), client));
        Assert.Null(client.Request);

        const string missingTurn = """
            {"hook_event_name":"UserPromptSubmit","session_id":"s"}
            """;
        Assert.Equal(0, await HookCommand.ExecuteAsync(new StringReader(missingTurn), client));
        Assert.Null(client.Request);
    }

    private static TimeSpan Percentile95(IEnumerable<TimeSpan> samples)
    {
        TimeSpan[] ordered = samples.Order().ToArray();
        int index = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95) - 1);
        return ordered[index];
    }

    private sealed class CapturingPipeClient : IPipeRequestClient
    {
        public PipeEnvelope? Request { get; private set; }

        public ValueTask<PipeEnvelope?> SendAsync(
            PipeEnvelope request,
            bool expectResponse,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult<PipeEnvelope?>(null);
        }
    }

    private sealed class ThrowingPipeClient : IPipeRequestClient
    {
        public ValueTask<PipeEnvelope?> SendAsync(
            PipeEnvelope request,
            bool expectResponse,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            throw new TimeoutException("Host is offline.");
        }
    }
}
