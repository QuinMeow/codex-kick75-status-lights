namespace AgentKick75.Core.Hooks;

/// <summary>
/// Codex lifecycle hook events that are relevant to the MVP state machine.
/// </summary>
public enum CodexHookEventKind
{
    UserPromptSubmit,
    PreToolUse,
    PermissionRequest,
    PostToolUse,
    GoalBlocked,
    Stop,
    SessionEnd,
}

/// <summary>
/// Privacy-trimmed hook data. Prompt text, tool payloads, tool responses,
/// transcripts, and assistant messages are deliberately not represented.
/// </summary>
public sealed record CodexHookEvent(
    CodexHookEventKind Kind,
    string SessionId,
    string? TurnId = null,
    string? ToolName = null)
{
    public bool IsTurnScoped => Kind != CodexHookEventKind.SessionEnd;
}
