// SPDX-License-Identifier: MIT
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests.Hid;

public sealed class HidDeviceSelectorTests
{
    private readonly HidDeviceSelector selector = new();

    [Fact]
    public void Select_WithUsbAndUnsupportedIdentity_SelectsOnlyUsb()
    {
        HidInterfaceDescriptor dongle = Device(0x2620, path: "z-dongle");
        HidInterfaceDescriptor usb = Device(0x1026, path: "usb");

        HidDeviceSelection result = selector.Select([dongle, usb]);

        Assert.True(result.IsWritable);
        Assert.Equal(HidTransportProfiles.Kick75Usb, result.Profile);
        Assert.Same(usb, result.Device);
        Assert.Equal(HidDeviceState.Present, result.State);
    }

    [Fact]
    public void Select_TwoMatchingInterfacesForSameProfile_IsAmbiguousAndNeverWritable()
    {
        HidInterfaceDescriptor firstUsb = Device(0x1026, path: "a-usb");
        HidInterfaceDescriptor secondUsb = Device(0x1026, path: "z-usb");

        HidDeviceSelection result = selector.Select([secondUsb, firstUsb]);

        Assert.False(result.IsWritable);
        Assert.Equal(HidDeviceState.Unsupported, result.State);
        Assert.Null(result.Device);
        Assert.Null(result.Profile);
        Assert.Equal(2, result.Candidates.Count(candidate => candidate.DescriptorMatches));
        Assert.Contains("ambiguous", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_HighIdentity_IsUnsupportedAndNeverWritable()
    {
        HidDeviceSelection result = selector.Select([Device(0x1027, path: "high")]);

        Assert.False(result.IsWritable);
        Assert.Null(result.Profile);
        Assert.Equal(HidDeviceState.Unsupported, result.State);
    }

    [Theory]
    [InlineData(0x2620, 0x0006, 65, 65)]
    [InlineData(0x2620, 0x0000, 63, 65)]
    [InlineData(0x1027, 0x0006, 65, 65)]
    [InlineData(0x1027, 0x0000, 65, 66)]
    public void Select_DiagnosticIdentityWithMalformedDescriptor_IsUnsupportedNotDiagnosticOnly(
        int productId,
        int usage,
        int inputLength,
        int outputLength)
    {
        HidInterfaceDescriptor malformed = Device(
            checked((ushort)productId),
            path: "malformed",
            usage: checked((ushort)usage),
            inputLength: checked((ushort)inputLength),
            outputLength: checked((ushort)outputLength));

        HidDeviceSelection result = selector.Select([malformed]);

        Assert.False(result.IsWritable);
        Assert.Equal(HidDeviceState.Unsupported, result.State);
        Assert.Null(result.Device);
        Assert.Null(result.Profile);
        HidCandidateDiagnostic diagnostic = Assert.Single(result.Candidates);
        Assert.False(diagnostic.DescriptorMatches);
        Assert.Contains("usage or report lengths", diagnostic.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0x2620)]
    [InlineData(0x1027)]
    public void Select_DiagnosticIdentityWithInspectionError_IsUnsupportedNotDiagnosticOnly(
        int productId)
    {
        HidInterfaceDescriptor malformed = Device(
            checked((ushort)productId),
            path: "inspection-error") with
        {
            EnumerationError = "Descriptor inspection failed.",
        };

        HidDeviceSelection result = selector.Select([malformed]);

        Assert.Equal(HidDeviceState.Unsupported, result.State);
        Assert.NotEqual(HidDeviceState.DiagnosticOnly, result.State);
        Assert.False(Assert.Single(result.Candidates).DescriptorMatches);
    }

    [Fact]
    public void Select_BootloaderIdentity_IsExcludedAndUnsupported()
    {
        HidDeviceSelection result = selector.Select([Device(0x1020, path: "boot")]);

        Assert.False(result.IsWritable);
        Assert.Null(result.Device);
        Assert.Equal(HidDeviceState.Unsupported, result.State);
        Assert.Contains(
            result.Candidates,
            candidate => candidate.Profile.Support == HidProfileSupport.Excluded);
    }

    [Fact]
    public void Select_WrongUsage_ReportsUnsupportedDescriptor()
    {
        HidInterfaceDescriptor keyboardInterface = Device(
            0x1026,
            path: "keyboard",
            usagePage: 0x0001,
            usage: 0x0006);

        HidDeviceSelection result = selector.Select([keyboardInterface]);

        Assert.False(result.IsWritable);
        Assert.Equal(HidDeviceState.Unsupported, result.State);
        Assert.Contains("usage", result.Candidates.Single().Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_Win32Caps64_BlocksWriteBecauseReportIdWouldTruncateProtocolFrame()
    {
        HidInterfaceDescriptor caps64 = Device(
            0x1026,
            path: "caps64",
            inputLength: 64,
            outputLength: 64);

        HidDeviceSelection result = selector.Select([caps64]);

        Assert.False(result.IsWritable);
        Assert.Equal(HidDeviceState.Unsupported, result.State);
        Assert.Contains("Report ID 0", result.Candidates.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_KnownIdentityWithSharingViolation_ReportsBusyWithoutGuessingDescriptor()
    {
        HidInterfaceDescriptor busy = new(
            "busy",
            0x19F5,
            0x1026,
            0,
            0,
            0,
            0,
            EnumerationError: "Sharing violation.",
            EnumerationErrorCode: 32);

        HidDeviceSelection result = selector.Select([busy]);

        Assert.False(result.IsWritable);
        Assert.Equal(HidDeviceState.Busy, result.State);
        Assert.Same(busy, result.Device);
        Assert.Equal(HidTransportProfiles.Kick75Usb, result.Profile);
    }

    [Fact]
    public async Task OpenAsync_ManuallyCraftedCaps64Selection_StillRefusesWin32Write()
    {
        HidInterfaceDescriptor caps64 = Device(
            0x1026,
            path: "caps64",
            inputLength: 64,
            outputLength: 64);
        HidDeviceSelection unsafeSelection = new(
            caps64,
            HidTransportProfiles.Kick75Usb,
            HidDeviceState.Present,
            "Manually crafted selection.",
            Array.Empty<HidCandidateDiagnostic>());
        Win32HidConnectionFactory factory = new();

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () =>
            {
                _ = await factory.OpenAsync(unsafeSelection);
            });

        Assert.Contains("Report ID 0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_FabricatedWritableProfile_IsRejectedBeforeOpeningPath()
    {
        HidInterfaceDescriptor arbitraryDevice = new(
            "must-not-open",
            0x1234,
            0x5678,
            0x0001,
            0x0000,
            65,
            65);
        HidTransportProfile fabricated = new(
            "fabricated",
            0x1234,
            0x5678,
            HidProfileSupport.Writable);
        HidDeviceSelection unsafeSelection = new(
            arbitraryDevice,
            fabricated,
            HidDeviceState.Present,
            "Manually crafted selection.",
            Array.Empty<HidCandidateDiagnostic>());
        Win32HidConnectionFactory factory = new();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                _ = await factory.OpenAsync(unsafeSelection);
            });

        Assert.Contains("allowlist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HidInterfaceDescriptor Device(
        ushort productId,
        string path,
        ushort usagePage = 0x0001,
        ushort usage = 0x0000,
        ushort inputLength = 65,
        ushort outputLength = 65) =>
        new(
            path,
            0x19F5,
            productId,
            usagePage,
            usage,
            inputLength,
            outputLength);
}
