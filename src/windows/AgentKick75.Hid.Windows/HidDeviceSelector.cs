// SPDX-License-Identifier: MIT
namespace AgentKick75.Hid.Windows;

public sealed class HidDeviceSelector
{
    public HidDeviceSelection Select(
        IEnumerable<HidInterfaceDescriptor> devices,
        HidTransportPreference preference = HidTransportPreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(devices);

        HidInterfaceDescriptor[] materialized = devices.ToArray();
        List<HidCandidateDiagnostic> diagnostics = materialized
            .Where(device => device.VendorId == HidTransportProfile.Kick75VendorId)
            .Select(CreateDiagnostic)
            .OrderBy(candidate => candidate.Profile.AutoPriority)
            .ThenBy(candidate => candidate.Device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<HidCandidateDiagnostic> requestedWritable = diagnostics
            .Where(candidate => candidate.Profile.Support == HidProfileSupport.Writable)
            .Where(candidate => preference == HidTransportPreference.Auto ||
                candidate.Profile.Transport == preference)
            .ToList();

        // Auto locks onto the highest-priority identity that is present. If USB
        // is present but busy or malformed, do not silently fall through to the
        // dongle and risk controlling the same keyboard through two paths.
        int selectedPriority = requestedWritable.Count == 0
            ? int.MaxValue
            : requestedWritable.Min(candidate => candidate.Profile.AutoPriority);
        List<HidCandidateDiagnostic> selectedProfileCandidates = requestedWritable
            .Where(candidate => candidate.Profile.AutoPriority == selectedPriority)
            .ToList();

        List<HidCandidateDiagnostic> matching = selectedProfileCandidates
            .Where(candidate => candidate.DescriptorMatches)
            .OrderBy(candidate => candidate.Profile.AutoPriority)
            .ThenBy(candidate => candidate.Device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matching.Count > 1)
        {
            return new HidDeviceSelection(
                preference,
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
            HidDeviceState state = selected.Profile.Transport == HidTransportPreference.Dongle
                ? HidDeviceState.ReceiverPresent
                : HidDeviceState.Present;
            return new HidDeviceSelection(
                preference,
                selected.Device,
                selected.Profile,
                state,
                $"Selected {selected.Profile.Id}; protocol handshake is still required.",
                diagnostics);
        }

        HidCandidateDiagnostic? busy = selectedProfileCandidates.FirstOrDefault(candidate =>
            candidate.Device.EnumerationErrorCode is
                Win32HidNative.ErrorAccessDenied or Win32HidNative.ErrorSharingViolation);
        if (busy is not null)
        {
            return new HidDeviceSelection(
                preference,
                busy.Device,
                busy.Profile,
                HidDeviceState.Busy,
                "A supported HID identity is present, but its descriptor is busy or access was denied.",
                diagnostics);
        }

        HidCandidateDiagnostic? diagnosticOnly = diagnostics.FirstOrDefault(candidate =>
            candidate.Profile.Support == HidProfileSupport.DiagnosticOnly &&
            candidate.DescriptorMatches &&
            (preference == HidTransportPreference.Auto ||
                candidate.Profile.Transport == preference));
        if (diagnosticOnly is not null)
        {
            return new HidDeviceSelection(
                preference,
                diagnosticOnly.Device,
                diagnosticOnly.Profile,
                HidDeviceState.DiagnosticOnly,
                $"{diagnosticOnly.Profile.Id} is diagnostic-only; lighting writes are blocked.",
                diagnostics);
        }

        bool hasRequestedIdentity = selectedProfileCandidates.Count > 0;
        if (hasRequestedIdentity)
        {
            HidCandidateDiagnostic rejected = selectedProfileCandidates[0];
            return new HidDeviceSelection(
                preference,
                rejected.Device,
                rejected.Profile,
                HidDeviceState.Unsupported,
                "A known identity was present, but no interface passed the strict HID descriptor filter.",
                diagnostics);
        }

        if (preference != HidTransportPreference.Auto &&
            diagnostics.All(candidate =>
                candidate.Profile.Transport != preference &&
                candidate.Profile.Transport != HidTransportPreference.Auto))
        {
            return new HidDeviceSelection(
                preference,
                null,
                null,
                HidDeviceState.Disconnected,
                $"No {preference.ToString().ToLowerInvariant()} Kick75 HID interface is connected.",
                diagnostics);
        }

        if (diagnostics.Count > 0)
        {
            return new HidDeviceSelection(
                preference,
                null,
                null,
                HidDeviceState.Unsupported,
                "Only excluded or unsupported NuPhy HID identities are connected.",
                diagnostics);
        }

        return new HidDeviceSelection(
            preference,
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
