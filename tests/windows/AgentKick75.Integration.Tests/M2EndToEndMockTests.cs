// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using AgentKick75.App.Hooks;
using AgentKick75.App.Hosting;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.Core.Hooks;
using AgentKick75.Core.State;

namespace AgentKick75.Integration.Tests;

/// <summary>
/// Exercises the complete M2 software path without opening a physical HID device:
/// Codex JSON stdin -> privacy normalizer -> real current-user Named Pipe -> Host
/// reducer -> worker -> mock lighting transport.
/// </summary>
public sealed class M2EndToEndMockTests
{
    private static readonly byte[] Baseline =
        [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];

    private static readonly byte[] Thinking =
        [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];

    private static readonly byte[] RequiresInput =
        [0x02, 0x64, 0x01, 0x00, 0x00, 0xFF, 0xB4, 0x00];

    private static readonly byte[] Complete =
        [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00];

    [Fact]
    public async Task HookStdin_LifecycleThroughRealPipe_DrivesMockLightsAndRestoresBaseline()
    {
        const string promptSecret = "PROMPT-SECRET-MUST-NOT-CROSS-IPC";
        const string toolSecret = "TOOL-PAYLOAD-MUST-NOT-CROSS-IPC";
        const string assistantSecret = "ASSISTANT-MESSAGE-MUST-NOT-CROSS-IPC";
        var clock = new ManualTimeProvider();
        await using M2Harness harness = M2Harness.Create(
            clock,
            completeTtl: TimeSpan.FromSeconds(10));

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "session-lifecycle",
            turn_id = "turn-lifecycle",
            prompt = promptSecret,
            transcript_path = "C:/private/transcript.jsonl",
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Thinking
                && harness.Transport.Writes.Count >= 1,
            "The prompt hook did not reach the Host and lighting worker.");
        Assert.Equal(Thinking, harness.Transport.Writes[^1]);

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "PermissionRequest",
            session_id = "session-lifecycle",
            turn_id = "turn-lifecycle",
            tool_name = "shell_command",
            tool_input = new { command = toolSecret },
            tool_response = new { output = toolSecret },
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.RequiresInput
                && harness.Transport.Writes.Count >= 2,
            "The permission hook did not reach the Host and lighting worker.");
        Assert.Equal(RequiresInput, harness.Transport.Writes[^1]);

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "PostToolUse",
            session_id = "session-lifecycle",
            turn_id = "turn-lifecycle",
            tool_name = "shell_command",
            tool_response = new { output = toolSecret },
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Thinking
                && harness.Transport.Writes.Count >= 3,
            "The matching PostToolUse did not resume the Host and lighting worker.");
        Assert.Equal(Thinking, harness.Transport.Writes[^1]);

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "Stop",
            session_id = "session-lifecycle",
            turn_id = "turn-lifecycle",
            last_assistant_message = assistantSecret,
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Complete
                && harness.Transport.Writes.Count >= 4,
            "The Stop hook did not reach the Host and lighting worker.");
        Assert.Equal(Complete, harness.Transport.Writes[^1]);

        string publicState = JsonSerializer.Serialize(new
        {
            Status = harness.Coordinator.GetStatus(),
            Tasks = harness.Reducer.Snapshot(),
        });

        clock.Advance(TimeSpan.FromSeconds(10));
        await harness.Coordinator.CleanupAsync();

        Assert.Equal(TaskVisualState.Idle, harness.Coordinator.GetStatus().AggregateState);
        Assert.Equal(Baseline, harness.Transport.Writes[^1]);
        Assert.Equal(HookEnablementState.Enabled, harness.Coordinator.GetStatus().HookEnablement);

        Assert.Equal(4, harness.Client.CapturedPayloads.Count);
        string capturedEnvelopes = string.Join("\n", harness.Client.CapturedPayloads);
        string[] allowedHookFields = ["kind", "sessionId", "turnId", "toolName"];
        foreach (string capturedPayload in harness.Client.CapturedPayloads)
        {
            using JsonDocument document = JsonDocument.Parse(capturedPayload);
            Assert.All(
                document.RootElement.EnumerateObject(),
                property => Assert.Contains(property.Name, allowedHookFields));
        }

        Assert.DoesNotContain(promptSecret, capturedEnvelopes, StringComparison.Ordinal);
        Assert.DoesNotContain(toolSecret, capturedEnvelopes, StringComparison.Ordinal);
        Assert.DoesNotContain(assistantSecret, capturedEnvelopes, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", capturedEnvelopes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_input", capturedEnvelopes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_response", capturedEnvelopes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("last_assistant_message", capturedEnvelopes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(promptSecret, publicState, StringComparison.Ordinal);
        Assert.DoesNotContain(toolSecret, publicState, StringComparison.Ordinal);
        Assert.DoesNotContain(assistantSecret, publicState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HookStdin_CorrelatedRequestUserInputThroughRealPipe_OnlyMatchingPostClearsWait()
    {
        await using M2Harness harness = M2Harness.Create();

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "session-correlated",
            turn_id = "turn-correlated",
        });
        await PipeBarrierAsync(harness.PipeName);
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Thinking
                && harness.Transport.Writes.Count >= 1,
            "The prompt hook did not establish the Thinking state.");
        Assert.Equal(Thinking, harness.Transport.Writes[^1]);

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "PreToolUse",
            session_id = "session-correlated",
            turn_id = "turn-correlated",
            tool_name = "request_user_input",
            tool_input = new { question = "private-question" },
        });
        await PipeBarrierAsync(harness.PipeName);
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.RequiresInput
                && harness.Transport.Writes.Count >= 2,
            "The correlated request_user_input hook did not establish RequiresInput.");
        Assert.Equal(RequiresInput, harness.Transport.Writes[^1]);

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "PostToolUse",
            session_id = "session-correlated",
            turn_id = "turn-correlated",
            tool_name = "shell_command",
            tool_response = new { output = "private-output" },
        });
        await PipeBarrierAsync(harness.PipeName);
        Assert.Equal(
            TaskVisualState.RequiresInput,
            harness.Coordinator.GetStatus().AggregateState);
        Assert.Equal(RequiresInput, harness.Transport.Writes[^1]);

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "PostToolUse",
            session_id = "session-correlated",
            turn_id = "turn-correlated",
            tool_name = "request_user_input",
            tool_response = new { answer = "private-answer" },
        });
        await PipeBarrierAsync(harness.PipeName);
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Thinking
                && harness.Transport.Writes.Count >= 3,
            "The matching PostToolUse did not clear the correlated wait.");
        Assert.Equal(Thinking, harness.Transport.Writes[^1]);

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "SessionEnd",
            session_id = "session-correlated",
            reason = "other",
        });
        await PipeBarrierAsync(harness.PipeName);
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Idle
                && harness.Transport.Writes.Count >= 4,
            "SessionEnd did not release the turn and restore the baseline.");
        Assert.Equal(Baseline, harness.Transport.Writes[^1]);
        Assert.Equal(LightingWorkerState.Idle, harness.WorkerSnapshot.State);
    }

    [Fact]
    public async Task HookStdin_TwoSessionsWithSameTurnId_PreservesAggregatePriority()
    {
        await using M2Harness harness = M2Harness.Create();

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "session-a",
            turn_id = "shared-turn",
            prompt = "private-a",
        });
        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "PermissionRequest",
            session_id = "session-b",
            turn_id = "shared-turn",
            tool_name = "shell_command",
            tool_input = new { command = "private-b" },
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus() is
            {
                AggregateState: TaskVisualState.RequiresInput,
                ActiveSessionCount: 2,
            },
            "RequiresInput did not win over the other session's Thinking state.");

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "PostToolUse",
            session_id = "session-b",
            turn_id = "shared-turn",
            tool_name = "shell_command",
            tool_response = new { output = "private-response" },
        });
        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "Stop",
            session_id = "session-a",
            turn_id = "shared-turn",
            last_assistant_message = "private-a-final",
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus() is
            {
                AggregateState: TaskVisualState.Thinking,
                ActiveSessionCount: 1,
            },
            "The running session did not win over the other session's Complete state.");

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "Stop",
            session_id = "session-b",
            turn_id = "shared-turn",
            last_assistant_message = "private-b-final",
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Complete,
            "Two completed sessions did not aggregate to Complete.");

        await SendHookAsync(harness.Client, new
        {
            hook_event_name = "SessionEnd",
            session_id = "session-b",
            reason = "other",
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus() is
            {
                AggregateState: TaskVisualState.Complete,
                ActiveSessionCount: 0,
            },
            "SessionEnd removed the wrong session entry.");

        SessionStateEntry remaining = Assert.Single(harness.Reducer.Snapshot().Sessions);
        Assert.Equal("session-a", remaining.SessionId);
        Assert.Equal("shared-turn", remaining.LastTurnId);
    }

    [Fact]
    public async Task RealPipe_AbandonedClient_DoesNotPreventNextHookFromReachingHost()
    {
        await using M2Harness harness = M2Harness.Create();

        using (var abandonedClient = new NamedPipeClientStream(
                   ".",
                   harness.PipeName,
                   PipeDirection.InOut,
                   PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await abandonedClient.ConnectAsync(connectTimeout.Token);
            // Dispose without sending even a framing header. The server must treat
            // this as one failed client, not as termination of the listener.
        }

        var recoveryClient = new NamedPipeRequestClient(harness.PipeName);
        PipeEnvelope? statusResponse = await recoveryClient.SendAsync(
            PipeEnvelope.Create(PipeMessageKinds.StatusRequest, new { }),
            expectResponse: true,
            TimeSpan.FromSeconds(2));
        Assert.NotNull(statusResponse);
        Assert.Equal(PipeMessageKinds.StatusResponse, statusResponse.Kind);

        var recoveryHookClient = new CapturingPipeClient(
            harness.PipeName,
            minimumTimeout: TimeSpan.FromSeconds(2));
        await SendHookAsync(recoveryHookClient, new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "session-after-disconnect",
            turn_id = "turn-after-disconnect",
            prompt = "still-private",
        });
        await WaitForAsync(
            () => harness.Coordinator.GetStatus().AggregateState == TaskVisualState.Thinking
                && harness.Transport.Writes.Count >= 1,
            "The Host listener did not process a Hook after an abandoned client.");

        Assert.Equal(Thinking, harness.Transport.Writes[^1]);
    }

    private static async Task SendHookAsync(CapturingPipeClient client, object hookInput)
    {
        string json = JsonSerializer.Serialize(hookInput);
        int exitCode = await HookCommand.ExecuteAsync(new StringReader(json), client);
        Assert.Equal(0, exitCode);
        Assert.Null(client.TakeLastError());
    }

    private static async Task PipeBarrierAsync(string pipeName)
    {
        var client = new NamedPipeRequestClient(pipeName);
        PipeEnvelope? response = await client.SendAsync(
            PipeEnvelope.Create(PipeMessageKinds.StatusRequest, new { }),
            expectResponse: true,
            TimeSpan.FromSeconds(2));

        Assert.NotNull(response);
        Assert.Equal(PipeMessageKinds.StatusResponse, response.Kind);
    }

    private static async Task WaitForAsync(Func<bool> condition, string failureMessage)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), failureMessage);
    }

    private sealed class CapturingPipeClient(
        string pipeName,
        TimeSpan? minimumTimeout = null) : IPipeRequestClient
    {
        private readonly NamedPipeRequestClient inner = new(pipeName);
        private readonly ConcurrentQueue<string> capturedPayloads = new();
        private Exception? lastError;

        public IReadOnlyList<string> CapturedPayloads => capturedPayloads.ToArray();

        public async ValueTask<PipeEnvelope?> SendAsync(
            PipeEnvelope request,
            bool expectResponse,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            capturedPayloads.Enqueue(request.Payload.GetRawText());
            try
            {
                TimeSpan effectiveTimeout = minimumTimeout is { } configuredMinimum
                    && timeout < configuredMinimum
                    ? configuredMinimum
                    : timeout;
                return await inner.SendAsync(
                    request,
                    expectResponse,
                    effectiveTimeout,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref lastError, exception);
                throw;
            }
        }

        public Exception? TakeLastError()
        {
            return Interlocked.Exchange(ref lastError, null);
        }
    }

    private sealed class M2Harness : IAsyncDisposable
    {
        private readonly NamedPipeMessageServer server;
        private readonly HidLightingWorker worker;

        private M2Harness(
            string pipeName,
            TaskStateReducer reducer,
            MockLightingTransport transport,
            HidLightingWorker worker,
            HostCoordinator coordinator,
            NamedPipeMessageServer server)
        {
            PipeName = pipeName;
            Reducer = reducer;
            Transport = transport;
            this.worker = worker;
            Coordinator = coordinator;
            this.server = server;
            Client = new CapturingPipeClient(pipeName);
        }

        public string PipeName { get; }

        public TaskStateReducer Reducer { get; }

        public MockLightingTransport Transport { get; }

        public HostCoordinator Coordinator { get; }

        public CapturingPipeClient Client { get; }

        public LightingWorkerSnapshot WorkerSnapshot => worker.Snapshot;

        public static M2Harness Create(
            TimeProvider? timeProvider = null,
            TimeSpan? completeTtl = null)
        {
            string pipeName = $"AgentKick75.M2E2E.{Guid.NewGuid():N}";
            var reducer = new TaskStateReducer(
                timeProvider,
                completeTtl,
                staleTimeout: TimeSpan.FromMinutes(30));
            var transport = new MockLightingTransport(Baseline);
            var worker = new HidLightingWorker(
                transport,
                new InMemoryBaselineOwnershipStore());
            worker.Start();
            var coordinator = new HostCoordinator(reducer, worker);
            coordinator.StartEventProcessing();
            var server = new NamedPipeMessageServer(coordinator.HandlePipeMessageAsync, pipeName);
            server.Start();
            return new M2Harness(pipeName, reducer, transport, worker, coordinator, server);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await server.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Coordinator.StopEventProcessingAsync();
                }
                finally
                {
                    await worker.DisposeAsync();
                }
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }
}
