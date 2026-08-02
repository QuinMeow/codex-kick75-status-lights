// SPDX-License-Identifier: MIT
using System.Threading.Channels;
using AgentKick75.Core.Baseline;

namespace AgentKick75.App.Lighting;

public enum LightingWorkerState
{
    Stopped,
    Idle,
    Applying,
    Active,
    Restoring,
    Paused,
    Reconnecting,
    Faulted,
}

public sealed record LightingWorkerSnapshot(
    LightingWorkerState State,
    string? DeviceIdentity,
    string? TransportProfile,
    LightingTransportFailureKind? LastFailure,
    int ReconnectAttempt,
    DateTimeOffset UpdatedAtUtc,
    string? InterfaceFingerprint = null,
    LightingDeviceObservationKind DeviceObservation = LightingDeviceObservationKind.None,
    LightingDeviceSupport? DeviceSupport = null,
    LightingDeviceDescriptorMetadata? DescriptorMetadata = null);

public sealed class HidLightingWorker : IAsyncDisposable
{
    public const int SideLightStateLength = 8;
    public static TimeSpan HealthProbeInterval { get; } = TimeSpan.FromSeconds(10);

    private readonly ILightingTransport transport;
    private readonly IBaselineOwnershipStore baselineStore;
    private readonly LayeredReconnectPolicy reconnectPolicy;
    private readonly IReconnectDelay reconnectDelay;
    private readonly TimeProvider timeProvider;
    private readonly Channel<WorkerCommand> commands;
    private readonly CancellationTokenSource stopSource = new();
    private readonly object snapshotGate = new();

    private Task? processingTask;
    private LightingDeviceSession? session;
    private LightingDeviceInspection? descriptorObservation;
    private BaselineOwnershipRecord? activeBaseline;
    private byte[]? desiredState;
    private bool targetInitialized;
    private DateTimeOffset? lastHealthProbeAt;
    private bool paused;
    private bool stopping;
    private int reconnectAttempt;
    private int retryGeneration;
    private LightingWorkerSnapshot snapshot;

    public HidLightingWorker(
        ILightingTransport transport,
        IBaselineOwnershipStore baselineStore,
        LayeredReconnectPolicy? reconnectPolicy = null,
        IReconnectDelay? reconnectDelay = null,
        TimeProvider? timeProvider = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.baselineStore = baselineStore ?? throw new ArgumentNullException(nameof(baselineStore));
        this.reconnectPolicy = reconnectPolicy ?? new LayeredReconnectPolicy();
        this.reconnectDelay = reconnectDelay ?? new SystemReconnectDelay();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        commands = Channel.CreateUnbounded<WorkerCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        snapshot = NewSnapshot(LightingWorkerState.Stopped);
    }

    public event EventHandler<LightingWorkerSnapshot>? SnapshotChanged;

    public LightingWorkerSnapshot Snapshot
    {
        get
        {
            lock (snapshotGate)
            {
                return snapshot;
            }
        }
    }

    public void Start()
    {
        if (processingTask is not null)
        {
            throw new InvalidOperationException("The HID worker has already been started.");
        }

        UpdateSnapshot(LightingWorkerState.Idle);
        processingTask = ProcessAsync(stopSource.Token);
    }

