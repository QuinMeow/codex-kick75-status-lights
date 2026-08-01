// SPDX-License-Identifier: MIT
using AgentKick75.App.Lighting;
using AgentKick75.Core.Protocol;
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests.Hid;

public sealed class WindowsLightingTransportTests
{
    private static readonly byte[] ThinkingState = Convert.FromHexString("0248010000FFBF00");

    [Theory]
    [InlineData(0x1026, "kick75-usb", LightingDeviceSupport.Writable)]
    [InlineData(0x2620, "kick75-u1-dongle", LightingDeviceSupport.DiagnosticOnly)]
    [InlineData(0x1027, "kick75-high-diagnostic", LightingDeviceSupport.DiagnosticOnly)]
    public async Task InspectAsync_StrictDescriptor_ReturnsMetadataWithoutOpeningProtocolConnection(
        int productId,
        string expectedProfile,
        LightingDeviceSupport expectedSupport)
    {
        HidInterfaceDescriptor descriptor = Device(checked((ushort)productId));
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(descriptor),
            new HidDeviceSelector(),
            factory);

        LightingDeviceInspection? inspection = await transport.InspectAsync();

        Assert.NotNull(inspection);
        Assert.Equal(expectedProfile, inspection.TransportProfile);
        Assert.Equal(expectedSupport, inspection.Support);
        Assert.Equal(descriptor.DeviceIdentity, inspection.DeviceIdentity);
        Assert.Equal(descriptor.InterfaceFingerprint, inspection.InterfaceFingerprint);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task InspectAsync_StrictDescriptor_CarriesSafeDescriptorDisplayMetadata()
    {
        HidInterfaceDescriptor descriptor = Device(0x1026) with
        {
            Manufacturer = " NuPhy ",
            Product = " Kick75 IO ",
            HidDescriptorVersionNumber = 0x0418,
        };
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(descriptor),
            new HidDeviceSelector(),
            factory);

        LightingDeviceInspection? inspection = await transport.InspectAsync();

        LightingDeviceDescriptorMetadata metadata = Assert.IsType<LightingDeviceDescriptorMetadata>(
            inspection?.DescriptorMetadata);
        Assert.Equal("Kick75 IO", metadata.Product);
        Assert.Equal("NuPhy", metadata.Manufacturer);
        Assert.Equal((ushort)0x0418, metadata.HidDescriptorVersionNumber);
        Assert.Equal(descriptor.DeviceIdentity, inspection?.DeviceIdentity);
        Assert.Equal(descriptor.InterfaceFingerprint, inspection?.InterfaceFingerprint);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task InspectAsync_UnsafeDescriptorDisplayStrings_DropsIdentityLikeText()
    {
        HidInterfaceDescriptor descriptor = Device(0x1026) with
        {
            Manufacturer = "serial=private-device-serial",
            Product = @"\\?\hid#vid_19f5&pid_1026#private-hid-path",
            HidDescriptorVersionNumber = 0x0418,
        };
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(descriptor),
            new HidDeviceSelector(),
            new CapturingConnectionFactory());

        LightingDeviceInspection? inspection = await transport.InspectAsync();

