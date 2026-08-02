// SPDX-License-Identifier: MIT
namespace AgentKick75.Hid.Windows;

public sealed record HidTransportProfile(
    string Id,
    ushort VendorId,
    ushort ProductId,
    HidProfileSupport Support)
{
    public const ushort Kick75VendorId = 0x19F5;
    public const ushort RawHidUsagePage = 0x0001;
    public const ushort RawHidUsage = 0x0000;
    public const int ProtocolReportLength = 64;

    public bool MatchesIdentity(HidInterfaceDescriptor device) =>
        device.VendorId == VendorId && device.ProductId == ProductId;

    public bool MatchesWritableDescriptor(HidInterfaceDescriptor device) =>
        MatchesIdentity(device) &&
        device.UsagePage == RawHidUsagePage &&
        device.Usage == RawHidUsage &&
        device.InputReportByteLength == ProtocolReportLength + 1 &&
        device.OutputReportByteLength == ProtocolReportLength + 1;

    public bool MatchesDiagnosticDescriptor(HidInterfaceDescriptor device) =>
        MatchesIdentity(device) &&
        device.UsagePage == RawHidUsagePage &&
        device.Usage == RawHidUsage &&
        IsSupportedReportLength(device.InputReportByteLength) &&
        IsSupportedReportLength(device.OutputReportByteLength);

    public static bool IsSupportedReportLength(int length) =>
        length is ProtocolReportLength or ProtocolReportLength + 1;
}

public static class HidTransportProfiles
{
    public static HidTransportProfile Kick75Usb { get; } = new(
        "kick75-usb",
        HidTransportProfile.Kick75VendorId,
        0x1026,
        HidProfileSupport.Writable);

    public static HidTransportProfile Kick75U1Dongle { get; } = new(
        "kick75-u1-dongle",
        HidTransportProfile.Kick75VendorId,
        0x2620,
        HidProfileSupport.DiagnosticOnly);

    public static HidTransportProfile Kick75HighDiagnostic { get; } = new(
        "kick75-high-diagnostic",
        HidTransportProfile.Kick75VendorId,
        0x1027,
        HidProfileSupport.DiagnosticOnly);

    public static HidTransportProfile U1BootloaderExcluded { get; } = new(
        "u1-bootloader-excluded",
        HidTransportProfile.Kick75VendorId,
        0x1020,
        HidProfileSupport.Excluded);

    public static IReadOnlyList<HidTransportProfile> Known { get; } =
        Array.AsReadOnly(
            new[]
            {
                Kick75Usb,
                Kick75U1Dongle,
                Kick75HighDiagnostic,
                U1BootloaderExcluded,
            });

    public static bool IsWritableAllowlisted(HidTransportProfile profile) =>
        profile == Kick75Usb;

    public static HidTransportProfile Match(HidInterfaceDescriptor device) =>
        Known.FirstOrDefault(profile => profile.MatchesIdentity(device)) ??
        new HidTransportProfile(
            "unsupported",
            device.VendorId,
            device.ProductId,
            HidProfileSupport.Unsupported);
}
