using AgentKick75.Core.Hooks;

namespace AgentKick75.Core.State;

/// <summary>
/// Reduces Codex lifecycle events into one state per session and then selects
/// the highest-priority state for the shared side lights. All operations are
/// thread-safe.
/// </summary>
public sealed class TaskStateReducer
{
    public static readonly TimeSpan DefaultCompleteTtl = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultStaleTimeout = TimeSpan.FromMinutes(30);

    private const string RequestUserInputToolName = "request_user_input";

    private readonly object syncRoot = new();
    private readonly Dictionary<string, MutableSessionState> sessions =
        new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private TimeSpan completeTtl;
    private DateTimeOffset? lastEventAtUtc;

    public TaskStateReducer(
        TimeProvider? timeProvider = null,
        TimeSpan? completeTtl = null,
        TimeSpan? staleTimeout = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.completeTtl = completeTtl ?? DefaultCompleteTtl;
        StaleTimeout = staleTimeout ?? DefaultStaleTimeout;

        ValidatePositiveDuration(this.completeTtl, nameof(completeTtl));
        ValidatePositiveDuration(StaleTimeout, nameof(staleTimeout));
    }

    public TimeSpan CompleteTtl
    {
        get
        {
            lock (syncRoot)
            {
                return completeTtl;
            }
        }
    }

    public TimeSpan StaleTimeout { get; }

