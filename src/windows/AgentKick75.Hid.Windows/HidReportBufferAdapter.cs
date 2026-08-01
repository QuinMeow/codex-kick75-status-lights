// SPDX-License-Identifier: MIT
namespace AgentKick75.Hid.Windows;

public static class HidReportBufferAdapter
{
    public static byte[] ToNativeOutput(ReadOnlySpan<byte> protocolFrame, int nativeReportLength)
    {
        if (protocolFrame.Length != HidTransportProfile.ProtocolReportLength)
        {
            throw new ArgumentException("Kick75 protocol frames must be exactly 64 bytes.", nameof(protocolFrame));
        }

        return nativeReportLength switch
        {
            HidTransportProfile.ProtocolReportLength => protocolFrame.ToArray(),
            HidTransportProfile.ProtocolReportLength + 1 => PrefixReportId(protocolFrame),
            _ => throw new NotSupportedException(
                $"Native HID report length {nativeReportLength} is not supported."),
        };
    }

    public static byte[] FromNativeInput(ReadOnlySpan<byte> nativeReport)
    {
        if (nativeReport.Length == HidTransportProfile.ProtocolReportLength)
        {
            return nativeReport.ToArray();
        }

        if (nativeReport.Length == HidTransportProfile.ProtocolReportLength + 1)
        {
            if (nativeReport[0] != 0)
            {
                throw new InvalidDataException(
                    $"Expected HID report ID 0, received {nativeReport[0]}.");
            }

            return nativeReport[1..].ToArray();
        }

        throw new InvalidDataException(
            $"Native HID input report was {nativeReport.Length} bytes; expected 64 or 65.");
    }

    private static byte[] PrefixReportId(ReadOnlySpan<byte> protocolFrame)
    {
        byte[] nativeReport = new byte[HidTransportProfile.ProtocolReportLength + 1];
        protocolFrame.CopyTo(nativeReport.AsSpan(1));
        return nativeReport;
    }
}
