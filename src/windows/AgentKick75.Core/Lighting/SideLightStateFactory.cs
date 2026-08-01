using AgentKick75.Core.State;

namespace AgentKick75.Core.Lighting;

public static class SideLightStateFactory
{
    private const byte StaticColorMode = 0x02;
    private const byte DefaultSpeed = 0x01;

    public static SideLightState CreateStaticColor(LightStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        return new SideLightState(
        [
            StaticColorMode,
            style.Brightness,
            DefaultSpeed,
            0x00,
            0x00,
            style.Color.Red,
            style.Color.Green,
            style.Color.Blue,
        ]);
    }

    /// <summary>
    /// Creates the state color for an aggregate task state. Idle returns null so
    /// the HID owner restores the exact captured baseline instead of synthesizing it.
    /// </summary>
    public static SideLightState? Create(
        TaskVisualState taskState,
        LightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        return taskState switch
        {
            TaskVisualState.Idle => null,
            TaskVisualState.Thinking => CreateStaticColor(settings.Thinking),
            TaskVisualState.RequiresInput => CreateStaticColor(settings.RequiresInput),
            TaskVisualState.Complete => CreateStaticColor(settings.Complete),
            _ => throw new ArgumentOutOfRangeException(nameof(taskState)),
        };
    }
}
