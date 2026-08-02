// SPDX-License-Identifier: MIT
using AgentKick75.App.Diagnostics;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using System.Runtime.ExceptionServices;

namespace AgentKick75.App.Hosting;

public sealed class HostRuntime : IAsyncDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CleanupFailureDelay = TimeSpan.FromMilliseconds(250);

    private readonly HidLightingWorker lightingWorker;
    private readonly HostCoordinator coordinator;
    private readonly NamedPipeMessageServer pipeServer;
    private readonly ISanitizedDiagnosticLog? diagnosticLog;
    private readonly CancellationTokenSource stopSource = new();
    private readonly object diagnosticStateGate = new();
    private Task? cleanupTask;
    private LightingWorkerSnapshot? previousLightingSnapshot;
    private bool started;

    public HostRuntime(
        HidLightingWorker lightingWorker,
        HostCoordinator coordinator,
        string? pipeName = null,
        ISanitizedDiagnosticLog? diagnosticLog = null)
    {
        this.lightingWorker = lightingWorker ?? throw new ArgumentNullException(nameof(lightingWorker));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.diagnosticLog = diagnosticLog;
        pipeServer = new NamedPipeMessageServer(
            coordinator.HandlePipeMessageAsync,
            pipeName,
            clientRequestTimeout: TimeSpan.FromSeconds(15),
            responseFlushed: HandlePipeResponseFlushedAsync);
    }

    public HostCoordinator Coordinator => coordinator;

    public void Start()
    {
        StartAsync().GetAwaiter().GetResult();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            throw new InvalidOperationException("The Host runtime has already been started.");
        }

        started = true;
        lock (diagnosticStateGate)
        {
            previousLightingSnapshot = lightingWorker.Snapshot;
        }

        lightingWorker.SnapshotChanged += HandleLightingSnapshotChanged;
        lightingWorker.Start();
        try
        {
            await lightingWorker.RecoverPendingRestoreAsync(cancellationToken).ConfigureAwait(false);
            if (coordinator.GetStatus().LifecycleState == ApplicationLifecycleState.Starting)
            {
                coordinator.MarkRunning();
            }
        }
        catch
        {
            coordinator.MarkStartupFault();
            lightingWorker.SnapshotChanged -= HandleLightingSnapshotChanged;
            throw;
        }

        coordinator.StartEventProcessing();
        pipeServer.Start();
        cleanupTask = CleanupLoopAsync(stopSource.Token);
        WriteDiagnostic(
            SanitizedDiagnosticEventType.HostStarted,
            code: SanitizedDiagnosticCode.Succeeded);
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        LifecycleStopResult? stopResult = null;
        if (started)
        {
            try
            {
                stopResult = await coordinator.StopAsync(LifecycleStopReason.NormalExit)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            await stopSource.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await pipeServer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await coordinator.StopEventProcessingAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (cleanupTask is not null)
        {
            try
            {
                await cleanupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            await lightingWorker.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            lightingWorker.SnapshotChanged -= HandleLightingSnapshotChanged;
            stopSource.Dispose();
            coordinator.MarkStopped();
        }

        if (started)
        {
            WriteDiagnostic(
                SanitizedDiagnosticEventType.HostStopped,
                code: stopResult?.Succeeded == false
                    ? SanitizedDiagnosticCode.RestoreFailed
                    : failures.Count == 0
                        ? SanitizedDiagnosticCode.Succeeded
                        : SanitizedDiagnosticCode.UnexpectedFailure);
        }

        if (diagnosticLog is not null)
        {
            try
            {
                await diagnosticLog.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Diagnostic persistence is advisory and must never mask a HID
                // restoration or Host shutdown result.
            }
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("Host shutdown encountered multiple failures.", failures);
        }
    }

    private ValueTask HandlePipeResponseFlushedAsync(
        PipeEnvelope request,
        PipeEnvelope response,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (request.Kind == PipeMessageKinds.PrepareUninstallRequest &&
            response.Kind == PipeMessageKinds.Accepted)
        {
            coordinator.NotifyPrepareUninstallResponseFlushed();
        }

        return ValueTask.CompletedTask;
    }

    private void HandleLightingSnapshotChanged(object? sender, LightingWorkerSnapshot current)
    {
        LightingWorkerSnapshot? previous;
        lock (diagnosticStateGate)
        {
            previous = previousLightingSnapshot;
            previousLightingSnapshot = current;
        }

        if (current.DeviceObservation == LightingDeviceObservationKind.Descriptor &&
            previous?.DeviceObservation != LightingDeviceObservationKind.Descriptor)
        {
            WriteDiagnostic(
                SanitizedDiagnosticEventType.DeviceDiscovered,
                code: SanitizedDiagnosticCode.Succeeded);
        }

        if (current.DeviceObservation == LightingDeviceObservationKind.RuntimeSession &&
            previous?.DeviceObservation != LightingDeviceObservationKind.RuntimeSession)
        {
            WriteDiagnostic(
                SanitizedDiagnosticEventType.DeviceConnected,
                code: SanitizedDiagnosticCode.Succeeded);
        }

        bool newFailure = current.LastFailure is not null &&
            (previous?.LastFailure != current.LastFailure || previous.State != current.State);
        if (newFailure)
        {
            WriteDiagnostic(
                current.State == LightingWorkerState.Reconnecting
                    ? SanitizedDiagnosticEventType.ReconnectScheduled
                    : SanitizedDiagnosticEventType.DeviceDisconnected,
                transportFailure: current.LastFailure,
                code: MapFailureCode(current.LastFailure!.Value));
        }
        else if (previous?.DeviceObservation == LightingDeviceObservationKind.RuntimeSession &&
                 current.DeviceObservation != LightingDeviceObservationKind.RuntimeSession)
        {
            WriteDiagnostic(
                SanitizedDiagnosticEventType.DeviceDisconnected,
                code: SanitizedDiagnosticCode.Succeeded);
        }
    }

    private void WriteDiagnostic(
        SanitizedDiagnosticEventType eventType,
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
                transportFailure: transportFailure,
                code: code);
            if (!pending.IsCompletedSuccessfully)
            {
                _ = ObserveDiagnosticWriteAsync(pending);
            }
        }
        catch (Exception)
        {
        }
    }

    private static SanitizedDiagnosticCode MapFailureCode(LightingTransportFailureKind failure)
    {
        return failure switch
        {
            LightingTransportFailureKind.Timeout => SanitizedDiagnosticCode.Timeout,
            LightingTransportFailureKind.ProtocolViolation => SanitizedDiagnosticCode.ProtocolRejected,
            LightingTransportFailureKind.BaselineMismatch => SanitizedDiagnosticCode.BaselineMismatch,
            _ => SanitizedDiagnosticCode.UnexpectedFailure,
        };
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

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await coordinator.CleanupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A transient storage/HID fault must not permanently disable stale
                // cleanup and health probing. Keep this path silent and bounded.
                await Task.Delay(CleanupFailureDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
