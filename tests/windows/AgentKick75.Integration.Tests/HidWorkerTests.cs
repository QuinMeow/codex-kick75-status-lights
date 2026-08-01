// SPDX-License-Identifier: MIT
using AgentKick75.App.Lighting;
using AgentKick75.Core.Baseline;

namespace AgentKick75.Integration.Tests;

public sealed class HidWorkerTests
{
    private static readonly byte[] Baseline = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];
    private static readonly byte[] Thinking = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];
    private static readonly byte[] Complete = [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00];

    [Fact]
    public async Task Snapshot_FreshIdleThenConnected_OnlyReportsObservedInterfaceFingerprint()
    {
        var session = new LightingDeviceSession(
            "mock-device",
            "kick75-usb",
            "descriptor-interface-fingerprint",
            CurrentMode: 0);
        var transport = new MockLightingTransport(Baseline, session);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());

        Assert.Null(worker.Snapshot.InterfaceFingerprint);
        worker.Start();
        Assert.Null(worker.Snapshot.InterfaceFingerprint);
        Assert.Empty(transport.ConnectionRequests);

        await worker.SetSideLightAsync(Thinking);

        Assert.Equal(
            "descriptor-interface-fingerprint",
            worker.Snapshot.InterfaceFingerprint);
        Assert.Equal(
            LightingDeviceObservationKind.RuntimeSession,
            worker.Snapshot.DeviceObservation);
        Assert.Equal(LightingDeviceSupport.Writable, worker.Snapshot.DeviceSupport);

        await worker.QuiesceAsync();

        Assert.Null(worker.Snapshot.InterfaceFingerprint);
        Assert.Equal(LightingDeviceObservationKind.None, worker.Snapshot.DeviceObservation);
    }

    [Fact]
    public async Task ProbeAsync_FreshIdle_InspectsStrictDescriptorWithoutConnectingAndIsRateLimited()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-31T00:00:00Z"));
        var transport = new MockLightingTransport(Baseline)
        {
            Inspection = new LightingDeviceInspection(
                "19F5:1026/path=private-path",
                "kick75-usb",
                "19F5:1026/0001:0000/in=65/out=65",
                LightingDeviceSupport.Writable),
        };
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore(),
            timeProvider: clock);
        worker.Start();

        Task[] concurrentProbes = Enumerable.Range(0, 20)
            .Select(_ => worker.ProbeAsync())
            .ToArray();
        await Task.WhenAll(concurrentProbes);

        Assert.Equal(LightingWorkerState.Idle, worker.Snapshot.State);
        Assert.Equal(
            LightingDeviceObservationKind.Descriptor,
            worker.Snapshot.DeviceObservation);
        Assert.Equal(LightingDeviceSupport.Writable, worker.Snapshot.DeviceSupport);
        Assert.Equal("kick75-usb", worker.Snapshot.TransportProfile);
        Assert.Equal("19F5:1026/path=private-path", worker.Snapshot.DeviceIdentity);
        Assert.Equal(
            "19F5:1026/0001:0000/in=65/out=65",
            worker.Snapshot.InterfaceFingerprint);
        Assert.Equal(1, transport.Operations.Count(operation => operation == "inspect"));
        Assert.Empty(transport.ConnectionRequests);
        Assert.Empty(transport.Writes);
        Assert.Equal(1, transport.MaximumConcurrency);

        clock.Advance(HidLightingWorker.HealthProbeInterval);
        await worker.ProbeAsync();

        Assert.Equal(2, transport.Operations.Count(operation => operation == "inspect"));
        Assert.Empty(transport.ConnectionRequests);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ProbeAsync_FreshIdleWithOwnedJournal_RestoresBeforeInspectionAndReleasesMarker()
    {
        var session = new LightingDeviceSession(
            "owned-device",
            "kick75-usb",
            "owned-interface",
            CurrentMode: 1);
        var transport = new MockLightingTransport(Thinking, session)
        {
            Inspection = new LightingDeviceInspection(
                "19F5:2620/path=must-not-inspect",
                "kick75-u1-dongle",
                "diagnostic-interface",
                LightingDeviceSupport.DiagnosticOnly),
        };
        var store = new InMemoryBaselineOwnershipStore();
        await store.SaveAsync(new BaselineOwnershipRecord(
            BaselineRecord.CurrentSchemaVersion,
            session.DeviceIdentity,
            session.TransportProfile,
            session.InterfaceFingerprint,
            Baseline,
            session.CurrentMode,
            BaselineOwnership.MarkerPrefix + "startup-recovery",
            IsOwned: true,
            DateTimeOffset.Parse("2026-07-31T00:00:00Z")));
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await worker.ProbeAsync();

        Assert.Equal(
            new[] { "connect", "write", "read", "disconnect" },
            transport.Operations);
        Assert.DoesNotContain("inspect", transport.Operations);
        Assert.Equal(Baseline, Assert.Single(transport.Writes));
        Assert.Equal(
            session.TransportProfile,
            Assert.Single(transport.ConnectionRequests).RequiredTransportProfileId);
        BaselineOwnershipRecord persisted = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.False(persisted.IsOwned);
        Assert.Equal(LightingWorkerState.Idle, worker.Snapshot.State);
        Assert.Equal(LightingDeviceObservationKind.None, worker.Snapshot.DeviceObservation);
    }

    [Fact]
    public async Task StopAsync_ActiveTakeover_RestoresExactBaselineAndReleasesMarker()
    {
        var transport = new MockLightingTransport(Baseline);
        var store = new InMemoryBaselineOwnershipStore();
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        await worker.StopAsync();

        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(Thinking, transport.Writes[0]);
        Assert.Equal(Baseline, transport.Writes[1]);
        Assert.Equal(1, transport.MaximumConcurrency);
        Assert.Null(Assert.Single(transport.ConnectionRequests).RequiredTransportProfileId);
        BaselineOwnershipRecord persisted = Assert.IsType<BaselineOwnershipRecord>(await store.LoadAsync());
        Assert.False(persisted.IsOwned);
        Assert.Equal(transport.Session.CurrentMode, persisted.CurrentMode);
    }

    [Fact]
    public async Task PauseAsync_ActiveTakeover_RestoresBeforeReportingPaused()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        await worker.PauseAsync();

        Assert.Equal(Baseline, transport.Writes[^1]);
        Assert.Equal(LightingWorkerState.Paused, worker.Snapshot.State);
    }

    [Fact]
    public async Task Reconnect_WriteDisconnect_RestoresOldOwnershipBeforeFreshAcquire()
    {
        var transport = new MockLightingTransport(Baseline);
        transport.FailNext(MockLightingOperation.Write, LightingTransportFailureKind.DeviceDisconnected);
        var store = new InMemoryBaselineOwnershipStore();
        await using var worker = new HidLightingWorker(
            transport,
            store,
            reconnectDelay: new ImmediateReconnectDelay());
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        await WaitForStateAsync(worker, LightingWorkerState.Active);

        // Failed target writes are not recorded. The first recorded write is the
        // exact old baseline restored after reconnect, then a new capture/marker
        // precedes replay of the target.
        Assert.Equal(Baseline, transport.Writes[0]);
        Assert.Equal(Thinking, transport.Writes[1]);
        Assert.Equal(
            new[]
            {
                "connect",
                "read",
                "disconnect",
                "connect",
                "write",
                "read", // verify the recovered baseline before releasing ownership
                "read", // capture a fresh baseline for the replayed target
                "write",
            },
            transport.Operations.Take(8));
        BaselineOwnershipRecord persisted = Assert.IsType<BaselineOwnershipRecord>(await store.LoadAsync());
        Assert.True(persisted.IsOwned);
        Assert.Equal(2, transport.ConnectionRequests.Count);
        Assert.Null(transport.ConnectionRequests[0].RequiredTransportProfileId);
        Assert.Equal(
            transport.Session.TransportProfile,
            transport.ConnectionRequests[1].RequiredTransportProfileId);
    }

    [Fact]
    public async Task ConcurrentRequests_AllTransportCallsRemainSerialized()
    {
        var transport = new MockLightingTransport(Baseline);
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore());
        worker.Start();

        Task[] requests = Enumerable.Range(0, 12)
            .Select(index => worker.SetSideLightAsync(
            new byte[]
            {
                0x02,
                0x64,
                0x01,
                0x00,
                0x00,
                (byte)index,
                0x00,
                0x00,
            }))
            .ToArray();
        await Task.WhenAll(requests);

        Assert.Equal(1, transport.MaximumConcurrency);
    }

    [Fact]
    public async Task SetSideLightAsync_FirstBaselineSaveFails_DoesNotWriteUntilRetryPersistsMarker()
    {
        var transport = new MockLightingTransport(Baseline);
        var store = new FailFirstSaveBaselineStore();
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await Assert.ThrowsAsync<IOException>(
            async () => await worker.SetSideLightAsync(Thinking));

        Assert.Equal(1, store.SaveAttempts);
        Assert.Empty(transport.Writes);

        // Retrying the same desired state must retry durable acquisition. A
        // failed SaveAsync is never allowed to turn target de-duplication into
        // an unmarked device write (or a permanently dropped target).
        await worker.SetSideLightAsync(Thinking);

        Assert.Equal(2, store.SaveAttempts);
        Assert.Single(transport.Writes);
        Assert.Equal(Thinking, transport.Writes[0]);
        BaselineOwnershipRecord persisted = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(persisted.IsOwned);
    }

    [Fact]
    public async Task SetSideLightAsync_RecoveryLoadFails_DoesNotConnectThenRestoresBeforeRetryWrite()
    {
        var transport = new MockLightingTransport(Thinking);
        var store = new FailFirstLoadOwnedBaselineStore(Baseline, transport.Session);
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await Assert.ThrowsAsync<IOException>(
            async () => await worker.SetSideLightAsync(Complete));

        Assert.Empty(transport.Writes);
        Assert.Empty(transport.Operations);
        Assert.Empty(transport.ConnectionRequests);

        await worker.SetSideLightAsync(Complete);

        Assert.Equal(Baseline, transport.Writes[0]);
        Assert.Equal(Complete, transport.Writes[1]);
        Assert.True(store.LoadAttempts >= 2);
        Assert.True(store.ReleaseCount >= 1);
        Assert.Equal(
            transport.Session.TransportProfile,
            Assert.Single(transport.ConnectionRequests).RequiredTransportProfileId);
    }

    [Fact]
    public async Task SetSideLightAsync_OwnedDongleBaseline_ConstrainsConnectionBeforeRestore()
    {
        var session = new LightingDeviceSession(
            "dongle-device",
            "kick75-u1-dongle",
            "dongle-interface",
            CurrentMode: 0);
        var transport = new MockLightingTransport(Thinking, session);
        var store = new InMemoryBaselineOwnershipStore();
        await store.SaveAsync(new BaselineOwnershipRecord(
            Version: BaselineRecord.CurrentSchemaVersion,
            session.DeviceIdentity,
            session.TransportProfile,
            session.InterfaceFingerprint,
            Baseline.ToArray(),
            session.CurrentMode,
            AgentKick75.Core.Baseline.BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
            IsOwned: true,
            DateTimeOffset.UtcNow));
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await worker.SetSideLightAsync(Complete);

        LightingConnectionRequest request = Assert.Single(transport.ConnectionRequests);
        Assert.Equal("kick75-u1-dongle", request.RequiredTransportProfileId);
        Assert.Equal(Baseline, transport.Writes[0]);
        Assert.Equal(Complete, transport.Writes[1]);
    }

    [Fact]
    public async Task SetSideLightAsync_OwnedBaselineCurrentModeMismatch_RefusesRestoreAndKeepsOwnership()
    {
        var session = new LightingDeviceSession(
            "mock-device",
            "mock",
            "mock-interface",
            CurrentMode: 0);
        var transport = new MockLightingTransport(Thinking, session);
        var store = new InMemoryBaselineOwnershipStore();
        await store.SaveAsync(new BaselineOwnershipRecord(
            BaselineRecord.CurrentSchemaVersion,
            session.DeviceIdentity,
            session.TransportProfile,
            session.InterfaceFingerprint,
            Baseline.ToArray(),
            CurrentMode: 1,
            BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
            IsOwned: true,
            DateTimeOffset.UtcNow));
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await worker.SetSideLightAsync(Complete);

        Assert.Equal(LightingWorkerState.Faulted, worker.Snapshot.State);
        Assert.Equal(LightingTransportFailureKind.BaselineMismatch, worker.Snapshot.LastFailure);
        Assert.Empty(transport.Writes);
        BaselineOwnershipRecord persisted = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(persisted.IsOwned);
        Assert.Equal((byte)1, persisted.CurrentMode);
    }

    [Fact]
    public async Task AbandonMismatchedBaselineAsync_RequiresCurrentChallengeAndNeverWritesHid()
    {
        var observedSession = new LightingDeviceSession(
            "19F5:1026/path=observed-device",
            "kick75-usb",
            "19F5:1026/0001:0000/in=65/out=65",
            CurrentMode: 0);
        var transport = new MockLightingTransport(Baseline, observedSession)
        {
            Inspection = new LightingDeviceInspection(
                observedSession.DeviceIdentity,
                observedSession.TransportProfile,
                observedSession.InterfaceFingerprint,
                LightingDeviceSupport.Writable),
        };
        var store = new InMemoryBaselineOwnershipStore();
        await store.SaveAsync(new BaselineOwnershipRecord(
            BaselineRecord.CurrentSchemaVersion,
            "19F5:1026/path=baseline-device",
            observedSession.TransportProfile,
            observedSession.InterfaceFingerprint,
            Baseline.ToArray(),
            observedSession.CurrentMode,
            BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
            IsOwned: true,
            DateTimeOffset.UtcNow));
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);

        BaselineIdentityMismatchNotice notice = Assert.IsType<BaselineIdentityMismatchNotice>(
            worker.Snapshot.BaselineMismatch);
        Assert.Equal(LightingWorkerState.Faulted, worker.Snapshot.State);
        Assert.Equal(LightingTransportFailureKind.BaselineMismatch, worker.Snapshot.LastFailure);
        Assert.Empty(transport.Writes);

        BaselineMismatchRecoveryResult stale = await worker
            .AbandonMismatchedBaselineAsync(Guid.NewGuid().ToString("N"), confirmed: true);
        BaselineMismatchRecoveryResult unconfirmed = await worker
            .AbandonMismatchedBaselineAsync(notice.ConfirmationId, confirmed: false);
        Assert.Equal(BaselineMismatchRecoveryStatus.StaleConfirmation, stale.Status);
        Assert.Equal(BaselineMismatchRecoveryStatus.NotConfirmed, unconfirmed.Status);
        Assert.True(Assert.IsType<BaselineOwnershipRecord>(await store.LoadAsync()).IsOwned);
        Assert.Empty(transport.Writes);

        BaselineMismatchRecoveryResult released = await worker
            .AbandonMismatchedBaselineAsync(notice.ConfirmationId, confirmed: true);
        BaselineMismatchRecoveryResult repeated = await worker
            .AbandonMismatchedBaselineAsync(notice.ConfirmationId, confirmed: true);

        Assert.Equal(BaselineMismatchRecoveryStatus.Released, released.Status);
        Assert.Equal(BaselineMismatchRecoveryStatus.NoPendingMismatch, repeated.Status);
        Assert.Equal(LightingWorkerState.Paused, worker.Snapshot.State);
        Assert.False(Assert.IsType<BaselineOwnershipRecord>(await store.LoadAsync()).IsOwned);
        Assert.Empty(transport.Writes);
        Assert.Equal(4, transport.Operations.Count(operation => operation == "inspect"));
    }

    [Fact]
    public async Task SetSideLightAsync_SameDesiredDuringReconnect_PreservesRetryAndWritesOnce()
    {
        var transport = new MockLightingTransport(Baseline);
        transport.FailNext(MockLightingOperation.Connect, LightingTransportFailureKind.DeviceDisconnected);
        var reconnectDelay = new ControlledReconnectDelay();
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore(),
            reconnectDelay: reconnectDelay);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        await WaitForStateAsync(worker, LightingWorkerState.Reconnecting);
        Assert.Equal(1, reconnectDelay.CallCount);

        // This duplicate must neither emit another write nor invalidate the
        // already scheduled retry generation.
        await worker.SetSideLightAsync(Thinking);
        Assert.Equal(LightingWorkerState.Reconnecting, worker.Snapshot.State);
        Assert.Equal(1, reconnectDelay.CallCount);

        reconnectDelay.Release();
        await WaitForStateAsync(worker, LightingWorkerState.Active);
        Assert.Single(transport.Writes);
        Assert.Equal(Thinking, transport.Writes[0]);

        await worker.SetSideLightAsync(Thinking);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task SetSideLightAsync_ProtocolViolation_LatchesWithoutSchedulingRetry()
    {
        var transport = new MockLightingTransport(Baseline);
        transport.FailNext(MockLightingOperation.Connect, LightingTransportFailureKind.ProtocolViolation);
        var reconnectDelay = new ControlledReconnectDelay();
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore(),
            reconnectDelay: reconnectDelay);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);

        Assert.Equal(LightingWorkerState.Faulted, worker.Snapshot.State);
        Assert.Equal(LightingTransportFailureKind.ProtocolViolation, worker.Snapshot.LastFailure);
        Assert.Equal(0, reconnectDelay.CallCount);
        Assert.Single(transport.ConnectionRequests);
        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData(LightingTransportFailureKind.DeviceDisconnected)]
    [InlineData(LightingTransportFailureKind.DeviceBusy)]
    [InlineData(LightingTransportFailureKind.ReceiverUnavailable)]
    [InlineData(LightingTransportFailureKind.KeyboardSleeping)]
    [InlineData(LightingTransportFailureKind.Timeout)]
    public void GetDelay_TransientFailureAtAnyAttempt_NeverExceedsFiveSeconds(
        LightingTransportFailureKind failureKind)
    {
        var policy = new LayeredReconnectPolicy();

        Assert.True(policy.IsTransient(failureKind));
        foreach (int attempt in new[] { 1, 2, 3, 10, 100 })
        {
            TimeSpan delay = policy.GetDelay(failureKind, attempt);
            Assert.InRange(delay, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ProbeAsync_ActiveDeviceSilentlyDisconnects_ReconnectsAndReplaysTarget()
    {
        var transport = new MockLightingTransport(Baseline);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-31T00:00:00Z"));
        var reconnectDelay = new ControlledReconnectDelay();
        await using var worker = new HidLightingWorker(
            transport,
            new InMemoryBaselineOwnershipStore(),
            reconnectDelay: reconnectDelay,
            timeProvider: clock);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        transport.FailNext(
            MockLightingOperation.Read,
            LightingTransportFailureKind.DeviceDisconnected);
        clock.Advance(HidLightingWorker.HealthProbeInterval);

        await worker.ProbeAsync();

        Assert.Equal(LightingWorkerState.Reconnecting, worker.Snapshot.State);
        reconnectDelay.Release();
        await WaitForStateAsync(worker, LightingWorkerState.Active);
        Assert.Equal(Thinking, transport.Writes[^1]);
        Assert.True(transport.Operations.Count(operation => operation == "connect") >= 2);
    }

    [Fact]
    public async Task ProbeAsync_ObservedStateDrifts_ReplaysDesiredWithoutRecapturingBaseline()
    {
        var transport = new MockLightingTransport(Baseline);
        var store = new InMemoryBaselineOwnershipStore();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-31T00:00:00Z"));
        await using var worker = new HidLightingWorker(
            transport,
            store,
            timeProvider: clock);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        BaselineOwnershipRecord originalOwnership = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        transport.SimulateExternalStateChange(Complete);
        clock.Advance(HidLightingWorker.HealthProbeInterval);

        await worker.ProbeAsync();

        Assert.Equal(LightingWorkerState.Active, worker.Snapshot.State);
        Assert.Equal(new[] { Thinking, Thinking }, transport.Writes);
        Assert.Equal(new[] { "read", "write", "read" }, transport.Operations.TakeLast(3));
        BaselineOwnershipRecord afterCorrection = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(afterCorrection.IsOwned);
        Assert.Equal(originalOwnership.OwnershipMarker, afterCorrection.OwnershipMarker);
        Assert.Equal(Baseline, afterCorrection.SideLightState);

        // A second probe at the same timestamp is not due and cannot loop writes.
        await worker.ProbeAsync();
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task ProbeAsync_CorrectiveWriteDoesNotApply_FaultsOnceAndKeepsOriginalOwnership()
    {
        var transport = new MockLightingTransport(Baseline);
        var store = new InMemoryBaselineOwnershipStore();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-31T00:00:00Z"));
        await using var worker = new HidLightingWorker(
            transport,
            store,
            timeProvider: clock);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        BaselineOwnershipRecord originalOwnership = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        transport.SimulateExternalStateChange(Complete);
        transport.IgnoreNextWrite();
        clock.Advance(HidLightingWorker.HealthProbeInterval);

        await worker.ProbeAsync();

        Assert.Equal(LightingWorkerState.Faulted, worker.Snapshot.State);
        Assert.Equal(LightingTransportFailureKind.ProtocolViolation, worker.Snapshot.LastFailure);
        Assert.Equal(new[] { Thinking, Thinking }, transport.Writes);
        BaselineOwnershipRecord afterFailure = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(afterFailure.IsOwned);
        Assert.Equal(originalOwnership.OwnershipMarker, afterFailure.OwnershipMarker);
        Assert.Equal(Baseline, afterFailure.SideLightState);

        clock.Advance(HidLightingWorker.HealthProbeInterval);
        await worker.ProbeAsync();
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task ProbeAsync_CorrectiveWriteIsBusy_RestoresOwnedBaselineBeforeReplay()
    {
        var transport = new MockLightingTransport(Baseline);
        var store = new InMemoryBaselineOwnershipStore();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-31T00:00:00Z"));
        var reconnectDelay = new ControlledReconnectDelay();
        await using var worker = new HidLightingWorker(
            transport,
            store,
            reconnectDelay: reconnectDelay,
            timeProvider: clock);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        BaselineOwnershipRecord originalOwnership = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        transport.SimulateExternalStateChange(Complete);
        transport.FailNext(MockLightingOperation.Write, LightingTransportFailureKind.DeviceBusy);
        clock.Advance(HidLightingWorker.HealthProbeInterval);

        await worker.ProbeAsync();

        Assert.Equal(LightingWorkerState.Reconnecting, worker.Snapshot.State);
        BaselineOwnershipRecord whileBusy = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(whileBusy.IsOwned);
        Assert.Equal(originalOwnership.OwnershipMarker, whileBusy.OwnershipMarker);
        Assert.Equal(Baseline, whileBusy.SideLightState);

        reconnectDelay.Release();
        await WaitForStateAsync(worker, LightingWorkerState.Active);

        Assert.Equal(new[] { Thinking, Baseline, Thinking }, transport.Writes);
        BaselineOwnershipRecord afterReplay = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(afterReplay.IsOwned);
        Assert.Equal(Baseline, afterReplay.SideLightState);
        Assert.NotEqual(originalOwnership.OwnershipMarker, afterReplay.OwnershipMarker);
    }

    [Fact]
    public async Task RestoreAsync_ReadbackMismatch_KeepsOwnershipMarkerOwned()
    {
        var transport = new MismatchingRestoreTransport(Baseline);
        var store = new InMemoryBaselineOwnershipStore();
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();

        await worker.SetSideLightAsync(Thinking);
        BaselineOwnershipRecord captured = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(captured.IsOwned);

        await worker.RestoreAsync();

        BaselineOwnershipRecord afterMismatch = Assert.IsType<BaselineOwnershipRecord>(
            await store.LoadAsync());
        Assert.True(afterMismatch.IsOwned);
        Assert.Equal(captured.OwnershipMarker, afterMismatch.OwnershipMarker);
        Assert.Equal(LightingWorkerState.Faulted, worker.Snapshot.State);
        Assert.Equal(LightingTransportFailureKind.BaselineMismatch, worker.Snapshot.LastFailure);
        int restoreWrite = transport.Operations.FindLastIndex(operation => operation == "write-baseline");
        Assert.True(restoreWrite >= 0 && restoreWrite + 1 < transport.Operations.Count);
        Assert.Equal("read-mismatch", transport.Operations[restoreWrite + 1]);
    }

    private static async Task WaitForStateAsync(
        HidLightingWorker worker,
        LightingWorkerState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (worker.Snapshot.State != expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ImmediateReconnectDelay : IReconnectDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MismatchingRestoreTransport : ILightingTransport
    {
        private readonly byte[] baseline;
        private byte[] currentState;
        private bool connected;
        private bool corruptNextRead;

        public MismatchingRestoreTransport(ReadOnlySpan<byte> baseline)
        {
            this.baseline = baseline.ToArray();
            currentState = baseline.ToArray();
        }

        public List<string> Operations { get; } = new();

        public ValueTask<LightingDeviceSession> ConnectAsync(
            LightingConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            connected = true;
            Operations.Add("connect");
            return ValueTask.FromResult(
                new LightingDeviceSession(
                    "mock-device",
                    "mock",
                    "mock-interface",
                    CurrentMode: 0));
        }

        public ValueTask<byte[]> ReadSideLightAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();
            if (corruptNextRead)
            {
                corruptNextRead = false;
                byte[] mismatch = currentState.ToArray();
                mismatch[0] ^= 0x01;
                Operations.Add("read-mismatch");
                return ValueTask.FromResult(mismatch);
            }

            Operations.Add("read");
            return ValueTask.FromResult(currentState.ToArray());
        }

        public ValueTask WriteSideLightAsync(
            ReadOnlyMemory<byte> sideLightState,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();
            currentState = sideLightState.ToArray();
            bool isBaseline = currentState.AsSpan().SequenceEqual(baseline);
            Operations.Add(isBaseline ? "write-baseline" : "write-state");
            corruptNextRead = isBaseline;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add("disconnect");
            connected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            connected = false;
            return ValueTask.CompletedTask;
        }

        private void EnsureConnected()
        {
            if (!connected)
            {
                throw new InvalidOperationException("Test transport is disconnected.");
            }
        }
    }

    private sealed class ControlledReconnectDelay : IReconnectDelay
    {
        private readonly TaskCompletionSource released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return new ValueTask(released.Task.WaitAsync(cancellationToken));
        }

        public void Release()
        {
            released.TrySetResult();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration)
        {
            current += duration;
        }
    }

    private sealed class FailFirstSaveBaselineStore : IBaselineOwnershipStore
    {
        private readonly InMemoryBaselineOwnershipStore inner = new();

        public int SaveAttempts { get; private set; }

        public ValueTask<BaselineOwnershipRecord?> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            return inner.LoadAsync(cancellationToken);
        }

        public ValueTask SaveAsync(
            BaselineOwnershipRecord record,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
            {
                throw new IOException("Injected baseline persistence failure.");
            }

            return inner.SaveAsync(record, cancellationToken);
        }

        public ValueTask MarkReleasedAsync(
            string ownershipMarker,
            CancellationToken cancellationToken = default)
        {
            return inner.MarkReleasedAsync(ownershipMarker, cancellationToken);
        }
    }

    private sealed class FailFirstLoadOwnedBaselineStore : IBaselineOwnershipStore
    {
        private BaselineOwnershipRecord record;

        public FailFirstLoadOwnedBaselineStore(
            byte[] baseline,
            LightingDeviceSession session)
        {
            record = new BaselineOwnershipRecord(
                BaselineRecord.CurrentSchemaVersion,
                session.DeviceIdentity,
                session.TransportProfile,
                session.InterfaceFingerprint,
                baseline.ToArray(),
                session.CurrentMode,
                AgentKick75.Core.Baseline.BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
                true,
                DateTimeOffset.UtcNow);
        }

        public int LoadAttempts { get; private set; }

        public int ReleaseCount { get; private set; }

        public ValueTask<BaselineOwnershipRecord?> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadAttempts++;
            if (LoadAttempts == 1)
            {
                throw new IOException("Injected recovery journal read failure.");
            }

            return ValueTask.FromResult<BaselineOwnershipRecord?>(
                record with { SideLightState = record.SideLightState.ToArray() });
        }

        public ValueTask SaveAsync(
            BaselineOwnershipRecord newRecord,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record = newRecord with { SideLightState = newRecord.SideLightState.ToArray() };
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkReleasedAsync(
            string ownershipMarker,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(record.OwnershipMarker, ownershipMarker);
            record = record with { IsOwned = false };
            ReleaseCount++;
            return ValueTask.CompletedTask;
        }
    }
}