    public TaskStateSnapshot Apply(CodexHookEvent hookEvent)
    {
        ArgumentNullException.ThrowIfNull(hookEvent);

        lock (syncRoot)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            RemoveExpired(now);

            if (string.IsNullOrWhiteSpace(hookEvent.SessionId))
            {
                return CreateSnapshot();
            }

            lastEventAtUtc = now;
            if (hookEvent.Kind == CodexHookEventKind.SessionEnd)
            {
                sessions.Remove(hookEvent.SessionId);
                return CreateSnapshot();
            }

            if (string.IsNullOrWhiteSpace(hookEvent.TurnId))
            {
                return CreateSnapshot();
            }

            sessions.TryGetValue(hookEvent.SessionId, out MutableSessionState? session);
            switch (hookEvent.Kind)
            {
                case CodexHookEventKind.UserPromptSubmit:
                    session = GetOrCreateSession(hookEvent.SessionId, hookEvent.TurnId, now);
                    session.Transition(hookEvent.TurnId, TaskVisualState.Thinking, now);
                    break;

                case CodexHookEventKind.PreToolUse
                    when string.Equals(
                        hookEvent.ToolName,
                        RequestUserInputToolName,
                        StringComparison.Ordinal):
                    if (session?.State == TaskVisualState.Interrupted)
                    {
                        break;
                    }

                    session = GetOrCreateSession(hookEvent.SessionId, hookEvent.TurnId, now);
                    session.RequireInput(hookEvent.TurnId, hookEvent.ToolName!, now);
                    break;

                case CodexHookEventKind.PermissionRequest:
                    if (session?.State == TaskVisualState.Interrupted)
                    {
                        break;
                    }

                    session = GetOrCreateSession(hookEvent.SessionId, hookEvent.TurnId, now);
                    session.RequireInput(hookEvent.TurnId, hookEvent.ToolName!, now);
                    break;

                case CodexHookEventKind.PostToolUse:
                    if (session is not null
                        && !session.IsTerminal
                        && session.CanResumeFrom(hookEvent.ToolName))
                    {
                        session.Transition(hookEvent.TurnId, TaskVisualState.Thinking, now);
                    }

                    break;

                case CodexHookEventKind.GoalBlocked:
                    session = GetOrCreateSession(hookEvent.SessionId, hookEvent.TurnId, now);
                    session.Transition(hookEvent.TurnId, TaskVisualState.Interrupted, now);
                    break;

                case CodexHookEventKind.Stop:
                    if (session?.State is TaskVisualState.RequiresInput or TaskVisualState.Interrupted)
                    {
                        break;
                    }

                    session = GetOrCreateSession(hookEvent.SessionId, hookEvent.TurnId, now);
                    session.Transition(hookEvent.TurnId, TaskVisualState.Complete, now);
                    break;

                case CodexHookEventKind.PreToolUse:
                case CodexHookEventKind.SessionEnd:
                default:
                    break;
            }

            return CreateSnapshot();
        }
    }

    public TaskStateSnapshot Snapshot()
    {
        lock (syncRoot)
        {
            RemoveExpired(timeProvider.GetUtcNow());
            return CreateSnapshot();
        }
    }

    public TaskStateSnapshot CleanupStale()
    {
        return Snapshot();
    }

    public TaskStateSnapshot UpdateCompleteTtl(TimeSpan value)
    {
        ValidatePositiveDuration(value, nameof(value));

        lock (syncRoot)
        {
            completeTtl = value;
            RemoveExpired(timeProvider.GetUtcNow());
            return CreateSnapshot();
        }
    }

    public TaskStateSnapshot Reset()
    {
        lock (syncRoot)
        {
            sessions.Clear();
            return CreateSnapshot();
        }
    }

    private static void ValidatePositiveDuration(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Duration must be positive.");
        }
    }

    private MutableSessionState GetOrCreateSession(
        string sessionId,
        string turnId,
        DateTimeOffset now)
    {
        if (!sessions.TryGetValue(sessionId, out MutableSessionState? session))
        {
            session = new MutableSessionState(turnId, now);
            sessions.Add(sessionId, session);
        }

        return session;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach ((string sessionId, MutableSessionState session) in sessions.ToArray())
        {
            TimeSpan age = now - session.LastUpdatedAtUtc;
            if (age < TimeSpan.Zero || session.State == TaskVisualState.Interrupted)
            {
                continue;
            }

            TimeSpan lifetime = session.State == TaskVisualState.Complete
                ? completeTtl
                : StaleTimeout;
            if (age >= lifetime)
            {
                sessions.Remove(sessionId);
            }
        }
    }

    private TaskStateSnapshot CreateSnapshot()
    {
        SessionStateEntry[] entries = sessions
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SessionStateEntry(
                pair.Key,
                pair.Value.TurnId,
                pair.Value.State,
                pair.Value.LastUpdatedAtUtc))
            .ToArray();

        TaskVisualState aggregateState = entries.Length == 0
            ? TaskVisualState.Idle
            : entries.MaxBy(entry => Priority(entry.State))!.State;
        int activeSessionCount = entries.Count(
            entry => entry.State != TaskVisualState.Complete);

        return new TaskStateSnapshot(
            aggregateState,
            activeSessionCount,
            lastEventAtUtc,
            Array.AsReadOnly(entries));
    }

    private static int Priority(TaskVisualState state)
    {
        return state switch
        {
            TaskVisualState.Idle => 0,
            TaskVisualState.Complete => 1,
            TaskVisualState.Thinking => 2,
            TaskVisualState.Interrupted => 3,
            TaskVisualState.RequiresInput => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }

    private sealed class MutableSessionState(string turnId, DateTimeOffset now)
    {
        public string TurnId { get; private set; } = turnId;

        public TaskVisualState State { get; private set; } = TaskVisualState.Thinking;

        public DateTimeOffset LastUpdatedAtUtc { get; private set; } = now;

        private string? WaitingToolName { get; set; }

        public bool IsTerminal => State is TaskVisualState.Complete or TaskVisualState.Interrupted;

        public bool CanResumeFrom(string? toolName)
        {
            return State != TaskVisualState.RequiresInput
                || string.Equals(WaitingToolName, toolName, StringComparison.Ordinal);
        }

        public void RequireInput(
            string nextTurnId,
            string toolName,
            DateTimeOffset updatedAtUtc)
        {
            Transition(nextTurnId, TaskVisualState.RequiresInput, updatedAtUtc);
            WaitingToolName = toolName;
        }

        public void Transition(
            string nextTurnId,
            TaskVisualState state,
            DateTimeOffset updatedAtUtc)
        {
            TurnId = nextTurnId;
            State = state;
            LastUpdatedAtUtc = updatedAtUtc;
            WaitingToolName = null;
        }
    }
}
