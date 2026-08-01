// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Threading.Channels;
using AgentKick75.App.Commands;
using AgentKick75.App.Diagnostics;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.Core.Hooks;
using AgentKick75.Core.Lighting;
using AgentKick75.Core.State;

namespace AgentKick75.App.Hosting;

public enum HookEnablementState
{
    Unconfirmed,
    Enabled,
    Disabled,
}

public sealed record HostStatusSnapshot(
    string Host,
    bool Paused,
    bool IsPreviewActive,
    HookEnablementState HookEnablement,
    TaskVisualState AggregateState,
    int ActiveTurnCount,
    int SessionCount,
    DateTimeOffset? LastEventAtUtc,
    LightingWorkerSnapshot Lighting);

public sealed class HostCoordinator
{
    private enum HookProcessingLifecycle
    {
        NotStarted,
        Running,
        Stopping,
        Stopped,
    }

    private readonly TaskStateReducer reducer;
    private readonly HidLightingWorker lightingWorker;
    private readonly IHardwareTestCommand hardwareTest;
    private readonly IHostSettingsPersistence settingsPersistence;
    private readonly ISanitizedDiagnosticLog? diagnosticLog;
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private readonly Channel<bool> hookReconcileRequests;
    private readonly object hookLifecycleGate = new();
    private readonly object stateGate = new();
    private LightingSettings lightingSettings;
    private bool startAtLogin;
    private bool paused;
    private bool previewActive;
    private int previewGeneration;
    private HookEnablementState hookEnablement;
    private Task? hookProcessingTask;
    private HookProcessingLifecycle hookProcessingLifecycle;
    private TaskCompletionSource<bool>? hookStopCompletion;
    private TaskCompletionSource<bool>? inlineHooksDrained;
    private int inlineHookCount;

    public HostCoordinator(
        TaskStateReducer reducer,
        HidLightingWorker lightingWorker,
        LightingSettings? lightingSettings = null,
        IHardwareTestCommand? hardwareTest = null,
        IHostSettingsPersistence? settingsPersistence = null,
        bool startAtLogin = false,
        ISanitizedDiagnosticLog? diagnosticLog = null)
    {
        this.reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
        this.lightingWorker = lightingWorker ?? throw new ArgumentNullException(nameof(lightingWorker));
        this.lightingSettings = lightingSettings ?? LightingSettings.Default;
        this.lightingSettings.Validate();
        // Tests and alternate hosts are deny-by-default. Production Program.cs
        // must opt in by injecting the guarded Win32 implementation explicitly.
        this.hardwareTest = hardwareTest ?? new SafeUnavailableHardwareTestCommand();
        this.settingsPersistence = settingsPersistence ?? new NullHostSettingsPersistence();
        this.startAtLogin = startAtLogin;
        this.diagnosticLog = diagnosticLog;
        // Hook events are reduced synchronously at the trusted Host boundary so
        // lifecycle transitions can never be lost to queue pressure. Physical
        // reconciliation is represented only by a capacity-one wake-up signal:
        // multiple signals may safely coalesce because each reconciliation reads
        // the reducer's latest complete state.
        hookReconcileRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        hookEnablement = HookEnablementState.Unconfirmed;
    }

    public event EventHandler<HostStatusSnapshot>? StatusChanged;

    public void StartEventProcessing()
    {
        lock (hookLifecycleGate)
        {
            if (hookProcessingLifecycle != HookProcessingLifecycle.NotStarted)
            {
                throw new InvalidOperationException("Hook event processing has already started or stopped.");
            }

            hookProcessingLifecycle = HookProcessingLifecycle.Running;
            hookProcessingTask = ProcessHookEventsAsync();
        }
    }

