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
        Assert.Empty(expired.Turns);
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
        Assert.Empty(snapshot.Turns);
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
    public void Apply_UnrelatedPostWhileCorrelatedUserInputPending_PreservesRequiresInput()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit));
        reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            toolName: "request_user_input",
            toolUseId: "ask-1"));

        TaskStateSnapshot unrelatedPost = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "shell_command",
            toolUseId: "shell-1"));
        TaskStateSnapshot matchingPost = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "request_user_input",
            toolUseId: "ask-1"));

        Assert.Equal(TaskVisualState.RequiresInput, unrelatedPost.AggregateState);
        Assert.Equal(TaskVisualState.Thinking, matchingPost.AggregateState);
    }

    [Fact]
    public void Apply_TwoCorrelatedUserInputs_RequiresEveryMatchingPostBeforeThinking()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            toolName: "request_user_input",
            toolUseId: "ask-1"));
        reducer.Apply(Event(
            CodexHookEventKind.PreToolUse,
            toolName: "request_user_input",
            toolUseId: "ask-2"));

        TaskStateSnapshot onePending = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "request_user_input",
            toolUseId: "ask-2"));
        TaskStateSnapshot nonePending = reducer.Apply(Event(
            CodexHookEventKind.PostToolUse,
            toolName: "request_user_input",
            toolUseId: "ask-1"));

        Assert.Equal(TaskVisualState.RequiresInput, onePending.AggregateState);
        Assert.Equal(TaskVisualState.Thinking, nonePending.AggregateState);
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
        Assert.Equal(3, requiresInput.TrackedTurnCount);
        Assert.Equal(3, requiresInput.SessionCount);

        TaskStateSnapshot afterWaitingEnds = reducer.Apply(SessionEnd("waiting"));
        Assert.Equal(TaskVisualState.Thinking, afterWaitingEnds.AggregateState);

        TaskStateSnapshot afterThinkingEnds = reducer.Apply(SessionEnd("thinking"));
        Assert.Equal(TaskVisualState.Complete, afterThinkingEnds.AggregateState);

        clock.Advance(TaskStateReducer.DefaultCompleteTtl);
        Assert.Equal(TaskVisualState.Idle, reducer.Snapshot().AggregateState);
    }

    [Fact]
    public void Apply_SameTurnIdInDifferentSessions_TracksIndependentKeys()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());

        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-a", "same-turn"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-b", "same-turn"));
        TaskStateSnapshot afterOneSessionEnds = reducer.Apply(SessionEnd("session-a"));

        TaskStateEntry remaining = Assert.Single(afterOneSessionEnds.Turns);
        Assert.Equal("session-b", remaining.Key.SessionId);
        Assert.Equal("same-turn", remaining.Key.TurnId);
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

        TaskStateEntry completed = Assert.Single(snapshot.Turns);
        Assert.Equal(TaskVisualState.Complete, snapshot.AggregateState);
        Assert.Equal("stop-turn", completed.Key.TurnId);
    }

    [Fact]
    public void Apply_SessionEnd_RemovesAllTurnsOnlyForThatSession()
    {
        var reducer = new TaskStateReducer(new ManualTimeProvider());
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-a", "turn-1"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-a", "turn-2"));
        reducer.Apply(Event(CodexHookEventKind.UserPromptSubmit, "session-b", "turn-1"));

        TaskStateSnapshot snapshot = reducer.Apply(SessionEnd("session-a"));

        TaskStateEntry remaining = Assert.Single(snapshot.Turns);
        Assert.Equal("session-b", remaining.Key.SessionId);
        Assert.Equal(1, snapshot.SessionCount);
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

        TaskStateEntry remaining = Assert.Single(snapshot.Turns);
        Assert.Equal("fresh", remaining.Key.SessionId);
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
        string? toolName = null,
        string? toolUseId = null)
    {
        return new CodexHookEvent(kind, sessionId, turnId, toolName, toolUseId);
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
