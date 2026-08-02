// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Commands;
using AgentKick75.Core.Protocol;
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests.Hid;

public sealed class GuardedHardwareTestServiceTests
{
    private static readonly byte[] Baseline = Convert.FromHexString("00000001A744E7B3");
    private static readonly byte[] Green = Convert.FromHexString("026401000000FF00");

    [Fact]
    public async Task RunAsync_Caps64_ReturnsExplicitNoGoWithoutOpeningDevice()
    {
        HidInterfaceDescriptor caps64 = UsbDevice(inputLength: 64, outputLength: 64);
        var device = new FakeHidDevice(Baseline);
        FakeConnectionFactory factory = new(() => new FakeHidConnection(caps64, device));
        GuardedHardwareTestService service = new(
            new FakeEnumerator(caps64),
            new HidDeviceSelector(),
            factory,
            new TestHardwareBaselineJournal(),
            new ImmediateDelay());

        HardwareTestResult result = await service.RunAsync(
            TestOptions(cycles: 1));

        Assert.Equal(HardwareTestOutcome.NoGo, result.Outcome);
        Assert.Equal(0, factory.OpenCount);
        Assert.Contains("Report ID 0", result.Message, StringComparison.Ordinal);
        Assert.Equal((ushort)64, result.NativeOutputReportLength);
    }

    [Fact]
    public async Task RunAsync_TwoCycles_GreenThenRestoresAndVerifiesEveryCycle()
    {
        var device = new FakeHidDevice(Baseline);
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        GuardedHardwareTestService service = CreateService(factory);

        HardwareTestResult result = await service.RunAsync(
            TestOptions(cycles: 2));

        Assert.Equal(HardwareTestOutcome.Passed, result.Outcome);
        Assert.Equal(HidDeviceState.Ready, result.DeviceState);
        Assert.True(result.AllBaselinesRestored);
        Assert.Equal(2, result.Cycles.Count);
        Assert.All(result.Cycles, cycle =>
        {
            Assert.True(cycle.BaselineRead);
            Assert.True(cycle.GreenAcknowledged);
            Assert.True(cycle.TargetReadBack);
            Assert.True(cycle.TargetMatched);
            Assert.True(cycle.HeldMatched);
            Assert.True(cycle.RestoreAttempted);
            Assert.True(cycle.RestoreAcknowledged);
            Assert.True(cycle.BaselineRestoredByteForByte);
        });
        Assert.Equal(4, factory.OpenCount);
        Assert.Equal(8, device.Writes.Count);
        Assert.Collection(
            device.Writes,
            write => AssertWrite(write, 9, Green),
            write => AssertWrite(write, 10, [100]),
            write => AssertWrite(write, 9, Baseline),
            write => AssertWrite(write, 10, [Baseline[1]]),
            write => AssertWrite(write, 9, Green),
            write => AssertWrite(write, 10, [100]),
            write => AssertWrite(write, 9, Baseline),
            write => AssertWrite(write, 10, [Baseline[1]]));
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.Equal(10, device.SideLightReadCount);
        Assert.All(device.ReadModes, mode => Assert.Equal(1, mode));
    }

    [Fact]
    public async Task RunAsync_KnownBaseline_DerivesExactCurrentNuPhyIoGreenCandidate()
    {
        var device = new FakeHidDevice(Convert.FromHexString("00000001A744E7B3"));
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        GuardedHardwareTestService service = CreateService(factory);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        Assert.Equal(HardwareTestOutcome.Passed, result.Outcome);
        Assert.Collection(
            device.Writes,
            write => AssertWrite(write, 9, Convert.FromHexString("026401000000FF00")),
            write => AssertWrite(write, 10, [100]),
            write => AssertWrite(write, 9, Convert.FromHexString("00000001A744E7B3")),
            write => AssertWrite(write, 10, [0]));
    }

