namespace AgentKick75.Core.State;

public readonly record struct TaskStateKey(string SessionId, string TurnId);

public sealed record TaskStateEntry(
    TaskStateKey Key,
    TaskVisualState State,
    DateTimeOffset LastUpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record TaskStateSnapshot(
    TaskVisualState AggregateState,
    int TrackedTurnCount,
    int SessionCount,
    DateTimeOffset? LastEventAtUtc,
    IReadOnlyList<TaskStateEntry> Turns);
