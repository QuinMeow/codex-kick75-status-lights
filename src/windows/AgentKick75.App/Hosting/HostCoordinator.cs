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

public enum ApplicationLifecycleState
{
    Starting,
    Running,
    Paused,
    Stopping,
    Faulted,
    Stopped,
}

public enum LifecycleFaultCode
{
    ProtocolError,
    RestoreFailed,
    StartupRecoveryFailed,
}

public enum LifecycleStopReason
{
    NormalExit,
    PrepareUninstall,
}

public sealed record LifecycleStopResult(bool Succeeded, LifecycleFaultCode? FaultCode = null);

public sealed record HostStatusSnapshot(
    string Host,
    ApplicationLifecycleState LifecycleState,
    LifecycleFaultCode? FaultCode,
    bool IsPreviewActive,
    HookEnablementState HookEnablement,
    TaskVisualState AggregateState,
    int ActiveSessionCount,
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
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private readonly Channel<bool> hookReconcileRequests;
    private readonly object hookLifecycleGate = new();
    private readonly object stateGate = new();
    private LightingSettings lightingSettings;
    private bool startAtLogin;
    private ApplicationLifecycleState lifecycleState;
    private LifecycleFaultCode? faultCode;
    private bool previewActive;
    private int previewGeneration;
    private HookEnablementState hookEnablement;
    private Task? hookProcessingTask;
    private HookProcessingLifecycle hookProcessingLifecycle;
    private TaskCompletionSource<bool>? hookStopCompletion;
    private TaskCompletionSource<bool>? inlineHooksDrained;
    private int inlineHookCount;
    private Lazy<Task<LifecycleStopResult>>? stopOperation;
    private TaskVisualState lastCoordinatedAggregate = TaskVisualState.Idle;
    private DateTimeOffset? lastKeepAwakeRefreshAt;

    public HostCoordinator(
        TaskStateReducer reducer,
        HidLightingWorker lightingWorker,
        LightingSettings? lightingSettings = null,
        IHardwareTestCommand? hardwareTest = null,
        IHostSettingsPersistence? settingsPersistence = null,
        bool startAtLogin = false,
        ISanitizedDiagnosticLog? diagnosticLog = null,
        ApplicationLifecycleState initialLifecycleState = ApplicationLifecycleState.Running,
        TimeProvider? timeProvider = null)
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
        this.timeProvider = timeProvider ?? TimeProvider.System;
        lifecycleState = initialLifecycleState;
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

    public event EventHandler? ShutdownRequested;

    public void MarkRunning()
    {
        lock (stateGate)
        {
            if (lifecycleState != ApplicationLifecycleState.Starting)
            {
                throw new InvalidOperationException("Only a starting Host can enter Running.");
            }

            lifecycleState = ApplicationLifecycleState.Running;
            faultCode = null;
        }

        PublishStatus();
    }

    public void MarkStopped()
    {
        lock (stateGate)
        {
            lifecycleState = ApplicationLifecycleState.Stopped;
        }
    }

    public void MarkStartupFault()
    {
        lock (stateGate)
        {
            if (lifecycleState == ApplicationLifecycleState.Starting)
            {
                lifecycleState = ApplicationLifecycleState.Faulted;
                faultCode = LifecycleFaultCode.StartupRecoveryFailed;
            }
        }

        PublishStatus();
    }

    public void NotifyPrepareUninstallResponseFlushed()
    {
        EventHandler? handlers = ShutdownRequested;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // The restore transaction and response are already complete.
                // One UI observer must not terminate the pipe listener.
            }
        }
    }

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
        EnsureHookAdmission();
        ApplyHookState(hookEvent);
        await ReconcileLightingAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CleanupAsync(CancellationToken cancellationToken = default)
    {
        reducer.CleanupStale();
        TaskStateSnapshot current = reducer.Snapshot();
        ApplicationLifecycleState lifecycle;
        bool isPreviewActive;
        lock (stateGate)
        {
            lifecycle = lifecycleState;
            isPreviewActive = previewActive;
        }

        if (lifecycle != ApplicationLifecycleState.Running || isPreviewActive)
        {
            return;
        }

        if (current.AggregateState != lastCoordinatedAggregate)
        {
            await ReconcileLightingAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LightingSettings settings;
            DateTimeOffset? lastRefresh;
            lock (stateGate)
            {
                settings = lightingSettings;
                lastRefresh = lastKeepAwakeRefreshAt;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            bool refreshDue = settings.KeepAwake.Policy != KeepAwakePolicy.Disabled &&
                (lastRefresh is null || now - lastRefresh >= settings.KeepAwake.RefreshInterval);

            if (refreshDue && current.AggregateState != TaskVisualState.Idle)
            {
                await lightingWorker.RefreshDesiredSideLightAsync(cancellationToken)
                    .ConfigureAwait(false);
                lock (stateGate)
                {
                    lastKeepAwakeRefreshAt = now;
                }
            }
            else if (refreshDue && settings.KeepAwake.Policy == KeepAwakePolicy.WhileHostRunning)
            {
                await lightingWorker.PulseCurrentSideLightAsync(cancellationToken)
                    .ConfigureAwait(false);
                lock (stateGate)
                {
                    lastKeepAwakeRefreshAt = now;
                }
            }
            else if (current.AggregateState != TaskVisualState.Idle)
            {
                await lightingWorker.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }

            PromoteWorkerFaultIfNeeded();
            PublishStatus();
        }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                RequireLifecycle(ApplicationLifecycleState.Running);
                lifecycleState = ApplicationLifecycleState.Paused;
                previewActive = false;
                previewGeneration++;
            }

            try
            {
                await lightingWorker.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                SetFault(LifecycleFaultCode.RestoreFailed);
                throw;
            }
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
            lock (stateGate)
            {
                RequireLifecycle(ApplicationLifecycleState.Paused);
            }

            try
            {
                // Stage the newest aggregate target while both Host and worker
                // remain paused. Commit the visible Host state only after the
                // worker has resumed successfully.
                await StageLatestTargetOnPausedWorkerUnderGateAsync(cancellationToken)
                    .ConfigureAwait(false);
                await lightingWorker.ResumeAsync(cancellationToken).ConfigureAwait(false);
                PromoteWorkerFaultIfNeeded();
                lock (stateGate)
                {
                    if (lifecycleState == ApplicationLifecycleState.Paused)
                    {
                        lifecycleState = ApplicationLifecycleState.Running;
                        faultCode = null;
                    }
                    else if (lifecycleState == ApplicationLifecycleState.Faulted)
                    {
                        throw new InvalidOperationException(
                            "The HID worker faulted while lighting takeover was resuming.");
                    }
                }
            }
            catch
            {
                bool alreadyFaulted;
                lock (stateGate)
                {
                    alreadyFaulted = lifecycleState == ApplicationLifecycleState.Faulted;
                }

                if (!alreadyFaulted)
                {
                    SetFault(LifecycleFaultCode.RestoreFailed);
                }

                throw;
            }
        }
        finally
        {
            reconcileGate.Release();
        }

        PublishStatus();
    }

    public Task<LifecycleStopResult> StopAsync(
        LifecycleStopReason reason,
        CancellationToken cancellationToken = default)
    {
        Lazy<Task<LifecycleStopResult>> operation;
        lock (stateGate)
        {
            stopOperation ??= new Lazy<Task<LifecycleStopResult>>(
                () => StopCoreAsync(reason),
                LazyThreadSafetyMode.ExecutionAndPublication);
            operation = stopOperation;
        }

        Task<LifecycleStopResult> task = operation.Value;
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task<LifecycleStopResult> StopCoreAsync(LifecycleStopReason reason)
    {
        _ = reason;
        lock (stateGate)
        {
            lifecycleState = ApplicationLifecycleState.Stopping;
            faultCode = null;
            previewActive = false;
            previewGeneration++;
        }

        PublishStatus();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await StopEventProcessingAsync().AsTask().WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            await reconcileGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                await lightingWorker.QuiesceAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                reconcileGate.Release();
            }

            lock (stateGate)
            {
                lifecycleState = ApplicationLifecycleState.Stopped;
            }

            PublishStatus();

            return new LifecycleStopResult(true);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lightingWorker.RequestCancellation();
            SetFault(LifecycleFaultCode.RestoreFailed, force: true);
            return new LifecycleStopResult(false, LifecycleFaultCode.RestoreFailed);
        }
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
                    RequireLifecycle(ApplicationLifecycleState.Running);

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
            if (lifecycleState is ApplicationLifecycleState.Starting or
                ApplicationLifecycleState.Stopping or
                ApplicationLifecycleState.Faulted or
                ApplicationLifecycleState.Stopped)
            {
                throw new InvalidOperationException("Settings cannot change in the current lifecycle state.");
            }

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
            lastKeepAwakeRefreshAt = null;
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
        bool quiesceAttempted = false;
        try
        {
            lock (stateGate)
            {
                RequireLifecycle(ApplicationLifecycleState.Running);
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
                    shouldResume = lifecycleState == ApplicationLifecycleState.Running;
                }

                if (quiesceAttempted && shouldResume)
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
                        SetFault(LifecycleFaultCode.RestoreFailed);
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

            case PipeMessageKinds.PrepareUninstallRequest:
                LifecycleStopResult result = await StopAsync(
                    LifecycleStopReason.PrepareUninstall,
                    cancellationToken).ConfigureAwait(false);
                return result.Succeeded
                    ? PipeEnvelope.Create(PipeMessageKinds.Accepted, new { })
                    : PipeEnvelope.Create(
                        PipeMessageKinds.Rejected,
                        new { reason = "restore-failed", faultCode = result.FaultCode?.ToString() });

            default:
                WriteDiagnostic(
                    SanitizedDiagnosticEventType.ControlRequestRejected,
                    code: SanitizedDiagnosticCode.InvalidInput);
                return PipeEnvelope.Create(PipeMessageKinds.Rejected, new { reason = "unknown-kind" });
        }
    }

    private async ValueTask<TaskStateSnapshot> ReconcileLightingAsync(
        CancellationToken cancellationToken)
    {
        TaskStateSnapshot task;
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            task = await ReconcileLatestLightingUnderGateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            PromoteWorkerFaultIfNeeded();
            PublishStatus();
            throw;
        }
        finally
        {
            reconcileGate.Release();
        }

        PublishStatus();
        PromoteWorkerFaultIfNeeded();
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

            bool running;
            lock (stateGate)
            {
                running = lifecycleState == ApplicationLifecycleState.Running;
            }

            if (shouldReplay && running)
            {
                try
                {
                    await ReconcileLatestLightingUnderGateAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    PromoteWorkerFaultIfNeeded();
                    throw;
                }
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
        ApplicationLifecycleState lifecycle;
        bool isPreviewActive;
        LightingSettings settings;
        lock (stateGate)
        {
            lifecycle = lifecycleState;
            isPreviewActive = previewActive;
            settings = lightingSettings;
        }

        if (lifecycle != ApplicationLifecycleState.Running || isPreviewActive)
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
            lock (stateGate)
            {
                lastKeepAwakeRefreshAt = timeProvider.GetUtcNow();
            }
        }

        lastCoordinatedAggregate = task.AggregateState;

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
        lock (stateGate)
        {
            if (lifecycleState is not (ApplicationLifecycleState.Running or
                ApplicationLifecycleState.Paused))
            {
                return PipeEnvelope.Create(
                    PipeMessageKinds.Rejected,
                    new { reason = "host-stopping" });
            }
        }

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

    private void EnsureHookAdmission()
    {
        lock (stateGate)
        {
            if (lifecycleState is not (ApplicationLifecycleState.Running or
                ApplicationLifecycleState.Paused))
            {
                throw new InvalidOperationException("The Host is not accepting Hook events.");
            }
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

    private void RequireLifecycle(ApplicationLifecycleState expected)
    {
        if (lifecycleState != expected)
        {
            throw new InvalidOperationException(
                $"This operation requires lifecycle {expected}; current state is {lifecycleState}.");
        }
    }

    private void SetFault(LifecycleFaultCode code, bool force = false)
    {
        bool changed = false;
        lock (stateGate)
        {
            if (!force && lifecycleState is (
                    ApplicationLifecycleState.Stopping or
                    ApplicationLifecycleState.Stopped))
            {
                return;
            }

            lifecycleState = ApplicationLifecycleState.Faulted;
            faultCode = code;
            previewActive = false;
            previewGeneration++;
            changed = true;
        }

        if (changed)
        {
            PublishStatus();
        }
    }

    private void PromoteWorkerFaultIfNeeded()
    {
        LightingWorkerSnapshot lighting = lightingWorker.Snapshot;
        if (lighting.State != LightingWorkerState.Faulted)
        {
            return;
        }

        bool changed = false;
        lock (stateGate)
        {
            if (lifecycleState is ApplicationLifecycleState.Running or
                ApplicationLifecycleState.Paused)
            {
                lifecycleState = ApplicationLifecycleState.Faulted;
                faultCode = lighting.LastFailure == LightingTransportFailureKind.BaselineMismatch
                    ? LifecycleFaultCode.RestoreFailed
                    : LifecycleFaultCode.ProtocolError;
                previewActive = false;
                previewGeneration++;
                changed = true;
            }
        }

        if (changed)
        {
            PublishStatus();
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
            lifecycleState,
            faultCode,
            previewActive,
            hookEnablement,
            task.AggregateState,
            task.ActiveSessionCount,
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

        if (hook.Kind == CodexHookEventKind.SessionEnd &&
            (hook.TurnId is not null || hook.ToolName is not null))
        {
            hook = null;
            return false;
        }

        if (!toolRequired && hook.ToolName is not null)
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
