using AgentKick75.Core.Hooks;
using AgentKick75.Core.State;

namespace AgentKick75.Core.Tests.State;

public sealed class TaskStateReducerTests
{
    [Fact]
    public void Apply_LifecycleSequence_MapsEveryStateAndExpiresComplete()
    {
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(clock);

        TaskStateSnapshot thinking = reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit));
        TaskStateSnapshot waiting = reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            toolName: "request_user_input"));
        TaskStateSnapshot resumed = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "request_user_input"));
        TaskStateSnapshot complete = reducer.Apply(Event(CodexHookEventKind.Stop));

        Assert.Equal(TaskVisualState.Thinking, thinking.AggregateState);
        Assert.Equal(TaskVisualState.RequiresInput, waiting.AggregateState);
        Assert.Equal(TaskVisualState.Thinking, resumed.AggregateState);
        Assert.Equal(TaskVisualState.Complete, complete.AggregateState);

        clock.Advance(TimeSpan.FromSeconds(10));
        TaskStateSnapshot expired = reducer.Snapshot();

        Assert.Equal(TaskVisualState.Idle, expired.AggregateState);
        Assert.Empty(expired.Sessions);
    }

    [Fact]
    public void UpdateCompleteTtl_ShorterDuration_ExpiresExistingCompleteImmediately()
    {
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(clock, completeTtl: TimeSpan.FromSeconds(10));
        reducer.Apply(Event(CodexHookEventKind.Stop));
        clock.Advance(TimeSpan.FromSeconds(5));

        TaskStateSnapshot snapshot = reducer.UpdateCompleteTtl(TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.FromSeconds(4), reducer.CompleteTtl);
        Assert.Equal(TaskVisualState.Idle, snapshot.AggregateState);
        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public void UpdateCompleteTtl_LongerDuration_ExtendsExistingCompleteImmediately()
    {
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(clock, completeTtl: TimeSpan.FromSeconds(2));
        reducer.Apply(Event(CodexHookEventKind.Stop));
        clock.Advance(TimeSpan.FromSeconds(1));

        TaskStateSnapshot updated = reducer.UpdateCompleteTtl(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(8));

        Assert.Equal(TaskVisualState.Complete, updated.AggregateState);
        Assert.Equal(TaskVisualState.Complete, reducer.Snapshot().AggregateState);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TaskVisualState.Idle, reducer.Snapshot().AggregateState);
    }

    [Fact]
    public void Apply_GoalBlocked_HoldsInterruptedUntilNextUserPromptAndIgnoresStop()
    {
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(
            clock,
            completeTtl: TimeSpan.FromSeconds(10),
            staleTimeout: TimeSpan.FromMinutes(30));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit));

        TaskStateSnapshot blocked = reducer.Apply(Event(CodexHookEventKind.GoalBlocked));
        TaskStateSnapshot afterStop = reducer.Apply(Event(CodexHookEventKind.Stop));
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(TaskVisualState.Interrupted, blocked.AggregateState);
        Assert.Equal(TaskVisualState.Interrupted, afterStop.AggregateState);
        Assert.Equal(TaskVisualState.Interrupted, reducer.Snapshot().AggregateState);

        TaskStateSnapshot resumed = reducer.Apply(Event(
            CodexHookEventKind.UserPromptSubmit,
            turnId: "turn-2"));

        Assert.Equal(TaskVisualState.Thinking, resumed.AggregateState);
        Assert.Single(resumed.Sessions);
        Assert.Equal("turn-2", resumed.Sessions[0].LastTurnId);
    }

    [Fact]
    public void Apply_PermissionThenPostToolUse_ReturnsToThinkingWithoutReadingToolPayload()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());

        TaskStateSnapshot waiting = reducer.Apply(Event(
            CodexHookEventKind.PermissionRequest,
            toolName: "shell_command"));
        TaskStateSnapshot resumed = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "shell_command"));

        Assert.Equal(TaskVisualState.RequiresInput, waiting.AggregateState);
        Assert.Equal(TaskVisualState.Thinking, resumed.AggregateState);
    }

    [Fact]
    public void Apply_UnrelatedPostWhileInputPending_PreservesRequiresInput()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            toolName: "request_user_input"));

        TaskStateSnapshot unrelated = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "shell_command"));
        TaskStateSnapshot matching = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "request_user_input"));

        Assert.Equal(TaskVisualState.RequiresInput, unrelated.AggregateState);
        Assert.Equal(TaskVisualState.Thinking, matching.AggregateState);
    }

    [Fact]
    public void Apply_ParallelTurns_AggregatesByRequiredPriority()
    {
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(clock);

        reducer.Apply(Event(CodexHookEventKind.Stop, "complete", "turn-1"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "thinking", "turn-2"));
        TaskStateSnapshot requiresInput = reducer.Apply(Event(
            CodexHookEventKind.PermissionRequest,
            "waiting",
            "turn-3",
            "shell_command"));

        Assert.Equal(TaskVisualState.RequiresInput, requiresInput.AggregateState);
        Assert.Equal(3, requiresInput.Sessions.Count);
        Assert.Equal(2, requiresInput.ActiveSessionCount);

        TaskStateSnapshot afterWaitingEnds = reducer.Apply(SessionEnd("waiting"));
        Assert.Equal(TaskVisualState.Thinking, afterWaitingEnds.AggregateState);

        TaskStateSnapshot afterThinkingEnds = reducer.Apply(SessionEnd("thinking"));
        Assert.Equal(TaskVisualState.Complete, afterThinkingEnds.AggregateState);

        clock.Advance(TaskStateReducer.DefaultCompleteTtl);
        Assert.Equal(TaskVisualState.Idle, reducer.Snapshot().AggregateState);
    }

    [Fact]
    public void Apply_StopWhileAnotherSessionThinks_PreservesPendingInputPriority()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(
            CodexHookEventKind.UserPromptSubmit,
            "thinking",
            "thinking-turn"));
        reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            "waiting",
            "waiting-turn",
            "request_user_input"));

        TaskStateSnapshot yielded = reducer.Apply(Event(
            CodexHookEventKind.Stop,
            "waiting",
            "notification-turn"));
        TaskStateSnapshot otherCompleted = reducer.Apply(Event(
            CodexHookEventKind.Stop,
            "thinking",
            "thinking-turn"));

        Assert.Equal(TaskVisualState.RequiresInput, yielded.AggregateState);
        Assert.Equal(TaskVisualState.RequiresInput, otherCompleted.AggregateState);
        Assert.Equal(1, otherCompleted.ActiveSessionCount);

        reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            "waiting",
            "waiting-turn",
            "request_user_input"));
        TaskStateSnapshot completed = reducer.Apply(Event(
            CodexHookEventKind.Stop,
            "waiting",
            "final-turn"));

        Assert.Equal(TaskVisualState.Complete, completed.AggregateState);
        Assert.Equal(2, completed.Sessions.Count);
        Assert.All(
            completed.Sessions,
            session => Assert.Equal(TaskVisualState.Complete, session.State));
    }

    [Fact]
    public void Apply_LateToolEventsAfterStop_DoNotResurrectThinking()
    {
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(
            clock,
            completeTtl: TimeSpan.FromSeconds(2));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit));
        reducer.Apply(Event(CodexHookEventKind.Stop));

        TaskStateSnapshot latePost = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            turnId: "late-turn",
            toolName: "shell_command"));

        Assert.Equal(TaskVisualState.Complete, latePost.AggregateState);

        clock.Advance(TimeSpan.FromSeconds(3));
        TaskStateSnapshot muchLaterPost = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            turnId: "much-later-turn",
            toolName: "shell_command"));

        Assert.Equal(TaskVisualState.Idle, muchLaterPost.AggregateState);
        Assert.Empty(muchLaterPost.Sessions);

        TaskStateSnapshot resumed = reducer.Apply(Event(
            CodexHookEventKind.UserPromptSubmit,
            turnId: "next-turn"));

        Assert.Equal(TaskVisualState.Thinking, resumed.AggregateState);
        Assert.Single(resumed.Sessions);
    }

    [Fact]
    public void Apply_UserPromptAfterRequiresInput_ClearsPreviousTurnWait()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            turnId: "waiting-turn",
            toolName: "request_user_input"));

        TaskStateSnapshot answered = reducer.Apply(Event(
            CodexHookEventKind.UserPromptSubmit,
            turnId: "answer-turn"));

        SessionStateEntry active = Assert.Single(answered.Sessions);
        Assert.Equal(TaskVisualState.Thinking, answered.AggregateState);
        Assert.Equal("answer-turn", active.LastTurnId);
    }

    [Fact]
    public void Apply_PostWithDifferentTurnId_ClearsPreviousTurnWait()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            turnId: "waiting-turn",
            toolName: "request_user_input"));

        TaskStateSnapshot answered = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            turnId: "result-turn",
            toolName: "request_user_input"));

        SessionStateEntry active = Assert.Single(answered.Sessions);
        Assert.Equal(TaskVisualState.Thinking, answered.AggregateState);
        Assert.Equal("result-turn", active.LastTurnId);
    }

    [Fact]
    public void Apply_SameTurnIdInDifferentSessions_TracksIndependentKeys()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());

        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-a", "same-turn"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-b", "same-turn"));
        TaskStateSnapshot afterOneSessionEnds = reducer.Apply(SessionEnd("session-a"));

        SessionStateEntry remaining = Assert.Single(afterOneSessionEnds.Sessions);
        Assert.Equal("session-b", remaining.SessionId);
        Assert.Equal("same-turn", remaining.LastTurnId);
    }

    [Fact]
    public void Apply_StopWithDifferentTurnId_CompletesSessionWithoutStaleThinking()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session", "prompt-turn"));
        reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            "session",
            "tool-turn",
            "shell_command"));

        TaskStateSnapshot snapshot = reducer.Apply(Event(
            CodexHookEventKind.Stop,
            "session",
            "stop-turn"));

        SessionStateEntry completed = Assert.Single(snapshot.Sessions);
        Assert.Equal(TaskVisualState.Complete, snapshot.AggregateState);
        Assert.Equal("stop-turn", completed.LastTurnId);
    }

    [Fact]
    public void Apply_SessionEnd_RemovesOnlyThatSession()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-a", "turn-1"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-a", "turn-2"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-b", "turn-1"));

        TaskStateSnapshot snapshot = reducer.Apply(SessionEnd("session-a"));

        SessionStateEntry remaining = Assert.Single(snapshot.Sessions);
        Assert.Equal("session-b", remaining.SessionId);
        Assert.Equal(1, snapshot.ActiveSessionCount);
    }

    [Fact]
    public void CleanupStale_StaleActiveTurn_RemovesOnlyUnrefreshedTurn()
    {
        var clock = new ManualTimeProvider();
        var reducer = new TaskStateReducer(
            clock,
            completeTtl: TimeSpan.FromSeconds(10),
            staleTimeout: TimeSpan.FromSeconds(30));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "stale", "turn-1"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "fresh", "turn-2"));

        clock.Advance(TimeSpan.FromSeconds(20));
        reducer.Apply(Event(CodexHookEventKind.PostToolUse, "fresh", "turn-2", "shell_command"));
        clock.Advance(TimeSpan.FromSeconds(10));

        TaskStateSnapshot snapshot = reducer.CleanupStale();

        SessionStateEntry remaining = Assert.Single(snapshot.Sessions);
        Assert.Equal("fresh", remaining.SessionId);
        Assert.Equal(TaskVisualState.Thinking, snapshot.AggregateState);
    }

    [Fact]
    public void Apply_NonRequestPreToolUse_DoesNotEnterRequiresInput()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit));

        TaskStateSnapshot snapshot = reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            toolName: "shell_command"));

        Assert.Equal(TaskVisualState.Thinking, snapshot.AggregateState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveDuration_Throws(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskStateReducer(
            completeTtl: TimeSpan.FromSeconds(seconds)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskStateReducer(
            staleTimeout: TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UpdateCompleteTtl_NonPositiveDuration_ThrowsWithoutChangingValue(int seconds)
    {
        var reducer = new TaskStateReducer(completeTtl: TimeSpan.FromSeconds(10));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            reducer.UpdateCompleteTtl(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(TimeSpan.FromSeconds(10), reducer.CompleteTtl);
    }

    private static CodexHookEvent Event(
        CodexHookEventKind kind,
        string sessionId = "session-1",
        string turnId = "turn-1",
        string? toolName = null)
    {
        return new CodexHookEvent(kind, sessionId, turnId, toolName);
    }

    private static CodexHookEvent SessionEnd(string sessionId)
    {
        return new CodexHookEvent(CodexHookEventKind.SessionEnd, sessionId);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }
}
