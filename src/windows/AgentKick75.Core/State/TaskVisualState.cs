namespace AgentKick75.Core.State;

/// <summary>
/// Effective state displayed by the shared Kick75 side lights.
/// Numeric values encode the aggregation priority.
/// </summary>
public enum TaskVisualState
{
    Idle = 0,
    Complete = 1,
    Thinking = 2,
    RequiresInput = 3,
}
