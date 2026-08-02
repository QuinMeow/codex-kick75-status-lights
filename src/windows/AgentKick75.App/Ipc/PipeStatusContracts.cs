// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using AgentKick75.App.Hosting;
using AgentKick75.App.Lighting;
using AgentKick75.Core.State;
using AgentKick75.Hid.Windows;

namespace AgentKick75.App.Ipc;

/// <summary>
/// Explicit allowlist for status data that may cross the current-user Pipe.
/// Internal Host/lighting snapshots deliberately retain the complete device
/// identity needed for safe baseline recovery and must never be serialized
/// directly at this boundary.
/// </summary>
public sealed record PipeStatusResponseDto(
    string Host,
    ApplicationLifecycleState LifecycleState,
    LifecycleFaultCode? FaultCode,
    bool IsPreviewActive,
    HookEnablementState HookEnablement,
    TaskVisualState AggregateState,
    int ActiveSessionCount,
    DateTimeOffset? LastEventAtUtc,
    PipeLightingStatusDto Lighting)
{
    public static PipeStatusResponseDto FromInternal(HostStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new PipeStatusResponseDto(
            PipeStatusPrivacy.SafeHost(status.Host) ?? "unavailable",
            status.LifecycleState,
            status.FaultCode,
            status.IsPreviewActive,
            status.HookEnablement,
            status.AggregateState,
            status.ActiveSessionCount,
            status.LastEventAtUtc,
            PipeLightingStatusDto.FromInternal(status.Lighting));
    }

    /// <summary>
    /// Rebuilds an allowlisted response from an untrusted Pipe payload. This is
    /// defense in depth for the CLI when it communicates with an older or
    /// malformed Host that returned an internal snapshot directly.
    /// </summary>
    public static bool TryReadSafe(
        JsonElement payload,
        out PipeStatusResponseDto? safeStatus)
    {
        try
        {
            PipeStatusResponseDto? candidate = payload.Deserialize<PipeStatusResponseDto>(
                PipeJson.Options);
            if (candidate?.Lighting is null)
            {
                safeStatus = null;
                return false;
            }

            string? safeHost = PipeStatusPrivacy.SafeHost(candidate.Host);
            if (safeHost is null)
            {
                safeStatus = null;
                return false;
            }

            safeStatus = candidate with
            {
                Host = safeHost,
                Lighting = candidate.Lighting.Sanitize(),
            };
            return true;
        }
        catch (JsonException)
        {
            safeStatus = null;
            return false;
        }
        catch (NotSupportedException)
        {
            safeStatus = null;
            return false;
        }
    }
}

public sealed record PipeLightingStatusDto(
    LightingWorkerState State,
    string? DeviceIdentity,
    string? TransportProfile,
    LightingTransportFailureKind? LastFailure,
    int ReconnectAttempt,
    DateTimeOffset UpdatedAtUtc,
    string? InterfaceFingerprint,
    LightingDeviceObservationKind DeviceObservation,
    LightingDeviceSupport? DeviceSupport)
{
    internal static PipeLightingStatusDto FromInternal(LightingWorkerSnapshot lighting)
    {
        ArgumentNullException.ThrowIfNull(lighting);
        return new PipeLightingStatusDto(
            lighting.State,
            PipeStatusPrivacy.ToVidPidOrNull(lighting.DeviceIdentity),
            PipeStatusPrivacy.SafeTransportProfile(lighting.TransportProfile),
            lighting.LastFailure,
            lighting.ReconnectAttempt,
            lighting.UpdatedAtUtc,
            PipeStatusPrivacy.SafeInterfaceFingerprint(lighting.InterfaceFingerprint),
            lighting.DeviceObservation,
            lighting.DeviceSupport);
    }

    internal PipeLightingStatusDto Sanitize()
    {
        return this with
        {
            DeviceIdentity = PipeStatusPrivacy.ToVidPidOrNull(DeviceIdentity),
            TransportProfile = PipeStatusPrivacy.SafeTransportProfile(TransportProfile),
            InterfaceFingerprint = PipeStatusPrivacy.SafeInterfaceFingerprint(
                InterfaceFingerprint),
        };
    }
}

internal static class PipeStatusPrivacy
{
    private const int VidPidLength = 9;

    public static string? SafeHost(string? host)
    {
        return string.Equals(host, "online", StringComparison.Ordinal)
            ? "online"
            : null;
    }

    public static string? SafeTransportProfile(string? transportProfile)
    {
        if (string.Equals(
                transportProfile,
                HidTransportProfiles.Kick75Usb.Id,
                StringComparison.Ordinal))
        {
            return HidTransportProfiles.Kick75Usb.Id;
        }

        if (string.Equals(
                transportProfile,
                HidTransportProfiles.Kick75U1Dongle.Id,
                StringComparison.Ordinal))
        {
            return HidTransportProfiles.Kick75U1Dongle.Id;
        }

        if (string.Equals(
                transportProfile,
                HidTransportProfiles.Kick75HighDiagnostic.Id,
                StringComparison.Ordinal))
        {
            return HidTransportProfiles.Kick75HighDiagnostic.Id;
        }

        return null;
    }

    public static string? SafeInterfaceFingerprint(string? interfaceFingerprint)
    {
        if (string.IsNullOrEmpty(interfaceFingerprint))
        {
            return null;
        }

        string[] segments = interfaceFingerprint.Split('/');
        if (segments.Length != 4 || segments[0].Length != VidPidLength)
        {
            return null;
        }

        string? vidPid = ToVidPidOrNull(segments[0]);
        if (vidPid is null ||
            !TryReadHexPair(segments[1], out ushort usagePage, out ushort usage) ||
            !TryReadReportLength(segments[2], "in=", out int inputLength) ||
            !TryReadReportLength(segments[3], "out=", out int outputLength))
        {
            return null;
        }

        return $"{vidPid}/{usagePage:X4}:{usage:X4}/in={inputLength}/out={outputLength}";
    }

    public static string? ToVidPidOrNull(string? deviceIdentity)
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

    private static bool TryReadHexPair(
        string value,
        out ushort first,
        out ushort second)
    {
        first = 0;
        second = 0;
        return value.Length == 9 &&
            value[4] == ':' &&
            ushort.TryParse(
                value.AsSpan(0, 4),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out first) &&
            ushort.TryParse(
                value.AsSpan(5, 4),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out second);
    }

    private static bool TryReadReportLength(
        string value,
        string prefix,
        out int reportLength)
    {
        reportLength = 0;
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(
                value.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out reportLength) &&
            HidTransportProfile.IsSupportedReportLength(reportLength);
    }
}
