// SPDX-License-Identifier: MIT
using AgentKick75.App.Lighting;
using AgentKick75.Core.State;

namespace AgentKick75.App.Diagnostics;

/// <summary>
/// Fixed event vocabulary for local diagnostics. Deliberately excludes any
/// caller-supplied message, payload, path, prompt, or exception text.
/// </summary>
public enum SanitizedDiagnosticEventType
{
    HostStarted,
    HostStopped,
    HookReceived,
    HookRejected,
    StateChanged,
    DeviceDiscovered,
    DeviceConnected,
    DeviceDisconnected,
    LightingWriteStarted,
    LightingWriteCompleted,
    LightingRestoreStarted,
    LightingRestoreCompleted,
    ReconnectScheduled,
    HardwareTestStarted,
    HardwareTestCompleted,
    ConfigurationLoaded,
    ConfigurationSaved,
    ControlRequestRejected,
}

/// <summary>
/// Fixed, non-sensitive outcomes that can accompany a diagnostic event.
/// </summary>
public enum SanitizedDiagnosticCode
{
    Succeeded,
    InvalidInput,
    PayloadTooLarge,
    HostUnavailable,
    AccessDenied,
    UnsupportedDevice,
    AmbiguousDevice,
    Timeout,
    ProtocolRejected,
    BaselineUnavailable,
    BaselineMismatch,
    RestoreFailed,
    RateLimited,
    Cancelled,
    UnexpectedFailure,
}

/// <summary>
/// Persisted and page-safe representation. It contains only the approved schema.
/// SessionHash is an in-process salted HMAC prefix, never the source session ID.
/// </summary>
public sealed record SanitizedDiagnosticEntry(
    DateTimeOffset TimestampUtc,
    SanitizedDiagnosticEventType EventType,
    string? SessionHash = null,
    TaskVisualState? VisualState = null,
    long? LatencyMilliseconds = null,
    LightingTransportFailureKind? TransportFailure = null,
    SanitizedDiagnosticCode? Code = null);

public interface ISanitizedDiagnosticLog : IAsyncDisposable
{
    ValueTask WriteAsync(
        SanitizedDiagnosticEventType eventType,
        string? sessionId = null,
        TaskVisualState? visualState = null,
        long? latencyMilliseconds = null,
        LightingTransportFailureKind? transportFailure = null,
        SanitizedDiagnosticCode? code = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads newest entries first. Requests are capped by the configured read limit.
    /// </summary>
    ValueTask<IReadOnlyList<SanitizedDiagnosticEntry>> ReadRecentAsync(
        int maxEntries,
        CancellationToken cancellationToken = default);
}

public sealed record SanitizedDiagnosticLogOptions
{
    public const int DefaultMaximumFileBytes = 1024 * 1024;
    public const int DefaultRetentionDays = 7;
    public const int DefaultMaximumFiles = 14;
    public const int DefaultMaximumReadEntries = 500;
    public const int DefaultQueueCapacity = 1024;

    public SanitizedDiagnosticLogOptions(
        int maximumFileBytes = DefaultMaximumFileBytes,
        int retentionDays = DefaultRetentionDays,
        int maximumFiles = DefaultMaximumFiles,
        int maximumReadEntries = DefaultMaximumReadEntries,
        int queueCapacity = DefaultQueueCapacity)
    {
        if (maximumFileBytes is < 512 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFileBytes),
                "Maximum file size must be between 512 bytes and 64 MiB.");
        }

        if (retentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                "Retention must be between 1 and 3650 UTC days.");
        }

        if (maximumFiles is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFiles),
                "Maximum file count must be between 1 and 1000.");
        }

        if (maximumReadEntries is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumReadEntries),
                "Maximum read count must be between 1 and 10000 entries.");
        }

        if (queueCapacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueCapacity),
                "Queue capacity must be between 1 and 100000 entries.");
        }

        MaximumFileBytes = maximumFileBytes;
        RetentionDays = retentionDays;
        MaximumFiles = maximumFiles;
        MaximumReadEntries = maximumReadEntries;
        QueueCapacity = queueCapacity;
    }

    public int MaximumFileBytes { get; }

    public int RetentionDays { get; }

    public int MaximumFiles { get; }

    public int MaximumReadEntries { get; }

    public int QueueCapacity { get; }
}
