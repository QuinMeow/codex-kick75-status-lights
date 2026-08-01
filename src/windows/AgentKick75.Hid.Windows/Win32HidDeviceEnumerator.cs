// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AgentKick75.Hid.Windows;

public sealed partial class Win32HidDeviceEnumerator : IHidDeviceEnumerator
{
    private const int HidStringBufferBytes = 512;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public IReadOnlyList<HidInterfaceDescriptor> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SetupAPI HID enumeration requires Windows.");
        }

        Win32HidNative.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfoSet = Win32HidNative.SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32HidNative.DigcfPresent | Win32HidNative.DigcfDeviceInterface);
        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate HID interfaces.");
        }

        try
        {
            List<HidInterfaceDescriptor> devices = new();
            for (uint index = 0; ; index++)
            {
                Win32HidNative.SpDeviceInterfaceData interfaceData = new()
                {
                    Size = Marshal.SizeOf<Win32HidNative.SpDeviceInterfaceData>(),
                };

                if (!Win32HidNative.SetupDiEnumDeviceInterfaces(
                    deviceInfoSet,
                    IntPtr.Zero,
                    ref hidGuid,
                    index,
                    ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == Win32HidNative.ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "SetupAPI failed while enumerating HID interfaces.");
                }

                string devicePath = GetDevicePath(deviceInfoSet, ref interfaceData);
                devices.Add(Inspect(devicePath));
            }

            return devices
                .OrderBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _ = Win32HidNative.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string GetDevicePath(
        IntPtr deviceInfoSet,
        ref Win32HidNative.SpDeviceInterfaceData interfaceData)
    {
        _ = Win32HidNative.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out int requiredSize,
            IntPtr.Zero);
        if (requiredSize <= 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetupAPI did not return a HID interface detail size.");
        }

        IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
            if (!Win32HidNative.SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet,
                ref interfaceData,
                detailBuffer,
                requiredSize,
                out _,
                IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to read a HID interface path.");
            }

            return Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, sizeof(int))) ??
                throw new InvalidDataException("SetupAPI returned an empty HID interface path.");
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static HidInterfaceDescriptor Inspect(string devicePath)
    {
        SafeFileHandle handle = Win32HidNative.CreateFile(
            devicePath,
            0,
            Win32HidNative.FileShareRead | Win32HidNative.FileShareWrite,
            IntPtr.Zero,
            Win32HidNative.OpenExisting,
            0,
            IntPtr.Zero);
        using (handle)
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                (ushort vendorId, ushort productId) = ParseIdentity(devicePath);
                return new HidInterfaceDescriptor(
                    devicePath,
                    vendorId,
                    productId,
                    0,
                    0,
                    0,
                    0,
                    EnumerationError: new Win32Exception(error).Message,
                    EnumerationErrorCode: error);
            }

            Win32HidNative.HiddAttributes attributes = new()
            {
                Size = Marshal.SizeOf<Win32HidNative.HiddAttributes>(),
            };
            if (!Win32HidNative.HidD_GetAttributes(handle, ref attributes))
            {
                int error = Marshal.GetLastWin32Error();
                (ushort vendorId, ushort productId) = ParseIdentity(devicePath);
                return new HidInterfaceDescriptor(
                    devicePath,
                    vendorId,
                    productId,
                    0,
                    0,
                    0,
                    0,
                    EnumerationError: new Win32Exception(error).Message,
                    EnumerationErrorCode: error);
            }

            string? manufacturer = ReadHidString(
                handle,
                Win32HidNative.HidD_GetManufacturerString);
            string? product = ReadHidString(
                handle,
                Win32HidNative.HidD_GetProductString);

            if (!Win32HidNative.HidD_GetPreparsedData(handle, out IntPtr preparsedData))
            {
                return CreateDescriptor(
                    devicePath,
                    attributes,
                    capabilities: null,
                    manufacturer: manufacturer,
                    product: product,
                    enumerationError: "HidD_GetPreparsedData failed.");
            }

            try
            {
                Win32HidNative.HidpCaps capabilities = new()
                {
                    Reserved = new ushort[17],
                };
                int status = Win32HidNative.HidP_GetCaps(preparsedData, ref capabilities);
                if (status != Win32HidNative.HidpStatusSuccess)
                {
                    return CreateDescriptor(
                        devicePath,
                        attributes,
                        capabilities: null,
                        manufacturer: manufacturer,
                        product: product,
                        enumerationError: $"HidP_GetCaps failed with NTSTATUS 0x{status:X8}.");
                }

                return CreateDescriptor(
                    devicePath,
                    attributes,
                    capabilities,
                    manufacturer,
                    product);
            }
            finally
            {
                _ = Win32HidNative.HidD_FreePreparsedData(preparsedData);
            }
        }
    }

    internal static HidInterfaceDescriptor CreateDescriptor(
        string devicePath,
        Win32HidNative.HiddAttributes attributes,
        Win32HidNative.HidpCaps? capabilities,
        string? manufacturer,
        string? product,
        string? enumerationError = null,
        int? enumerationErrorCode = null)
    {
        return new HidInterfaceDescriptor(
            devicePath,
            attributes.VendorId,
            attributes.ProductId,
            capabilities?.UsagePage ?? 0,
            capabilities?.Usage ?? 0,
            capabilities?.InputReportByteLength ?? 0,
            capabilities?.OutputReportByteLength ?? 0,
            Manufacturer: HidDescriptorText.Normalize(manufacturer),
            Product: HidDescriptorText.Normalize(product),
            EnumerationError: enumerationError,
            EnumerationErrorCode: enumerationErrorCode,
            HidDescriptorVersionNumber: attributes.VersionNumber);
    }

    private static string? ReadHidString(
        SafeFileHandle handle,
        HidStringReader read)
    {
        IntPtr buffer = Marshal.AllocHGlobal(HidStringBufferBytes);
        try
        {
            // Ensure even a short or malformed driver response cannot expose
            // uninitialized process memory through display metadata.
            Marshal.Copy(new byte[HidStringBufferBytes], 0, buffer, HidStringBufferBytes);
            if (!read(handle, buffer, HidStringBufferBytes))
            {
                return null;
            }

            string? bounded = Marshal.PtrToStringUni(
                buffer,
                HidStringBufferBytes / sizeof(char));
            int terminator = bounded?.IndexOf('\0') ?? -1;
            if (terminator >= 0)
            {
                bounded = bounded![..terminator];
            }

            return HidDescriptorText.Normalize(bounded);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (ushort VendorId, ushort ProductId) ParseIdentity(string devicePath)
    {
        Match match = VidPidRegex().Match(devicePath);
        if (!match.Success)
        {
            return (0, 0);
        }

        return (
            ushort.Parse(match.Groups["vid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ushort.Parse(match.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(
        "vid_(?<vid>[0-9a-f]{4}).*pid_(?<pid>[0-9a-f]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();

    private delegate bool HidStringReader(
        SafeFileHandle handle,
        IntPtr buffer,
        int bufferLength);
}