    [Fact]
    public async Task RunAsync_GreenAckWithoutApplyingTarget_FailsAndStillRestores()
    {
        var device = new FakeHidDevice(Baseline)
        {
            AcknowledgeGreenWithoutApplying = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        var delay = new RecordingDelay();
        GuardedHardwareTestService service = CreateService(
            factory,
            delay,
            baselineJournal: journal);

        TimeSpan greenDuration = TimeSpan.FromSeconds(10);
        HardwareTestResult result = await service.RunAsync(
            TestOptions(cycles: 1) with { GreenDuration = greenDuration });

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.True(cycle.GreenAcknowledged);
        Assert.True(cycle.TargetReadBack);
        Assert.False(cycle.TargetMatched);
        Assert.False(cycle.HeldMatched);
        Assert.True(cycle.BaselineRestoredByteForByte);
        Assert.False(cycle.Passed);
        Assert.Contains("immediate", cycle.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.Equal(1, journal.AcquireCount);
        Assert.Equal(1, journal.ReleaseCount);
        Assert.False(journal.IsOwned);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), greenDuration, TimeSpan.FromMilliseconds(100)],
            delay.Durations);
    }

    [Fact]
    public async Task CommandRunAsync_FailedCycle_EmitsSanitizedStageDiagnosticsInJson()
    {
        var device = new FakeHidDevice(Baseline)
        {
            AcknowledgeGreenWithoutApplying = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        GuardedHardwareTestService service = CreateService(factory);
        var command = new GuardedHardwareTestCommand(service);

        HardwareTestCommandResult result = await command.RunAsync(
            new HardwareTestArguments(
                cycles: 1,
                greenDuration: TimeSpan.Zero));

        HardwareTestCycleResult cycle = Assert.IsType<HardwareTestCycleResult>(result.LastCycle);
        Assert.False(result.Succeeded);
        Assert.True(result.AllBaselinesRestored);
        Assert.True(cycle.GreenAcknowledged);
        Assert.True(cycle.TargetReadBack);
        Assert.False(cycle.TargetMatched);
        Assert.False(cycle.HeldMatched);
        Assert.True(cycle.RestoreAttempted);
        Assert.True(cycle.RestoreAcknowledged);
        Assert.True(cycle.BaselineRestoredByteForByte);
        Assert.Contains("immediate", cycle.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        JsonElement lastCycle = json.RootElement.GetProperty(nameof(HardwareTestCommandResult.LastCycle));
        Assert.True(lastCycle.GetProperty(nameof(HardwareTestCycleResult.GreenAcknowledged)).GetBoolean());
        Assert.True(lastCycle.GetProperty(nameof(HardwareTestCycleResult.TargetReadBack)).GetBoolean());
        Assert.False(lastCycle.GetProperty(nameof(HardwareTestCycleResult.TargetMatched)).GetBoolean());
        Assert.False(lastCycle.GetProperty(nameof(HardwareTestCycleResult.HeldMatched)).GetBoolean());
        Assert.True(lastCycle.GetProperty(nameof(HardwareTestCycleResult.RestoreAcknowledged)).GetBoolean());
        Assert.True(lastCycle.GetProperty(nameof(HardwareTestCycleResult.BaselineRestoredByteForByte)).GetBoolean());
        Assert.Contains(
            "immediate",
            lastCycle.GetProperty(nameof(HardwareTestCycleResult.Error)).GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RecoveryReportsDifferentCurrentMode_BlocksRestoreToWrongBank()
    {
        var device = new FakeHidDevice(Baseline);
        int connectionNumber = 0;
        FakeConnectionFactory factory = new(
            () => new FakeHidConnection(
                UsbDevice(),
                device,
                reportedCurrentMode: connectionNumber++ == 0 ? (byte)1 : (byte)0));
        var journal = new TestHardwareBaselineJournal();
        GuardedHardwareTestService service = CreateService(factory, baselineJournal: journal);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.True(cycle.GreenAcknowledged);
        Assert.True(cycle.RestoreAttempted);
        Assert.False(cycle.RestoreAcknowledged);
        Assert.False(cycle.BaselineRestoredByteForByte);
        Assert.Contains("different bank is blocked", cycle.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, device.Writes.Count);
        AssertWrite(device.Writes[0], 9, Green);
        AssertWrite(device.Writes[1], 10, [100]);
        Assert.True(journal.IsOwned);
        Assert.Equal(0, journal.ReleaseCount);
    }

    [Fact]
    public async Task RunAsync_CurrentModeChangesBeforeJournalRelease_KeepsBaselineOwned()
    {
        var device = new FakeHidDevice(Baseline)
        {
            SwitchToModeZeroOnBaseInfoRead = 10,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        GuardedHardwareTestService service = CreateService(factory, baselineJournal: journal);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.True(cycle.RestoreAcknowledged);
        Assert.False(cycle.BaselineRestoredByteForByte);
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.Contains("changed from 1 to 0", cycle.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.True(journal.IsOwned);
        Assert.Equal(0, journal.ReleaseCount);
        Assert.Equal(10, device.BaseInfoReadCount);
    }

    [Fact]
    public async Task RunAsync_ImmediateTargetReadTimesOut_CompletesHoldThenReopensForRestore()
    {
        var device = new FakeHidDevice(Baseline)
        {
            FailImmediateTargetReadback = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        var delay = new RecordingDelay();
        GuardedHardwareTestService service = CreateService(
            factory,
            delay,
            baselineJournal: journal);

        TimeSpan greenDuration = TimeSpan.FromSeconds(10);
        HardwareTestResult result = await service.RunAsync(
            TestOptions(cycles: 1) with { GreenDuration = greenDuration });

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.True(cycle.GreenAcknowledged);
        Assert.False(cycle.TargetReadBack);
        Assert.False(cycle.TargetMatched);
        Assert.False(cycle.HeldMatched);
        Assert.True(cycle.RestoreAcknowledged);
        Assert.True(cycle.BaselineRestoredByteForByte);
        Assert.Equal(2, factory.OpenCount);
        Assert.NotSame(factory.Connections[0], factory.Connections[1]);
        Assert.All(factory.Connections, connection => Assert.Equal(1, connection.SessionHandshakeCount));
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.Equal(1, journal.ReleaseCount);
        Assert.False(journal.IsOwned);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), greenDuration, TimeSpan.FromMilliseconds(100)],
            delay.Durations);
    }

    [Fact]
    public async Task RunAsync_TargetOverriddenDuringHold_FailsAndStillRestores()
    {
        var device = new FakeHidDevice(Baseline)
        {
            OverrideDuringGreenHold = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        GuardedHardwareTestService service = CreateService(factory);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.True(cycle.TargetReadBack);
        Assert.True(cycle.TargetMatched);
        Assert.False(cycle.HeldMatched);
        Assert.True(cycle.BaselineRestoredByteForByte);
        Assert.False(cycle.Passed);
        Assert.Contains("hold", cycle.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Baseline, device.CurrentSideLight);
    }

    [Fact]
    public async Task RunAsync_BaselineAlreadyEqualsGreen_ReturnsNoGoWithoutCountingOrWriting()
    {
        var device = new FakeHidDevice(Green);
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        GuardedHardwareTestService service = CreateService(factory, baselineJournal: journal);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        Assert.Equal(HardwareTestOutcome.NoGo, result.Outcome);
        Assert.Empty(result.Cycles);
        Assert.Contains("cannot prove", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(device.Writes);
        Assert.Equal(1, device.SideLightReadCount);
        Assert.Equal(0, journal.AcquireCount);
        Assert.Equal(0, journal.ReleaseCount);
        Assert.False(journal.IsOwned);
    }

    [Fact]
    public async Task RunAsync_GreenHoldCancelled_StillRestoresAndReadsBackBaseline()
    {
        var device = new FakeHidDevice(Baseline);
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        GuardedHardwareTestService service = CreateService(factory, new CancellingDelay());

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.True(cycle.TargetMatched);
        Assert.False(cycle.HeldMatched);
        Assert.True(cycle.BaselineRestoredByteForByte);
        Assert.False(cycle.Passed);
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.Equal(4, device.Writes.Count);
    }

    [Fact]
    public async Task RunAsync_GreenAckTimesOutAfterPossibleWrite_FinallyRestoresBaseline()
    {
        var device = new FakeHidDevice(Baseline)
        {
            FailFirstGreenAcknowledgement = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        GuardedHardwareTestService service = CreateService(factory);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.False(cycle.GreenAcknowledged);
        Assert.False(cycle.TargetReadBack);
        Assert.False(cycle.TargetMatched);
        Assert.False(cycle.HeldMatched);
        Assert.True(cycle.RestoreAcknowledged);
        Assert.True(cycle.BaselineRestoredByteForByte);
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.True(factory.OpenCount >= 2);
    }

    [Fact]
    public async Task RunAsync_StableRestoreReadbackMismatch_ReturnsFailedNoGo()
    {
        var device = new FakeHidDevice(Baseline)
        {
            CorruptStableReadbackAfterRestore = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        GuardedHardwareTestService service = CreateService(factory, baselineJournal: journal);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.True(cycle.TargetMatched);
        Assert.True(cycle.HeldMatched);
        Assert.False(cycle.BaselineRestoredByteForByte);
        Assert.Contains("byte-for-byte", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not match", cycle.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(journal.IsOwned);
        Assert.Equal(5, device.SideLightReadCount);
    }

    [Fact]
    public async Task RunAsync_TargetBrightnessAckTimesOut_ReopensAndRestoresWithNewSession()
    {
        var device = new FakeHidDevice(Baseline)
        {
            FailFirstGreenBrightnessAcknowledgement = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        GuardedHardwareTestService service = CreateService(factory, baselineJournal: journal);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.False(cycle.GreenAcknowledged);
        Assert.True(cycle.RestoreAcknowledged);
        Assert.True(cycle.BaselineRestoredByteForByte);
        Assert.Equal(2, factory.OpenCount);
        Assert.NotSame(factory.Connections[0], factory.Connections[1]);
        Assert.All(factory.Connections, connection => Assert.Equal(1, connection.SessionHandshakeCount));
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.Equal(1, journal.ReleaseCount);
        Assert.False(journal.IsOwned);
    }

    [Fact]
    public async Task RunAsync_RestoreBrightnessAckTimesOut_KeepsJournalOwnedDespiteMatchingBytes()
    {
        var device = new FakeHidDevice(Baseline)
        {
            FailFirstRestoreBrightnessAcknowledgement = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        GuardedHardwareTestService service = CreateService(factory, baselineJournal: journal);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.False(cycle.RestoreAcknowledged);
        Assert.False(cycle.BaselineRestoredByteForByte);
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.True(factory.OpenCount >= 3);
        Assert.True(journal.IsOwned);
        Assert.Equal(0, journal.ReleaseCount);
    }

    [Fact]
    public async Task RunAsync_RestoreBlockAckTimesOut_StopsPairBeforeBrightnessAndKeepsJournalOwned()
    {
        var device = new FakeHidDevice(Baseline)
        {
            FailFirstRestoreBlockAcknowledgement = true,
        };
        FakeConnectionFactory factory = new(() => new FakeHidConnection(UsbDevice(), device));
        var journal = new TestHardwareBaselineJournal();
        GuardedHardwareTestService service = CreateService(factory, baselineJournal: journal);

        HardwareTestResult result = await service.RunAsync(TestOptions(cycles: 1));

        HardwareTestCycleResult cycle = Assert.Single(result.Cycles);
        Assert.Equal(HardwareTestOutcome.Failed, result.Outcome);
        Assert.False(cycle.RestoreAcknowledged);
        Assert.False(cycle.BaselineRestoredByteForByte);
        Assert.Equal(Baseline, device.CurrentSideLight);
        Assert.DoesNotContain(
            device.Writes,
            write => write.Address == Kick75ProtocolCodec.SideLightBrightnessAddress &&
                write.Payload.AsSpan().SequenceEqual([Baseline[1]]));
        Assert.True(factory.OpenCount >= 3);
        Assert.True(journal.IsOwned);
        Assert.Equal(0, journal.ReleaseCount);
    }

    [Fact]
    public void PublicLightingTransportSurface_ExposesOnlyPairedWrite()
    {
        string[] interfaceWrites = typeof(IKick75LightingTransport)
            .GetMethods()
            .Where(method => method.Name.StartsWith("Write", StringComparison.Ordinal))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] clientWrites = typeof(Kick75HidProtocolClient)
            .GetMethods()
            .Where(method =>
                method.DeclaringType == typeof(Kick75HidProtocolClient) &&
                method.Name.StartsWith("Write", StringComparison.Ordinal))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([nameof(Kick75HidProtocolClient.WriteSideLightFullStateAsync)], interfaceWrites);
        Assert.Equal([nameof(Kick75HidProtocolClient.WriteSideLightFullStateAsync)], clientWrites);
    }

    [Fact]
    public async Task WriteSideLightFullStateAsync_ValidState_UsesOneA0ThenAdjacentD6Pair()
    {
        var device = new FakeHidDevice(Baseline);
        FakeHidConnection connection = new(UsbDevice(), device);
        await using Kick75HidProtocolClient client = new(connection, TimeSpan.FromMilliseconds(100));
        await client.InitializeAsync();
        connection.WrittenFrames.Clear();

        await client.WriteSideLightFullStateAsync(Green);

        Assert.Collection(
            connection.WrittenFrames,
            frame => Assert.Equal((byte)Kick75ProtocolCommand.GetBaseInfo, frame[1]),
            frame => AssertSetLightFrame(
                frame,
                connection.SessionKey,
                Kick75ProtocolCodec.SideLightAddress,
                Green),
            frame => AssertSetLightFrame(
                frame,
                connection.SessionKey,
                Kick75ProtocolCodec.SideLightBrightnessAddress,
                [Green[Kick75ProtocolCodec.SideLightBrightnessOffset]]));
        Assert.True(client.IsReady);
    }

    [Fact]
    public async Task WriteSideLightFullStateAsync_BrightnessAckTimesOut_PoisonsClient()
    {
        var device = new FakeHidDevice(Baseline)
        {
            FailFirstGreenBrightnessAcknowledgement = true,
        };
        FakeHidConnection connection = new(UsbDevice(), device);
        await using Kick75HidProtocolClient client = new(connection, TimeSpan.FromMilliseconds(100));
        await client.InitializeAsync();
        connection.WrittenFrames.Clear();

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await client.WriteSideLightFullStateAsync(Green));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ReadSideLightStateAsync());

        Assert.Equal(
            [
                (byte)Kick75ProtocolCommand.GetBaseInfo,
                (byte)Kick75ProtocolCommand.SetLightState,
                (byte)Kick75ProtocolCommand.SetLightState,
            ],
            connection.WrittenFrames.Select(frame => frame[1]));
        Assert.False(client.IsReady);
    }

    [Fact]
    public async Task WriteSideLightFullStateAsync_CurrentModeChanged_BlocksBeforePairAndPoisonsClient()
    {
        var device = new FakeHidDevice(Baseline);
        FakeHidConnection connection = new(UsbDevice(), device, reportedCurrentMode: 1);
        await using Kick75HidProtocolClient client = new(connection, TimeSpan.FromMilliseconds(100));
        await client.InitializeAsync();
        connection.WrittenFrames.Clear();
        connection.ReportedCurrentMode = 0;

        Kick75ProtocolException exception = await Assert.ThrowsAsync<Kick75ProtocolException>(
            async () => await client.WriteSideLightFullStateAsync(Green));

        Assert.Contains("changed from 1 to 0", exception.Message, StringComparison.Ordinal);
        Assert.Collection(
            connection.WrittenFrames,
            frame => Assert.Equal((byte)Kick75ProtocolCommand.GetBaseInfo, frame[1]));
        Assert.Empty(device.Writes);
        Assert.False(client.IsReady);
        Assert.Equal(HidDeviceState.Unresponsive, client.State);
    }

    private static void AssertWrite(SetLightWrite write, int address, byte[] payload)
    {
        Assert.Equal(address, write.Address);
        Assert.Equal(1, write.CurrentMode);
        Assert.Equal(payload, write.Payload);
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
                .Select(value => (byte)(value ^ sessionKey)));
    }

    private static GuardedHardwareTestService CreateService(
        FakeConnectionFactory factory,
        IHardwareTestDelay? delay = null,
        IHardwareTestBaselineJournal? baselineJournal = null) =>
        new(
            new FakeEnumerator(UsbDevice()),
            new HidDeviceSelector(),
            factory,
            baselineJournal ?? new TestHardwareBaselineJournal(),
            delay ?? new ImmediateDelay());

    private static HardwareTestOptions TestOptions(int cycles) =>
        new()
        {
            Cycles = cycles,
            GreenDuration = TimeSpan.Zero,
            CommandTimeout = TimeSpan.FromMilliseconds(100),
        };

    private static HidInterfaceDescriptor UsbDevice(
        ushort inputLength = 65,
        ushort outputLength = 65) =>
        new("usb", 0x19F5, 0x1026, 0x0001, 0x0000, inputLength, outputLength);

    private sealed class FakeEnumerator(params HidInterfaceDescriptor[] devices) : IHidDeviceEnumerator
    {
        public IReadOnlyList<HidInterfaceDescriptor> Enumerate() => devices;
    }

    private sealed class FakeConnectionFactory(Func<FakeHidConnection> create) : IHidConnectionFactory
    {
        public int OpenCount { get; private set; }

        public List<FakeHidConnection> Connections { get; } = new();

        public ValueTask<IHidReportConnection> OpenAsync(
            HidDeviceSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            FakeHidConnection connection = create();
            Connections.Add(connection);
            return ValueTask.FromResult<IHidReportConnection>(connection);
        }
    }

    private sealed class ImmediateDelay : IHardwareTestDelay
    {
        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDelay : IHardwareTestDelay
    {
        public List<TimeSpan> Durations { get; } = new();

        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Durations.Add(duration);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingDelay : IHardwareTestDelay
    {
        private int callCount;

        public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref callCount) == 2)
            {
                return ValueTask.FromException(
                    new OperationCanceledException("Test cancellation."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SetLightWrite(ushort Address, byte CurrentMode, byte[] Payload);

    private enum FakeWritePhase
    {
        TargetBlock,
        TargetBrightness,
        RestoreBlock,
        RestoreBrightness,
    }

    private sealed class FakeHidDevice
    {
        private bool failedGreenAcknowledgement;
        private bool failedGreenBrightnessAcknowledgement;
        private bool failedRestoreBlockAcknowledgement;
        private bool failedRestoreBrightnessAcknowledgement;
        private bool failedImmediateTargetReadback;

        public FakeHidDevice(byte[] baseline)
        {
            OriginalBaseline = baseline.ToArray();
            CurrentSideLight = baseline.ToArray();
        }

        public byte[] OriginalBaseline { get; }

        public byte[] CurrentSideLight { get; set; }

        public List<SetLightWrite> Writes { get; } = new();

        public List<byte> ReadModes { get; } = new();

        public int SideLightReadCount { get; set; }

        public int BaseInfoReadCount { get; set; }

        public int TargetReadbackCount { get; set; }

        public int RestoreReadbackCount { get; set; }

        public FakeWritePhase Phase { get; set; } = FakeWritePhase.TargetBlock;

        public bool RestoreCompleted { get; set; }

        public bool FailFirstGreenAcknowledgement { get; init; }

        public bool FailFirstGreenBrightnessAcknowledgement { get; init; }

        public bool FailFirstRestoreBlockAcknowledgement { get; init; }

        public bool FailFirstRestoreBrightnessAcknowledgement { get; init; }

        public bool FailImmediateTargetReadback { get; init; }

        public bool AcknowledgeGreenWithoutApplying { get; init; }

        public bool OverrideDuringGreenHold { get; init; }

        public bool CorruptStableReadbackAfterRestore { get; init; }

        public int? SwitchToModeZeroOnBaseInfoRead { get; init; }

        public bool ShouldFailGreenAcknowledgement() =>
            FailFirstGreenAcknowledgement && !failedGreenAcknowledgement &&
            (failedGreenAcknowledgement = true);

        public bool ShouldFailGreenBrightnessAcknowledgement() =>
            FailFirstGreenBrightnessAcknowledgement && !failedGreenBrightnessAcknowledgement &&
            (failedGreenBrightnessAcknowledgement = true);

        public bool ShouldFailRestoreBlockAcknowledgement() =>
            FailFirstRestoreBlockAcknowledgement && !failedRestoreBlockAcknowledgement &&
            (failedRestoreBlockAcknowledgement = true);

        public bool ShouldFailRestoreBrightnessAcknowledgement() =>
            FailFirstRestoreBrightnessAcknowledgement && !failedRestoreBrightnessAcknowledgement &&
            (failedRestoreBrightnessAcknowledgement = true);

        public bool ShouldFailImmediateTargetReadback() =>
            FailImmediateTargetReadback &&
            TargetReadbackCount == 1 &&
            !failedImmediateTargetReadback &&
            (failedImmediateTargetReadback = true);
    }

    private sealed class FakeHidConnection : IHidReportConnection
    {
        private readonly Queue<byte[]> responses = new();
        private readonly FakeHidDevice device;
        private byte reportedCurrentMode;
        private byte sessionKey;

        public FakeHidConnection(
            HidInterfaceDescriptor descriptor,
            FakeHidDevice device,
            byte reportedCurrentMode = 1)
        {
            Device = descriptor;
            this.device = device;
            ReportedCurrentMode = reportedCurrentMode;
            State = HidDeviceState.Present;
        }

        public HidInterfaceDescriptor Device { get; }

        public HidDeviceState State { get; private set; }

        public byte SessionKey => sessionKey;

        public int SessionHandshakeCount { get; private set; }

        public List<byte[]> WrittenFrames { get; } = new();

        public byte ReportedCurrentMode
        {
            get => reportedCurrentMode;
            set => reportedCurrentMode = value <= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public ValueTask WriteReportAsync(
            ReadOnlyMemory<byte> protocolFrame,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] frame = protocolFrame.ToArray();
            WrittenFrames.Add(frame);
            Assert.Equal(Kick75ProtocolCodec.ReportSize, frame.Length);
            switch (frame[1])
            {
                case (byte)Kick75ProtocolCommand.SetSecretKey:
                    sessionKey = frame[28];
                    SessionHandshakeCount++;
                    responses.Enqueue(SessionResponse(frame));
                    break;
                case (byte)Kick75ProtocolCommand.GetBaseInfo:
                    responses.Enqueue(GetBaseInfoResponse(frame));
                    break;
                case (byte)Kick75ProtocolCommand.GetLightState:
                    responses.Enqueue(GetLightResponse(RequireCurrentMode(frame)));
                    break;
                case (byte)Kick75ProtocolCommand.SetLightState:
                    return HandleSetLightWrite(frame);
                default:
                    throw new InvalidOperationException("Unexpected opcode.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadReportAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (device.ShouldFailImmediateTargetReadback())
            {
                return ValueTask.FromException<byte[]>(
                    new TimeoutException("Simulated immediate target readback timeout."));
            }

            return ValueTask.FromResult(responses.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            State = HidDeviceState.Disconnected;
            return ValueTask.CompletedTask;
        }

        private byte[] GetBaseInfoResponse(ReadOnlySpan<byte> request)
        {
            Assert.Equal(0, request[7] ^ sessionKey);
            device.BaseInfoReadCount++;
            if (device.SwitchToModeZeroOnBaseInfoRead == device.BaseInfoReadCount)
            {
                ReportedCurrentMode = 0;
            }

            byte[] response = Response(
                Kick75ProtocolCommand.GetBaseInfo,
                Kick75ProtocolCodec.BaseInfoLength,
                0,
                currentMode: 0);
            response[Kick75ProtocolCodec.HeaderSize] = (byte)(ReportedCurrentMode ^ sessionKey);
            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private byte[] GetLightResponse(byte currentMode)
        {
            device.ReadModes.Add(currentMode);
            device.SideLightReadCount++;
            if (device.Phase == FakeWritePhase.RestoreBlock)
            {
                device.TargetReadbackCount++;
                if (device.OverrideDuringGreenHold && device.TargetReadbackCount == 2)
                {
                    device.CurrentSideLight = device.OriginalBaseline.ToArray();
                }
            }

            if (device.RestoreCompleted)
            {
                device.RestoreReadbackCount++;
            }

            byte[] response = Response(
                Kick75ProtocolCommand.GetLightState,
                Kick75ProtocolCodec.LightStateLength,
                0,
                currentMode);
            byte[] fullState = new byte[Kick75ProtocolCodec.LightStateLength];
            byte[] sideLight = device.CorruptStableReadbackAfterRestore &&
                device.RestoreCompleted &&
                device.RestoreReadbackCount == 2
                ? device.CurrentSideLight.Select(
                    (value, index) => index == 0 ? (byte)(value ^ 1) : value).ToArray()
                : device.CurrentSideLight;
            sideLight.CopyTo(fullState, Kick75ProtocolCodec.SideLightOffsetInLightState);
            for (int index = 0; index < fullState.Length; index++)
            {
                response[8 + index] = (byte)(fullState[index] ^ sessionKey);
            }

            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private ValueTask HandleSetLightWrite(byte[] frame)
        {
            byte currentMode = RequireCurrentMode(frame);
            int logicalLength = frame[4] ^ sessionKey;
            ushort address = (ushort)(
                (frame[5] ^ sessionKey) |
                ((frame[6] ^ sessionKey) << 8));
            Assert.True(
                (address == Kick75ProtocolCodec.SideLightAddress &&
                    logicalLength == Kick75ProtocolCodec.SideLightLength) ||
                (address == Kick75ProtocolCodec.SideLightBrightnessAddress &&
                    logicalLength == Kick75ProtocolCodec.SideLightBrightnessLength));
            byte[] payload = frame
                .AsSpan(Kick75ProtocolCodec.HeaderSize, logicalLength)
                .ToArray()
                .Select(value => (byte)(value ^ sessionKey))
                .ToArray();
            device.Writes.Add(new SetLightWrite(address, currentMode, payload.ToArray()));

            if (address == Kick75ProtocolCodec.SideLightAddress)
            {
                bool isTarget = payload.AsSpan().SequenceEqual(Green);
                bool isRestore = payload.AsSpan().SequenceEqual(device.OriginalBaseline);
                Assert.True(isTarget || isRestore, "Unexpected eight-byte fake side-light payload.");
                if (isTarget)
                {
                    device.RestoreCompleted = false;
                    device.TargetReadbackCount = 0;
                    device.RestoreReadbackCount = 0;
                    if (!device.AcknowledgeGreenWithoutApplying)
                    {
                        device.CurrentSideLight = payload.ToArray();
                    }

                    device.Phase = FakeWritePhase.TargetBrightness;
                    if (device.ShouldFailGreenAcknowledgement())
                    {
                        return ValueTask.FromException(
                            new TimeoutException("Simulated missing target block acknowledgement."));
                    }
                }
                else
                {
                    device.CurrentSideLight = payload.ToArray();
                    device.RestoreCompleted = false;
                    device.RestoreReadbackCount = 0;
                    device.Phase = FakeWritePhase.RestoreBrightness;
                    if (device.ShouldFailRestoreBlockAcknowledgement())
                    {
                        return ValueTask.FromException(
                            new TimeoutException("Simulated missing restore block acknowledgement."));
                    }
                }
            }
            else if (device.Phase == FakeWritePhase.TargetBrightness)
            {
                device.CurrentSideLight[Kick75ProtocolCodec.SideLightBrightnessOffset] = payload[0];
                device.Phase = FakeWritePhase.RestoreBlock;
                if (device.ShouldFailGreenBrightnessAcknowledgement())
                {
                    return ValueTask.FromException(
                        new TimeoutException("Simulated missing target brightness acknowledgement."));
                }
            }
            else
            {
                Assert.Equal(FakeWritePhase.RestoreBrightness, device.Phase);
                device.CurrentSideLight[Kick75ProtocolCodec.SideLightBrightnessOffset] = payload[0];
                device.Phase = FakeWritePhase.TargetBlock;
                device.RestoreCompleted = true;
                if (device.ShouldFailRestoreBrightnessAcknowledgement())
                {
                    return ValueTask.FromException(
                        new TimeoutException("Simulated missing restore brightness acknowledgement."));
                }
            }

            responses.Enqueue(Response(
                Kick75ProtocolCommand.SetLightState,
                logicalLength,
                address,
                currentMode));
            return ValueTask.CompletedTask;
        }

        private byte RequireCurrentMode(ReadOnlySpan<byte> frame)
        {
            byte actualCurrentMode = (byte)(frame[7] ^ sessionKey);
            if (actualCurrentMode != ReportedCurrentMode)
            {
                throw new InvalidOperationException(
                    $"The mock rejected currentMode {actualCurrentMode}; expected {ReportedCurrentMode}.");
            }

            return actualCurrentMode;
        }

        private byte[] SessionResponse(ReadOnlySpan<byte> request)
        {
            byte[] response = Response(
                Kick75ProtocolCommand.SetSecretKey,
                logicalLength: 0,
                address: 0,
                currentMode: 0);
            for (int index = Kick75ProtocolCodec.HeaderSize; index < response.Length; index++)
            {
                response[index] = (byte)(request[index] ^ sessionKey);
            }

            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private byte[] Response(
            Kick75ProtocolCommand command,
            int logicalLength,
            ushort address,
            byte currentMode)
        {
            byte[] response = new byte[Kick75ProtocolCodec.ReportSize];
            response[0] = Kick75ProtocolCodec.DeviceDirection;
            response[1] = (byte)command;
            SetEncodedFields(response, logicalLength, address, currentMode);
            response[3] = Kick75ProtocolCodec.CalculateChecksum(response);
            return response;
        }

        private void SetEncodedFields(
            Span<byte> response,
            int logicalLength,
            ushort address,
            byte currentMode)
        {
            response[4] = (byte)(logicalLength ^ sessionKey);
            response[5] = (byte)((address & 0xFF) ^ sessionKey);
            response[6] = (byte)((address >> 8) ^ sessionKey);
            response[7] = (byte)(currentMode ^ sessionKey);
        }
    }
}
