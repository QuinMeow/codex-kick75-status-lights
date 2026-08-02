// SPDX-License-Identifier: MIT
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests.Hid;

public sealed class Win32HidConnectionFactoryTests
{
    [Fact]
    public async Task OpenAsync_NonHidHandleCannotFlushQueue_FailsClosedAndReleasesHandle()
    {
        string path = Path.GetTempFileName();
        try
        {
            HidInterfaceDescriptor descriptor = new(
                path,
                HidTransportProfile.Kick75VendorId,
                HidTransportProfiles.Kick75Usb.ProductId,
                HidTransportProfile.RawHidUsagePage,
                HidTransportProfile.RawHidUsage,
                HidTransportProfile.ProtocolReportLength + 1,
                HidTransportProfile.ProtocolReportLength + 1);
            HidDeviceSelection selection = new(
                descriptor,
                HidTransportProfiles.Kick75Usb,
                HidDeviceState.Present,
                "Synthetic descriptor for the native queue-flush failure path.",
                Array.Empty<HidCandidateDiagnostic>());
            Win32HidConnectionFactory factory = new();

            HidTransportException exception = await Assert.ThrowsAsync<HidTransportException>(
                async () => _ = await factory.OpenAsync(selection));

            Assert.Contains("clear pending HID input reports", exception.Message, StringComparison.Ordinal);
            Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerException);

            // The failed queue flush must not leak the exclusive CreateFile
            // handle. Opening the same path with FileShare.None proves cleanup.
            await using FileStream reopened = new(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
