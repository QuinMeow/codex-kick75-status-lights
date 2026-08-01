using System.Text;
using System.Text.Json;

namespace AgentKick75.Core.Hooks;

/// <summary>
/// Parses untrusted Codex hook stdin and projects it onto a small allowlisted schema.
/// Invalid, incomplete, unknown, or oversized input is ignored.
/// </summary>
public sealed class CodexHookNormalizer
{
    public const int DefaultMaxInputBytes = 64 * 1024;
    public const int MaxIdentifierLength = 256;

    private const string RequestUserInputToolName = "request_user_input";

    public CodexHookNormalizer(int maxInputBytes = DefaultMaxInputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputBytes);
        MaxInputBytes = maxInputBytes;
    }

    public int MaxInputBytes { get; }

    public CodexHookEvent? Normalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        int byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount == 0 || byteCount > MaxInputBytes)
        {
            return null;
        }

        return Normalize(Encoding.UTF8.GetBytes(json));
    }

    public CodexHookEvent? Normalize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaxInputBytes)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });

            return NormalizeObject(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<CodexHookEvent?> NormalizeAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var buffer = new MemoryStream(Math.Min(MaxInputBytes, 4096));
        byte[] chunk = new byte[Math.Min(MaxInputBytes + 1, 4096)];

        try
        {
            while (buffer.Length <= MaxInputBytes)
            {
                int remaining = MaxInputBytes + 1 - checked((int)buffer.Length);
                int bytesRead = await input.ReadAsync(
                    chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, bytesRead);
            }
        }
        catch (IOException)
        {
            return null;
        }

        if (buffer.Length == 0 || buffer.Length > MaxInputBytes)
        {
            return null;
        }

        return Normalize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    private static CodexHookEvent? NormalizeObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? eventName = null;
        string? sessionId = null;
        string? turnId = null;
        string? toolName = null;
        string? toolUseId = null;
        var seenAllowedProperties = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!IsAllowedProperty(property.Name))
            {
                continue;
            }

            if (!seenAllowedProperties.Add(property.Name)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = property.Value.GetString();
            if (!IsValidField(value))
            {
                return null;
            }

            switch (property.Name)
            {
                case "hook_event_name":
                    eventName = value;
                    break;
                case "session_id":
                    sessionId = value;
                    break;
                case "turn_id":
                    turnId = value;
                    break;
                case "tool_name":
                    toolName = value;
                    break;
                case "tool_use_id":
                    toolUseId = value;
                    break;
            }
        }

        if (!TryParseKind(eventName, out CodexHookEventKind kind) || sessionId is null)
        {
            return null;
        }

        if (kind == CodexHookEventKind.SessionEnd)
        {
            return new CodexHookEvent(kind, sessionId);
        }

        if (turnId is null)
        {
            return null;
        }

        bool isToolEvent = kind is CodexHookEventKind.PreToolUse
            or CodexHookEventKind.PermissionRequest
            or CodexHookEventKind.PostToolUse;
        if (isToolEvent && toolName is null)
        {
            return null;
        }

        bool hasCorrelatableToolUse = kind is CodexHookEventKind.PreToolUse
            or CodexHookEventKind.PostToolUse;
        return new CodexHookEvent(
            kind,
            sessionId,
            turnId,
            isToolEvent ? toolName : null,
            hasCorrelatableToolUse ? toolUseId : null);
    }

    private static bool IsAllowedProperty(string propertyName)
    {
        return propertyName is "hook_event_name"
            or "session_id"
            or "turn_id"
            or "tool_name"
            or "tool_use_id";
    }

    private static bool IsValidField(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxIdentifierLength
            && !value.Any(char.IsControl);
    }

    private static bool TryParseKind(string? eventName, out CodexHookEventKind kind)
    {
        kind = eventName switch
        {
            "UserPromptSubmit" => CodexHookEventKind.UserPromptSubmit,
            "PreToolUse" => CodexHookEventKind.PreToolUse,
            "PermissionRequest" => CodexHookEventKind.PermissionRequest,
            "PostToolUse" => CodexHookEventKind.PostToolUse,
            "Stop" => CodexHookEventKind.Stop,
            "SessionEnd" => CodexHookEventKind.SessionEnd,
            _ => default,
        };

        return eventName is "UserPromptSubmit"
            or "PreToolUse"
            or "PermissionRequest"
            or "PostToolUse"
            or "Stop"
            or "SessionEnd";
    }
}
