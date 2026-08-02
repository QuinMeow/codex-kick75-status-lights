// SPDX-License-Identifier: MIT
using AgentKick75.Core.Protocol;

namespace AgentKick75.Hid.Windows;

public enum HardwareTestOutcome
{
    NoGo,
    Passed,
    Failed,
}

public sealed record HardwareTestOptions
{
    public TimeSpan GreenDuration { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan CommandTimeout { get; init; } = Kick75HidProtocolClient.DefaultCommandTimeout;

    public int Cycles { get; init; } = 1;

    internal void Validate()
    {
        if (GreenDuration < TimeSpan.Zero || GreenDuration > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(GreenDuration),
                "Green duration must be between zero and one minute.");
        }

        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeout),
                "Command timeout must be between zero and 30 seconds.");
        }

        if (Cycles is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Cycles), "Cycles must be between 1 and 100.");
        }
    }
}

public sealed record HardwareTestCycleResult(
    int Cycle,
    HidDeviceState DeviceState,
    bool BaselineRead,
    bool GreenAcknowledged,
    bool TargetReadBack,
    bool TargetMatched,
    bool HeldMatched,
    bool RestoreAttempted,
    bool RestoreAcknowledged,
    bool BaselineRestoredByteForByte,
    string? Error)
{
    public bool Passed =>
        BaselineRead &&
        GreenAcknowledged &&
        TargetReadBack &&
        TargetMatched &&
        HeldMatched &&
        RestoreAttempted &&
        RestoreAcknowledged &&
        BaselineRestoredByteForByte &&
        Error is null;
}

public sealed record HardwareTestResult(
    HardwareTestOutcome Outcome,
    HidDeviceState DeviceState,
    string Message,
    string? TransportProfileId,
    string? DevicePath,
    string? InterfaceFingerprint,
    ushort? NativeInputReportLength,
    ushort? NativeOutputReportLength,
    IReadOnlyList<HardwareTestCycleResult> Cycles)
{
    public bool AllBaselinesRestored =>
        Cycles.Count > 0 && Cycles.All(cycle => cycle.BaselineRestoredByteForByte);
}

public interface IHardwareTestDelay
{
    ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed record HardwareTestBaselineLease(string OwnershipMarker, byte CurrentMode);

/// <summary>
/// Persists an ownership marker before a guarded test writes any lighting.
/// Implementations must survive process termination so a later Host can restore it.
/// </summary>
public interface IHardwareTestBaselineJournal
{
    ValueTask<HardwareTestBaselineLease> AcquireAsync(
        HidInterfaceDescriptor device,
        HidTransportProfile profile,
        ReadOnlyMemory<byte> baseline,
        byte currentMode,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(
        HardwareTestBaselineLease lease,
        CancellationToken cancellationToken);
}

public sealed class SystemHardwareTestDelay : IHardwareTestDelay
{
    public async ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GuardedHardwareTestService
{
    private const byte StaticColorMode = 0x02;
    private const byte FullBrightness = 100;
    private const byte StaticSpeed = 0x01;
    private const byte CustomColorFlag = 0x00;
    private const byte CustomColorIndex = 0x00;
    private static readonly TimeSpan TargetSettleDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RestoreStabilizationDelay = TimeSpan.FromMilliseconds(100);

    private readonly IHidDeviceEnumerator enumerator;
    private readonly HidDeviceSelector selector;
    private readonly IHidConnectionFactory connectionFactory;
    private readonly IHardwareTestBaselineJournal baselineJournal;
    private readonly IHardwareTestDelay delay;

    public GuardedHardwareTestService(
        IHidDeviceEnumerator enumerator,
        HidDeviceSelector selector,
        IHidConnectionFactory connectionFactory,
        IHardwareTestBaselineJournal baselineJournal,
        IHardwareTestDelay? delay = null)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(baselineJournal);

        this.enumerator = enumerator;
        this.selector = selector;
        this.connectionFactory = connectionFactory;
        this.baselineJournal = baselineJournal;
        this.delay = delay ?? new SystemHardwareTestDelay();
    }

