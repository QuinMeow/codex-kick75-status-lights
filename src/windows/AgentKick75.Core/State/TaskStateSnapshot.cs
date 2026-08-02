namespace AgentKick75.Core.State;

public sealed record SessionStateEntry(
    string SessionId,
    string LastTurnId,
    TaskVisualState State,
    DateTimeOffset LastUpdatedAtUtc);

public sealed record TaskStateSnapshot(
    TaskVisualState AggregateState,
    int ActiveSessionCount,
    DateTimeOffset? LastEventAtUtc,
    IReadOnlyList<SessionStateEntry> Sessions);
