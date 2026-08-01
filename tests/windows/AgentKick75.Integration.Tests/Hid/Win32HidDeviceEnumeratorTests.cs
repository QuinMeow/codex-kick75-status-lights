// SPDX-License-Identifier: MIT
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests.Hid;

public sealed class Win32HidDeviceEnumeratorTests
{
    [Fact]
    public void CreateDescriptor_AttributesCapsAndStrings_MapsDescriptorMetadata()
    {
        const string devicePath = @"\\?\hid#vid_19f5&pid_1026#private-interface-path";
        Win32HidNative.HiddAttributes attributes = new()
        {
            VendorId = 0x19F5,
            ProductId = 0x1026,
            VersionNumber = 0x0418,
        };
        Win32HidNative.HidpCaps capabilities = new()
        {
            UsagePage = 0x0001,
            Usage = 0x0000,
            InputReportByteLength = 65,
            OutputReportByteLength = 65,
            Reserved = new ushort[17],
        };

        HidInterfaceDescriptor descriptor = Win32HidDeviceEnumerator.CreateDescriptor(
            devicePath,
            attributes,
            capabilities,
            " NuPhy ",
            " Kick75 IO ");

        Assert.Equal("NuPhy", descriptor.Manufacturer);
        Assert.Equal("Kick75 IO", descriptor.Product);
        Assert.Equal((ushort)0x0418, descriptor.HidDescriptorVersionNumber);
        Assert.Equal(
            "19F5:1026/0001:0000/in=65/out=65",
            descriptor.InterfaceFingerprint);
        Assert.Equal($"19F5:1026/path={devicePath}", descriptor.DeviceIdentity);
    }

    [Fact]
    public void CreateDescriptor_CapsUnavailable_PreservesSafeAttributeMetadata()
    {
        Win32HidNative.HiddAttributes attributes = new()
        {
            VendorId = 0x19F5,
            ProductId = 0x2620,
            VersionNumber = 0x0102,
        };

        HidInterfaceDescriptor descriptor = Win32HidDeviceEnumerator.CreateDescriptor(
            "private-enumerator-path",
            attributes,
            capabilities: null,
            manufacturer: "NuPhy",
            product: "Kick75 U1 Receiver",
            enumerationError: "HidD_GetPreparsedData failed.");

        Assert.Equal("NuPhy", descriptor.Manufacturer);
        Assert.Equal("Kick75 U1 Receiver", descriptor.Product);
        Assert.Equal((ushort)0x0102, descriptor.HidDescriptorVersionNumber);
        Assert.Equal((ushort)0, descriptor.UsagePage);
        Assert.Equal((ushort)0, descriptor.InputReportByteLength);
        Assert.NotNull(descriptor.EnumerationError);
    }

    [Theory]
    [InlineData("serial=private-device-serial")]
    [InlineData(@"\\?\hid#vid_19f5&pid_1026#private-hid-path")]
    [InlineData("product\npath=private-hid-path")]
    public void CreateDescriptor_IdentityLikeDisplayString_DropsIt(string unsafeText)
    {
        Win32HidNative.HiddAttributes attributes = new()
        {
            VendorId = 0x19F5,
            ProductId = 0x1026,
            VersionNumber = 0x0418,
        };

        HidInterfaceDescriptor descriptor = Win32HidDeviceEnumerator.CreateDescriptor(
            "private-enumerator-path",
            attributes,
            capabilities: null,
            manufacturer: unsafeText,
            product: unsafeText);

        Assert.Null(descriptor.Manufacturer);
        Assert.Null(descriptor.Product);
    }
}
