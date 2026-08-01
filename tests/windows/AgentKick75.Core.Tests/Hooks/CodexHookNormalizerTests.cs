using System.Text;
using System.Text.Json;
using AgentKick75.Core.Hooks;

namespace AgentKick75.Core.Tests.Hooks;

public sealed class CodexHookNormalizerTests
{
    [Fact]
    public void Normalize_UserPromptWithPrivateContent_ReturnsPrivacyTrimmedEvent()
    {
        const string json = """
            {
              "hook_event_name": "UserPromptSubmit",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "prompt": "TOP SECRET prompt",
              "transcript_path": "C:/private/transcript.jsonl",
              "tool_name": "injected-tool",
              "unknown": { "nested": [1, 2, 3] }
            }
            """;

        CodexHookEvent? result = new CodexHookNormalizer().Normalize(json);

        Assert.NotNull(result);
        Assert.Equal(CodexHookEventKind.UserPromptSubmit, result.Kind);
        Assert.Equal("session-1", result.SessionId);
        Assert.Equal("turn-1", result.TurnId);
        Assert.Null(result.ToolName);

        string normalizedJson = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("TOP SECRET", normalizedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", normalizedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", normalizedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PreToolUse", true)]
    [InlineData("PermissionRequest", false)]
    [InlineData("PostToolUse", true)]
    public void Normalize_ToolEvent_RetainsOnlyCanonicalIdentifiers(
        string eventName,
        bool toolUseIdExpected)
    {
        string json = $$"""
            {
              "hook_event_name": "{{eventName}}",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "tool_name": "request_user_input",
              "tool_use_id": "tool-call-1",
              "tool_input": { "question": "secret" },
              "tool_response": { "answer": "secret" }
            }
            """;

        CodexHookEvent? result = new CodexHookNormalizer().Normalize(json);

        Assert.NotNull(result);
        Assert.Equal("request_user_input", result.ToolName);
        Assert.Equal(toolUseIdExpected ? "tool-call-1" : null, result.ToolUseId);
        string normalizedJson = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("question", normalizedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("answer", normalizedJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PreToolUse")]
    [InlineData("PostToolUse")]
    public void Normalize_CorrelatableToolEventWithoutToolUseId_RemainsBackwardCompatible(
        string eventName)
    {
        string json = $$"""
            {
              "hook_event_name": "{{eventName}}",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "tool_name": "request_user_input"
            }
            """;

        CodexHookEvent? result = new CodexHookNormalizer().Normalize(json);

        Assert.NotNull(result);
        Assert.Null(result.ToolUseId);
    }

    [Fact]
    public void Normalize_StopWithAssistantMessage_DropsAssistantMessage()
    {
        const string json = """
            {
              "hook_event_name": "Stop",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "last_assistant_message": "private final answer"
            }
            """;

        CodexHookEvent? result = new CodexHookNormalizer().Normalize(json);

        Assert.NotNull(result);
        Assert.Equal(CodexHookEventKind.Stop, result.Kind);
        Assert.DoesNotContain("private final answer", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_SessionEndWithoutTurn_ReturnsSessionScopedEvent()
    {
        const string json = """
            {
              "hook_event_name": "SessionEnd",
              "session_id": "session-1",
              "reason": "other"
            }
            """;

        CodexHookEvent? result = new CodexHookNormalizer().Normalize(json);

        Assert.NotNull(result);
        Assert.Equal(CodexHookEventKind.SessionEnd, result.Kind);
        Assert.False(result.IsTurnScoped);
        Assert.Null(result.TurnId);
        Assert.Null(result.ToolName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"hook_event_name\":\"Unknown\",\"session_id\":\"s\",\"turn_id\":\"t\"}")]
    [InlineData("{\"hook_event_name\":\"Stop\",\"turn_id\":\"t\"}")]
    [InlineData("{\"hook_event_name\":\"Stop\",\"session_id\":\"s\"}")]
    [InlineData("{\"hook_event_name\":\"PostToolUse\",\"session_id\":\"s\",\"turn_id\":\"t\"}")]
    [InlineData("{\"hook_event_name\":1,\"session_id\":\"s\",\"turn_id\":\"t\"}")]
    [InlineData("{\"hook_event_name\":\"Stop\",\"session_id\":{},\"turn_id\":\"t\"}")]
    [InlineData("{\"hook_event_name\":\"PostToolUse\",\"session_id\":\"s\",\"turn_id\":\"t\",\"tool_name\":\"x\",\"tool_use_id\":1}")]
    [InlineData("{\"hook_event_name\":\"PostToolUse\",\"session_id\":\"s\",\"turn_id\":\"t\",\"tool_name\":\"x\",\"tool_use_id\":\"bad\\u0000id\"}")]
    public void Normalize_InvalidOrIncompleteSchema_ReturnsNull(string json)
    {
        Assert.Null(new CodexHookNormalizer().Normalize(json));
    }

    [Fact]
    public void Normalize_DuplicateAllowedProperty_ReturnsNull()
    {
        const string json = """
            {
              "hook_event_name": "Stop",
              "session_id": "first",
              "session_id": "second",
              "turn_id": "turn-1"
            }
            """;

        Assert.Null(new CodexHookNormalizer().Normalize(json));
    }

    [Fact]
    public void Normalize_ToolUseIdOverIdentifierLimit_ReturnsNull()
    {
        string toolUseId = new('x', CodexHookNormalizer.MaxIdentifierLength + 1);
        string json = $$"""
            {
              "hook_event_name": "PostToolUse",
              "session_id": "s",
              "turn_id": "t",
              "tool_name": "request_user_input",
              "tool_use_id": "{{toolUseId}}"
            }
            """;

        Assert.Null(new CodexHookNormalizer().Normalize(json));
    }

    [Fact]
    public void Normalize_PayloadAtLimit_AcceptsExactLimitAndRejectsOneByteLessLimit()
    {
        const string json = """{"hook_event_name":"Stop","session_id":"s","turn_id":"t"}""";
        byte[] payload = Encoding.UTF8.GetBytes(json);

        Assert.NotNull(new CodexHookNormalizer(payload.Length).Normalize(payload));
        Assert.Null(new CodexHookNormalizer(payload.Length - 1).Normalize(payload));
    }

    [Fact]
    public async Task NormalizeAsync_OversizedStdin_ReturnsNull()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            $$"""{"hook_event_name":"Stop","session_id":"s","turn_id":"t","prompt":"{{new string('x', 256)}}"}""");
        await using var input = new MemoryStream(payload);

        CodexHookEvent? result = await new CodexHookNormalizer(64).NormalizeAsync(input);

        Assert.Null(result);
        Assert.InRange(input.Position, 65, payload.Length);
    }
}
