using AgentKick75.Core.Hooks;

namespace AgentKick75.Core.State;

/// <summary>
/// Reduces normalized Codex hooks into per-(session, turn) state and a shared
/// visual state. All public operations are thread-safe.
/// </summary>
public sealed class TaskStateReducer
{
    public static readonly TimeSpan DefaultCompleteTtl = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultStaleTimeout = TimeSpan.FromMinutes(30);

    private const string RequestUserInputToolName = "request_user_input";

    private readonly object syncRoot = new();
    private readonly Dictionary<TaskStateKey, MutableTaskState> turns = [];
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
                RemoveSession(hookEvent.SessionId);
                return CreateSnapshot();
            }

            if (string.IsNullOrWhiteSpace(hookEvent.TurnId))
            {
                return CreateSnapshot();
            }

            var key = new TaskStateKey(hookEvent.SessionId, hookEvent.TurnId);
            switch (hookEvent.Kind)
            {
                case CodexHookEventKind.UserPromptSubmit:
                    ReplaceState(key, TaskVisualState.Thinking, now);
                    break;
                case CodexHookEventKind.PreToolUse:
                    if (string.Equals(
                        hookEvent.ToolName,
                        RequestUserInputToolName,
                        StringComparison.Ordinal))
                    {
                        TrackRequestUserInput(key, hookEvent.ToolUseId, now);
                    }

                    break;
                case CodexHookEventKind.PermissionRequest:
                    TrackUncorrelatedPermissionRequest(key, now);
                    break;
                case CodexHookEventKind.PostToolUse:
                    ApplyPostToolUse(key, hookEvent.ToolUseId, now);
                    break;
                case CodexHookEventKind.Stop:
                    // Codex lifecycle hooks can report different turn_id values
                    // within one top-level response. Stop ends that response, so
                    // clear any earlier Thinking/RequiresInput entry for the same
                    // session before holding Complete.
                    RemoveSession(hookEvent.SessionId);
                    ReplaceState(key, TaskVisualState.Complete, now);
                    break;
                case CodexHookEventKind.SessionEnd:
                    // Handled above because this event has no turn_id.
                    break;
                default:
                    break;
            }

            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Returns a current snapshot after applying complete TTL and stale cleanup.
    /// Calling this periodically is sufficient to drive expiry without a dedicated timer.
    /// </summary>
    public TaskStateSnapshot Snapshot()
    {
        lock (syncRoot)
        {
            RemoveExpired(timeProvider.GetUtcNow());
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Explicit cleanup entry point for a host timer.
    /// </summary>
    public TaskStateSnapshot CleanupStale()
    {
        return Snapshot();
    }

    /// <summary>
    /// Applies a new Complete hold duration to both existing and future turns.
    /// Existing Complete turns are expired immediately when their elapsed age
    /// already exceeds the new duration.
    /// </summary>
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
            turns.Clear();
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

    private void ReplaceState(TaskStateKey key, TaskVisualState state, DateTimeOffset now)
    {
        turns[key] = new MutableTaskState(
            state,
            now,
            state == TaskVisualState.Complete ? now : null);
    }

    private void TrackRequestUserInput(
        TaskStateKey key,
        string? toolUseId,
        DateTimeOffset now)
    {
        MutableTaskState task = GetOrCreateTask(key, now);
        if (toolUseId is null)
        {
            // Older hook fixtures did not carry tool_use_id. Keep their former
            // one-wait behavior, but never let that legacy latch clear a modern,
            // explicitly correlated request_user_input call.
            task.HasUncorrelatedRequiresInput = true;
        }
        else
        {
            task.PendingUserInputToolUseIds.Add(toolUseId);
        }

        UpdateTask(task, TaskVisualState.RequiresInput, now);
    }

    private void TrackUncorrelatedPermissionRequest(TaskStateKey key, DateTimeOffset now)
    {
        MutableTaskState task = GetOrCreateTask(key, now);

        // Codex PermissionRequest stdin currently has no tool_use_id. Do not
        // guess an identity from tool name or arrival order: equal-named tools
        // can run concurrently and a denied call may never emit PostToolUse.
        task.HasUncorrelatedRequiresInput = true;
        UpdateTask(task, TaskVisualState.RequiresInput, now);
    }

    private void ApplyPostToolUse(
        TaskStateKey key,
        string? toolUseId,
        DateTimeOffset now)
    {
        if (!turns.TryGetValue(key, out MutableTaskState? task))
        {
            ReplaceState(key, TaskVisualState.Thinking, now);
            return;
        }

        if (toolUseId is not null)
        {
            task.PendingUserInputToolUseIds.Remove(toolUseId);
        }

        // Preserve the documented legacy PermissionRequest transition without
        // pretending that an uncorrelated approval can be paired exactly. A
        // non-matching PostToolUse can clear this legacy latch, but it cannot
        // clear any request_user_input call that has an explicit tool_use_id.
        task.HasUncorrelatedRequiresInput = false;
        TaskVisualState state = task.PendingUserInputToolUseIds.Count > 0
            ? TaskVisualState.RequiresInput
            : TaskVisualState.Thinking;
        UpdateTask(task, state, now);
    }

    private MutableTaskState GetOrCreateTask(TaskStateKey key, DateTimeOffset now)
    {
        if (!turns.TryGetValue(key, out MutableTaskState? task))
        {
            task = new MutableTaskState(TaskVisualState.Thinking, now, null);
            turns.Add(key, task);
        }

        return task;
    }

    private static void UpdateTask(
        MutableTaskState task,
        TaskVisualState state,
        DateTimeOffset now)
    {
        task.State = state;
        task.LastUpdatedAtUtc = now;
        task.CompletedAtUtc = state == TaskVisualState.Complete ? now : null;
    }

    private void RemoveSession(string sessionId)
    {
        foreach (TaskStateKey key in turns.Keys
                     .Where(key => string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
                     .ToArray())
        {
            turns.Remove(key);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach ((TaskStateKey key, MutableTaskState task) in turns.ToArray())
        {
            TimeSpan staleAge = now - task.LastUpdatedAtUtc;
            bool isStale = staleAge >= StaleTimeout;

            bool isCompleteExpired = task.CompletedAtUtc is { } completedAtUtc
                && now - completedAtUtc >= completeTtl;

            if ((staleAge >= TimeSpan.Zero && isStale)
                || (task.CompletedAtUtc is not null && isCompleteExpired))
            {
                turns.Remove(key);
            }
        }
    }

    private TaskStateSnapshot CreateSnapshot()
    {
        TaskStateEntry[] entries = turns
            .OrderBy(pair => pair.Key.SessionId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.TurnId, StringComparer.Ordinal)
            .Select(pair => new TaskStateEntry(
                pair.Key,
                pair.Value.State,
                pair.Value.LastUpdatedAtUtc,
                pair.Value.CompletedAtUtc))
            .ToArray();

        TaskVisualState aggregateState = entries.Length == 0
            ? TaskVisualState.Idle
            : entries.Max(entry => entry.State);

        int sessionCount = entries
            .Select(entry => entry.Key.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new TaskStateSnapshot(
            aggregateState,
            entries.Length,
            sessionCount,
            lastEventAtUtc,
            Array.AsReadOnly(entries));
    }

    private sealed class MutableTaskState(
        TaskVisualState state,
        DateTimeOffset lastUpdatedAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        public TaskVisualState State { get; set; } = state;

        public DateTimeOffset LastUpdatedAtUtc { get; set; } = lastUpdatedAtUtc;

        public DateTimeOffset? CompletedAtUtc { get; set; } = completedAtUtc;

        public HashSet<string> PendingUserInputToolUseIds { get; } = new(StringComparer.Ordinal);

        public bool HasUncorrelatedRequiresInput { get; set; }
    }
}
