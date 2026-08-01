// SPDX-License-Identifier: MIT
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests.Hid;

public sealed class HidReportBufferAdapterTests
{
    [Fact]
    public void ToNativeOutput_Native65_PrefixesReportIdZeroWithoutChangingFrame()
    {
        byte[] frame = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();

        byte[] native = HidReportBufferAdapter.ToNativeOutput(frame, 65);

        Assert.Equal(65, native.Length);
        Assert.Equal(0, native[0]);
        Assert.Equal(frame, native[1..]);
    }

    [Fact]
    public void FromNativeInput_Native65_StripsOnlyReportIdZero()
    {
        byte[] frame = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        byte[] native = new byte[65];
        frame.CopyTo(native, 1);

        byte[] decoded = HidReportBufferAdapter.FromNativeInput(native);

        Assert.Equal(frame, decoded);
    }

    [Fact]
    public void FromNativeInput_NonzeroReportId_Throws()
    {
        byte[] native = new byte[65];
        native[0] = 1;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => HidReportBufferAdapter.FromNativeInput(native));

        Assert.Contains("report ID 0", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToNativeOutput_Raw64Backend_PreservesCompleteFrame()
    {
        byte[] frame = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();

        byte[] native = HidReportBufferAdapter.ToNativeOutput(frame, 64);

        Assert.Equal(frame, native);
        Assert.NotSame(frame, native);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(66)]
    public void ToNativeOutput_UnknownNativeLength_Throws(int nativeLength)
    {
        Assert.Throws<NotSupportedException>(
            () => HidReportBufferAdapter.ToNativeOutput(new byte[64], nativeLength));
    }
}
