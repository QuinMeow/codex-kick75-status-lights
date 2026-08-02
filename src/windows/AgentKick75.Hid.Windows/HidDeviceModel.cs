// SPDX-License-Identifier: MIT
namespace AgentKick75.Hid.Windows;

public enum HidDeviceState
{
    Disconnected,
    Present,
    Ready,
    Busy,
    Unresponsive,
    DiagnosticOnly,
    Unsupported,
    Faulted,
}

public enum HidProfileSupport
{
    Writable,
    DiagnosticOnly,
    Excluded,
    Unsupported,
}

public sealed record HidInterfaceDescriptor(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportByteLength,
    ushort OutputReportByteLength,
    string? Manufacturer = null,
    string? Product = null,
    string? SerialNumber = null,
    string? EnumerationError = null,
    int? EnumerationErrorCode = null,
    ushort? HidDescriptorVersionNumber = null)
{
    public string? SanitizedManufacturer => HidDescriptorText.Normalize(Manufacturer);

    public string? SanitizedProduct => HidDescriptorText.Normalize(Product);

    public string DeviceIdentity => string.IsNullOrWhiteSpace(SerialNumber)
        ? $"{VendorId:X4}:{ProductId:X4}/path={DevicePath}"
        : $"{VendorId:X4}:{ProductId:X4}/serial={SerialNumber}";

    public string InterfaceFingerprint =>
        $"{VendorId:X4}:{ProductId:X4}/{UsagePage:X4}:{Usage:X4}/" +
        $"in={InputReportByteLength}/out={OutputReportByteLength}";
}

internal static class HidDescriptorText
{
    private const int MaximumLength = 128;

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length > MaximumLength ||
            normalized.Any(char.IsControl) ||
            normalized.Contains("path=", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("serial=", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalized.Contains("#vid_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
    }
}

public sealed record HidCandidateDiagnostic(
    HidInterfaceDescriptor Device,
    HidTransportProfile Profile,
    bool DescriptorMatches,
    string Reason);

public sealed record HidDeviceSelection(
    HidInterfaceDescriptor? Device,
    HidTransportProfile? Profile,
    HidDeviceState State,
    string Message,
    IReadOnlyList<HidCandidateDiagnostic> Candidates)
{
    public bool IsWritable =>
        Device is not null &&
        Profile is { Support: HidProfileSupport.Writable } &&
        State == HidDeviceState.Present;
}
