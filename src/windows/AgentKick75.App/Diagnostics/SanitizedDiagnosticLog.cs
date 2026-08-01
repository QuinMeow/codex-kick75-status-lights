// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AgentKick75.App.Lighting;
using AgentKick75.Core.State;
using AgentKick75.Core.Storage;

namespace AgentKick75.App.Diagnostics;

/// <summary>
/// A bounded, asynchronous JSONL diagnostic sink. The public write path performs
/// only validation, irreversible session hashing, and a non-blocking channel
/// enqueue; filesystem work is owned by one background consumer.
/// </summary>
public sealed class SanitizedDiagnosticLog : ISanitizedDiagnosticLog
{
    private const string FilePrefix = "agentkick75-diagnostics-";
    private const string FileSuffix = ".jsonl";
    private const int SessionHashBytes = 16;
    private const int MaximumSessionIdentifierLength = 256;
    private const long MaximumLatencyMilliseconds = 24 * 60 * 60 * 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly HashSet<string> PersistedPropertyNames = new(StringComparer.Ordinal)
    {
        "timestampUtc",
        "eventType",
        "sessionHash",
        "visualState",
        "latencyMilliseconds",
        "transportFailure",
        "code",
    };

    private readonly string logDirectory;
    private readonly SecureDiagnosticFileSystem fileSystem;
    private readonly SanitizedDiagnosticLogOptions options;
    private readonly TimeProvider timeProvider;
    private readonly byte[] sessionHashKey;
    private readonly Channel<SanitizedDiagnosticEntry> entries;
    private readonly SemaphoreSlim ioGate = new(1, 1);
    private readonly TaskCompletionSource readersDrained = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposalCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task writerTask;
    private FileStream? activeStream;
    private string? activePath;
    private DateOnly? activeDate;
    private long activeLength;
    private long pendingEntryCount;
    private long droppedEntryCount;
    private int activeReaderCount;
    private int disposed;

