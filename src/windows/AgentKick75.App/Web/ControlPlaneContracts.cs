// SPDX-License-Identifier: MIT
using System.Globalization;

namespace AgentKick75.App.Web;

/// <summary>
/// Provides the privacy-safe state and commands exposed by the local control page.
/// Implementations must not place prompts, transcripts, tool payloads, or assistant
/// messages in any returned value.
/// </summary>
public interface IControlPlane
{
    ValueTask<ControlStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask<ControlSettingsDto> GetSettingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Persists settings and returns the normalized stored value. Lighting values
    /// may take effect immediately; installation preferences can be deferred.
    /// </summary>
    ValueTask<ControlSettingsDto> ApplySettingsAsync(
        ControlSettingsDto settings,
        CancellationToken cancellationToken);

    ValueTask PreviewAsync(
        ControlPreviewState state,
        TimeSpan duration,
        CancellationToken cancellationToken);

    ValueTask<ControlStatusDto> SetPausedAsync(
        bool isPaused,
        CancellationToken cancellationToken);

    ValueTask<HookInstallationResultDto> InstallCodexHooksAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new HookInstallationResultDto(
            false,
            false,
            0,
            "unavailable",
            "当前主程序不支持安装 Codex Hook。"));

    IAsyncEnumerable<ControlEventDto> WatchEventsAsync(CancellationToken cancellationToken);
}

public enum ControlPreviewState
{
    Thinking,
    RequiresInput,
    Complete,
    Interrupted,
}

public sealed record ControlStatusDto(
    string AggregateState,
    int ActiveSessionCount,
    DateTimeOffset? LastEventAt,
    string LifecycleState,
    string? FaultCode,
    bool IsPreviewActive,
    string HookStatus,
    DeviceDiagnosticsDto Device);

public sealed record DeviceDiagnosticsDto(
    string Model,
    string Transport,
    string ReceiverStatus,
    string KeyboardStatus,
    string SupportStatus,
    string? FirmwareVersion,
    string? DeviceIdentity,
    string? LastErrorCode,
    string? InterfaceFingerprint = null);

public sealed record ControlLightStyleDto(
    string Color,
    int Brightness,
    string Effect = "static",
    int Speed = 1);

public sealed record ControlSettingsDto(
    ControlLightStyleDto Thinking,
    ControlLightStyleDto RequiresInput,
    ControlLightStyleDto Complete,
    int CompleteHoldSeconds,
    bool LaunchAtSignIn,
    ControlLightStyleDto? Interrupted = null,
    string KeepAwakePolicy = "disabled",
    int KeepAwakeRefreshSeconds = 60,
    string KeepAwakeRegion = "sideLights");

public sealed record PauseRequestDto(bool Paused);

public sealed record HookInstallationResultDto(
    bool Succeeded,
    bool Changed,
    int RegisteredHandlerCount,
    string Status,
    string Message);

/// <summary>
/// A deliberately narrow event contract. DiagnosticCode must be an allowlisted code,
/// never user or model content.
/// </summary>
public sealed record ControlEventDto(
    long Sequence,
    string Kind,
    DateTimeOffset OccurredAt,
    ControlStatusDto? Status = null,
    string? DiagnosticCode = null);

internal static class ControlPlanePrivacy
{
    private const int VidPidLength = 9;

    public static string? SafeDeviceIdentity(string? deviceIdentity)
    {
        if (string.IsNullOrEmpty(deviceIdentity) ||
            deviceIdentity.Length < VidPidLength ||
            deviceIdentity[4] != ':' ||
            (deviceIdentity.Length > VidPidLength && deviceIdentity[VidPidLength] != '/') ||
            !ushort.TryParse(
                deviceIdentity.AsSpan(0, 4),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ushort vendorId) ||
            !ushort.TryParse(
                deviceIdentity.AsSpan(5, 4),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ushort productId))
        {
            return null;
        }

        return $"{vendorId:X4}:{productId:X4}";
    }

    public static ControlStatusDto SanitizeStatus(ControlStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status with
        {
            Device = status.Device with
            {
                DeviceIdentity = SafeDeviceIdentity(status.Device.DeviceIdentity),
            },
        };
    }

    public static ControlEventDto SanitizeEvent(ControlEventDto controlEvent)
    {
        ArgumentNullException.ThrowIfNull(controlEvent);
        return controlEvent.Status is null
            ? controlEvent
            : controlEvent with { Status = SanitizeStatus(controlEvent.Status) };
    }
}