        LightingDeviceDescriptorMetadata metadata = Assert.IsType<LightingDeviceDescriptorMetadata>(
            inspection?.DescriptorMetadata);
        Assert.Null(metadata.Product);
        Assert.Null(metadata.Manufacturer);
        Assert.Equal((ushort)0x0418, metadata.HidDescriptorVersionNumber);
    }

    [Theory]
    [InlineData(0x2620)]
    [InlineData(0x1027)]
    public async Task InspectAsync_MalformedDiagnosticDescriptor_ReturnsNullAndNeverOpens(
        int productId)
    {
        HidInterfaceDescriptor malformed = Device(checked((ushort)productId)) with
        {
            Usage = 0x0006,
        };
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(malformed),
            new HidDeviceSelector(),
            factory);

        LightingDeviceInspection? inspection = await transport.InspectAsync();

        Assert.Null(inspection);
        Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData("kick75-usb", 0x1026)]
    public async Task ConnectAsync_RequiredOwnedProfile_SelectsOnlyThatTransport(
        string requiredProfile,
        int expectedProductId)
    {
        HidInterfaceDescriptor usb = Device(0x1026);
        HidInterfaceDescriptor dongle = Device(0x2620);
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new MultipleDeviceEnumerator(usb, dongle),
            new HidDeviceSelector(),
            factory);

        LightingDeviceSession session = await transport.ConnectAsync(
            LightingConnectionRequest.ForOwnedBaseline(requiredProfile));

        Assert.Equal(requiredProfile, session.TransportProfile);
        Assert.Equal(1, session.CurrentMode);
        Assert.Equal((ushort)expectedProductId, factory.Selection?.Device?.ProductId);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ConnectAsync_UsbSession_CarriesDescriptorMetadataWithoutChangingRecoveryKeys()
    {
        HidInterfaceDescriptor usb = Device(0x1026) with
        {
            Manufacturer = "NuPhy",
            Product = "Kick75 IO",
            HidDescriptorVersionNumber = 0x0418,
        };
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(usb),
            new HidDeviceSelector(),
            factory);

        LightingDeviceSession session = await transport.ConnectAsync(LightingConnectionRequest.Auto);

        Assert.Equal(usb.DeviceIdentity, session.DeviceIdentity);
        Assert.Equal(usb.InterfaceFingerprint, session.InterfaceFingerprint);
        Assert.Equal("Kick75 IO", session.DescriptorMetadata?.Product);
        Assert.Equal("NuPhy", session.DescriptorMetadata?.Manufacturer);
        Assert.Equal((ushort)0x0418, session.DescriptorMetadata?.HidDescriptorVersionNumber);
    }

    [Fact]
    public async Task ConnectAsync_RequiredDongleProfile_IsDiagnosticOnlyAndNeverOpened()
    {
        HidInterfaceDescriptor dongle = Device(0x2620);
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(dongle),
            new HidDeviceSelector(),
            factory);

        LightingTransportException exception = await Assert.ThrowsAsync<LightingTransportException>(
            async () =>
            {
                _ = await transport.ConnectAsync(
                    LightingConnectionRequest.ForOwnedBaseline("kick75-u1-dongle"));
            });

        Assert.Equal(LightingTransportFailureKind.ProtocolViolation, exception.Kind);
        Assert.Equal(0, factory.OpenCount);
        Assert.Contains("diagnostic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectAsync_UnknownOwnedProfile_FailsClosedBeforeOpeningDevice()
    {
        HidInterfaceDescriptor usb = Device(0x1026);
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(usb),
            new HidDeviceSelector(),
            factory);

        LightingTransportException exception = await Assert.ThrowsAsync<LightingTransportException>(
            async () =>
            {
                _ = await transport.ConnectAsync(
                    LightingConnectionRequest.ForOwnedBaseline("unknown-profile"));
            });

        Assert.Equal(LightingTransportFailureKind.BaselineMismatch, exception.Kind);
        Assert.Equal(0, factory.OpenCount);
        Assert.Contains("fallback is blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectAsync_InvalidHandshakeResponse_IsNonTransientProtocolViolation()
    {
        HidInterfaceDescriptor device = Device(0x1026);
        var connection = new InvalidHandshakeConnection(device);
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(device),
            new HidDeviceSelector(),
            new SingleConnectionFactory(connection));

        LightingTransportException exception = await Assert.ThrowsAsync<LightingTransportException>(
            async () =>
            {
                _ = await transport.ConnectAsync(LightingConnectionRequest.Auto);
            });

        Assert.Equal(LightingTransportFailureKind.ProtocolViolation, exception.Kind);
        Assert.False(new LayeredReconnectPolicy().IsTransient(exception.Kind));
        Assert.IsType<Kick75ProtocolException>(exception.InnerException);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ConnectAsync_ConnectionFactoryReportsBusy_MapsToTransientTwoSecondRetry()
    {
        HidInterfaceDescriptor device = Device(0x1026);
        var busyException = new HidDeviceBusyException(
            "Simulated sharing violation.",
            new InvalidOperationException("Simulated Win32 open failure."));
        var factory = new ThrowingConnectionFactory(busyException);
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(device),
            new HidDeviceSelector(),
            factory);

        LightingTransportException exception = await Assert.ThrowsAsync<LightingTransportException>(
            async () =>
            {
                _ = await transport.ConnectAsync(LightingConnectionRequest.Auto);
            });
        var reconnectPolicy = new LayeredReconnectPolicy();

        Assert.Equal(LightingTransportFailureKind.DeviceBusy, exception.Kind);
        Assert.True(reconnectPolicy.IsTransient(exception.Kind));
        Assert.Equal(TimeSpan.FromSeconds(2), reconnectPolicy.GetDelay(exception.Kind, attempt: 1));
        Assert.Same(busyException, exception.InnerException);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ConnectAsync_DongleIdentity_IsDiagnosticOnlyBeforeHandshake()
    {
        HidInterfaceDescriptor dongle = Device(0x2620);
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(dongle),
            new HidDeviceSelector(),
            factory);

        LightingTransportException exception = await Assert.ThrowsAsync<LightingTransportException>(
            async () =>
            {
                _ = await transport.ConnectAsync(LightingConnectionRequest.Auto);
            });

        Assert.Equal(LightingTransportFailureKind.ProtocolViolation, exception.Kind);
        Assert.False(new LayeredReconnectPolicy().IsTransient(exception.Kind));
        Assert.Null(exception.InnerException);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task ConnectAsync_UnsupportedDescriptor_IsNonTransientProtocolViolation()
    {
        HidInterfaceDescriptor wrongUsage = Device(0x1026) with { Usage = 0x0006 };
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(wrongUsage),
            new HidDeviceSelector(),
            factory);

        LightingTransportException exception = await Assert.ThrowsAsync<LightingTransportException>(
            async () =>
            {
                _ = await transport.ConnectAsync(LightingConnectionRequest.Auto);
            });

        Assert.Equal(LightingTransportFailureKind.ProtocolViolation, exception.Kind);
        Assert.False(new LayeredReconnectPolicy().IsTransient(exception.Kind));
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task WriteSideLightAsync_ValidState_SendsOneA0ThenAdjacentD6Pair()
    {
        HidInterfaceDescriptor usb = Device(0x1026);
        var factory = new CapturingConnectionFactory();
        await using var transport = new WindowsLightingTransport(
            new SingleDeviceEnumerator(usb),
            new HidDeviceSelector(),
            factory);
        _ = await transport.ConnectAsync(LightingConnectionRequest.Auto);
        ValidHandshakeConnection connection = Assert.IsType<ValidHandshakeConnection>(
            factory.Connection);
        connection.WrittenFrames.Clear();

        await transport.WriteSideLightAsync(ThinkingState);

        Assert.Collection(
            connection.WrittenFrames,
            frame => Assert.Equal((byte)Kick75ProtocolCommand.GetBaseInfo, frame[1]),
            frame => AssertSetLightFrame(
                frame,
                connection.SessionKey,
                Kick75ProtocolCodec.SideLightAddress,
                ThinkingState),
            frame => AssertSetLightFrame(
                frame,
                connection.SessionKey,
                Kick75ProtocolCodec.SideLightBrightnessAddress,
                [ThinkingState[Kick75ProtocolCodec.SideLightBrightnessOffset]]));
    }

    private static void AssertSetLightFrame(
        byte[] frame,
        byte sessionKey,
        int address,
        byte[] payload)
    {
        Assert.Equal((byte)Kick75ProtocolCommand.SetLightState, frame[1]);
        Assert.Equal(payload.Length, frame[4] ^ sessionKey);
        Assert.Equal(address, (frame[5] ^ sessionKey) | ((frame[6] ^ sessionKey) << 8));
        Assert.Equal(1, frame[7] ^ sessionKey);
        Assert.Equal(
            payload,
            frame
                .AsSpan(Kick75ProtocolCodec.HeaderSize, payload.Length)
                .ToArray()
                .Select(value => (byte)(value ^ sessionKey))
                .ToArray());
    }

    private static HidInterfaceDescriptor Device(ushort productId) =>
        new(
            $"device-{productId:X4}",
            0x19F5,
            productId,
            0x0001,
            0x0000,
            65,
            65);

    private sealed class SingleDeviceEnumerator(HidInterfaceDescriptor device) : IHidDeviceEnumerator
    {
        public IReadOnlyList<HidInterfaceDescriptor> Enumerate() => [device];
    }

    private sealed class MultipleDeviceEnumerator(params HidInterfaceDescriptor[] devices)
        : IHidDeviceEnumerator
    {
        public IReadOnlyList<HidInterfaceDescriptor> Enumerate() => devices;
    }

    private sealed class SingleConnectionFactory(IHidReportConnection connection) : IHidConnectionFactory
    {
        public ValueTask<IHidReportConnection> OpenAsync(
            HidDeviceSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(connection);
        }
    }

    private sealed class ThrowingConnectionFactory(Exception exception) : IHidConnectionFactory
    {
        public int OpenCount { get; private set; }

        public ValueTask<IHidReportConnection> OpenAsync(
            HidDeviceSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            throw exception;
        }
    }

    private sealed class CapturingConnectionFactory : IHidConnectionFactory
    {
        public int OpenCount { get; private set; }

        public HidDeviceSelection? Selection { get; private set; }

        public IHidReportConnection? Connection { get; private set; }

        public ValueTask<IHidReportConnection> OpenAsync(
            HidDeviceSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            Selection = selection;
            var connection = new ValidHandshakeConnection(selection.Device!, selection.State);
            Connection = connection;
            return ValueTask.FromResult<IHidReportConnection>(connection);
        }
    }

    private sealed class ValidHandshakeConnection(
        HidInterfaceDescriptor device,
        HidDeviceState state) : IHidReportConnection
    {
        private readonly Queue<byte[]> responses = new();
        private byte sessionKey;

        public HidInterfaceDescriptor Device { get; } = device;

        public HidDeviceState State { get; } = state;

        public byte SessionKey => sessionKey;

        public List<byte[]> WrittenFrames { get; } = new();

        public ValueTask WriteReportAsync(
            ReadOnlyMemory<byte> protocolFrame,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] frame = protocolFrame.ToArray();
            WrittenFrames.Add(frame);
            switch ((Kick75ProtocolCommand)frame[1])
            {
                case Kick75ProtocolCommand.SetSecretKey:
                    sessionKey = frame[28];
                    responses.Enqueue(SessionResponse(frame));
                    break;
                case Kick75ProtocolCommand.GetBaseInfo:
                    responses.Enqueue(GetBaseInfoResponse());
                    break;
                case Kick75ProtocolCommand.SetLightState:
                    responses.Enqueue(SetLightResponse(frame));
                    break;
                default:
                    throw new InvalidOperationException("Unexpected protocol opcode.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadReportAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(responses.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private byte[] SessionResponse(ReadOnlySpan<byte> request)
        {
            byte[] response = new byte[Kick75ProtocolCodec.ReportSize];
            response[0] = Kick75ProtocolCodec.DeviceDirection;
            response[1] = (byte)Kick75ProtocolCommand.SetSecretKey;
            response[4] = sessionKey;
            response[5] = sessionKey;
            response[6] = sessionKey;
            response[7] = sessionKey;
            for (int index = Kick75ProtocolCodec.HeaderSize; index < response.Length; index++)
            {
                response[index] = (byte)(request[index] ^ sessionKey);
            }

            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private byte[] GetBaseInfoResponse()
        {
            byte[] response = new byte[Kick75ProtocolCodec.ReportSize];
            response[0] = Kick75ProtocolCodec.DeviceDirection;
            response[1] = (byte)Kick75ProtocolCommand.GetBaseInfo;
            response[4] = (byte)(Kick75ProtocolCodec.BaseInfoLength ^ sessionKey);
            response[5] = sessionKey;
            response[6] = sessionKey;
            response[7] = sessionKey;
            response[Kick75ProtocolCodec.HeaderSize] = (byte)(1 ^ sessionKey);
            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private byte[] SetLightResponse(ReadOnlySpan<byte> request)
        {
            byte[] response = new byte[Kick75ProtocolCodec.ReportSize];
            response[0] = Kick75ProtocolCodec.DeviceDirection;
            response[1] = (byte)Kick75ProtocolCommand.SetLightState;
            request.Slice(4, 4).CopyTo(response.AsSpan(4, 4));
            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }
    }

    private sealed class TimeoutHandshakeConnection(HidInterfaceDescriptor device) : IHidReportConnection
    {
        public HidInterfaceDescriptor Device { get; } = device;

        public HidDeviceState State { get; } = HidDeviceState.ReceiverPresent;

        public ValueTask WriteReportAsync(
            ReadOnlyMemory<byte> protocolFrame,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal((byte)Kick75ProtocolCommand.SetSecretKey, protocolFrame.Span[1]);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadReportAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<byte[]>(new TimeoutException("Simulated sleeping keyboard."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InvalidHandshakeConnection : IHidReportConnection
    {
        public InvalidHandshakeConnection(HidInterfaceDescriptor device)
        {
            Device = device;
            State = device.ProductId == HidTransportProfiles.Kick75U1Dongle.ProductId
                ? HidDeviceState.ReceiverPresent
                : HidDeviceState.Present;
        }

        public HidInterfaceDescriptor Device { get; }

        public HidDeviceState State { get; }

        public int DisposeCount { get; private set; }

        public ValueTask WriteReportAsync(
            ReadOnlyMemory<byte> protocolFrame,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal((byte)Kick75ProtocolCommand.SetSecretKey, protocolFrame.Span[1]);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadReportAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] invalidResponse = new byte[Kick75ProtocolCodec.ReportSize];
            invalidResponse[0] = 0x00;
            invalidResponse[1] = (byte)Kick75ProtocolCommand.SetSecretKey;
            return ValueTask.FromResult(invalidResponse);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
