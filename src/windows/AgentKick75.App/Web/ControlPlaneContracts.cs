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

    ValueTask RestoreOriginalLightingAsync(CancellationToken cancellationToken);

    ValueTask<HardwareTestResultDto> RunHardwareTestAsync(
        HardwareTestRequestDto request,
        CancellationToken cancellationToken);

    ValueTask<BaselineRecoveryDispositionDto> AbandonMismatchedBaselineAsync(
        BaselineRecoveryDispositionRequestDto request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new BaselineRecoveryDispositionDto(
            false,
            "unavailable",
            "Baseline mismatch disposition is unavailable."));

    IAsyncEnumerable<ControlEventDto> WatchEventsAsync(CancellationToken cancellationToken);
}

public enum ControlPreviewState
{
    Thinking,
    RequiresInput,
    Complete,
}

public sealed record ControlStatusDto(
    string AggregateState,
    int ActiveSessionCount,
    DateTimeOffset? LastEventAt,
    bool IsPaused,
    bool IsPreviewActive,
    string HookStatus,
    DeviceDiagnosticsDto Device,
    BaselineRecoveryRiskDto? BaselineRecovery = null);

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

public sealed record ControlLightStyleDto(string Color, int Brightness);

public sealed record ControlSettingsDto(
    ControlLightStyleDto Thinking,
    ControlLightStyleDto RequiresInput,
    ControlLightStyleDto Complete,
    int CompleteHoldSeconds,
    bool LaunchAtSignIn);

public sealed record PauseRequestDto(bool Paused);

public sealed record HardwareTestRequestDto(string Transport);

public sealed record HardwareTestResultDto(
    bool Succeeded,
    string Status,
    string Message,
    string? Transport);

public sealed record BaselineRecoveryRiskDto(
    string Code,
    string ConfirmationId,
    string Message,
    string? BaselineDeviceIdentity,
    string? ObservedDeviceIdentity);

public sealed record BaselineRecoveryDispositionRequestDto(
    string ConfirmationId,
    bool Confirmed);

public sealed record BaselineRecoveryDispositionDto(
    bool Succeeded,
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
            BaselineRecovery = status.BaselineRecovery is null
                ? null
                : status.BaselineRecovery with
                {
                    BaselineDeviceIdentity = SafeDeviceIdentity(
                        status.BaselineRecovery.BaselineDeviceIdentity),
                    ObservedDeviceIdentity = SafeDeviceIdentity(
                        status.BaselineRecovery.ObservedDeviceIdentity),
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