    public async ValueTask<HardwareTestResult> RunAsync(
        HardwareTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HardwareTestOptions();
        options.Validate();

        HidDeviceSelection selection;
        try
        {
            selection = selector.Select(enumerator.Enumerate());
        }
        catch (Exception exception)
        {
            return new HardwareTestResult(
                HardwareTestOutcome.NoGo,
                HidDeviceState.Faulted,
                $"HID enumeration failed: {SanitizeError(exception)}",
                null,
                null,
                null,
                null,
                null,
                Array.Empty<HardwareTestCycleResult>());
        }

        if (!selection.IsWritable)
        {
            string noGoReason = selection.Candidates
                .FirstOrDefault(candidate =>
                    candidate.Profile == HidTransportProfiles.Kick75Usb)?.Reason ?? selection.Message;
            return CreateResult(
                HardwareTestOutcome.NoGo,
                selection,
                $"No-Go: {noGoReason}",
                Array.Empty<HardwareTestCycleResult>());
        }

        List<HardwareTestCycleResult> cycleResults = new();
        HidDeviceState finalState = selection.State;
        for (int cycleNumber = 1; cycleNumber <= options.Cycles; cycleNumber++)
        {
            HardwareTestCycleResult cycle;
            try
            {
                cycle = await RunCycleAsync(
                    selection,
                    cycleNumber,
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TargetAlreadyAppliedException exception)
            {
                return CreateResult(
                    HardwareTestOutcome.NoGo,
                    selection with { State = HidDeviceState.Ready },
                    $"No-Go: {SanitizeError(exception)}",
                    cycleResults);
            }
            catch (HidDeviceBusyException exception)
            {
                finalState = HidDeviceState.Busy;
                cycle = FailedBeforeBaseline(cycleNumber, finalState, exception);
            }
            catch (HidDeviceDisconnectedException exception)
            {
                finalState = HidDeviceState.Disconnected;
                cycle = FailedBeforeBaseline(cycleNumber, finalState, exception);
            }
            catch (TimeoutException exception)
            {
                finalState = HidDeviceState.Unresponsive;
                cycle = FailedBeforeBaseline(cycleNumber, finalState, exception);
            }
            catch (Exception exception)
            {
                finalState = HidDeviceState.Faulted;
                cycle = FailedBeforeBaseline(cycleNumber, finalState, exception);
            }

            cycleResults.Add(cycle);
            finalState = cycle.DeviceState;
            if (!cycle.Passed)
            {
                string reason = cycle.BaselineRestoredByteForByte
                    ? "the baseline was restored, but another guarded step failed"
                    : "byte-for-byte baseline restoration was not proven";
                return CreateResult(
                    HardwareTestOutcome.Failed,
                    selection with
                    {
                        State = cycle.BaselineRestoredByteForByte
                            ? HidDeviceState.Ready
                            : finalState,
                    },
                    $"No-Go: hardware cycle {cycleNumber} failed because {reason}.",
                    cycleResults);
            }

            finalState = HidDeviceState.Ready;
        }

        return CreateResult(
            HardwareTestOutcome.Passed,
            selection with { State = finalState },
            $"All {cycleResults.Count} protocol cycle(s) matched the green target during the hold " +
            "and restored the original side-light bytes twice. Physical lighting observation is still required.",
            cycleResults);
    }

    private async ValueTask<HardwareTestCycleResult> RunCycleAsync(
        HidDeviceSelection selection,
        int cycleNumber,
        HardwareTestOptions options,
        CancellationToken cancellationToken)
    {
        Kick75HidProtocolClient? client = null;
        byte[]? baseline = null;
        byte? baselineCurrentMode = null;
        HardwareTestBaselineLease? baselineLease = null;
        bool baselineRead = false;
        bool greenAcknowledged = false;
        bool targetReadBack = false;
        bool targetMatched = false;
        bool heldMatched = false;
        bool restoreAttempted = false;
        bool restoreAcknowledged = false;
        bool restored = false;
        Exception? operationError = null;
        HidDeviceState observedState = selection.State;

        try
        {
            client = await OpenInitializedClientAsync(
                selection,
                options.CommandTimeout,
                cancellationToken).ConfigureAwait(false);
            observedState = client.State;
            byte capturedCurrentMode = client.CurrentMode;
            baselineCurrentMode = capturedCurrentMode;
            baseline = await client.ReadSideLightStateAsync(cancellationToken).ConfigureAwait(false);
            baselineRead = true;
            byte[] greenCandidate = CreateCurrentNuPhyIoGreenCandidate(baseline);
            if (baseline.AsSpan().SequenceEqual(greenCandidate))
            {
                throw new TargetAlreadyAppliedException(
                    "The captured baseline already equals the green target, so this run cannot prove a lighting transition.");
            }

            baselineLease = await baselineJournal.AcquireAsync(
                selection.Device!,
                selection.Profile!,
                baseline,
                capturedCurrentMode,
                cancellationToken).ConfigureAwait(false);
            if (baselineLease.CurrentMode != capturedCurrentMode)
            {
                throw new InvalidDataException(
                    $"The baseline journal returned currentMode {baselineLease.CurrentMode}, " +
                    $"but the captured lighting bank was {capturedCurrentMode}.");
            }
            await client.WriteSideLightFullStateAsync(greenCandidate, cancellationToken)
                .ConfigureAwait(false);
            greenAcknowledged = true;

            // NuPhyIO sends the D6 block and D6 brightness refresh back-to-back. Give
            // firmware a short settle interval before readback, but keep this separate
            // from the full user-visible observation window.
            await delay.DelayAsync(TargetSettleDelay, cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] targetState = await client.ReadSideLightStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                targetReadBack = true;
                targetMatched = greenCandidate.AsSpan().SequenceEqual(targetState);
                if (!targetMatched)
                {
                    operationError = new InvalidDataException(
                        "The immediate side-light readback did not match the green target byte-for-byte.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Once both target writes were acknowledged, preserve the complete
                // observation window even if this read poisoned the connection.
                observedState = client.State;
                operationError = Combine(operationError, exception);
            }

            await delay.DelayAsync(options.GreenDuration, cancellationToken).ConfigureAwait(false);
            if (client.IsReady)
            {
                try
                {
                    byte[] heldState = await client.ReadSideLightStateAsync(cancellationToken)
                        .ConfigureAwait(false);
                    heldMatched = greenCandidate.AsSpan().SequenceEqual(heldState);
                    if (!heldMatched)
                    {
                        operationError = Combine(
                            operationError,
                            new InvalidDataException(
                                "The side-light readback at the end of the hold did not match the green target byte-for-byte."));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    observedState = client.State;
                    operationError = Combine(operationError, exception);
                }
            }
        }
        catch (TargetAlreadyAppliedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            operationError = exception;
            if (client is not null)
            {
                observedState = client.State;
            }
        }
        finally
        {
            operationError = await DisposeClientAsync(client, operationError).ConfigureAwait(false);
            client = null;

            if (baseline is not null &&
                baselineLease is not null &&
                baselineCurrentMode is byte restoreCurrentMode)
            {
                restoreAttempted = true;
                bool immediateRestoreMatched = false;
                bool stableRestoreMatched = false;
                bool restoreModeVerifiedForRelease = false;

                (client, operationError) = await TryOpenRecoveryClientAsync(
                    selection,
                    options.CommandTimeout,
                    restoreCurrentMode,
                    operationError).ConfigureAwait(false);

                if (client is not null)
                {
                    try
                    {
                        // Restoration deliberately ignores caller cancellation and remains bounded by
                        // the per-command timeout. Cancelling the green hold must not skip cleanup.
                        await client.WriteSideLightFullStateAsync(baseline, CancellationToken.None)
                            .ConfigureAwait(false);
                        restoreAcknowledged = true;
                    }
                    catch (Exception exception)
                    {
                        observedState = client.State;
                        operationError = Combine(operationError, exception);
                        operationError = await DisposeClientAsync(client, operationError).ConfigureAwait(false);
                        client = null;
                    }
                }

                if (client is null)
                {
                    (client, operationError) = await TryOpenRecoveryClientAsync(
                        selection,
                        options.CommandTimeout,
                        restoreCurrentMode,
                        operationError).ConfigureAwait(false);
                }

                if (client is not null)
                {
                    try
                    {
                        byte[] readBack = await client.ReadSideLightStateAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        immediateRestoreMatched = baseline.AsSpan().SequenceEqual(readBack);
                        await delay.DelayAsync(RestoreStabilizationDelay, CancellationToken.None)
                            .ConfigureAwait(false);
                        byte[] stableReadBack = await client.ReadSideLightStateAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        stableRestoreMatched = baseline.AsSpan().SequenceEqual(stableReadBack);
                        await client.VerifyCurrentModeAsync(CancellationToken.None).ConfigureAwait(false);
                        restoreModeVerifiedForRelease = true;
                        observedState = client.State;
                    }
                    catch (Exception exception)
                    {
                        observedState = client.State;
                        operationError = Combine(operationError, exception);
                    }
                }

                restored =
                    restoreAcknowledged &&
                    immediateRestoreMatched &&
                    stableRestoreMatched &&
                    restoreModeVerifiedForRelease;
                if (!restored && operationError is null)
                {
                    operationError = new InvalidDataException(
                        "The two-step side-light restore or one of its verification reads did not match the captured baseline.");
                }

                if (restored)
                {
                    try
                    {
                        await baselineJournal.ReleaseAsync(baselineLease, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        restored = false;
                        operationError = Combine(operationError, exception);
                    }
                }
            }

            operationError = await DisposeClientAsync(client, operationError).ConfigureAwait(false);
        }

        return new HardwareTestCycleResult(
            cycleNumber,
            restored
                ? HidDeviceState.Ready
                : observedState == HidDeviceState.Ready
                    ? HidDeviceState.Unresponsive
                    : observedState,
            baselineRead,
            greenAcknowledged,
            targetReadBack,
            targetMatched,
            heldMatched,
            restoreAttempted,
            restoreAcknowledged,
            restored,
            operationError is null ? null : SanitizeError(operationError));
    }

    private async ValueTask<Kick75HidProtocolClient> OpenInitializedClientAsync(
        HidDeviceSelection selection,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        IHidReportConnection connection = await connectionFactory
            .OpenAsync(selection, cancellationToken).ConfigureAwait(false);
        var client = new Kick75HidProtocolClient(connection, commandTimeout);
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<(Kick75HidProtocolClient? Client, Exception? Error)> TryOpenRecoveryClientAsync(
        HidDeviceSelection selection,
        TimeSpan commandTimeout,
        byte expectedCurrentMode,
        Exception? existingError)
    {
        try
        {
            Kick75HidProtocolClient client = await OpenInitializedClientAsync(
                selection,
                commandTimeout,
                CancellationToken.None).ConfigureAwait(false);
            if (client.CurrentMode != expectedCurrentMode)
            {
                byte actualCurrentMode = client.CurrentMode;
                await client.DisposeAsync().ConfigureAwait(false);
                throw new InvalidDataException(
                    $"Recovery opened currentMode {actualCurrentMode}, but the owned baseline belongs to " +
                    $"currentMode {expectedCurrentMode}; restoration to a different bank is blocked.");
            }

            return (client, existingError);
        }
        catch (Exception exception)
        {
            return (null, Combine(existingError, exception));
        }
    }

    private static async ValueTask<Exception?> DisposeClientAsync(
        Kick75HidProtocolClient? client,
        Exception? existingError)
    {
        if (client is null)
        {
            return existingError;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return existingError;
        }
        catch (Exception exception)
        {
            return Combine(existingError, exception);
        }
    }

    private static Exception Combine(Exception? existingError, Exception nextError) =>
        existingError is null
            ? nextError
            : new AggregateException(existingError, nextError);

    private static byte[] CreateCurrentNuPhyIoGreenCandidate(ReadOnlySpan<byte> baseline)
    {
        if (baseline.Length != Kick75ProtocolCodec.SideLightLength)
        {
            throw new ArgumentException(
                $"Expected exactly {Kick75ProtocolCodec.SideLightLength} side-light bytes.",
                nameof(baseline));
        }

        // Official NuPhyIO side-light layout:
        // mode, brightness, speed, isRGB/custom, color index, R, G, B.
        return
        [
            StaticColorMode,
            FullBrightness,
            StaticSpeed,
            CustomColorFlag,
            CustomColorIndex,
            0x00,
            0xFF,
            0x00,
        ];
    }

    private static HardwareTestCycleResult FailedBeforeBaseline(
        int cycle,
        HidDeviceState deviceState,
        Exception exception) =>
        new(
            cycle,
            deviceState,
            BaselineRead: false,
            GreenAcknowledged: false,
            TargetReadBack: false,
            TargetMatched: false,
            HeldMatched: false,
            RestoreAttempted: false,
            RestoreAcknowledged: false,
            BaselineRestoredByteForByte: false,
            SanitizeError(exception));

    private static HardwareTestResult CreateResult(
        HardwareTestOutcome outcome,
        HidDeviceSelection selection,
        string message,
        IReadOnlyList<HardwareTestCycleResult> cycles) =>
        new(
            outcome,
            selection.State,
            message,
            selection.Profile?.Id,
            selection.Device?.DevicePath,
            selection.Device?.InterfaceFingerprint,
            selection.Device?.InputReportByteLength,
            selection.Device?.OutputReportByteLength,
            cycles);

    private static string SanitizeError(Exception exception) =>
        exception switch
        {
            AggregateException aggregate => string.Join(
                " | ",
                aggregate.Flatten().InnerExceptions.Select(SanitizeError)),
            _ => exception.Message,
        };

    private sealed class TargetAlreadyAppliedException(string message) : Exception(message);
}