    public ValueTask StopEventProcessingAsync()
    {
        Task completionTask;
        TaskCompletionSource<bool>? ownedCompletion = null;
        Task processingTask = Task.CompletedTask;
        Task inlineDrainTask = Task.CompletedTask;
        lock (hookLifecycleGate)
        {
            if (hookProcessingLifecycle is HookProcessingLifecycle.Stopping or
                HookProcessingLifecycle.Stopped)
            {
                completionTask = hookStopCompletion?.Task ??
                    Task.WhenAll(
                        hookProcessingTask ?? Task.CompletedTask,
                        inlineHooksDrained?.Task ?? Task.CompletedTask);
            }
            else
            {
                hookProcessingLifecycle = HookProcessingLifecycle.Stopping;
                hookReconcileRequests.Writer.TryComplete();
                processingTask = hookProcessingTask ?? Task.CompletedTask;
                inlineDrainTask = inlineHooksDrained?.Task ?? Task.CompletedTask;
                ownedCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                hookStopCompletion = ownedCompletion;
                completionTask = ownedCompletion.Task;
            }
        }

        if (ownedCompletion is not null)
        {
            _ = CompleteHookStopAsync(processingTask, inlineDrainTask, ownedCompletion);
        }

        return new ValueTask(completionTask);
    }

    public LightingSettings LightingSettings
    {
        get
        {
            lock (stateGate)
            {
                return lightingSettings;
            }
        }
    }

    public bool StartAtLogin
    {
        get
        {
            lock (stateGate)
            {
                return startAtLogin;
            }
        }
    }

    public HostStatusSnapshot GetStatus()
    {
        TaskStateSnapshot task = reducer.Snapshot();
        lock (stateGate)
        {
            return CreateStatus(task);
        }
    }

    public void SetHookEnablement(HookEnablementState state)
    {
        lock (stateGate)
        {
            hookEnablement = state;
        }

        PublishStatus();
    }

    public async ValueTask ApplyHookAsync(
        CodexHookEvent hookEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hookEvent);
        ApplyHookState(hookEvent);
        await ReconcileLightingAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CleanupAsync(CancellationToken cancellationToken = default)
    {
        reducer.CleanupStale();
        await ReconcileLightingAsync(cancellationToken, probeConnection: true).ConfigureAwait(false);
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                paused = true;
                previewActive = false;
                previewGeneration++;
            }

