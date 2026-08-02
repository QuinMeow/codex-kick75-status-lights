using AgentKick75.Core.State;

namespace AgentKick75.Core.Lighting;

public static class SideLightStateFactory
{
    public static SideLightState CreateStyle(LightStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        return new SideLightState(
        [
            (byte)style.Effect,
            style.Brightness,
            style.Speed,
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
            TaskVisualState.Thinking => CreateStyle(settings.Thinking),
            TaskVisualState.RequiresInput => CreateStyle(settings.RequiresInput),
            TaskVisualState.Complete => CreateStyle(settings.Complete),
            TaskVisualState.Interrupted => CreateStyle(settings.Interrupted),
            _ => throw new ArgumentOutOfRangeException(nameof(taskState)),
        };
    }
}
