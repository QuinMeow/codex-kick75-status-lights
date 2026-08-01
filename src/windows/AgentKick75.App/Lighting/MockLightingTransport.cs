// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;

namespace AgentKick75.App.Lighting;

public enum MockLightingOperation
{
    Inspect,
    Connect,
    Read,
    Write,
    Disconnect,
}

public sealed class MockLightingTransport : ILightingTransport
{
    private readonly ConcurrentDictionary<MockLightingOperation, ConcurrentQueue<LightingTransportFailureKind>> failures = new();
    private readonly ConcurrentQueue<string> operations = new();
    private readonly ConcurrentQueue<LightingConnectionRequest> connectionRequests = new();
    private readonly ConcurrentQueue<byte[]> writes = new();
    private byte[] currentState;
    private int activeOperations;
    private int maximumConcurrency;
    private int ignoreNextWrite;
    private bool connected;
    private LightingDeviceInspection? inspection;

    public MockLightingTransport(
        ReadOnlySpan<byte> baseline,
        LightingDeviceSession? session = null)
    {
        InMemoryBaselineOwnershipStore.ValidateSideLight(baseline);
        currentState = baseline.ToArray();
        Session = session ?? new LightingDeviceSession(
            "mock-device",
            "mock",
            "mock-interface",
            CurrentMode: 0);
    }

    public LightingDeviceSession Session { get; }

    public IReadOnlyList<string> Operations => operations.ToArray();

    public IReadOnlyList<LightingConnectionRequest> ConnectionRequests => connectionRequests.ToArray();

    public IReadOnlyList<byte[]> Writes => writes.Select(static value => value.ToArray()).ToArray();

    public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

    public LightingDeviceInspection? Inspection
    {
        get => Volatile.Read(ref inspection);
        set => Volatile.Write(ref inspection, value);
    }

    public void SimulateExternalStateChange(ReadOnlySpan<byte> sideLightState)
    {
        InMemoryBaselineOwnershipStore.ValidateSideLight(sideLightState);
        currentState = sideLightState.ToArray();
    }

    public void IgnoreNextWrite()
    {
        Interlocked.Exchange(ref ignoreNextWrite, 1);
    }

    public void FailNext(MockLightingOperation operation, LightingTransportFailureKind failureKind)
    {
        failures.GetOrAdd(operation, static _ => new()).Enqueue(failureKind);
    }

    public async ValueTask<LightingDeviceInspection?> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        using IDisposable scope = EnterOperation();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfQueued(MockLightingOperation.Inspect);
        operations.Enqueue("inspect");
        return Inspection;
    }

    public async ValueTask<LightingDeviceSession> ConnectAsync(
        LightingConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using IDisposable scope = EnterOperation();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        connectionRequests.Enqueue(request);
        ThrowIfQueued(MockLightingOperation.Connect);
        operations.Enqueue("connect");
        connected = true;
        return Session;
    }

    public async ValueTask<byte[]> ReadSideLightAsync(
        CancellationToken cancellationToken = default)
    {
        using IDisposable scope = EnterOperation();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ThrowIfQueued(MockLightingOperation.Read);
        operations.Enqueue("read");
        return currentState.ToArray();
    }

    public async ValueTask WriteSideLightAsync(
        ReadOnlyMemory<byte> sideLightState,
        CancellationToken cancellationToken = default)
    {
        InMemoryBaselineOwnershipStore.ValidateSideLight(sideLightState.Span);
        using IDisposable scope = EnterOperation();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ThrowIfQueued(MockLightingOperation.Write);
        operations.Enqueue("write");
        byte[] requestedState = sideLightState.ToArray();
        writes.Enqueue(requestedState.ToArray());
        if (Interlocked.Exchange(ref ignoreNextWrite, 0) == 0)
        {
            currentState = requestedState;
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable scope = EnterOperation();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfQueued(MockLightingOperation.Disconnect);
        operations.Enqueue("disconnect");
        connected = false;
    }

    public ValueTask DisposeAsync()
    {
        connected = false;
        return ValueTask.CompletedTask;
    }

    private IDisposable EnterOperation()
    {
        int active = Interlocked.Increment(ref activeOperations);
        int observed;
        do
        {
            observed = maximumConcurrency;
            if (active <= observed)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref maximumConcurrency, active, observed) != observed);

        return new OperationScope(this);
    }

    private void ExitOperation()
    {
        Interlocked.Decrement(ref activeOperations);
    }

    private void EnsureConnected()
    {
        if (!connected)
        {
            throw new LightingTransportException(
                LightingTransportFailureKind.DeviceDisconnected,
                "Mock transport is disconnected.");
        }
    }

    private void ThrowIfQueued(MockLightingOperation operation)
    {
        if (failures.TryGetValue(operation, out ConcurrentQueue<LightingTransportFailureKind>? queue) &&
            queue.TryDequeue(out LightingTransportFailureKind failureKind))
        {
            connected = failureKind is LightingTransportFailureKind.DeviceBusy;
            throw new LightingTransportException(failureKind, $"Injected mock {operation} failure.");
        }
    }

    private sealed class OperationScope(MockLightingTransport owner) : IDisposable
    {
        public void Dispose()
        {
            owner.ExitOperation();
        }
    }
}