    public SanitizedDiagnosticLog(
        string logDirectory,
        SanitizedDiagnosticLogOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        this.options = options ?? new SanitizedDiagnosticLogOptions();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        fileSystem = SecureDiagnosticFileSystem.Acquire(logDirectory);
        this.logDirectory = fileSystem.DirectoryPath;
        try
        {
            sessionHashKey = RandomNumberGenerator.GetBytes(32);
            entries = Channel.CreateBounded<SanitizedDiagnosticEntry>(new BoundedChannelOptions(
                this.options.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

            CleanupRetention(this.timeProvider.GetUtcNow(), this.options.MaximumFiles);
            writerTask = ProcessEntriesAsync();
        }
        catch
        {
            fileSystem.Dispose();
            throw;
        }
    }

    public long DroppedEntryCount => Interlocked.Read(ref droppedEntryCount);

    public ValueTask WriteAsync(
        SanitizedDiagnosticEventType eventType,
        string? sessionId = null,
        TaskVisualState? visualState = null,
        long? latencyMilliseconds = null,
        LightingTransportFailureKind? transportFailure = null,
        SanitizedDiagnosticCode? code = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFields(eventType, sessionId, visualState, latencyMilliseconds, transportFailure, code);

        var entry = new SanitizedDiagnosticEntry(
            timeProvider.GetUtcNow().ToUniversalTime(),
            eventType,
            HashSessionIdentifier(sessionId),
            visualState,
            latencyMilliseconds,
            transportFailure,
            code);

        Interlocked.Increment(ref pendingEntryCount);
        if (!entries.Writer.TryWrite(entry))
        {
            Interlocked.Decrement(ref pendingEntryCount);
            Interlocked.Increment(ref droppedEntryCount);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<SanitizedDiagnosticEntry>> ReadRecentAsync(
        int maxEntries,
        CancellationToken cancellationToken = default)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        EnterReader();
        try
        {
            int effectiveMaximum = Math.Min(maxEntries, options.MaximumReadEntries);
            await WaitForPendingEntriesAsync(cancellationToken).ConfigureAwait(false);
            await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Close the append handle before opening verified read handles. The
                // writer deliberately holds DELETE access without sharing it so the
                // terminal file cannot be renamed or replaced while active.
                await CloseActiveStreamAsync().ConfigureAwait(false);
                CleanupRetention(timeProvider.GetUtcNow(), options.MaximumFiles);

                var result = new List<SanitizedDiagnosticEntry>(effectiveMaximum);
                foreach (LogFileInfo file in EnumerateLogFiles()
                             .OrderByDescending(item => item.Date)
                             .ThenByDescending(item => item.Sequence))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string[] lines;
                    try
                    {
                        lines = await ReadAllLinesAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    for (int index = lines.Length - 1;
                         index >= 0 && result.Count < effectiveMaximum;
                         index--)
                    {
                        if (TryDeserializePersistedEntry(lines[index], out SanitizedDiagnosticEntry? entry))
                        {
                            result.Add(entry!);
                        }
                    }

                    if (result.Count == effectiveMaximum)
                    {
                        break;
                    }
                }

                return result.AsReadOnly();
            }
            finally
            {
                ioGate.Release();
            }
        }
        finally
        {
            ExitReader();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            await disposalCompleted.Task.ConfigureAwait(false);
            return;
        }

        entries.Writer.TryComplete();
        if (Volatile.Read(ref activeReaderCount) == 0)
        {
            readersDrained.TrySetResult();
        }

        try
        {
            await writerTask.ConfigureAwait(false);
            await readersDrained.Task.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                CryptographicOperations.ZeroMemory(sessionHashKey);
                fileSystem.Dispose();
                ioGate.Dispose();
            }
            finally
            {
                disposalCompleted.TrySetResult();
            }
        }
    }

    private static void ValidateFields(
        SanitizedDiagnosticEventType eventType,
        string? sessionId,
        TaskVisualState? visualState,
        long? latencyMilliseconds,
        LightingTransportFailureKind? transportFailure,
        SanitizedDiagnosticCode? code)
    {
        if (!Enum.IsDefined(eventType))
        {
            throw new ArgumentOutOfRangeException(nameof(eventType));
        }

        if (sessionId is not null &&
            (string.IsNullOrWhiteSpace(sessionId) ||
             sessionId.Length > MaximumSessionIdentifierLength ||
             sessionId.Any(char.IsControl)))
        {
            throw new ArgumentException("Session identifier is outside the diagnostic allowlist.", nameof(sessionId));
        }

        if (visualState is not null && !Enum.IsDefined(visualState.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(visualState));
        }

        if (latencyMilliseconds is < 0 or > MaximumLatencyMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
        }

        if (transportFailure is not null && !Enum.IsDefined(transportFailure.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(transportFailure));
        }

        if (code is not null && !Enum.IsDefined(code.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }
    }

    private string? HashSessionIdentifier(string? sessionId)
    {
        if (sessionId is null)
        {
            return null;
        }

        byte[] identifierBytes = Encoding.UTF8.GetBytes(sessionId);
        byte[] digest = HMACSHA256.HashData(sessionHashKey, identifierBytes);
        try
        {
            return Convert.ToHexString(digest.AsSpan(0, SessionHashBytes))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identifierBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private async Task ProcessEntriesAsync()
    {
        try
        {
            await foreach (SanitizedDiagnosticEntry entry in entries.Reader.ReadAllAsync()
                               .ConfigureAwait(false))
            {
                try
                {
                    await WriteEntryAsync(entry).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref droppedEntryCount);
                    try
                    {
                        await CloseActiveStreamUnderGateAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref pendingEntryCount);
                }
            }
        }
        finally
        {
            try
            {
                await CloseActiveStreamUnderGateAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task WriteEntryAsync(SanitizedDiagnosticEntry entry)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        Array.Resize(ref line, line.Length + 1);
        line[^1] = (byte)'\n';

        await ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureActiveStreamAsync(entry.TimestampUtc, line.Length).ConfigureAwait(false);
            fileSystem.ValidateActiveFile(activeStream!, activePath!);
            await activeStream!.WriteAsync(line).ConfigureAwait(false);
            await activeStream.FlushAsync().ConfigureAwait(false);
            fileSystem.ValidateActiveFile(activeStream, activePath!);
            activeLength += line.Length;
        }
        finally
        {
            ioGate.Release();
        }
    }

    private async Task EnsureActiveStreamAsync(DateTimeOffset timestampUtc, int incomingBytes)
    {
        DateOnly entryDate = DateOnly.FromDateTime(timestampUtc.UtcDateTime);
        if (activeStream is not null &&
            activeDate == entryDate &&
            (activeLength == 0 || activeLength + incomingBytes <= options.MaximumFileBytes))
        {
            return;
        }

        await CloseActiveStreamAsync().ConfigureAwait(false);
        if (!TryReserveLogSlot(timestampUtc))
        {
            throw new IOException(
                "The diagnostic log file cap is occupied by entries that cannot be safely removed.");
        }

        LogFileInfo? newest = EnumerateLogFiles()
            .Where(file => file.Date == entryDate)
            .OrderByDescending(file => file.Sequence)
            .FirstOrDefault();
        int sequence = newest?.Sequence ?? 0;
        sequence++;
        CreatedLogFile created = CreateLogFile(entryDate, ref sequence);
        bool accepted = false;
        try
        {
            if (CountMatchingLogEntriesUpTo(options.MaximumFiles + 1) > options.MaximumFiles)
            {
                fileSystem.MarkOpenFileForDeletion(created.Stream, created.Path);
                throw new IOException(
                    "The diagnostic log file cap changed while reserving a new file.");
            }

            activePath = created.Path;
            activeStream = created.Stream;
            activeLength = 0;
            activeDate = entryDate;
            accepted = true;
        }
        finally
        {
            if (!accepted)
            {
                await created.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private CreatedLogFile CreateLogFile(DateOnly date, ref int sequence)
    {
        while (true)
        {
            ValidateLogDirectory();
            string path = Path.Combine(
                logDirectory,
                $"{FilePrefix}{date:yyyyMMdd}-{sequence:D4}{FileSuffix}");
            if (fileSystem.TryCreateNewWriteStream(path, out FileStream? stream))
            {
                return new CreatedLogFile(path, stream!);
            }

            sequence++;
        }
    }

    private async Task CloseActiveStreamAsync()
    {
        FileStream? stream = activeStream;
        activeStream = null;
        activePath = null;
        activeDate = null;
        activeLength = 0;
        if (stream is null)
        {
            return;
        }

        try
        {
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WaitForPendingEntriesAsync(CancellationToken cancellationToken)
    {
        while (Interlocked.Read(ref pendingEntryCount) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnterReader()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interlocked.Increment(ref activeReaderCount);
        if (Volatile.Read(ref disposed) == 0)
        {
            return;
        }

        ExitReader();
        throw new ObjectDisposedException(nameof(SanitizedDiagnosticLog));
    }

    private void ExitReader()
    {
        int remaining = Interlocked.Decrement(ref activeReaderCount);
        if (remaining < 0)
        {
            throw new InvalidOperationException("Diagnostic reader accounting underflowed.");
        }

        if (remaining == 0 && Volatile.Read(ref disposed) != 0)
        {
            readersDrained.TrySetResult();
        }
    }

    private async Task CloseActiveStreamUnderGateAsync()
    {
        await ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseActiveStreamAsync().ConfigureAwait(false);
        }
        finally
        {
            ioGate.Release();
        }
    }

    private bool TryReserveLogSlot(DateTimeOffset nowUtc)
    {
        int maximumExistingEntries = options.MaximumFiles - 1;
        CleanupRetention(nowUtc, maximumExistingEntries);
        return CountMatchingLogEntriesUpTo(options.MaximumFiles) < options.MaximumFiles;
    }

    private void CleanupRetention(DateTimeOffset nowUtc, int maximumEntries)
    {
        DateOnly today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        DateOnly cutoff = today.AddDays(-(options.RetentionDays - 1));
        foreach (LogFileInfo file in EnumerateLogFiles().Where(file => file.Date < cutoff))
        {
            _ = TryDelete(file.Path);
        }

        LogFileInfo[] deletionCandidates = EnumerateLogFiles()
            .OrderBy(file => file.Date)
            .ThenBy(file => file.Sequence)
            .ToArray();
        foreach (LogFileInfo file in deletionCandidates)
        {
            if (CountMatchingLogEntriesUpTo(maximumEntries + 1) <= maximumEntries)
            {
                break;
            }

            _ = TryDelete(file.Path);
        }
    }

    private int CountMatchingLogEntriesUpTo(int limit)
    {
        ValidateLogDirectory();
        try
        {
            int count = 0;
            foreach (string _ in Directory.EnumerateFileSystemEntries(
                         logDirectory,
                         $"{FilePrefix}*{FileSuffix}",
                         SearchOption.TopDirectoryOnly))
            {
                count++;
                if (count >= limit)
                {
                    break;
                }
            }

            ValidateLogDirectory();
            return count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Enumeration uncertainty is fail-closed: report the requested
            // threshold as occupied so the caller cannot create another file.
            return limit;
        }
    }

    private void ValidateLogDirectory()
    {
        fileSystem.ValidateDirectoryLeases();
    }

    private IEnumerable<LogFileInfo> EnumerateLogFiles()
    {
        ValidateLogDirectory();
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(
                logDirectory,
                $"{FilePrefix}*{FileSuffix}",
                SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        ValidateLogDirectory();

        foreach (string path in paths)
        {
            LogFileInfo? parsed = TryParseLogFile(path);
            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    private LogFileInfo? TryParseLogFile(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(FileSuffix, StringComparison.Ordinal))
            {
                return null;
            }

            string payload = name[FilePrefix.Length..^FileSuffix.Length];
            if (payload.Length < 10 || payload[8] != '-' ||
                !DateOnly.TryParseExact(
                    payload.AsSpan(0, 8),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly date) ||
                !int.TryParse(
                    payload.AsSpan(9),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int sequence) ||
                sequence < 0)
            {
                return null;
            }

            string fullPath = Path.GetFullPath(path);
            return fileSystem.TryGetTrustedFileLength(fullPath, out long length)
                ? new LogFileInfo(fullPath, date, sequence, length)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            return fileSystem.TryDeleteTrustedFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsValidPersistedEntry(SanitizedDiagnosticEntry entry)
    {
        return entry.TimestampUtc.Offset == TimeSpan.Zero &&
            Enum.IsDefined(entry.EventType) &&
            (entry.SessionHash is null || IsSessionHash(entry.SessionHash)) &&
            (entry.VisualState is null || Enum.IsDefined(entry.VisualState.Value)) &&
            entry.LatencyMilliseconds is not (< 0 or > MaximumLatencyMilliseconds) &&
            (entry.TransportFailure is null || Enum.IsDefined(entry.TransportFailure.Value)) &&
            (entry.Code is null || Enum.IsDefined(entry.Code.Value));
    }

    private static bool TryDeserializePersistedEntry(
        string json,
        out SanitizedDiagnosticEntry? entry)
    {
        entry = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!PersistedPropertyNames.Contains(property.Name) || !seen.Add(property.Name))
                {
                    return false;
                }
            }

            if (!seen.Contains("timestampUtc") || !seen.Contains("eventType"))
            {
                return false;
            }

            entry = document.RootElement.Deserialize<SanitizedDiagnosticEntry>(JsonOptions);
            return entry is not null && IsValidPersistedEntry(entry);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSessionHash(string value)
    {
        return value.Length == SessionHashBytes * 2 &&
            value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private async Task<string[]> ReadAllLinesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = fileSystem.OpenReadStream(path);
        if (stream.Length > options.MaximumFileBytes)
        {
            throw new IOException("The diagnostic log exceeds the configured per-file limit.");
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private sealed record CreatedLogFile(string Path, FileStream Stream);

    private sealed record LogFileInfo(string Path, DateOnly Date, int Sequence, long Length);
}
