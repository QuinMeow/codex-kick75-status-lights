using AgentKick75.Core.Lighting;
using AgentKick75.Core.State;

namespace AgentKick75.Core.Tests.Lighting;

public sealed class SideLightStateTests
{
    [Fact]
    public void Constructor_ExactEightBytes_ClonesAndPreservesRawState()
    {
        byte[] raw = [0x00, 0x64, 0x01, 0x01, 0x00, 0xE9, 0xFF, 0xFB];

        var state = new SideLightState(raw);
        raw[0] = 0xFF;

        Assert.Equal("0064010100e9fffb", state.ToString());
        Assert.Equal(8, state.Bytes.Length);
        Assert.Equal(0x00, state.Bytes.Span[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void Constructor_NonEightByteInput_Throws(int length)
    {
        Assert.Throws<ArgumentException>(() => new SideLightState(new byte[length]));
    }

    [Fact]
    public void TryCreate_NonEightByteInput_ReturnsFalse()
    {
        Assert.False(SideLightState.TryCreate(new byte[7], out SideLightState? state));
        Assert.Null(state);
    }

    [Fact]
    public void CreateStaticColor_ValidStyle_ProducesProtocolEightByteState()
    {
        var style = new LightStyle(RgbColor.Parse("#123ABC"), 42);

        SideLightState state = SideLightStateFactory.CreateStaticColor(style);

        Assert.Equal(new byte[] { 0x02, 0x2A, 0x01, 0x00, 0x00, 0x12, 0x3A, 0xBC }, state.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void LightStyle_BrightnessOutsideFirmwareRange_Throws(int brightness)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LightStyle(default, brightness));
    }

    [Theory]
    [InlineData("#123abc", 0x12, 0x3A, 0xBC)]
    [InlineData("#000000", 0x00, 0x00, 0x00)]
    [InlineData("#FFFFFF", 0xFF, 0xFF, 0xFF)]
    public void Parse_ValidColor_ReturnsRgbAndCanonicalText(
        string text,
        byte red,
        byte green,
        byte blue)
    {
        RgbColor color = RgbColor.Parse(text);

        Assert.Equal(new RgbColor(red, green, blue), color);
        Assert.Equal(text.ToUpperInvariant(), color.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GG0000")]
    public void Parse_InvalidColor_Throws(string text)
    {
        Assert.Throws<FormatException>(() => RgbColor.Parse(text));
    }

    [Fact]
    public void Constructor_InvalidSettings_Throws()
    {
        var style = new LightStyle(default, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LightingSettings(
            style,
            style,
            style,
            TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LightingSettings(
            style,
            style,
            style,
            TimeSpan.FromSeconds(10),
            version: 2));
        Assert.Throws<ArgumentNullException>(() => new LightingSettings(
            null!,
            style,
            style,
            TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(TaskVisualState.Thinking, "0264010000006bff")]
    [InlineData(TaskVisualState.RequiresInput, "0264010000ffb400")]
    [InlineData(TaskVisualState.Complete, "026401000000ff00")]
    public void Create_ActiveAggregateState_UsesValidatedDefaultStyle(
        TaskVisualState taskState,
        string expectedHex)
    {
        SideLightState? result = SideLightStateFactory.Create(taskState, LightingSettings.Default);

        Assert.NotNull(result);
        Assert.Equal(expectedHex, result.ToString());
    }

    [Fact]
    public void Create_IdleState_ReturnsNullForBaselineRestore()
    {
        Assert.Null(SideLightStateFactory.Create(TaskVisualState.Idle, LightingSettings.Default));
    }
}
