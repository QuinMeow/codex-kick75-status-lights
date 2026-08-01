using System.Text.Json.Nodes;
using AgentKick75.Core.Baseline;

namespace AgentKick75.Core.Tests;

public sealed class BaselineStoreTests
{
    private const byte CurrentMode = 1;

    private static readonly DateTimeOffset AcquiredAtUtc = new(
        2026,
        7,
        31,
        12,
        34,
        56,
        TimeSpan.Zero);

    private static readonly Guid OwnershipId = Guid.Parse("f73e775c-3dba-4bff-b040-f18b2a504a28");

    [Fact]
    public void Acquire_ValidDeviceAndBytes_CopiesEightBytesAndCreatesOwnershipMarker()
    {
        byte[] bytes = [0, 1, 2, 3, 252, 253, 254, 255];

        BaselineRecord baseline = BaselineRecord.Acquire(
            Device(),
            bytes,
            CurrentMode,
            AcquiredAtUtc,
            OwnershipId);
        bytes[0] = 99;

        Assert.Equal(BaselineRecord.CurrentSchemaVersion, baseline.SchemaVersion);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 252, 253, 254, 255 }, baseline.OriginalSideLightBytes);
        Assert.Equal(CurrentMode, baseline.CurrentMode);
        Assert.Equal("agent-kick75:" + OwnershipId.ToString("N"), baseline.Ownership.Marker);
        Assert.True(baseline.Ownership.IsOwned);
    }

    [Fact]
    public void Acquire_WrongByteCount_ThrowsValidationError()
    {
        BaselineValidationException exception = Assert.Throws<BaselineValidationException>(
            () => BaselineRecord.Acquire(Device(), new byte[7], CurrentMode, AcquiredAtUtc));

        Assert.Equal(BaselineValidationError.InvalidSideLightBytes, exception.Error);
    }

    [Fact]
    public void Acquire_UnsupportedCurrentMode_ThrowsValidationError()
    {
        BaselineValidationException exception = Assert.Throws<BaselineValidationException>(
            () => BaselineRecord.Acquire(Device(), new byte[8], 2, AcquiredAtUtc));

        Assert.Equal(BaselineValidationError.InvalidCurrentMode, exception.Error);
    }

    [Fact]
    public async Task SaveAsync_ValidBaseline_RoundTripsExplicitByteArray()
    {
        using var directory = new BaselineTemporaryDirectory();
        string path = directory.File("baseline.json");
        var store = new BaselineStore(path);
        BaselineRecord expected = OwnedBaseline();

        await store.SaveAsync(expected);
        BaselineLoadResult result = await store.LoadAsync();

        Assert.Equal(BaselineLoadStatus.Loaded, result.Status);
        Assert.NotNull(result.Baseline);
        Assert.Equal(expected.Device, result.Baseline.Device);
        Assert.Equal(expected.OriginalSideLightBytes, result.Baseline.OriginalSideLightBytes);
        Assert.Equal(expected.CurrentMode, result.Baseline.CurrentMode);
        Assert.Equal(expected.Ownership, result.Baseline.Ownership);
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(path)));
        JsonArray bytes = Assert.IsType<JsonArray>(json["originalSideLightBytes"]);
        Assert.Equal(8, bytes.Count);
        Assert.Equal(CurrentMode, json["currentMode"]!.GetValue<int>());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task AbandonOwnedDeviceMismatchAsync_RequiresExactOwnedMismatchAndIsOneShot()
    {
        using var directory = new BaselineTemporaryDirectory();
        var store = new BaselineStore(directory.File("baseline.json"));
        BaselineRecord baseline = OwnedBaseline();
        await store.SaveAsync(baseline);

        BaselineMismatchDispositionResult noMismatch = await store
            .AbandonOwnedDeviceMismatchAsync(
                baseline.Ownership.Marker,
                baseline.Device.DeviceIdentity,
                baseline.Device.DeviceIdentity,
                AcquiredAtUtc.AddMinutes(1));
        BaselineMismatchDispositionResult stale = await store
            .AbandonOwnedDeviceMismatchAsync(
                BaselineOwnership.MarkerPrefix + Guid.NewGuid().ToString("N"),
                baseline.Device.DeviceIdentity,
                "vid=19f5;pid=1026;serial=other",
                AcquiredAtUtc.AddMinutes(1));
        BaselineMismatchDispositionResult released = await store
            .AbandonOwnedDeviceMismatchAsync(
                baseline.Ownership.Marker,
                baseline.Device.DeviceIdentity,
                "vid=19f5;pid=1026;serial=other",
                AcquiredAtUtc.AddMinutes(1));
        BaselineMismatchDispositionResult repeated = await store
            .AbandonOwnedDeviceMismatchAsync(
                baseline.Ownership.Marker,
                baseline.Device.DeviceIdentity,
                "vid=19f5;pid=1026;serial=other",
                AcquiredAtUtc.AddMinutes(2));

        Assert.Equal(BaselineMismatchDispositionStatus.NoDeviceIdentityMismatch, noMismatch.Status);
        Assert.Equal(BaselineMismatchDispositionStatus.StaleOwnership, stale.Status);
        Assert.Equal(BaselineMismatchDispositionStatus.Released, released.Status);
        Assert.Equal(BaselineMismatchDispositionStatus.NotOwned, repeated.Status);
        BaselineRecord persisted = Assert.IsType<BaselineRecord>((await store.LoadAsync()).Baseline);
        Assert.False(persisted.Ownership.IsOwned);
        Assert.Equal(AcquiredAtUtc.AddMinutes(1), persisted.Ownership.ReleasedAtUtc);
    }

    [Fact]
    public async Task LoadAsync_CorruptBaseline_ReturnsSafetyStatus()
    {
        using var directory = new BaselineTemporaryDirectory();
        string path = directory.File("baseline.json");
        await File.WriteAllTextAsync(path, "not json");
        var store = new BaselineStore(path);

        BaselineLoadResult result = await store.LoadAsync();
        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            result,
            Device(),
            CurrentMode);

        Assert.Equal(BaselineLoadStatus.Corrupt, result.Status);
        Assert.Equal(BaselineRecoveryAction.RefuseCorruptBaseline, decision.Action);
        Assert.False(decision.MayWriteDevice);
    }

    [Fact]
    public async Task LoadAsync_UnsupportedVersion_RefusesReacquisition()
    {
        using var directory = new BaselineTemporaryDirectory();
        string path = directory.File("baseline.json");
        await File.WriteAllTextAsync(path, """{"schemaVersion":42}""");
        var store = new BaselineStore(path);

        BaselineLoadResult result = await store.LoadAsync();
        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            result,
            Device(),
            CurrentMode);

        Assert.Equal(BaselineLoadStatus.UnsupportedVersion, result.Status);
        Assert.Equal(BaselineRecoveryAction.RefuseUnsupportedVersion, decision.Action);
    }

    [Fact]
    public async Task LoadAsync_OwnedVersionOneWithoutCurrentMode_FailsClosedAsUnsupported()
    {
        using var directory = new BaselineTemporaryDirectory();
        string path = directory.File("baseline.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "device": {
                "identity": "vid=19f5;pid=1026;serial=abc",
                "transportProfile": "kick75-usb",
                "interfaceFingerprint": "fp-1"
              },
              "originalSideLightBytes": [0, 17, 34, 51, 68, 85, 102, 255],
              "ownership": {
                "marker": "agent-kick75:f73e775c3dba4bffb040f18b2a504a28",
                "isOwned": true,
                "acquiredAtUtc": "2026-07-31T12:34:56.0000000+00:00",
                "releasedAtUtc": null
              }
            }
            """);
        var store = new BaselineStore(path);

        BaselineLoadResult result = await store.LoadAsync();
        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            result,
            Device(),
            CurrentMode);

        Assert.Equal(BaselineLoadStatus.UnsupportedVersion, result.Status);
        Assert.Equal(BaselineRecoveryAction.RefuseUnsupportedVersion, decision.Action);
        Assert.False(decision.MayWriteDevice);
    }

    [Fact]
    public async Task LoadAsync_ReleasedVersionOneWithoutCurrentMode_AllowsFreshV2Capture()
    {
        using var directory = new BaselineTemporaryDirectory();
        string path = directory.File("baseline.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "device": {
                "identity": "vid=19f5;pid=1026;serial=abc",
                "transportProfile": "kick75-usb",
                "interfaceFingerprint": "fp-1"
              },
              "originalSideLightBytes": [0, 17, 34, 51, 68, 85, 102, 255],
              "ownership": {
                "marker": "agent-kick75:f73e775c3dba4bffb040f18b2a504a28",
                "isOwned": false,
                "acquiredAtUtc": "2026-07-31T12:34:56.0000000+00:00",
                "releasedAtUtc": "2026-07-31T12:35:56.0000000+00:00"
              }
            }
            """);
        var store = new BaselineStore(path);

        BaselineLoadResult result = await store.LoadAsync();
        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            result,
            Device(),
            CurrentMode);

        Assert.Equal(BaselineLoadStatus.Loaded, result.Status);
        Assert.Equal(BaselineRecord.CurrentSchemaVersion, result.Baseline?.SchemaVersion);
        Assert.False(result.Baseline?.Ownership.IsOwned);
        Assert.Equal(BaselineRecoveryAction.CaptureNewBaseline, decision.Action);
        Assert.True(decision.MayWriteDevice);
    }

    [Fact]
    public void Decide_UnreleasedMatchingOwnership_RestoresBeforeNewCapture()
    {
        BaselineRecord baseline = OwnedBaseline();
        var loaded = new BaselineLoadResult(BaselineLoadStatus.Loaded, baseline);

        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            loaded,
            Device(),
            CurrentMode);

        Assert.Equal(BaselineRecoveryAction.RestoreBeforeAcquire, decision.Action);
        Assert.Same(baseline, decision.Baseline);
        Assert.True(decision.MayWriteDevice);
    }

    [Theory]
    [InlineData("different-device", "kick75-usb", "fp-1", BaselineRecoveryAction.RefuseDeviceIdentityMismatch)]
    [InlineData("vid=19f5;pid=1026;serial=abc", "kick75-u1-dongle", "fp-1", BaselineRecoveryAction.RefuseTransportProfileMismatch)]
    [InlineData("vid=19f5;pid=1026;serial=abc", "kick75-usb", "fp-2", BaselineRecoveryAction.RefuseInterfaceFingerprintMismatch)]
    public void Decide_UnreleasedMismatchedIdentity_RefusesRestore(
        string identity,
        string profile,
        string fingerprint,
        BaselineRecoveryAction expected)
    {
        BaselineRecord baseline = OwnedBaseline();
        var loaded = new BaselineLoadResult(BaselineLoadStatus.Loaded, baseline);
        var current = new BaselineDeviceIdentity(identity, profile, fingerprint);

        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            loaded,
            current,
            CurrentMode);

        Assert.Equal(expected, decision.Action);
        Assert.False(decision.MayWriteDevice);
    }

    [Fact]
    public void Decide_UnreleasedCurrentModeMismatch_RefusesRestore()
    {
        BaselineRecord baseline = OwnedBaseline();
        var loaded = new BaselineLoadResult(BaselineLoadStatus.Loaded, baseline);

        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            loaded,
            Device(),
            currentMode: 0);

        Assert.Equal(BaselineRecoveryAction.RefuseCurrentModeMismatch, decision.Action);
        Assert.False(decision.MayWriteDevice);
    }

    [Fact]
    public void Decide_ReleasedOwnership_AllowsFreshCapture()
    {
        BaselineRecord released = OwnedBaseline().Release(AcquiredAtUtc.AddMinutes(1));
        var loaded = new BaselineLoadResult(BaselineLoadStatus.Loaded, released);

        BaselineRecoveryDecision decision = BaselineRecoveryPlanner.Decide(
            loaded,
            Device(),
            currentMode: 0);

        Assert.Equal(BaselineRecoveryAction.CaptureNewBaseline, decision.Action);
        Assert.False(released.Ownership.IsOwned);
        Assert.NotNull(released.Ownership.ReleasedAtUtc);
    }

    private static BaselineRecord OwnedBaseline()
    {
        return BaselineRecord.Acquire(
            Device(),
            new byte[] { 0, 17, 34, 51, 68, 85, 102, 255 },
            CurrentMode,
            AcquiredAtUtc,
            OwnershipId);
    }

    private static BaselineDeviceIdentity Device()
    {
        return new BaselineDeviceIdentity(
            "vid=19f5;pid=1026;serial=abc",
            "kick75-usb",
            "fp-1");
    }

    private sealed class BaselineTemporaryDirectory : IDisposable
    {
        public BaselineTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AgentKick75.BaselineTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
