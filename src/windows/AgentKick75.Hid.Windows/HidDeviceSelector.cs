// SPDX-License-Identifier: MIT
namespace AgentKick75.Hid.Windows;

public sealed class HidDeviceSelector
{
    public HidDeviceSelection Select(IEnumerable<HidInterfaceDescriptor> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        HidInterfaceDescriptor[] materialized = devices.ToArray();
        List<HidCandidateDiagnostic> diagnostics = materialized
            .Where(device => device.VendorId == HidTransportProfile.Kick75VendorId)
            .Select(CreateDiagnostic)
            .OrderBy(candidate => candidate.Device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<HidCandidateDiagnostic> usbCandidates = diagnostics
            .Where(candidate => candidate.Profile == HidTransportProfiles.Kick75Usb)
            .ToList();
        List<HidCandidateDiagnostic> matching = usbCandidates
            .Where(candidate => candidate.DescriptorMatches)
            .OrderBy(candidate => candidate.Device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matching.Count > 1)
        {
            return new HidDeviceSelection(
                null,
                null,
                HidDeviceState.Unsupported,
                $"Found {matching.Count} equally preferred writable-looking HID interfaces; " +
                "selection is ambiguous and writes are blocked.",
                diagnostics);
        }

        HidCandidateDiagnostic? selected = matching.SingleOrDefault();

        if (selected is not null)
        {
            return new HidDeviceSelection(
                selected.Device,
                selected.Profile,
                HidDeviceState.Present,
                $"Selected {selected.Profile.Id}; protocol handshake is still required.",
                diagnostics);
        }

        HidCandidateDiagnostic? busy = usbCandidates.FirstOrDefault(candidate =>
            candidate.Device.EnumerationErrorCode is
                Win32HidNative.ErrorAccessDenied or Win32HidNative.ErrorSharingViolation);
        if (busy is not null)
        {
            return new HidDeviceSelection(
                busy.Device,
                busy.Profile,
                HidDeviceState.Busy,
                "A supported HID identity is present, but its descriptor is busy or access was denied.",
                diagnostics);
        }

        if (usbCandidates.Count > 0)
        {
            HidCandidateDiagnostic rejected = usbCandidates[0];
            return new HidDeviceSelection(
                rejected.Device,
                rejected.Profile,
                HidDeviceState.Unsupported,
                "A known identity was present, but no interface passed the strict HID descriptor filter.",
                diagnostics);
        }

        if (diagnostics.Count > 0)
        {
            return new HidDeviceSelection(
                null,
                null,
                HidDeviceState.Unsupported,
                "Only excluded or unsupported NuPhy HID identities are connected.",
                diagnostics);
        }

        return new HidDeviceSelection(
            null,
            null,
            HidDeviceState.Disconnected,
            "No supported Kick75 HID interface is connected.",
            diagnostics);
    }

    private static HidCandidateDiagnostic CreateDiagnostic(HidInterfaceDescriptor device)
    {
        HidTransportProfile profile = HidTransportProfiles.Match(device);
        bool inspectionSucceeded = device.EnumerationError is null &&
            device.EnumerationErrorCode is null;
        bool matches = inspectionSucceeded && profile.Support switch
        {
            HidProfileSupport.Writable => profile.MatchesWritableDescriptor(device),
            HidProfileSupport.DiagnosticOnly => profile.MatchesDiagnosticDescriptor(device),
            _ => false,
        };

        string reason = !inspectionSucceeded
            ? $"Descriptor inspection failed: {device.EnumerationError ?? $"Win32 error {device.EnumerationErrorCode}"}"
            : profile.Support switch
            {
                HidProfileSupport.DiagnosticOnly when !profile.MatchesDiagnosticDescriptor(device) =>
                    "Identity matched, but usage or report lengths did not.",
                HidProfileSupport.DiagnosticOnly => "Known identity; diagnostic-only and never writable.",
                HidProfileSupport.Excluded => "Bootloader/upgrader identity is permanently excluded.",
                HidProfileSupport.Unsupported => "VID/PID is not on the supported allowlist.",
                _ when !profile.MatchesDiagnosticDescriptor(device) =>
                    "Identity matched, but usage or report lengths did not.",
                _ when !matches =>
                    "Descriptor reports 64-byte native buffers. Win32 WriteFile requires " +
                    "Report ID 0 plus the complete 64-byte protocol frame, so writes are blocked.",
                _ => "Writable profile and descriptor matched.",
            };

        return new HidCandidateDiagnostic(device, profile, matches, reason);
    }
}
