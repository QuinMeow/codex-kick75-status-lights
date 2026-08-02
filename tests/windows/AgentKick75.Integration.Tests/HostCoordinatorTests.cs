// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Hosting;
using AgentKick75.App.Commands;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.App.Web;
using AgentKick75.Core.Hooks;
using AgentKick75.Core.Lighting;
using AgentKick75.Core.State;

namespace AgentKick75.Integration.Tests;

public sealed class HostCoordinatorTests
{
    [Fact]
    public async Task ApplyHook_ThinkingThenPause_WritesColorThenExactBaseline()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);

        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));
        await coordinator.PauseAsync();

        Assert.Equal(TaskVisualState.Thinking, coordinator.GetStatus().AggregateState);
        Assert.Equal(HookEnablementState.Enabled, coordinator.GetStatus().HookEnablement);
        Assert.Equal(ApplicationLifecycleState.Paused, coordinator.GetStatus().LifecycleState);
        Assert.Equal([0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF], transport.Writes[0]);
        Assert.Equal(baseline, transport.Writes[1]);
    }

    [Fact]
    public async Task Pause_HooksContinueInMemory_ResumeAppliesOnlyLatestAggregate()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);

        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));
        await coordinator.PauseAsync();
        int writesAfterPause = transport.Writes.Count;

        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.Stop,
            "session",
            "turn"));

        Assert.Equal(writesAfterPause, transport.Writes.Count);
        Assert.Equal(TaskVisualState.Complete, coordinator.GetStatus().AggregateState);
        Assert.Equal(ApplicationLifecycleState.Paused, coordinator.GetStatus().LifecycleState);

        await coordinator.ResumeAsync();

        Assert.Equal(ApplicationLifecycleState.Running, coordinator.GetStatus().LifecycleState);
        Assert.Equal(
            [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00],
            transport.Writes[^1]);
        Assert.Equal(writesAfterPause + 1, transport.Writes.Count);
    }

    [Fact]
    public async Task StopAsync_ConcurrentCallersShareOneRestoreAndOneTerminalResult()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));

        Task<LifecycleStopResult> first = coordinator.StopAsync(LifecycleStopReason.NormalExit);
        Task<LifecycleStopResult> second = coordinator.StopAsync(
            LifecycleStopReason.PrepareUninstall);

        Assert.Same(first, second);
        LifecycleStopResult[] results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(ApplicationLifecycleState.Stopped, coordinator.GetStatus().LifecycleState);
        Assert.Equal(1, transport.Writes.Count(write => write.SequenceEqual(baseline)));
    }

    [Fact]
    public async Task HandlePipeMessage_ExtraSensitiveField_IsRejected()
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        PipeEnvelope envelope = PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
        {
            kind = (int)CodexHookEventKind.UserPromptSubmit,
            sessionId = "session",
            turnId = "turn",
            prompt = "must not cross the Host boundary",
        });

        PipeEnvelope? response = await coordinator.HandlePipeMessageAsync(envelope);

        Assert.NotNull(response);
        Assert.Equal(PipeMessageKinds.Rejected, response.Kind);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task HandlePipeMessage_MissingKind_IsRejectedWithoutDefaultingToUserPrompt()
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using JsonDocument payload = JsonDocument.Parse(
            """{"sessionId":"session","turnId":"turn"}""");
        var envelope = new PipeEnvelope(
            PipeEnvelope.CurrentVersion,
            PipeMessageKinds.HookEvent,
            payload.RootElement.Clone());

        PipeEnvelope? response = await coordinator.HandlePipeMessageAsync(envelope);

        Assert.NotNull(response);
        Assert.Equal(PipeMessageKinds.Rejected, response.Kind);
        Assert.Equal(TaskVisualState.Idle, coordinator.GetStatus().AggregateState);
        Assert.Equal(HookEnablementState.Unconfirmed, coordinator.GetStatus().HookEnablement);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task HandlePipeMessage_GoalBlocked_RemainsInterruptedUntilNextUserPrompt()
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);

        Assert.Null(await coordinator.HandlePipeMessageAsync(
            HookEnvelope(CodexHookEventKind.UserPromptSubmit, "session", "turn-1")));
        Assert.Null(await coordinator.HandlePipeMessageAsync(
            HookEnvelope(CodexHookEventKind.GoalBlocked, "session", "turn-1")));
        Assert.Null(await coordinator.HandlePipeMessageAsync(
            HookEnvelope(CodexHookEventKind.Stop, "session", "turn-1")));

        Assert.Equal(TaskVisualState.Interrupted, coordinator.GetStatus().AggregateState);

        Assert.Null(await coordinator.HandlePipeMessageAsync(
            HookEnvelope(CodexHookEventKind.UserPromptSubmit, "session", "turn-2")));
        Assert.Equal(TaskVisualState.Thinking, coordinator.GetStatus().AggregateState);
    }

    [Theory]
    [InlineData("{\"kind\":0,\"kind\":0,\"sessionId\":\"session\",\"turnId\":\"turn\"}")]
    [InlineData("{\"kind\":0,\"sessionId\":\"first\",\"sessionId\":\"second\",\"turnId\":\"turn\"}")]
    public async Task HandlePipeMessage_DuplicateAllowlistedProperty_IsRejected(string json)
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using JsonDocument payload = JsonDocument.Parse(json);
        var envelope = new PipeEnvelope(
            PipeEnvelope.CurrentVersion,
            PipeMessageKinds.HookEvent,
            payload.RootElement.Clone());

        PipeEnvelope? response = await coordinator.HandlePipeMessageAsync(envelope);

        Assert.NotNull(response);
        Assert.Equal(PipeMessageKinds.Rejected, response.Kind);
        Assert.Equal(TaskVisualState.Idle, coordinator.GetStatus().AggregateState);
        Assert.Equal(HookEnablementState.Unconfirmed, coordinator.GetStatus().HookEnablement);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task HandlePipeMessage_PostToolUse_ResumesWaitingSession()
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);

        PipeEnvelope? preResponse = await coordinator.HandlePipeMessageAsync(PipeEnvelope.Create(
            PipeMessageKinds.HookEvent,
            new
            {
                kind = (int)CodexHookEventKind.PreToolUse,
                sessionId = "session",
                turnId = "turn",
                toolName = "request_user_input",
            }));
        PipeEnvelope? unrelatedResponse = await coordinator.HandlePipeMessageAsync(PipeEnvelope.Create(
            PipeMessageKinds.HookEvent,
            new
            {
                kind = (int)CodexHookEventKind.PostToolUse,
                sessionId = "session",
                turnId = "turn",
                toolName = "shell_command",
            }));

        Assert.Null(preResponse);
        Assert.Null(unrelatedResponse);
        Assert.Equal(TaskVisualState.RequiresInput, coordinator.GetStatus().AggregateState);

        PipeEnvelope? matchingResponse = await coordinator.HandlePipeMessageAsync(PipeEnvelope.Create(
            PipeMessageKinds.HookEvent,
            new
            {
                kind = (int)CodexHookEventKind.PostToolUse,
                sessionId = "session",
                turnId = "turn",
                toolName = "request_user_input",
            }));

        Assert.Null(matchingResponse);
        Assert.Equal(TaskVisualState.Thinking, coordinator.GetStatus().AggregateState);
    }

    [Fact]
    public async Task HandlePipeMessage_ParallelPermissionRequests_UseSingleUncorrelatedLatch()
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);

        PipeEnvelope? firstPermission = await coordinator.HandlePipeMessageAsync(
            HookEnvelope(
                CodexHookEventKind.PermissionRequest,
                "session",
                "turn",
                "shell_command"));
        PipeEnvelope? secondPermission = await coordinator.HandlePipeMessageAsync(
            HookEnvelope(
                CodexHookEventKind.PermissionRequest,
                "session",
                "turn",
                "shell_command"));
        PipeEnvelope? onePost = await coordinator.HandlePipeMessageAsync(
            HookEnvelope(
                CodexHookEventKind.PostToolUse,
                "session",
                "turn",
                "shell_command"));

        Assert.Null(firstPermission);
        Assert.Null(secondPermission);
        Assert.Null(onePost);
        // The reducer keeps one current state per session, so any accepted
        // PostToolUse moves that session back to Thinking.
        Assert.Equal(TaskVisualState.Thinking, coordinator.GetStatus().AggregateState);
    }

    [Fact]
    public async Task HandlePipeMessage_ReconcileBackpressure_CoalescesWithoutLosingLifecycleEvents()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var hardwareTest = new BlockingHardwareTestCommand();
        var coordinator = new HostCoordinator(
            new TaskStateReducer(),
            worker,
            hardwareTest: hardwareTest);
        coordinator.StartEventProcessing();
        Task<HardwareTestCommandResult> blockedOperation = coordinator.RunHardwareTestAsync(
            new HardwareTestArguments()).AsTask();
        await hardwareTest.Started.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            const int turnCount = 300;
            for (int index = 0; index < turnCount; index++)
            {
                PipeEnvelope envelope = PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
                {
                    kind = (int)CodexHookEventKind.UserPromptSubmit,
                    sessionId = "busy-session",
                    turnId = $"turn-{index}",
                });

                PipeEnvelope? response = await coordinator.HandlePipeMessageAsync(envelope);
                Assert.Null(response);
            }

            HostStatusSnapshot busy = coordinator.GetStatus();
            Assert.Equal(1, busy.ActiveSessionCount);
            Assert.Equal(TaskVisualState.Thinking, busy.AggregateState);

            PipeEnvelope sessionEnd = PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
            {
                kind = (int)CodexHookEventKind.SessionEnd,
                sessionId = "busy-session",
            });
            PipeEnvelope? sessionEndResponse = await coordinator.HandlePipeMessageAsync(sessionEnd);

            Assert.Null(sessionEndResponse);
            HostStatusSnapshot ended = coordinator.GetStatus();
            Assert.Equal(0, ended.ActiveSessionCount);
            Assert.Equal(TaskVisualState.Idle, ended.AggregateState);
        }
        finally
        {
            hardwareTest.Release();
            await blockedOperation.WaitAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopEventProcessingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ReconcileBackpressure_LatestReducerStateEventuallyReachesTransport()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var transport = new BlockingFirstWriteTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        var firstPublishedStatus = new TaskCompletionSource<HostStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) => firstPublishedStatus.TrySetResult(status);
        coordinator.StartEventProcessing();

        try
        {
            PipeEnvelope? promptResponse = await coordinator.HandlePipeMessageAsync(
                HookEnvelope(CodexHookEventKind.UserPromptSubmit, "session", "turn"));
            Assert.Null(promptResponse);
            await transport.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));

            PipeEnvelope? waitingResponse = await coordinator.HandlePipeMessageAsync(
                HookEnvelope(
                    CodexHookEventKind.PermissionRequest,
                    "session",
                    "turn",
                    "shell_command"));
            PipeEnvelope? endResponse = await coordinator.HandlePipeMessageAsync(
                HookEnvelope(CodexHookEventKind.SessionEnd, "session"));

            Assert.Null(waitingResponse);
            Assert.Null(endResponse);
            Assert.Equal(TaskVisualState.Idle, coordinator.GetStatus().AggregateState);

            transport.ReleaseFirstWrite();
            await transport.BaselineRestored.WaitAsync(TimeSpan.FromSeconds(2));
            HostStatusSnapshot published = await firstPublishedStatus.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.Equal(TaskVisualState.Idle, published.AggregateState);
            Assert.Equal(2, transport.Writes.Count);
            Assert.Equal(baseline, transport.Writes[^1]);
        }
        finally
        {
            transport.ReleaseFirstWrite();
            await coordinator.StopEventProcessingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopEventProcessing_RejectsLateHookBeforeReducerAndDrainsAcceptedHook()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        byte[] thinking = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];
        var transport = new BlockingFirstWriteTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        coordinator.StartEventProcessing();
        Task? stopTask = null;

        try
        {
            PipeEnvelope? accepted = await coordinator.HandlePipeMessageAsync(
                HookEnvelope(CodexHookEventKind.UserPromptSubmit, "session", "accepted"));
            Assert.Null(accepted);
            await transport.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));

            stopTask = coordinator.StopEventProcessingAsync().AsTask();
            PipeEnvelope? rejected = await coordinator.HandlePipeMessageAsync(
                HookEnvelope(CodexHookEventKind.UserPromptSubmit, "session", "late"));

            Assert.NotNull(rejected);
            Assert.Equal(PipeMessageKinds.Rejected, rejected.Kind);
            Assert.Equal(1, coordinator.GetStatus().ActiveSessionCount);
            Assert.False(stopTask.IsCompleted);

            transport.ReleaseFirstWrite();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, coordinator.GetStatus().ActiveSessionCount);
            Assert.Equal(thinking, Assert.Single(transport.Writes));
        }
        finally
        {
            transport.ReleaseFirstWrite();
            await coordinator.StopEventProcessingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StartEventProcessing_ConcurrentCalls_StartExactlyOneConsumer()
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();

        Task<bool>[] attempts = Enumerable.Range(0, 2)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    ready.Signal();
                    start.Wait();
                    try
                    {
                        coordinator.StartEventProcessing();
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        bool allReady = ready.Wait(TimeSpan.FromSeconds(2));
        start.Set();
        try
        {
            Assert.True(allReady);
            bool[] results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Single(results, static started => started);
        }
        finally
        {
            await coordinator.StopEventProcessingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StatusChanged_ThrowingSubscriber_DoesNotStopConsumerOrLaterSubscribers()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        byte[] complete = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        var firstNotification = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondNotification = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int notificationCount = 0;
        coordinator.StatusChanged += static (_, _) =>
            throw new InvalidOperationException("Injected observer failure.");
        coordinator.StatusChanged += (_, _) =>
        {
            int count = Interlocked.Increment(ref notificationCount);
            if (count == 1)
            {
                firstNotification.TrySetResult(true);
            }
            else if (count == 2)
            {
                secondNotification.TrySetResult(true);
            }
        };
        coordinator.StartEventProcessing();

        try
        {
            PipeEnvelope? promptResponse = await coordinator.HandlePipeMessageAsync(
                HookEnvelope(CodexHookEventKind.UserPromptSubmit, "session", "turn"));
            Assert.Null(promptResponse);
            await firstNotification.Task.WaitAsync(TimeSpan.FromSeconds(2));

            PipeEnvelope? stopResponse = await coordinator.HandlePipeMessageAsync(
                HookEnvelope(CodexHookEventKind.Stop, "session", "turn"));
            Assert.Null(stopResponse);
            await secondNotification.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(notificationCount >= 2);
            Assert.Equal(TaskVisualState.Complete, coordinator.GetStatus().AggregateState);
            Assert.Equal(complete, transport.Writes[^1]);
        }
        finally
        {
            await coordinator.StopEventProcessingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ControlSettings_MaximumTtlAndStartupFlag_ArePersistedAndEchoed()
    {
        var transport = new MockLightingTransport([0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var persistence = new CapturingSettingsPersistence();
        var coordinator = new HostCoordinator(
            new TaskStateReducer(),
            worker,
            settingsPersistence: persistence);
        using var adapter = new HostControlPlaneAdapter(coordinator);
        var requested = new ControlSettingsDto(
            new ControlLightStyleDto("#010203", 10),
            new ControlLightStyleDto("#040506", 20),
            new ControlLightStyleDto("#070809", 30),
            CompleteHoldSeconds: 3600,
            LaunchAtSignIn: true);

        ControlSettingsDto effective = await adapter.ApplySettingsAsync(requested, CancellationToken.None);
        ControlSettingsDto reloaded = await adapter.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(3600, effective.CompleteHoldSeconds);
        Assert.True(effective.LaunchAtSignIn);
        Assert.Equal(effective, reloaded);
        Assert.True(persistence.StartAtLogin);
        Assert.Equal(TimeSpan.FromSeconds(3600), persistence.Settings!.CompleteTtl);
    }

    [Fact]
    public async Task CleanupAsync_CodexActiveKeepAwake_RewritesOnlyAfterConfiguredInterval()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(clock);
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore(),
            timeProvider: clock);
        worker.Start();
        var settings = new LightingSettings(
            LightingSettings.Default.Thinking,
            LightingSettings.Default.RequiresInput,
            LightingSettings.Default.Complete,
            LightingSettings.Default.Interrupted,
            LightingSettings.Default.CompleteTtl,
            new KeepAwakeSettings(
                KeepAwakePolicy.WhileCodexActive,
                KeepAwakeRegion.SideLightsOnly,
                TimeSpan.FromSeconds(10)));
        var coordinator = new HostCoordinator(
            reducer,
            worker,
            settings,
            timeProvider: clock);

        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));
        await coordinator.CleanupAsync();
        Assert.Single(transport.Writes);

        clock.Advance(TimeSpan.FromSeconds(10));
        await coordinator.CleanupAsync();

        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(transport.Writes[0], transport.Writes[1]);
    }

    [Fact]
    public async Task CleanupAsync_HostRunningKeepAwake_IdlePulsesBaselineWithoutRetainingOwnership()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var clock = new ManualTimeProvider();
        var transport = new MockLightingTransport(baseline);
        var store = new InMemoryBaselineOwnershipStore();
        await using var worker = new HidLightingWorker(transport, store, timeProvider: clock);
        worker.Start();
        var settings = new LightingSettings(
            LightingSettings.Default.Thinking,
            LightingSettings.Default.RequiresInput,
            LightingSettings.Default.Complete,
            LightingSettings.Default.Interrupted,
            LightingSettings.Default.CompleteTtl,
            new KeepAwakeSettings(
                KeepAwakePolicy.WhileHostRunning,
                KeepAwakeRegion.SideLightsOnly,
                TimeSpan.FromSeconds(10)));
        var coordinator = new HostCoordinator(
            new TaskStateReducer(clock),
            worker,
            settings,
            timeProvider: clock);

        await coordinator.CleanupAsync();

        Assert.Equal(baseline, Assert.Single(transport.Writes));
        Assert.False((await store.LoadAsync())!.IsOwned);
        Assert.Equal(LightingWorkerState.Idle, worker.Snapshot.State);
    }

    [Fact]
    public async Task UpdateSettings_ShorterCompleteTtl_ExpiresActiveCompleteAndRestoresImmediately()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(clock, completeTtl: TimeSpan.FromSeconds(10));
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(reducer, worker);
        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.Stop,
            "session",
            "turn"));
        clock.Advance(TimeSpan.FromSeconds(5));
        var updated = new LightingSettings(
            LightingSettings.Default.Thinking,
            LightingSettings.Default.RequiresInput,
            LightingSettings.Default.Complete,
            TimeSpan.FromSeconds(4));

        await coordinator.UpdateSettingsAsync(updated);

        Assert.Equal(TimeSpan.FromSeconds(4), reducer.CompleteTtl);
        Assert.Equal(TaskVisualState.Idle, coordinator.GetStatus().AggregateState);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(baseline, transport.Writes[^1]);
    }

    [Fact]
    public async Task Preview_NormalEnd_ClearsOwnershipAndReplaysCurrentState()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        byte[] thinking = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];
        byte[] complete = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));

        await coordinator.PreviewAsync(TaskVisualState.Complete, TimeSpan.FromMilliseconds(10));

        Assert.False(coordinator.GetStatus().IsPreviewActive);
        Assert.Equal(TaskVisualState.Thinking, coordinator.GetStatus().AggregateState);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(thinking, transport.Writes[0]);
        Assert.Equal(complete, transport.Writes[1]);
        Assert.Equal(thinking, transport.Writes[2]);
    }

    [Fact]
    public async Task Preview_Cancellation_ClearsOwnershipAndReplaysLatestReducerState()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        byte[] thinking = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];
        byte[] complete = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00];
        byte[] requiresInput = [0x02, 0x64, 0x01, 0x00, 0x00, 0xFF, 0xB4, 0x00];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));
        var previewStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.IsPreviewActive)
            {
                previewStarted.TrySetResult(true);
            }
        };
        using var cancellation = new CancellationTokenSource();

        Task preview = coordinator.PreviewAsync(
            TaskVisualState.Complete,
            TimeSpan.FromSeconds(10),
            cancellation.Token).AsTask();
        await previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.PermissionRequest,
            "session",
            "turn",
            "request_user_input"));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview);
        Assert.False(coordinator.GetStatus().IsPreviewActive);
        Assert.Equal(TaskVisualState.RequiresInput, coordinator.GetStatus().AggregateState);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(thinking, transport.Writes[0]);
        Assert.Equal(complete, transport.Writes[1]);
        Assert.Equal(requiresInput, transport.Writes[2]);
    }

    [Fact]
    public async Task Preview_InitialWriteThrows_ClearsOwnershipAndReplaysCurrentState()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        byte[] thinking = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];
        var transport = new FailOnceWriteTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));
        transport.FailNextWrite();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PreviewAsync(
            TaskVisualState.Complete,
            TimeSpan.FromSeconds(3)).AsTask());

        Assert.False(coordinator.GetStatus().IsPreviewActive);
        Assert.Equal(TaskVisualState.Thinking, coordinator.GetStatus().AggregateState);
        Assert.Equal(LightingWorkerState.Active, worker.Snapshot.State);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(thinking, transport.Writes[0]);
        Assert.Equal(thinking, transport.Writes[1]);
    }

    [Fact]
    public async Task Preview_ReplacedPreview_FirstCancellationDoesNotClearNewOwner()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        byte[] complete = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00];
        var transport = new MockLightingTransport(baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);
        int activeNotificationCount = 0;
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (!status.IsPreviewActive)
            {
                return;
            }

            int count = Interlocked.Increment(ref activeNotificationCount);
            if (count == 1)
            {
                firstStarted.TrySetResult(true);
            }
            else if (count == 2)
            {
                secondStarted.TrySetResult(true);
            }
        };
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        Task first = coordinator.PreviewAsync(
            TaskVisualState.Thinking,
            TimeSpan.FromSeconds(10),
            firstCancellation.Token).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = coordinator.PreviewAsync(
            TaskVisualState.Complete,
            TimeSpan.FromSeconds(10),
            secondCancellation.Token).AsTask();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(coordinator.GetStatus().IsPreviewActive);
        Assert.Equal(complete, transport.Writes[^1]);

        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(coordinator.GetStatus().IsPreviewActive);
        Assert.Equal(baseline, transport.Writes[^1]);
    }

    [Fact]
    public async Task RunHardwareTest_QuiescesWorkerThenRestoresLatestReducerState()
    {
        byte[] baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
        byte[] thinking = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];
        byte[] requiresInput = [0x02, 0x64, 0x01, 0x00, 0x00, 0xFF, 0xB4, 0x00];
        var transport = new MockLightingTransport(baseline);
        var reducer = new TaskStateReducer();
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();
        var hardwareTest = new ObservingHardwareTestCommand(reducer, worker, transport);
        var coordinator = new HostCoordinator(reducer, worker, hardwareTest: hardwareTest);

        await coordinator.ApplyHookAsync(new CodexHookEvent(
            CodexHookEventKind.UserPromptSubmit,
            "session",
            "turn"));
        Assert.Equal(thinking, Assert.Single(transport.Writes));

        HardwareTestCommandResult result = await coordinator.RunHardwareTestAsync(
            new HardwareTestArguments());

        Assert.True(result.Succeeded);
        Assert.Equal(LightingWorkerState.Paused, hardwareTest.ObservedWorkerState);
        Assert.Equal(
            new[] { "connect", "read", "write", "write", "read", "disconnect" },
            hardwareTest.ObservedOperations);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(thinking, transport.Writes[0]);
        Assert.Equal(baseline, transport.Writes[1]);
        Assert.Equal(requiresInput, transport.Writes[2]);
        Assert.Equal(LightingWorkerState.Active, worker.Snapshot.State);
        Assert.Equal(TaskVisualState.RequiresInput, coordinator.GetStatus().AggregateState);
    }

    private static PipeEnvelope HookEnvelope(
        CodexHookEventKind kind,
        string sessionId,
        string? turnId = null,
        string? toolName = null)
    {
        return PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
        {
            kind = (int)kind,
            sessionId,
            turnId,
            toolName,
        });
    }

    private sealed class CapturingSettingsPersistence : IHostSettingsPersistence
    {
        public LightingSettings? Settings { get; private set; }

        public bool StartAtLogin { get; private set; }

        public ValueTask SaveAsync(
            LightingSettings settings,
            bool startAtLogin,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Settings = settings;
            StartAtLogin = startAtLogin;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceWriteTransport : ILightingTransport
    {
        private readonly MockLightingTransport inner;
        private int failNextWrite;

        public FailOnceWriteTransport(ReadOnlySpan<byte> baseline)
        {
            inner = new MockLightingTransport(baseline);
        }

        public IReadOnlyList<byte[]> Writes => inner.Writes;

        public void FailNextWrite()
        {
            Interlocked.Exchange(ref failNextWrite, 1);
        }

        public ValueTask<LightingDeviceSession> ConnectAsync(
            LightingConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            return inner.ConnectAsync(request, cancellationToken);
        }

        public ValueTask<byte[]> ReadSideLightAsync(CancellationToken cancellationToken = default)
        {
            return inner.ReadSideLightAsync(cancellationToken);
        }

        public async ValueTask WriteSideLightAsync(
            ReadOnlyMemory<byte> sideLightState,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref failNextWrite, 0) != 0)
            {
                throw new InvalidOperationException("Injected non-transport preview write failure.");
            }

            await inner.WriteSideLightAsync(sideLightState, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            return inner.DisconnectAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }
    }

    private sealed class BlockingFirstWriteTransport : ILightingTransport
    {
        private readonly MockLightingTransport inner;
        private readonly byte[] baseline;
        private readonly TaskCompletionSource<bool> firstWriteStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseFirstWrite = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> baselineRestored = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int blockFirstWrite = 1;

        public BlockingFirstWriteTransport(ReadOnlySpan<byte> baseline)
        {
            this.baseline = baseline.ToArray();
            inner = new MockLightingTransport(baseline);
        }

        public Task FirstWriteStarted => firstWriteStarted.Task;

        public Task BaselineRestored => baselineRestored.Task;

        public IReadOnlyList<byte[]> Writes => inner.Writes;

        public void ReleaseFirstWrite()
        {
            releaseFirstWrite.TrySetResult(true);
        }

        public ValueTask<LightingDeviceSession> ConnectAsync(
            LightingConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            return inner.ConnectAsync(request, cancellationToken);
        }

        public ValueTask<byte[]> ReadSideLightAsync(CancellationToken cancellationToken = default)
        {
            return inner.ReadSideLightAsync(cancellationToken);
        }

        public async ValueTask WriteSideLightAsync(
            ReadOnlyMemory<byte> sideLightState,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref blockFirstWrite, 0) != 0)
            {
                firstWriteStarted.TrySetResult(true);
                await releaseFirstWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await inner.WriteSideLightAsync(sideLightState, cancellationToken).ConfigureAwait(false);
            if (sideLightState.Span.SequenceEqual(baseline))
            {
                baselineRestored.TrySetResult(true);
            }
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            return inner.DisconnectAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
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

    private sealed class ObservingHardwareTestCommand(
        TaskStateReducer reducer,
        HidLightingWorker worker,
        MockLightingTransport transport) : IHardwareTestCommand
    {
        public LightingWorkerState ObservedWorkerState { get; private set; }

        public IReadOnlyList<string> ObservedOperations { get; private set; } = [];

        public ValueTask<HardwareTestCommandResult> RunAsync(
            HardwareTestArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedWorkerState = worker.Snapshot.State;
            ObservedOperations = transport.Operations.ToArray();

            // ApplyHookAsync updates the reducer before it waits on the Host's
            // reconcile gate. Updating it directly here models the newest event
            // arriving while the guarded hardware test owns that gate.
            reducer.Apply(new CodexHookEvent(
                CodexHookEventKind.PermissionRequest,
                "session",
                "turn",
                "request_user_input"));

            return ValueTask.FromResult(new HardwareTestCommandResult(
                true,
                "Passed: simulated guarded hardware test."));
        }
    }

    private sealed class BlockingHardwareTestCommand : IHardwareTestCommand
    {
        private readonly TaskCompletionSource<bool> started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public void Release()
        {
            release.TrySetResult(true);
        }

        public async ValueTask<HardwareTestCommandResult> RunAsync(
            HardwareTestArguments arguments,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HardwareTestCommandResult(true, "Passed: released blocking hardware test.");
        }
    }
}
