// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Commands;
using AgentKick75.App.Hosting;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.Core.Baseline;
using AgentKick75.Core.State;

namespace AgentKick75.Integration.Tests;

public sealed class PipeStatusPrivacyTests
{
    private static readonly byte[] Baseline =
        [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];

    private static readonly byte[] Thinking =
        [0x02, 0x64, 0x01, 0x00, 0x00, 0x00, 0x6B, 0xFF];

    [Fact]
    public async Task HandlePipeMessage_StatusResponse_RedactsDeviceIdentity()
    {
        const string observedIdentity = "19F5:1026/path=private-hid-path";
        const string baselineIdentity = "19F5:1026/serial=private-baseline-serial";
        var session = new LightingDeviceSession(
            observedIdentity,
            "kick75-usb",
            "19F5:1026/0001:0000/in=65/out=65",
            CurrentMode: 0,
            DescriptorMetadata: new LightingDeviceDescriptorMetadata(
                @"\\?\hid#vid_19f5&pid_1026#private-product-path",
                "serial=private-manufacturer-serial",
                0x0418));
        var transport = new MockLightingTransport(Baseline, session);
        var store = new InMemoryBaselineOwnershipStore();
        await store.SaveAsync(new BaselineOwnershipRecord(
            BaselineRecord.CurrentSchemaVersion,
            baselineIdentity,
            session.TransportProfile,
            session.InterfaceFingerprint,
            Baseline.ToArray(),
            session.CurrentMode,
            BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
            IsOwned: true,
            DateTimeOffset.UtcNow));
        await using var worker = new HidLightingWorker(transport, store);
        worker.Start();
        await worker.SetSideLightAsync(Thinking);
        var coordinator = new HostCoordinator(new TaskStateReducer(), worker);

        Assert.Equal(observedIdentity, worker.Snapshot.DeviceIdentity);
        Assert.Null(worker.Snapshot.DescriptorMetadata?.Product);
        Assert.Null(worker.Snapshot.DescriptorMetadata?.Manufacturer);

        PipeEnvelope? response = await coordinator.HandlePipeMessageAsync(
            PipeEnvelope.Create(PipeMessageKinds.StatusRequest, new { }));

        Assert.NotNull(response);
        Assert.Equal(PipeMessageKinds.StatusResponse, response.Kind);
        JsonElement lighting = response.Payload.GetProperty("lighting");
        Assert.Equal("19F5:1026", lighting.GetProperty("deviceIdentity").GetString());
        string pipeJson = response.Payload.GetRawText();
        Assert.False(lighting.TryGetProperty("baselineMismatch", out _));
        AssertContainsNoPrivateIdentity(pipeJson);

        // Boundary redaction must not mutate the identities required for the
        // worker's in-process recovery safety decision.
        Assert.Equal(observedIdentity, worker.Snapshot.DeviceIdentity);
    }

    [Fact]
    public async Task ExecuteAsync_LegacyRawStatus_RebuildsSafeConsoleJson()
    {
        HostStatusSnapshot rawStatus = CreateLegacyRawStatus("online");
        var client = new FixedResponsePipeClient(
            PipeEnvelope.Create(PipeMessageKinds.StatusResponse, rawStatus));
        using var output = new StringWriter();

        int exitCode = await StatusCommand.ExecuteAsync(client, output);

        Assert.Equal(0, exitCode);
        string consoleJson = output.ToString();
        using JsonDocument document = JsonDocument.Parse(consoleJson);
        JsonElement lighting = document.RootElement.GetProperty("lighting");
        Assert.Equal(JsonValueKind.Null, lighting.GetProperty("deviceIdentity").ValueKind);
        Assert.Equal(JsonValueKind.Null, lighting.GetProperty("transportProfile").ValueKind);
        Assert.Equal(JsonValueKind.Null, lighting.GetProperty("interfaceFingerprint").ValueKind);
        Assert.False(lighting.TryGetProperty("baselineMismatch", out _));
        Assert.DoesNotContain(
            "00112233445566778899aabbccddeeff",
            consoleJson,
            StringComparison.Ordinal);
        AssertContainsNoPrivateIdentity(consoleJson);
    }

    [Fact]
    public async Task ExecuteAsync_StatusWithNonAllowlistedHost_ReturnsFixedUnavailableJson()
    {
        HostStatusSnapshot rawStatus = CreateLegacyRawStatus("online/path=private-host-path");
        var client = new FixedResponsePipeClient(
            PipeEnvelope.Create(PipeMessageKinds.StatusResponse, rawStatus));
        using var output = new StringWriter();

        int exitCode = await StatusCommand.ExecuteAsync(client, output);

        Assert.Equal(1, exitCode);
        Assert.Equal("{\"host\":\"unavailable\"}", output.ToString().Trim());
        AssertContainsNoPrivateIdentity(output.ToString());
    }

    private static HostStatusSnapshot CreateLegacyRawStatus(string host)
    {
        return new HostStatusSnapshot(
            host,
            ApplicationLifecycleState.Running,
            FaultCode: null,
            IsPreviewActive: false,
            HookEnablementState.Enabled,
            TaskVisualState.Idle,
            ActiveSessionCount: 0,
            LastEventAtUtc: null,
            new LightingWorkerSnapshot(
                LightingWorkerState.Faulted,
                "serial=private-device-serial",
                "kick75-usb/path=private-transport-profile",
                LightingTransportFailureKind.BaselineMismatch,
                ReconnectAttempt: 0,
                DateTimeOffset.UtcNow,
                "19F5:1026/0001:0000/in=65/out=65/path=private-fingerprint",
                LightingDeviceObservationKind.Descriptor,
                LightingDeviceSupport.Writable));
    }

    private static void AssertContainsNoPrivateIdentity(string json)
    {
        Assert.DoesNotContain("path=", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial=", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-hid-path", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-baseline-serial", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-observed-serial", json, StringComparison.Ordinal);
    }

    private sealed class FixedResponsePipeClient(PipeEnvelope response) : IPipeRequestClient
    {
        public ValueTask<PipeEnvelope?> SendAsync(
            PipeEnvelope request,
            bool expectResponse,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PipeMessageKinds.StatusRequest, request.Kind);
            Assert.True(expectResponse);
            Assert.True(timeout > TimeSpan.Zero);
            return ValueTask.FromResult<PipeEnvelope?>(response);
        }
    }
}