    public Task SetSideLightAsync(
        ReadOnlyMemory<byte> sideLightState,
        CancellationToken cancellationToken = default)
    {
        InMemoryBaselineOwnershipStore.ValidateSideLight(sideLightState.Span);
        return EnqueueAsync(new SetStateCommand(sideLightState.ToArray()), cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new PauseCommand(), cancellationToken);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new ResumeCommand(), cancellationToken);
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new RestoreCommand(), cancellationToken);
    }

    /// <summary>
    /// Restores and verifies any owned baseline, then closes the HID session so
    /// one guarded external hardware test can obtain exclusive device access.
    /// The desired state is retained and can be replayed with <see cref="ResumeAsync"/>.
    /// </summary>
    public Task QuiesceAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new QuiesceCommand(), cancellationToken);
    }

    public Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new ProbeCommand(), cancellationToken);
    }

    public Task RefreshDesiredSideLightAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new RefreshDesiredStateCommand(), cancellationToken);
    }

    public Task PulseCurrentSideLightAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new PulseCurrentStateCommand(), cancellationToken);
    }

    /// <summary>
    /// Completes an unfinished restore transaction before any Host listener is
    /// exposed. A missing record succeeds without opening the device.
    /// </summary>
    public Task RecoverPendingRestoreAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(new RecoverPendingRestoreCommand(), cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (processingTask is null)
        {
            return;
        }

        if (stopSource.IsCancellationRequested)
        {
            try
            {
                await processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
            }

            return;
        }

        if (!stopping)
        {
            await EnqueueAsync(new StopCommand(), cancellationToken).ConfigureAwait(false);
        }

        await processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void RequestCancellation()
    {
        try
        {
            stopSource.Cancel();
        }
        catch (Exception)
        {
            // Stop has already failed. Cancellation is best-effort and must not
            // replace the restore failure returned to the Host.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await stopSource.CancelAsync().ConfigureAwait(false);
            stopSource.Dispose();
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task EnqueueAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        if (processingTask is null)
        {
            throw new InvalidOperationException("The HID worker has not been started.");
        }

        if (stopping)
        {
            throw new InvalidOperationException("The HID worker is stopping.");
        }

        return EnqueueCoreAsync(command, cancellationToken);
    }

    private async Task EnqueueCoreAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        await commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (WorkerCommand command in commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    bool keepRunning = await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
                    command.Completion.TrySetResult();
                    if (!keepRunning)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    command.Completion.TrySetCanceled(exception.CancellationToken);
                    break;
                }
                catch (Exception exception)
                {
                    UpdateSnapshot(
                        LightingWorkerState.Faulted,
                        exception is LightingTransportException transportException
                            ? transportException.Kind
                            : null);
                    command.Completion.TrySetException(exception);
                }
            }
        }
        finally
        {
            commands.Writer.TryComplete();
            var stoppedToken = cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true);
            while (commands.Reader.TryRead(out WorkerCommand? pending))
            {
                pending!.Completion.TrySetCanceled(stoppedToken);
            }

            UpdateSnapshot(LightingWorkerState.Stopped);
        }
    }

    private async ValueTask<bool> ExecuteCommandAsync(
        WorkerCommand command,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case SetStateCommand setState:
                if (targetInitialized &&
                    desiredState is not null &&
                    desiredState.AsSpan().SequenceEqual(setState.State) &&
                    CanSkipDuplicateTarget())
                {
                    return true;
                }

                desiredState = setState.State;
                targetInitialized = true;
                InvalidatePendingRetry();
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case PauseCommand:
                paused = true;
                InvalidatePendingRetry();
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case ResumeCommand:
                paused = false;
                InvalidatePendingRetry();
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case RestoreCommand:
                if (targetInitialized && desiredState is null && CanSkipDuplicateTarget())
                {
                    return true;
                }

                desiredState = null;
                targetInitialized = true;
                InvalidatePendingRetry();
                await RestoreIfOwnedAsync(cancellationToken).ConfigureAwait(false);
                await DisconnectAsync(cancellationToken).ConfigureAwait(false);
                descriptorObservation = null;
                reconnectAttempt = 0;
                UpdateSnapshot(paused ? LightingWorkerState.Paused : LightingWorkerState.Idle);
                return true;

            case QuiesceCommand:
                paused = true;
                InvalidatePendingRetry();
                await RestoreIfOwnedAsync(cancellationToken).ConfigureAwait(false);
                await DisconnectAsync(cancellationToken).ConfigureAwait(false);
                descriptorObservation = null;
                UpdateSnapshot(LightingWorkerState.Paused);
                return true;

            case ProbeCommand:
                await ProbeIfDueAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case RefreshDesiredStateCommand:
                if (!paused && desiredState is not null)
                {
                    InvalidatePendingRetry();
                    await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                }

                return true;

            case PulseCurrentStateCommand:
                await PulseCurrentStateAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case RecoverPendingRestoreCommand:
                BaselineOwnershipRecord? pending = await baselineStore.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (pending?.IsOwned == true)
                {
                    await EnsureConnectedAsync(pending, cancellationToken).ConfigureAwait(false);
                    await DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }

                descriptorObservation = null;
                reconnectAttempt = 0;
                UpdateSnapshot(LightingWorkerState.Idle);
                return true;

            case RetryCommand retry when retry.Generation == retryGeneration:
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case RetryCommand:
                return true;

            case StopCommand:
                stopping = true;
                paused = true;
                desiredState = null;
                targetInitialized = true;
                descriptorObservation = null;
                InvalidatePendingRetry();
                await TryRestoreForShutdownAsync(cancellationToken).ConfigureAwait(false);
                await TryDisconnectForShutdownAsync(cancellationToken).ConfigureAwait(false);
                return false;

            default:
                throw new InvalidOperationException("Unknown HID worker command.");
        }
    }

    private async ValueTask ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (paused || desiredState is null)
            {
                await RestoreIfOwnedAsync(cancellationToken).ConfigureAwait(false);
                UpdateSnapshot(paused ? LightingWorkerState.Paused : LightingWorkerState.Idle);
            }
            else
            {
                UpdateSnapshot(LightingWorkerState.Applying);
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await EnsureBaselineOwnedAsync(cancellationToken).ConfigureAwait(false);
                await transport.WriteSideLightAsync(desiredState, cancellationToken).ConfigureAwait(false);
                lastHealthProbeAt = timeProvider.GetUtcNow();
                reconnectAttempt = 0;
                UpdateSnapshot(LightingWorkerState.Active);
            }
        }
        catch (LightingTransportException exception)
        {
            await HandleTransportFailureAsync(exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask PulseCurrentStateAsync(CancellationToken cancellationToken)
    {
        if (paused || desiredState is not null)
        {
            return;
        }

        try
        {
            UpdateSnapshot(LightingWorkerState.Applying);
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureBaselineOwnedAsync(cancellationToken).ConfigureAwait(false);
            BaselineOwnershipRecord baseline = activeBaseline ?? throw new InvalidOperationException(
                "A side-light keep-awake pulse requires a captured baseline.");

            // The pulse deliberately writes the exact state just captured. The
            // normal restore transaction supplies durable recovery, readback,
            // and ownership release even though the intended visual delta is zero.
            await RestoreAndReleaseAsync(baseline, cancellationToken).ConfigureAwait(false);
            activeBaseline = null;
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            descriptorObservation = null;
            reconnectAttempt = 0;
            lastHealthProbeAt = timeProvider.GetUtcNow();
            UpdateSnapshot(LightingWorkerState.Idle);
        }
        catch (LightingTransportException exception)
        {
            await HandleTransportFailureAsync(exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask EnsureConnectedAsync(CancellationToken cancellationToken) =>
        EnsureConnectedAsync(persistedOverride: null, cancellationToken);

    private async ValueTask EnsureConnectedAsync(
        BaselineOwnershipRecord? persistedOverride,
        CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            return;
        }

        // Validate an owned record's profile before opening any HID handle. Old
        // or unknown profiles must fail closed; the runtime only selects USB.
        BaselineOwnershipRecord? persisted = persistedOverride ??
            await baselineStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        LightingConnectionRequest connectionRequest = persisted?.IsOwned == true
            ? LightingConnectionRequest.ForOwnedBaseline(persisted.TransportProfile)
            : LightingConnectionRequest.Usb;
        LightingDeviceSession connected = await transport
            .ConnectAsync(connectionRequest, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (persisted?.IsOwned == true)
            {
                BaselineRecoveryDecision decision = PlanOwnedRecovery(persisted, connected);
                if (decision.Action != BaselineRecoveryAction.RestoreBeforeAcquire)
                {
                    if (decision.Action is BaselineRecoveryAction.RefuseDeviceIdentityMismatch or
                        BaselineRecoveryAction.RefuseInterfaceFingerprintMismatch or
                        BaselineRecoveryAction.RefuseTransportProfileMismatch)
                    {
                        // The record belongs to another keyboard or HID interface.
                        // Abandon it without writing any saved bytes to this device.
                        await baselineStore.MarkReleasedAsync(
                            persisted.OwnershipMarker,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new LightingTransportException(
                            LightingTransportFailureKind.BaselineMismatch,
                            decision.Reason);
                    }
                }
                else
                {
                    InMemoryBaselineOwnershipStore.ValidateSideLight(persisted.SideLightState);
                    UpdateSnapshot(LightingWorkerState.Restoring);
                    await RestoreAndReleaseAsync(persisted, cancellationToken).ConfigureAwait(false);
                }
            }

            // A session becomes visible only after durable recovery state has
            // been loaded and any prior ownership has been verified/released.
            session = connected;
            descriptorObservation = null;
        }
        catch
        {
            try
            {
                await transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the original recovery/storage failure. The logical
                // session is still discarded and no lighting write may follow.
            }

            session = null;
            throw;
        }
    }

    private async ValueTask EnsureBaselineOwnedAsync(CancellationToken cancellationToken)
    {
        if (activeBaseline is not null)
        {
            return;
        }

        LightingDeviceSession currentSession = session ??
            throw new InvalidOperationException("Cannot capture a baseline without a transport session.");
        byte[] baseline = await transport.ReadSideLightAsync(cancellationToken).ConfigureAwait(false);
        InMemoryBaselineOwnershipStore.ValidateSideLight(baseline);

        var capturedBaseline = new BaselineOwnershipRecord(
            Version: BaselineRecord.CurrentSchemaVersion,
            currentSession.DeviceIdentity,
            currentSession.TransportProfile,
            currentSession.InterfaceFingerprint,
            baseline.ToArray(),
            currentSession.CurrentMode,
            BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
            IsOwned: true,
            timeProvider.GetUtcNow());
        // The durable restore record is the safety boundary. Never expose an
        // in-memory baseline (and therefore never write lighting) until the
        // minimal recovery data has been persisted successfully.
        await baselineStore.SaveAsync(capturedBaseline, cancellationToken).ConfigureAwait(false);
        activeBaseline = capturedBaseline;
    }

    private async ValueTask RestoreIfOwnedAsync(CancellationToken cancellationToken)
    {
        if (activeBaseline is null)
        {
            BaselineOwnershipRecord? persisted = await baselineStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (persisted?.IsOwned != true)
            {
                return;
            }

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        UpdateSnapshot(LightingWorkerState.Restoring);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await RestoreAndReleaseAsync(activeBaseline, cancellationToken).ConfigureAwait(false);
        activeBaseline = null;
        reconnectAttempt = 0;
    }

    private async ValueTask RestoreAndReleaseAsync(
        BaselineOwnershipRecord baseline,
        CancellationToken cancellationToken)
    {
        await transport.WriteSideLightAsync(baseline.SideLightState, cancellationToken).ConfigureAwait(false);
        byte[] readBack = await transport.ReadSideLightAsync(cancellationToken).ConfigureAwait(false);
        InMemoryBaselineOwnershipStore.ValidateSideLight(readBack);
        if (!baseline.SideLightState.AsSpan().SequenceEqual(readBack))
        {
            throw new LightingTransportException(
                LightingTransportFailureKind.BaselineMismatch,
                "The side-light baseline readback did not match; ownership remains active.");
        }

        await baselineStore.MarkReleasedAsync(
            baseline.OwnershipMarker,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TryRestoreForShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RestoreIfOwnedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The restore record deliberately remains when physical restore cannot
            // complete. A future Host start handles it before accepting Hooks.
            LightingTransportFailureKind failure = exception is LightingTransportException transportException
                ? transportException.Kind
                : LightingTransportFailureKind.ProtocolViolation;
            UpdateSnapshot(LightingWorkerState.Faulted, failure);
        }
    }

    private async ValueTask ProbeIfDueAsync(CancellationToken cancellationToken)
    {
        LightingWorkerSnapshot current = Snapshot;
        if (session is null)
        {
            if (current.State is not (LightingWorkerState.Idle or LightingWorkerState.Paused))
            {
                return;
            }

            BaselineOwnershipRecord? persisted = await baselineStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (persisted?.IsOwned == true)
            {
                await RecoverOwnedBaselineFromIdleProbeAsync(
                    persisted,
                    current.State,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await InspectDescriptorsIfDueAsync(current.State, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        byte[]? target = desiredState;
        if (paused || target is null || current.State != LightingWorkerState.Active)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (lastHealthProbeAt is not null && now - lastHealthProbeAt < HealthProbeInterval)
        {
            return;
        }

        lastHealthProbeAt = now;
        try
        {
            byte[] observed = await transport.ReadSideLightAsync(cancellationToken).ConfigureAwait(false);
            InMemoryBaselineOwnershipStore.ValidateSideLight(observed);
            if (!observed.AsSpan().SequenceEqual(target))
            {
                if (activeBaseline is null)
                {
                    throw new LightingTransportException(
                        LightingTransportFailureKind.BaselineMismatch,
                        "The active side-light state drifted without an owned baseline; corrective writes are blocked.");
                }

                // Keep the original durable baseline and correct the drift in
                // the same owned session. Never recapture the observed external
                // state as a new baseline here.
                UpdateSnapshot(LightingWorkerState.Applying);
                await transport.WriteSideLightAsync(target, cancellationToken).ConfigureAwait(false);
                byte[] corrected = await transport.ReadSideLightAsync(cancellationToken).ConfigureAwait(false);
                InMemoryBaselineOwnershipStore.ValidateSideLight(corrected);
                if (!corrected.AsSpan().SequenceEqual(target))
                {
                    throw new LightingTransportException(
                        LightingTransportFailureKind.ProtocolViolation,
                        "The health-probe corrective write did not match the desired side-light state.");
                }
            }

            reconnectAttempt = 0;
            UpdateSnapshot(LightingWorkerState.Active);
        }
        catch (LightingTransportException exception)
        {
            await HandleTransportFailureAsync(exception, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await HandleTransportFailureAsync(
                new LightingTransportException(
                    LightingTransportFailureKind.ProtocolViolation,
                    "The health probe returned an invalid side-light state.",
                    exception),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RecoverOwnedBaselineFromIdleProbeAsync(
        BaselineOwnershipRecord persisted,
        LightingWorkerState currentState,
        CancellationToken cancellationToken)
    {
        try
        {
            // Startup recovery always wins over descriptor-only inventory. The
            // persisted profile constrains selection before a HID session opens;
            // EnsureConnectedAsync restores, verifies, and releases the marker.
            await EnsureConnectedAsync(persisted, cancellationToken).ConfigureAwait(false);
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            descriptorObservation = null;
            reconnectAttempt = 0;
            UpdateSnapshot(currentState);
        }
        catch (LightingTransportException exception)
        {
            await HandleTransportFailureAsync(exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask InspectDescriptorsIfDueAsync(
        LightingWorkerState currentState,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (lastHealthProbeAt is not null && now - lastHealthProbeAt < HealthProbeInterval)
        {
            return;
        }

        lastHealthProbeAt = now;
        try
        {
            descriptorObservation = await transport.InspectAsync(cancellationToken)
                .ConfigureAwait(false);
            UpdateSnapshot(currentState);
        }
        catch (LightingTransportException exception)
        {
            descriptorObservation = null;
            UpdateSnapshot(currentState, exception.Kind);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            descriptorObservation = null;
            UpdateSnapshot(currentState, LightingTransportFailureKind.ProtocolViolation);
        }
    }

    private async ValueTask TryDisconnectForShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LightingTransportFailureKind failure = exception is LightingTransportException transportException
                ? transportException.Kind
                : LightingTransportFailureKind.DeviceDisconnected;
            UpdateSnapshot(LightingWorkerState.Faulted, failure);
        }
    }

    private async ValueTask HandleTransportFailureAsync(
        LightingTransportException exception,
        CancellationToken cancellationToken)
    {
        // The persisted marker must stay owned, but the in-memory capture no
        // longer represents a live takeover. On reconnect EnsureConnectedAsync
        // first restores that marker, then a fresh baseline is captured before
        // the desired state is replayed.
        activeBaseline = null;
        await DisconnectAfterFailureAsync(cancellationToken).ConfigureAwait(false);
        reconnectAttempt++;

        if (!reconnectPolicy.IsTransient(exception.Kind))
        {
            UpdateSnapshot(LightingWorkerState.Faulted, exception.Kind);
            return;
        }

        TimeSpan delay = reconnectPolicy.GetDelay(exception.Kind, reconnectAttempt);
        int generation = ++retryGeneration;
        UpdateSnapshot(LightingWorkerState.Reconnecting, exception.Kind);
        _ = ScheduleRetryAsync(generation, delay, stopSource.Token);
    }

    private async Task ScheduleRetryAsync(
        int generation,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await reconnectDelay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            await commands.Writer.WriteAsync(new RetryCommand(generation), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return;
        }

        await transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        session = null;
    }

    private async ValueTask DisconnectAfterFailureAsync(CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LightingTransportException)
        {
            // A failed/disappeared device can make close diagnostics fail too.
            // The logical session is still dropped and the durable restore record
            // is deliberately preserved for recovery.
        }
        finally
        {
            session = null;
        }
    }

    private void InvalidatePendingRetry()
    {
        retryGeneration++;
        reconnectAttempt = 0;
    }

    private bool CanSkipDuplicateTarget()
    {
        LightingWorkerSnapshot current = Snapshot;
        if (current.State != LightingWorkerState.Faulted)
        {
            return true;
        }

        // Explicit protocol/baseline No-Go failures remain latched until the
        // requested target changes. Generic storage faults have no allowlisted
        // failure code and may be retried by the next identical cleanup request.
        return current.LastFailure is
            LightingTransportFailureKind.ProtocolViolation or
            LightingTransportFailureKind.BaselineMismatch;
    }

    private static BaselineRecoveryDecision PlanOwnedRecovery(
        BaselineOwnershipRecord baseline,
        LightingDeviceSession currentSession)
    {
        var baselineIdentity = new BaselineDeviceIdentity(
            baseline.DeviceIdentity,
            baseline.TransportProfile,
            baseline.InterfaceFingerprint);
        var ownership = new BaselineOwnership(
            baseline.OwnershipMarker,
            isOwned: true,
            baseline.CapturedAtUtc);
        var coreRecord = new BaselineRecord(
            baselineIdentity,
            baseline.SideLightState,
            baseline.CurrentMode,
            ownership,
            baseline.Version);
        var currentIdentity = new BaselineDeviceIdentity(
            currentSession.DeviceIdentity,
            currentSession.TransportProfile,
            currentSession.InterfaceFingerprint);
        return BaselineRecoveryPlanner.Decide(
            new BaselineLoadResult(BaselineLoadStatus.Loaded, coreRecord),
            currentIdentity,
            currentSession.CurrentMode);
    }

    private LightingWorkerSnapshot NewSnapshot(
        LightingWorkerState state,
        LightingTransportFailureKind? failure = null)
    {
        LightingDeviceSession? currentSession = session;
        LightingDeviceInspection? currentDescriptor = descriptorObservation;
        return new(
            state,
            currentSession?.DeviceIdentity ?? currentDescriptor?.DeviceIdentity,
            currentSession?.TransportProfile ?? currentDescriptor?.TransportProfile,
            failure,
            reconnectAttempt,
            timeProvider.GetUtcNow(),
            currentSession?.InterfaceFingerprint ?? currentDescriptor?.InterfaceFingerprint,
            currentSession is not null
                ? LightingDeviceObservationKind.RuntimeSession
                : currentDescriptor is not null
                    ? LightingDeviceObservationKind.Descriptor
                    : LightingDeviceObservationKind.None,
            currentSession is not null
                ? LightingDeviceSupport.Writable
                : currentDescriptor?.Support,
            currentSession?.DescriptorMetadata ?? currentDescriptor?.DescriptorMetadata);
    }

    private void UpdateSnapshot(
        LightingWorkerState state,
        LightingTransportFailureKind? failure = null)
    {
        LightingWorkerSnapshot updated = NewSnapshot(state, failure);
        lock (snapshotGate)
        {
            snapshot = updated;
        }

        SnapshotChanged?.Invoke(this, updated);
    }

    private abstract record WorkerCommand
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record SetStateCommand(byte[] State) : WorkerCommand;

    private sealed record PauseCommand : WorkerCommand;

    private sealed record ResumeCommand : WorkerCommand;

    private sealed record RestoreCommand : WorkerCommand;

    private sealed record QuiesceCommand : WorkerCommand;

    private sealed record ProbeCommand : WorkerCommand;

    private sealed record RefreshDesiredStateCommand : WorkerCommand;

    private sealed record PulseCurrentStateCommand : WorkerCommand;

    private sealed record RecoverPendingRestoreCommand : WorkerCommand;

    private sealed record RetryCommand(int Generation) : WorkerCommand;

    private sealed record StopCommand : WorkerCommand;
}