            await lightingWorker.PauseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            reconcileGate.Release();
        }

        PublishStatus();
    }

    public async ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasPaused;
            lock (stateGate)
            {
                wasPaused = paused;
            }

            if (wasPaused)
            {
                // Stage the newest aggregate target while both Host and worker
                // remain paused. Commit the visible Host state only after the
                // worker has resumed successfully.
                await StageLatestTargetOnPausedWorkerUnderGateAsync(cancellationToken)
                    .ConfigureAwait(false);
                await lightingWorker.ResumeAsync(cancellationToken).ConfigureAwait(false);
                lock (stateGate)
                {
                    paused = false;
                }
            }
            else
            {
                await ReconcileLatestLightingUnderGateAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            reconcileGate.Release();
        }

        PublishStatus();
    }

    public async ValueTask<BaselineMismatchRecoveryResult> AbandonMismatchedBaselineAsync(
        string confirmationId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BaselineMismatchRecoveryResult result;
        try
        {
            result = await lightingWorker.AbandonMismatchedBaselineAsync(
                confirmationId,
                confirmed,
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                lock (stateGate)
                {
                    paused = true;
                    previewActive = false;
                    previewGeneration++;
                }
            }
        }
        finally
        {
            reconcileGate.Release();
        }

        PublishStatus();
        return result;
    }

    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                previewActive = false;
                previewGeneration++;
            }

            await lightingWorker.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            reconcileGate.Release();
        }

        PublishStatus();
    }

    public async ValueTask PreviewAsync(
        TaskVisualState state,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (state == TaskVisualState.Idle)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Idle is not a preview color.");
        }

        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        int generation = 0;
        bool previewClaimed = false;
        Exception? operationFailure = null;
        try
        {
            await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LightingSettings settings;
                lock (stateGate)
                {
                    if (paused)
                    {
                        throw new InvalidOperationException("Lighting takeover is paused.");
                    }

                    previewActive = true;
                    generation = ++previewGeneration;
                    previewClaimed = true;
                    settings = lightingSettings;
                }

                SideLightState target = SideLightStateFactory.Create(state, settings)
                    ?? throw new InvalidOperationException("A preview state must produce a side-light color.");
                await lightingWorker.SetSideLightAsync(target.Bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                reconcileGate.Release();
            }

            PublishStatus();
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            if (previewClaimed)
            {
                try
                {
                    await FinishPreviewAsync(generation).ConfigureAwait(false);
                }
                catch (Exception) when (operationFailure is not null)
                {
                    // Preserve the original write/cancellation failure. The worker
                    // snapshot still exposes a privacy-safe diagnostic for a failed
                    // compensation attempt, and preview ownership is already clear.
                }
            }
        }
    }

    public async ValueTask<LightingSettings> UpdateSettingsAsync(
        LightingSettings settings,
        bool? newStartAtLogin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        bool effectiveStartAtLogin;
        lock (stateGate)
        {
            effectiveStartAtLogin = newStartAtLogin ?? startAtLogin;
        }

        await settingsPersistence.SaveAsync(
            settings,
            effectiveStartAtLogin,
            cancellationToken).ConfigureAwait(false);
        reducer.UpdateCompleteTtl(settings.CompleteTtl);
        lock (stateGate)
        {
            lightingSettings = settings;
            startAtLogin = effectiveStartAtLogin;
        }

        await ReconcileLightingAsync(cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public async ValueTask<HardwareTestCommandResult> RunHardwareTestAsync(
        HardwareTestArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool wasPaused = false;
        bool quiesceAttempted = false;
        try
        {
            lock (stateGate)
            {
                wasPaused = paused;
                if (previewActive)
                {
                    return new HardwareTestCommandResult(
                        false,
                        "Refused: wait for the three-second lighting preview to finish.");
                }

            }

            // A guarded test owns its own HID session. First restore/release the
            // worker baseline and close the worker handle so the two paths can
            // never write concurrently or select different profiles at once.
            quiesceAttempted = true;
            await lightingWorker.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            return await hardwareTest.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                bool shouldResume;
                lock (stateGate)
                {
                    shouldResume = !paused;
                }

                if (quiesceAttempted && !wasPaused && shouldResume)
                {
                    // Reducer state may have changed while the physical test held the
                    // gate. Stage the newest target while paused, then resume once.
                    try
                    {
                        await StageLatestTargetOnPausedWorkerUnderGateAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        await lightingWorker.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        lock (stateGate)
                        {
                            paused = true;
                        }

                        throw;
                    }
                }
            }
            finally
            {
                // Compensation is allowed to fail, but never strand the global
                // physical-operation gate and deadlock all future Hook commands.
                reconcileGate.Release();
                PublishStatus();
            }
        }
    }

    public async ValueTask<PipeEnvelope?> HandlePipeMessageAsync(
        PipeEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.Kind)
        {
            case PipeMessageKinds.HookEvent:
                if (!TryReadHook(envelope.Payload, out CodexHookEvent? hook))
                {
                    WriteDiagnostic(
                        SanitizedDiagnosticEventType.HookRejected,
                        code: SanitizedDiagnosticCode.InvalidInput);
                    return PipeEnvelope.Create(PipeMessageKinds.Rejected, new { reason = "invalid-hook" });
                }

                return await HandleHookMessageAsync(hook!, cancellationToken).ConfigureAwait(false);

            case PipeMessageKinds.StatusRequest:
                return PipeEnvelope.Create(
                    PipeMessageKinds.StatusResponse,
                    PipeStatusResponseDto.FromInternal(GetStatus()));

            default:
                WriteDiagnostic(
                    SanitizedDiagnosticEventType.ControlRequestRejected,
                    code: SanitizedDiagnosticCode.InvalidInput);
                return PipeEnvelope.Create(PipeMessageKinds.Rejected, new { reason = "unknown-kind" });
        }
    }

    private async ValueTask<TaskStateSnapshot> ReconcileLightingAsync(
        CancellationToken cancellationToken,
        bool probeConnection = false)
    {
        TaskStateSnapshot task;
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            task = await ReconcileLatestLightingUnderGateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (probeConnection)
            {
                await lightingWorker.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            reconcileGate.Release();
        }

        PublishStatus();
        return task;
    }

    private async ValueTask FinishPreviewAsync(int generation)
    {
        bool shouldReplay = false;
        await reconcileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (previewActive && previewGeneration == generation)
                {
                    previewActive = false;
                    shouldReplay = true;
                }
            }

            if (shouldReplay)
            {
                await ReconcileLatestLightingUnderGateAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            reconcileGate.Release();
            if (shouldReplay)
            {
                PublishStatus();
            }
        }
    }

    private async ValueTask<TaskStateSnapshot> ReconcileLatestLightingUnderGateAsync(
        CancellationToken cancellationToken)
    {
        TaskStateSnapshot task = reducer.Snapshot();
        bool isPaused;
        bool isPreviewActive;
        LightingSettings settings;
        lock (stateGate)
        {
            isPaused = paused;
            isPreviewActive = previewActive;
            settings = lightingSettings;
        }

        if (isPaused || isPreviewActive)
        {
            return task;
        }

        SideLightState? target = SideLightStateFactory.Create(task.AggregateState, settings);
        if (target is null)
        {
            await lightingWorker.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await lightingWorker.SetSideLightAsync(target.Bytes, cancellationToken).ConfigureAwait(false);
        }

        return task;
    }

    private async ValueTask<TaskStateSnapshot> StageLatestTargetOnPausedWorkerUnderGateAsync(
        CancellationToken cancellationToken)
    {
        TaskStateSnapshot task = reducer.Snapshot();
        LightingSettings settings;
        lock (stateGate)
        {
            settings = lightingSettings;
        }

        SideLightState? target = SideLightStateFactory.Create(task.AggregateState, settings);
        if (target is null)
        {
            await lightingWorker.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await lightingWorker.SetSideLightAsync(target.Bytes, cancellationToken).ConfigureAwait(false);
        }

        return task;
    }

    private async Task ProcessHookEventsAsync()
    {
        bool needsFinalReconcile = false;
        try
        {
            await foreach (bool _ in hookReconcileRequests.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await ReconcileLightingAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A storage/HID failure must not terminate hook ingestion. The
                    // worker snapshot exposes only an allowlisted diagnostic state.
                    PublishStatus();
                }
            }
        }
        catch (Exception)
        {
            // The reader itself is not expected to fail, but an unexpected
            // consumer exit must close admission before another Hook mutates
            // the reducer with nobody left to reconcile it.
        }
        finally
        {
            lock (hookLifecycleGate)
            {
                if (hookProcessingLifecycle == HookProcessingLifecycle.Running)
                {
                    hookProcessingLifecycle = HookProcessingLifecycle.Stopping;
                    hookReconcileRequests.Writer.TryComplete();
                    needsFinalReconcile = true;
                }
            }

            if (needsFinalReconcile)
            {
                try
                {
                    // Any Hook admitted before the unexpected exit has already
                    // reached the reducer. Reconcile the latest complete state
                    // once before declaring this consumer stopped.
                    await ReconcileLightingAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    PublishStatus();
                }

                lock (hookLifecycleGate)
                {
                    if (hookProcessingLifecycle == HookProcessingLifecycle.Stopping &&
                        hookStopCompletion is null)
                    {
                        hookProcessingLifecycle = HookProcessingLifecycle.Stopped;
                    }
                }
            }
        }
    }

    private async ValueTask<PipeEnvelope?> HandleHookMessageAsync(
        CodexHookEvent hookEvent,
        CancellationToken cancellationToken)
    {
        bool reconcileInline = false;
        lock (hookLifecycleGate)
        {
            switch (hookProcessingLifecycle)
            {
                case HookProcessingLifecycle.NotStarted:
                    // Tests and alternate hosts intentionally retain the direct
                    // path. Count it so Stop cannot report completion while an
                    // already-admitted Hook is still reconciling.
                    ApplyHookState(hookEvent);
                    if (inlineHookCount == 0)
                    {
                        inlineHooksDrained = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    inlineHookCount++;
                    reconcileInline = true;
                    break;

                case HookProcessingLifecycle.Running:
                    if (hookProcessingTask is null || hookProcessingTask.IsCompleted)
                    {
                        hookProcessingLifecycle = HookProcessingLifecycle.Stopped;
                        hookReconcileRequests.Writer.TryComplete();
                        WriteDiagnostic(
                            SanitizedDiagnosticEventType.HookRejected,
                            hookEvent.SessionId,
                            code: SanitizedDiagnosticCode.HostUnavailable);
                        return PipeEnvelope.Create(
                            PipeMessageKinds.Rejected,
                            new { reason = "host-stopping" });
                    }

                    // Admission, reducer mutation, and wake-up publication are
                    // atomic with respect to Stop. A full capacity-one channel
                    // may coalesce this signal, because its queued signal will
                    // read the reducer's latest complete state.
                    ApplyHookState(hookEvent);
                    if (!hookReconcileRequests.Writer.TryWrite(true))
                    {
                        // Completion is impossible while Running under this
                        // lock, but keep the accepted event safe if the channel
                        // implementation ever violates that invariant.
                        hookProcessingLifecycle = HookProcessingLifecycle.Stopping;
                        hookReconcileRequests.Writer.TryComplete();
                        if (inlineHookCount == 0)
                        {
                            inlineHooksDrained = new TaskCompletionSource<bool>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        inlineHookCount++;
                        reconcileInline = true;
                    }

                    break;

                case HookProcessingLifecycle.Stopping:
                case HookProcessingLifecycle.Stopped:
                    WriteDiagnostic(
                        SanitizedDiagnosticEventType.HookRejected,
                        hookEvent.SessionId,
                        code: SanitizedDiagnosticCode.HostUnavailable);
                    return PipeEnvelope.Create(
                        PipeMessageKinds.Rejected,
                        new { reason = "host-stopping" });

                default:
                    throw new InvalidOperationException("Unknown Hook processing lifecycle state.");
            }
        }

        if (!reconcileInline)
        {
            return null;
        }

        try
        {
            await ReconcileLightingAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (hookLifecycleGate)
            {
                if (inlineHookCount > 0)
                {
                    inlineHookCount--;
                    if (inlineHookCount == 0)
                    {
                        inlineHooksDrained?.TrySetResult(true);
                        inlineHooksDrained = null;
                    }
                }
                else if (hookProcessingLifecycle == HookProcessingLifecycle.Stopping &&
                    hookStopCompletion is null)
                {
                    // Defensive channel-completion fallback from the Running
                    // branch above reconciled the accepted state inline.
                    hookProcessingLifecycle = HookProcessingLifecycle.Stopped;
                }
            }

        }

        return null;
    }

    private async Task CompleteHookStopAsync(
        Task processingTask,
        Task inlineDrainTask,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await Task.WhenAll(processingTask, inlineDrainTask).ConfigureAwait(false);
            lock (hookLifecycleGate)
            {
                hookProcessingLifecycle = HookProcessingLifecycle.Stopped;
            }

            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            lock (hookLifecycleGate)
            {
                hookProcessingLifecycle = HookProcessingLifecycle.Stopped;
            }

            completion.TrySetException(exception);
        }
    }

    private void ApplyHookState(CodexHookEvent hookEvent)
    {
        lock (stateGate)
        {
            // A successfully normalized event reaching the Host proves that the
            // configured Codex command hook is both trusted and delivering.
            hookEnablement = HookEnablementState.Enabled;
        }

        TaskStateSnapshot previous = reducer.Snapshot();
        TaskStateSnapshot current = reducer.Apply(hookEvent);
        WriteDiagnostic(
            SanitizedDiagnosticEventType.HookReceived,
            hookEvent.SessionId,
            code: SanitizedDiagnosticCode.Succeeded);
        if (previous.AggregateState != current.AggregateState)
        {
            WriteDiagnostic(
                SanitizedDiagnosticEventType.StateChanged,
                hookEvent.SessionId,
                current.AggregateState,
                code: SanitizedDiagnosticCode.Succeeded);
        }
    }

    private void WriteDiagnostic(
        SanitizedDiagnosticEventType eventType,
        string? sessionId = null,
        TaskVisualState? visualState = null,
        long? latencyMilliseconds = null,
        LightingTransportFailureKind? transportFailure = null,
        SanitizedDiagnosticCode? code = null)
    {
        if (diagnosticLog is null)
        {
            return;
        }

        try
        {
            ValueTask pending = diagnosticLog.WriteAsync(
                eventType,
                sessionId,
                visualState,
                latencyMilliseconds,
                transportFailure,
                code);
            if (!pending.IsCompletedSuccessfully)
            {
                _ = ObserveDiagnosticWriteAsync(pending);
            }
        }
        catch (Exception)
        {
            // Diagnostics are advisory and must never reject an otherwise valid
            // Hook or delay physical state reconciliation.
        }
    }

    private static async Task ObserveDiagnosticWriteAsync(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void PublishStatus()
    {
        TaskStateSnapshot current = reducer.Snapshot();
        HostStatusSnapshot status;
        lock (stateGate)
        {
            status = CreateStatus(current);
        }

        EventHandler<HostStatusSnapshot>? handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<HostStatusSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, status);
            }
            catch (Exception)
            {
                // Status observers are advisory. One UI/SSE subscriber must
                // never abort the Host operation or starve later observers.
            }
        }
    }

    private HostStatusSnapshot CreateStatus(TaskStateSnapshot task)
    {
        return new HostStatusSnapshot(
            "online",
            paused,
            previewActive,
            hookEnablement,
            task.AggregateState,
            task.TrackedTurnCount,
            task.SessionCount,
            task.LastEventAtUtc,
            lightingWorker.Snapshot);
    }

    private static bool TryReadHook(JsonElement payload, out CodexHookEvent? hook)
    {
        hook = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "kind",
            "sessionId",
            "turnId",
            "toolName",
            "toolUseId",
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool hasKind = false;
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }

            hasKind |= string.Equals(property.Name, "kind", StringComparison.Ordinal);
        }

        if (!hasKind)
        {
            return false;
        }

        try
        {
            hook = payload.Deserialize<CodexHookEvent>(PipeJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (hook is null ||
            !Enum.IsDefined(hook.Kind) ||
            !IsIdentifier(hook.SessionId) ||
            (hook.IsTurnScoped && !IsIdentifier(hook.TurnId)))
        {
            hook = null;
            return false;
        }

        bool toolRequired = hook.Kind is CodexHookEventKind.PreToolUse or
            CodexHookEventKind.PermissionRequest or CodexHookEventKind.PostToolUse;
        if (toolRequired && !IsIdentifier(hook.ToolName))
        {
            hook = null;
            return false;
        }

        bool toolUseIdAllowed = hook.Kind is CodexHookEventKind.PreToolUse or
            CodexHookEventKind.PostToolUse;
        if (hook.ToolUseId is not null &&
            (!toolUseIdAllowed || !IsIdentifier(hook.ToolUseId)))
        {
            hook = null;
            return false;
        }

        if (hook.Kind == CodexHookEventKind.SessionEnd &&
            (hook.TurnId is not null || hook.ToolName is not null || hook.ToolUseId is not null))
        {
            hook = null;
            return false;
        }

        if (!toolRequired && (hook.ToolName is not null || hook.ToolUseId is not null))
        {
            hook = null;
            return false;
        }

        return true;
    }

    private static bool IsIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Length <= CodexHookNormalizer.MaxIdentifierLength &&
            !value.Any(char.IsControl);
    }
}
