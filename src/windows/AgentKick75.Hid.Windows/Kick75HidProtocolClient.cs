// SPDX-License-Identifier: MIT
using AgentKick75.Core.Protocol;

namespace AgentKick75.Hid.Windows;

public interface IKick75LightingTransport : IAsyncDisposable
{
    HidInterfaceDescriptor Device { get; }

    HidDeviceState State { get; }

    bool IsReady { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReadSideLightStateAsync(CancellationToken cancellationToken = default);

    ValueTask WriteSideLightFullStateAsync(
        ReadOnlyMemory<byte> sideLightState,
        CancellationToken cancellationToken = default);
}

public sealed class Kick75HidProtocolClient : IKick75LightingTransport
{
    public static TimeSpan DefaultCommandTimeout { get; } = TimeSpan.FromSeconds(2);

    private readonly IHidReportConnection connection;
    private readonly TimeSpan commandTimeout;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly TaskCompletionSource<bool> disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private byte sessionKey;
    private byte? currentMode;
    private bool connectionPoisoned;
    private int disposeState;

    public Kick75HidProtocolClient(
        IHidReportConnection connection,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        TimeSpan effectiveTimeout = commandTimeout ?? DefaultCommandTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                "Command timeout must be positive.");
        }

        this.connection = connection;
        this.commandTimeout = effectiveTimeout;
        State = connection.State;
    }

    public HidInterfaceDescriptor Device => connection.Device;

    public HidDeviceState State { get; private set; }

    public bool IsReady =>
        Volatile.Read(ref disposeState) == 0 &&
        !connectionPoisoned &&
        sessionKey != 0 &&
        currentMode.HasValue &&
        State == HidDeviceState.Ready;

    /// <summary>
    /// Gets the active NuPhy lighting/configuration bank reported by A0 GetBaseInfo.
    /// </summary>
    public byte CurrentMode => currentMode ?? throw new InvalidOperationException(
        "The Kick75 current mode is unavailable until EE and A0 initialization completes.");

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        bool transactionAttempted = false;
        try
        {
            ThrowIfConnectionPoisoned();
            Kick75SessionRequest request = Kick75ProtocolCodec.BuildSessionRequest();
            transactionAttempted = true;
            byte[] response = await ExchangeAsync(request.Frame, cancellationToken).ConfigureAwait(false);
            sessionKey = Kick75ProtocolCodec.ValidateSessionResponse(response, request);
            byte[] getBaseInfoRequest = Kick75ProtocolCodec.BuildGetBaseInfoRequest(sessionKey);
            response = await ExchangeAsync(getBaseInfoRequest, cancellationToken).ConfigureAwait(false);
            currentMode = Kick75ProtocolCodec.DecodeGetBaseInfoResponse(response, sessionKey);
            State = HidDeviceState.Ready;
        }
        catch (Exception exception)
        {
            if (transactionAttempted)
            {
                PoisonConnection();
            }

            State = ClassifyFailure(exception);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<byte[]> ReadSideLightStateAsync(
        CancellationToken cancellationToken = default)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        bool transactionAttempted = false;
        try
        {
            EnsureSessionEstablished();
            transactionAttempted = true;
            byte mode = await VerifyCurrentModeUnderLockAsync(cancellationToken).ConfigureAwait(false);
            byte[] request = Kick75ProtocolCodec.BuildGetLightStateRequest(sessionKey, mode);
            byte[] response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
            byte[] lightState = Kick75ProtocolCodec.DecodeGetLightStateResponse(
                response,
                sessionKey,
                mode);
            State = HidDeviceState.Ready;
            return Kick75ProtocolCodec.ExtractSideLightState(lightState);
        }
        catch (Exception exception)
        {
            if (transactionAttempted)
            {
                PoisonConnection();
            }

            State = ClassifyFailure(exception);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>
    /// Writes one complete side-light state using NuPhyIO's paired D6 commit:
    /// address 9/length 8 immediately followed by the mirrored brightness byte
    /// at address 10/length 1. The active mode is checked once before the pair;
    /// no A0, D5, or other protocol packet is inserted between the two D6 exchanges.
    /// </summary>
    public async ValueTask WriteSideLightFullStateAsync(
        ReadOnlyMemory<byte> sideLightState,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        if (sideLightState.Length != Kick75ProtocolCodec.SideLightLength)
        {
            throw new ArgumentException(
                $"The side-light state must contain exactly {Kick75ProtocolCodec.SideLightLength} bytes.",
                nameof(sideLightState));
        }

        byte[] stableSideLightState = sideLightState.ToArray();
        byte brightness = stableSideLightState[Kick75ProtocolCodec.SideLightBrightnessOffset];
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        bool transactionAttempted = false;
        try
        {
            EnsureSessionEstablished();
            transactionAttempted = true;
            byte mode = await VerifyCurrentModeUnderLockAsync(cancellationToken).ConfigureAwait(false);

            byte[] blockRequest = Kick75ProtocolCodec.BuildSetSideLightRequest(
                sessionKey,
                mode,
                stableSideLightState);
            byte[] response = await ExchangeAsync(blockRequest, cancellationToken).ConfigureAwait(false);
            Kick75ProtocolCodec.ValidateSetSideLightResponse(response, sessionKey, mode);

            byte[] brightnessRequest = Kick75ProtocolCodec.BuildSetSideLightBrightnessRequest(
                sessionKey,
                mode,
                brightness);
            response = await ExchangeAsync(brightnessRequest, cancellationToken).ConfigureAwait(false);
            Kick75ProtocolCodec.ValidateSetSideLightBrightnessResponse(response, sessionKey, mode);
            State = HidDeviceState.Ready;
        }
        catch (Exception exception)
        {
            if (transactionAttempted)
            {
                PoisonConnection();
            }

            State = ClassifyFailure(exception);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>
    /// Re-reads A0 and verifies that the device is still using the lighting bank
    /// captured during initialization. A changed bank poisons this session.
    /// </summary>
    public async ValueTask VerifyCurrentModeAsync(
        CancellationToken cancellationToken = default)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        bool transactionAttempted = false;
        try
        {
            EnsureSessionEstablished();
            transactionAttempted = true;
            _ = await VerifyCurrentModeUnderLockAsync(cancellationToken).ConfigureAwait(false);
            State = HidDeviceState.Ready;
        }
        catch (Exception exception)
        {
            if (transactionAttempted)
            {
                PoisonConnection();
            }

            State = ClassifyFailure(exception);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposeState, 1, 0) == 0)
        {
            _ = DisposeCoreAsync();
        }

        return new ValueTask(disposeCompletion.Task);
    }

    private async Task DisposeCoreAsync()
    {
        Exception? disposeException = null;
        bool lockTaken = false;
        try
        {
            await operationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            lockTaken = true;
            connectionPoisoned = true;
            sessionKey = 0;
            currentMode = null;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposeException = exception;
        }
        finally
        {
            State = HidDeviceState.Disconnected;
            Volatile.Write(ref disposeState, 2);
            if (lockTaken)
            {
                operationLock.Release();
            }

            if (disposeException is null)
            {
                disposeCompletion.TrySetResult(true);
            }
            else
            {
                disposeCompletion.TrySetException(disposeException);
            }
        }
    }

    private async ValueTask EnterOperationAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
        }
        catch
        {
            operationLock.Release();
            throw;
        }
    }

    private void ThrowIfDisposingOrDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
    }

    private async ValueTask<byte[]> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        await connection.WriteReportAsync(
            request,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return await connection.ReadReportAsync(
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte> VerifyCurrentModeUnderLockAsync(
        CancellationToken cancellationToken)
    {
        byte expectedCurrentMode = CurrentMode;
        byte[] request = Kick75ProtocolCodec.BuildGetBaseInfoRequest(sessionKey);
        byte[] response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        byte observedCurrentMode = Kick75ProtocolCodec.DecodeGetBaseInfoResponse(response, sessionKey);
        if (observedCurrentMode != expectedCurrentMode)
        {
            throw new Kick75ProtocolException(
                $"The active currentMode changed from {expectedCurrentMode} to {observedCurrentMode}; " +
                "the lighting operation was blocked before accessing another bank.");
        }

        return expectedCurrentMode;
    }

    private void EnsureSessionEstablished()
    {
        ThrowIfConnectionPoisoned();
        if (sessionKey == 0)
        {
            throw new InvalidOperationException(
                "The Kick75 protocol session has not completed a valid 0xEE handshake.");
        }
    }

    private void ThrowIfConnectionPoisoned()
    {
        if (connectionPoisoned)
        {
            throw new InvalidOperationException(
                "The HID transaction stream is no longer trustworthy; dispose this client and open a new connection.");
        }
    }

    private void PoisonConnection()
    {
        connectionPoisoned = true;
        sessionKey = 0;
        currentMode = null;
    }

    private HidDeviceState ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => State,
            HidDeviceDisconnectedException => HidDeviceState.Disconnected,
            HidDeviceBusyException => HidDeviceState.Busy,
            TimeoutException when Device.ProductId == HidTransportProfiles.Kick75U1Dongle.ProductId =>
                HidDeviceState.ReceiverPresent,
            TimeoutException => HidDeviceState.Unresponsive,
            Kick75ProtocolException => HidDeviceState.Unresponsive,
            _ => HidDeviceState.Faulted,
        };
    }
}
