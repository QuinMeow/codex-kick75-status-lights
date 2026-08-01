// SPDX-License-Identifier: MIT
using AgentKick75.Core.Protocol;
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests.Hid;

public sealed class Kick75HidProtocolClientLifecycleTests
{
    private static readonly byte[] SideLightState = Convert.FromHexString("026401000000FF00");

    [Fact]
    public async Task DisposeAsync_ReadIsBlocked_WaitsForOperationBeforeClosingConnection()
    {
        var connection = new BlockingHidConnection();
        var client = new Kick75HidProtocolClient(connection, TimeSpan.FromSeconds(1));
        await client.InitializeAsync();
        connection.BlockNext(BlockingOperation.Read);

        Task<byte[]> readTask = client.ReadSideLightStateAsync().AsTask();
        await connection.BlockStarted.WaitAsync(TimeSpan.FromSeconds(2));

        Task firstDisposeTask = client.DisposeAsync().AsTask();
        Task secondDisposeTask = client.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.False(firstDisposeTask.IsCompleted);
        Assert.False(secondDisposeTask.IsCompleted);
        Assert.False(connection.IsDisposed);
        Assert.False(client.IsReady);

        connection.ReleaseBlock();
        byte[] result = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(firstDisposeTask, secondDisposeTask).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[Kick75ProtocolCodec.SideLightLength], result);
        Assert.False(connection.DisposedDuringBlockedOperation);
        Assert.Equal(1, connection.DisposeCallCount);
        Assert.Equal(HidDeviceState.Disconnected, client.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await client.ReadSideLightStateAsync());
    }

    [Fact]
    public async Task DisposeAsync_WriteIsBlocked_WaitsForOperationBeforeClosingConnection()
    {
        var connection = new BlockingHidConnection();
        var client = new Kick75HidProtocolClient(connection, TimeSpan.FromSeconds(1));
        await client.InitializeAsync();
        connection.BlockNext(BlockingOperation.Write);

        Task writeTask = client.WriteSideLightFullStateAsync(SideLightState).AsTask();
        await connection.BlockStarted.WaitAsync(TimeSpan.FromSeconds(2));

        Task disposeTask = client.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.False(disposeTask.IsCompleted);
        Assert.False(connection.IsDisposed);
        Assert.False(client.IsReady);

        connection.ReleaseBlock();
        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(connection.DisposedDuringBlockedOperation);
        Assert.Equal(1, connection.DisposeCallCount);
        Assert.Equal(HidDeviceState.Disconnected, client.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await client.WriteSideLightFullStateAsync(SideLightState));
    }

    private enum BlockingOperation
    {
        None,
        Read,
        Write,
    }

    private sealed class BlockingHidConnection : IHidReportConnection
    {
        private readonly Queue<byte[]> responses = new();
        private readonly TaskCompletionSource<bool> blockStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseBlock = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private BlockingOperation blockingOperation;
        private byte sessionKey;
        private int blockArmed;
        private int blockedOperationCount;
        private bool disposed;

        public HidInterfaceDescriptor Device { get; } = new(
            "lifecycle-test",
            HidTransportProfiles.Kick75Usb.VendorId,
            HidTransportProfiles.Kick75Usb.ProductId,
            HidTransportProfile.RawHidUsagePage,
            HidTransportProfile.RawHidUsage,
            65,
            65);

        public HidDeviceState State => disposed
            ? HidDeviceState.Disconnected
            : HidDeviceState.Present;

        public Task BlockStarted => blockStarted.Task;

        public bool IsDisposed => disposed;

        public bool DisposedDuringBlockedOperation { get; private set; }

        public int DisposeCallCount { get; private set; }

        public void BlockNext(BlockingOperation operation)
        {
            Assert.NotEqual(BlockingOperation.None, operation);
            blockingOperation = operation;
            Volatile.Write(ref blockArmed, 1);
        }

        public void ReleaseBlock() => releaseBlock.TrySetResult(true);

        public async ValueTask WriteReportAsync(
            ReadOnlyMemory<byte> protocolFrame,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            await WaitIfBlockedAsync(BlockingOperation.Write, cancellationToken).ConfigureAwait(false);
            ThrowIfDisposed();

            byte[] request = protocolFrame.ToArray();
            Assert.Equal(Kick75ProtocolCodec.ReportSize, request.Length);
            Kick75ProtocolCommand command = (Kick75ProtocolCommand)request[1];
            switch (command)
            {
                case Kick75ProtocolCommand.SetSecretKey:
                    sessionKey = request[28];
                    responses.Enqueue(CreateSessionResponse(request));
                    break;
                case Kick75ProtocolCommand.GetBaseInfo:
                    responses.Enqueue(CreateResponse(command, 8, 0, 0, [1]));
                    break;
                case Kick75ProtocolCommand.GetLightState:
                    responses.Enqueue(CreateResponse(command, 17, 0, 1, new byte[17]));
                    break;
                case Kick75ProtocolCommand.SetLightState:
                    int length = request[4] ^ sessionKey;
                    ushort address = (ushort)(
                        (request[5] ^ sessionKey) |
                        ((request[6] ^ sessionKey) << 8));
                    byte currentMode = (byte)(request[7] ^ sessionKey);
                    responses.Enqueue(CreateResponse(command, length, address, currentMode, []));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected opcode 0x{request[1]:X2}.");
            }
        }

        public async ValueTask<byte[]> ReadReportAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            await WaitIfBlockedAsync(BlockingOperation.Read, cancellationToken).ConfigureAwait(false);
            ThrowIfDisposed();
            return responses.Dequeue();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            if (Volatile.Read(ref blockedOperationCount) != 0)
            {
                DisposedDuringBlockedOperation = true;
            }

            disposed = true;
            return ValueTask.CompletedTask;
        }

        private async ValueTask WaitIfBlockedAsync(
            BlockingOperation operation,
            CancellationToken cancellationToken)
        {
            if (blockingOperation != operation || Interlocked.Exchange(ref blockArmed, 0) == 0)
            {
                return;
            }

            Interlocked.Increment(ref blockedOperationCount);
            blockStarted.TrySetResult(true);
            try
            {
                await releaseBlock.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref blockedOperationCount);
            }
        }

        private byte[] CreateSessionResponse(ReadOnlySpan<byte> request)
        {
            byte[] response = CreateResponse(
                Kick75ProtocolCommand.SetSecretKey,
                logicalLength: 0,
                address: 0,
                currentMode: 0,
                payload: []);
            for (int index = Kick75ProtocolCodec.HeaderSize; index < response.Length; index++)
            {
                response[index] = (byte)(request[index] ^ sessionKey);
            }

            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private byte[] CreateResponse(
            Kick75ProtocolCommand command,
            int logicalLength,
            ushort address,
            byte currentMode,
            ReadOnlySpan<byte> payload)
        {
            byte[] response = new byte[Kick75ProtocolCodec.ReportSize];
            response[0] = Kick75ProtocolCodec.DeviceDirection;
            response[1] = (byte)command;
            response[4] = (byte)(logicalLength ^ sessionKey);
            response[5] = (byte)((address & 0xFF) ^ sessionKey);
            response[6] = (byte)((address >> 8) ^ sessionKey);
            response[7] = (byte)(currentMode ^ sessionKey);
            for (int index = 0; index < payload.Length; index++)
            {
                response[Kick75ProtocolCodec.HeaderSize + index] = (byte)(payload[index] ^ sessionKey);
            }

            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }
}
