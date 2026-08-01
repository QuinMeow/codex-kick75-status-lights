// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentKick75.App.Diagnostics;
using AgentKick75.App.Hosting;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.Core.Hooks;
using AgentKick75.Core.State;
using AgentKick75.Core.Storage;

namespace AgentKick75.Integration.Tests;

public sealed class SanitizedDiagnosticLogTests
{
    [Fact]
    public async Task WriteAsync_SensitiveSession_PersistsOnlyAllowlistedHashedSchema()
    {
        string directory = CreateTemporaryDirectory();
        const string sessionId = "session-sensitive-source-value";
        try
        {
            await using var log = new SanitizedDiagnosticLog(directory);

            await log.WriteAsync(
                SanitizedDiagnosticEventType.StateChanged,
                sessionId,
                TaskVisualState.Thinking,
                latencyMilliseconds: 12,
                transportFailure: LightingTransportFailureKind.Timeout,
                code: SanitizedDiagnosticCode.Timeout);
            await log.WriteAsync(
                SanitizedDiagnosticEventType.HookReceived,
                sessionId,
                code: SanitizedDiagnosticCode.Succeeded);

            IReadOnlyList<SanitizedDiagnosticEntry> entries = await log.ReadRecentAsync(10);

            Assert.Equal(2, entries.Count);
            Assert.NotNull(entries[0].SessionHash);
            Assert.Equal(entries[0].SessionHash, entries[1].SessionHash);
            string sessionHash = entries[0].SessionHash!;
            Assert.Equal(32, sessionHash.Length);
            Assert.All(sessionHash, character => Assert.True(
                character is >= '0' and <= '9' or >= 'a' and <= 'f'));

            string file = Assert.Single(Directory.GetFiles(
                directory,
                "agentkick75-diagnostics-*.jsonl"));
            string raw = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(sessionId, raw, StringComparison.Ordinal);
            Assert.DoesNotContain("prompt", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("toolPayload", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("devicePath", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("serial", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);

            using JsonDocument document = JsonDocument.Parse(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
            string[] properties = document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToArray();
            Assert.Equal(
                [
                    "timestampUtc",
                    "eventType",
                    "sessionHash",
                    "visualState",
                    "latencyMilliseconds",
                    "transportFailure",
                    "code",
                ],
                properties);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadRecentAsync_UnknownPersistedProperty_RejectsEntireEntry()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            _ = UserDataDirectorySecurity.EnsureSecureDirectory(directory);
            string path = Path.Combine(
                directory,
                "agentkick75-diagnostics-20260801-0001.jsonl");
            await File.WriteAllTextAsync(
                path,
                """
                {"timestampUtc":"2026-08-01T00:00:00Z","eventType":"hookReceived","sessionHash":"00000000000000000000000000000000","code":"succeeded","prompt":"must not be accepted"}
                {"timestampUtc":"2026-08-01T00:00:00Z","sessionHash":"00000000000000000000000000000000","code":"succeeded"}
                {"timestampUtc":"2026-08-01T00:00:00Z","eventType":"hookReceived","eventType":"hookRejected","code":"succeeded"}
                """);

            await using var log = new SanitizedDiagnosticLog(directory);
            IReadOnlyList<SanitizedDiagnosticEntry> entries = await log.ReadRecentAsync(10);

            Assert.Empty(entries);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task WriteAsync_LiveLogDirectoryRenameOrDelete_IsBlockedAndTargetRemainsUntouched()
    {
        string root = CreateTemporaryDirectory();
        string logDirectory = Path.Combine(root, "logs");
        string movedDirectory = Path.Combine(root, "moved-logs");
        string target = Path.Combine(root, "replacement-target");
        Directory.CreateDirectory(target);
        try
        {
            await using var log = new SanitizedDiagnosticLog(logDirectory);

            Assert.ThrowsAny<IOException>(() => Directory.Move(logDirectory, movedDirectory));
            Assert.ThrowsAny<IOException>(() => Directory.Delete(logDirectory));

            await log.WriteAsync(
                SanitizedDiagnosticEventType.HostStarted,
                code: SanitizedDiagnosticCode.Succeeded);
            SanitizedDiagnosticEntry entry = Assert.Single(await log.ReadRecentAsync(10));

            Assert.Equal(SanitizedDiagnosticEventType.HostStarted, entry.EventType);
            Assert.True(Directory.Exists(logDirectory));
            Assert.False(Directory.Exists(movedDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task WriteReadAndRetention_PreexistingHardLinks_NeverTouchExternalTarget()
    {
        string root = CreateTemporaryDirectory();
        string logDirectory = Path.Combine(root, "logs");
        string target = Path.Combine(root, "external-target.jsonl");
        string currentLink = Path.Combine(
            logDirectory,
            "agentkick75-diagnostics-20260801-0001.jsonl");
        string expiredLink = Path.Combine(
            logDirectory,
            "agentkick75-diagnostics-20260701-0001.jsonl");
        const string targetContent =
            "{\"timestampUtc\":\"2026-08-01T00:00:00Z\",\"eventType\":\"hostStopped\",\"code\":\"succeeded\"}\n";
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(target, targetContent);
        CreateHardLink(currentLink, target);
        CreateHardLink(expiredLink, target);
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            var options = new SanitizedDiagnosticLogOptions(retentionDays: 1);
            await using var log = new SanitizedDiagnosticLog(logDirectory, options, clock);

            await log.WriteAsync(
                SanitizedDiagnosticEventType.HostStarted,
                code: SanitizedDiagnosticCode.Succeeded);
            IReadOnlyList<SanitizedDiagnosticEntry> entries = await log.ReadRecentAsync(10);

            SanitizedDiagnosticEntry entry = Assert.Single(entries);
            Assert.Equal(SanitizedDiagnosticEventType.HostStarted, entry.EventType);
            Assert.True(File.Exists(currentLink));
            Assert.True(File.Exists(expiredLink));
            Assert.Equal(targetContent, await File.ReadAllTextAsync(target));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task WriteAsync_ActiveLogBecomesHardLinked_DropsLaterAppendWithoutChangingAlias()
    {
        string root = CreateTemporaryDirectory();
        string logDirectory = Path.Combine(root, "logs");
        string externalAlias = Path.Combine(root, "external-alias.jsonl");
        try
        {
            await using var log = new SanitizedDiagnosticLog(logDirectory);
            await log.WriteAsync(
                SanitizedDiagnosticEventType.HostStarted,
                code: SanitizedDiagnosticCode.Succeeded);

            string activePath = await WaitForSingleNonEmptyLogAsync(logDirectory);
            CreateHardLink(externalAlias, activePath);
            string before = await ReadAllTextSharedAsync(externalAlias);

            await log.WriteAsync(
                SanitizedDiagnosticEventType.HostStopped,
                code: SanitizedDiagnosticCode.Succeeded);
            _ = await log.ReadRecentAsync(10);

            Assert.Equal(before, await File.ReadAllTextAsync(externalAlias));
            Assert.DoesNotContain("hostStopped", before, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task WriteAsync_InvalidStructuredField_IsRejectedBeforeQueueing()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using var log = new SanitizedDiagnosticLog(directory);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _ = log.WriteAsync((SanitizedDiagnosticEventType)int.MaxValue));
            Assert.Throws<ArgumentException>(() =>
                _ = log.WriteAsync(
                    SanitizedDiagnosticEventType.HookReceived,
                    "session\rforged"));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _ = log.WriteAsync(
                    SanitizedDiagnosticEventType.HookReceived,
                    latencyMilliseconds: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _ = log.WriteAsync(
                    SanitizedDiagnosticEventType.StateChanged,
                    visualState: (TaskVisualState)int.MaxValue));

            Assert.Empty(await log.ReadRecentAsync(10));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task WriteAsync_FullBoundedQueue_AlwaysCompletesSynchronously()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var options = new SanitizedDiagnosticLogOptions(queueCapacity: 1);
            await using var log = new SanitizedDiagnosticLog(directory, options);

            for (int index = 0; index < 1000; index++)
            {
                ValueTask write = log.WriteAsync(
                    SanitizedDiagnosticEventType.HookReceived,
                    $"session-{index}",
                    code: SanitizedDiagnosticCode.Succeeded);
                Assert.True(write.IsCompletedSuccessfully);
            }

            Assert.True(log.DroppedEntryCount > 0);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Rotation_SizeDateAndRetention_KeepOnlyConfiguredRecentFiles()
    {
        string directory = CreateTemporaryDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            var options = new SanitizedDiagnosticLogOptions(
                maximumFileBytes: 512,
                retentionDays: 2,
                maximumFiles: 2,
                queueCapacity: 128);
            await using var log = new SanitizedDiagnosticLog(directory, options, clock);
            for (int index = 0; index < 20; index++)
            {
                await log.WriteAsync(
                    SanitizedDiagnosticEventType.StateChanged,
                    $"session-{index}",
                    TaskVisualState.RequiresInput,
                    code: SanitizedDiagnosticCode.Succeeded);
            }

            _ = await log.ReadRecentAsync(100);
            string[] rotated = Directory.GetFiles(directory, "agentkick75-diagnostics-*.jsonl");
            Assert.Equal(2, rotated.Length);
            Assert.All(rotated, path => Assert.InRange(new FileInfo(path).Length, 1, 512));

            clock.Advance(TimeSpan.FromDays(3));
            await log.WriteAsync(
                SanitizedDiagnosticEventType.HostStarted,
                code: SanitizedDiagnosticCode.Succeeded);
            _ = await log.ReadRecentAsync(100);

            string current = Assert.Single(Directory.GetFiles(
                directory,
                "agentkick75-diagnostics-*.jsonl"));
            Assert.Contains("20260804", Path.GetFileName(current), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Rotation_ActiveWriterCountsTowardMaximumFileLimit()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var options = new SanitizedDiagnosticLogOptions(
                maximumFileBytes: 512,
                retentionDays: 7,
                maximumFiles: 2,
                queueCapacity: 128);
            await using var log = new SanitizedDiagnosticLog(directory, options);
            for (int index = 0; index < 20; index++)
            {
                await log.WriteAsync(
                    SanitizedDiagnosticEventType.StateChanged,
                    $"session-{index}",
                    TaskVisualState.RequiresInput,
                    code: SanitizedDiagnosticCode.Succeeded);
            }

            // Dispose drains the writer and closes its active handle without
            // performing an extra read-side retention pass.
            await log.DisposeAsync();

            string[] files = Directory.GetFiles(
                directory,
                "agentkick75-diagnostics-*.jsonl");
            Assert.Equal(2, files.Length);
            Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 512));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task WriteAsync_UndeletableMatchingEntryOccupiesHardCap_DoesNotCreateAnotherFile()
    {
        string directory = CreateTemporaryDirectory();
        string occupiedPath = Path.Combine(
            directory,
            "agentkick75-diagnostics-20260801-0001.jsonl");
        const string original = "occupied-cap-slot";
        await File.WriteAllTextAsync(occupiedPath, original);
        try
        {
            await using var blocker = new FileStream(
                occupiedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.None);
            var options = new SanitizedDiagnosticLogOptions(maximumFiles: 1);
            await using var log = new SanitizedDiagnosticLog(directory, options);

            ValueTask write = log.WriteAsync(
                SanitizedDiagnosticEventType.HostStarted,
                code: SanitizedDiagnosticCode.Succeeded);
            Assert.True(write.IsCompletedSuccessfully);
            Assert.Empty(await log.ReadRecentAsync(10));
            Assert.Equal(1, log.DroppedEntryCount);

            string matching = Assert.Single(Directory.GetFileSystemEntries(
                directory,
                "agentkick75-diagnostics-*.jsonl"));
            Assert.Equal(occupiedPath, matching, ignoreCase: true);
            blocker.Position = 0;
            using var reader = new StreamReader(blocker, leaveOpen: true);
            Assert.Equal(original, await reader.ReadToEndAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadRecentAsync_OversizeMatchingFile_RejectsBeforeReadingContents()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(
            directory,
            "agentkick75-diagnostics-20260801-0001.jsonl");
        const string validPrefix =
            "{\"timestampUtc\":\"2026-08-01T00:00:00Z\",\"eventType\":\"hostStarted\",\"code\":\"succeeded\"}\n";
        await File.WriteAllTextAsync(path, validPrefix);
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 1,
                         FileOptions.Asynchronous))
        {
            stream.SetLength(16 * 1024 * 1024);
        }

        try
        {
            var options = new SanitizedDiagnosticLogOptions(maximumFileBytes: 512);
            await using var log = new SanitizedDiagnosticLog(directory, options);

            Assert.Empty(await log.ReadRecentAsync(10));
            Assert.Equal(16 * 1024 * 1024, new FileInfo(path).Length);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DisposeAsync_InFlightReaderWaitingForIoGate_DrainsBeforeReleasingResources()
    {
        string directory = CreateTemporaryDirectory();
        var log = new SanitizedDiagnosticLog(directory);
        SemaphoreSlim? ioGate = null;
        bool gateHeld = false;
        try
        {
            await log.WriteAsync(
                SanitizedDiagnosticEventType.HostStarted,
                code: SanitizedDiagnosticCode.Succeeded);
            _ = await WaitForSingleNonEmptyLogAsync(directory);

            ioGate = GetPrivateField<SemaphoreSlim>(log, "ioGate");
            await ioGate.WaitAsync();
            gateHeld = true;

            Task<IReadOnlyList<SanitizedDiagnosticEntry>> readTask =
                log.ReadRecentAsync(10).AsTask();
            await WaitForPrivateIntAsync(log, "activeReaderCount", expected: 1);
            Task disposeTask = log.DisposeAsync().AsTask();
            Task concurrentDisposeTask = log.DisposeAsync().AsTask();

            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(disposeTask.IsCompleted);
            Assert.False(concurrentDisposeTask.IsCompleted);

            ioGate.Release();
            gateHeld = false;
            IReadOnlyList<SanitizedDiagnosticEntry> entries = await readTask;
            await Task.WhenAll(disposeTask, concurrentDisposeTask);

            Assert.Equal(
                SanitizedDiagnosticEventType.HostStarted,
                Assert.Single(entries).EventType);
        }
        finally
        {
            if (gateHeld)
            {
                ioGate!.Release();
            }

            await log.DisposeAsync();
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task HostRuntime_RealProductionWiring_LogsLifecycleHooksStateAndDeviceWithoutRawIdentity()
    {
        string directory = CreateTemporaryDirectory();
        const string sessionId = "runtime-session-sensitive";
        try
        {
            var log = new SanitizedDiagnosticLog(directory);
            var transport = new MockLightingTransport(
                [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB]);
            var worker = new HidLightingWorker(
                transport,
                new InMemoryBaselineOwnershipStore());
            var coordinator = new HostCoordinator(
                new TaskStateReducer(),
                worker,
                diagnosticLog: log);
            var runtime = new HostRuntime(
                worker,
                coordinator,
                $"AgentKick75-Diagnostics-{Guid.NewGuid():N}",
                log);

            runtime.Start();
            await coordinator.ApplyHookAsync(new CodexHookEvent(
                CodexHookEventKind.UserPromptSubmit,
                sessionId,
                "turn"));
            PipeEnvelope? rejected = await coordinator.HandlePipeMessageAsync(
                PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
                {
                    kind = (int)CodexHookEventKind.UserPromptSubmit,
                    sessionId,
                    turnId = "turn",
                    prompt = "must not enter diagnostics",
                }));
            Assert.NotNull(rejected);
            await runtime.DisposeAsync();

            await using var reader = new SanitizedDiagnosticLog(directory);
            IReadOnlyList<SanitizedDiagnosticEntry> entries = await reader.ReadRecentAsync(50);
            SanitizedDiagnosticEventType[] types = entries.Select(entry => entry.EventType).ToArray();
            Assert.Contains(SanitizedDiagnosticEventType.HostStarted, types);
            Assert.Contains(SanitizedDiagnosticEventType.HostStopped, types);
            Assert.Contains(SanitizedDiagnosticEventType.HookReceived, types);
            Assert.Contains(SanitizedDiagnosticEventType.HookRejected, types);
            Assert.Contains(SanitizedDiagnosticEventType.StateChanged, types);
            Assert.Contains(SanitizedDiagnosticEventType.DeviceConnected, types);

            string raw = string.Concat(
                Directory.GetFiles(directory, "agentkick75-diagnostics-*.jsonl")
                    .Select(File.ReadAllText));
            Assert.DoesNotContain(sessionId, raw, StringComparison.Ordinal);
            Assert.DoesNotContain("prompt", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mock-device", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "AgentKick75-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<string> WaitForSingleNonEmptyLogAsync(string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            string[] files = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "agentkick75-diagnostics-*.jsonl")
                : [];
            if (files.Length == 1 && new FileInfo(files[0]).Length > 0)
            {
                return files[0];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task<string> ReadAllTextSharedAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"Missing private test field '{fieldName}'.");
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static async Task WaitForPrivateIntAsync(
        object instance,
        string fieldName,
        int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (GetPrivateField<int>(instance, fieldName) != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
        {
            throw new IOException(
                $"Unable to create test hard link. Win32 error: {Marshal.GetLastWin32Error()}.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }
}
