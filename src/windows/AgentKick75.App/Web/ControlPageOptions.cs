// SPDX-License-Identifier: MIT
using System.Security.Cryptography;

namespace AgentKick75.App.Web;

public sealed class ControlPageOptions
{
    public const string TokenHeaderName = "X-AgentKick75-Token";
    public const string HookTokenHeaderName = "X-AgentKick75-Hook-Token";
    public const int TokenByteLength = 32;

    private ControlPageOptions(string writeToken, string hookToken)
    {
        if (!IsValidToken(writeToken))
        {
            throw new ArgumentException(
                "The control-page token must contain at least 32 base64url characters.",
                nameof(writeToken));
        }

        if (!IsValidToken(hookToken))
        {
            throw new ArgumentException(
                "The Hook token must contain at least 32 base64url characters.",
                nameof(hookToken));
        }

        WriteToken = writeToken;
        HookToken = hookToken;
    }

    /// <summary>
    /// Gets the current Host-instance secret sent only by the same-origin control
    /// page. The value is memory-only and must never be persisted or logged.
    /// </summary>
    public string WriteToken { get; }

    public string HookToken { get; }

    public TimeSpan PreviewDuration => TimeSpan.FromSeconds(3);

    public static ControlPageOptions CreateWithRandomToken()
    {
        return new ControlPageOptions(GenerateToken(), GenerateToken());
    }

    public static ControlPageOptions FromHostInstanceToken(string writeToken)
    {
        return new ControlPageOptions(writeToken, GenerateToken());
    }

    internal static bool IsValidToken(string? token) =>
        token is not null &&
        token.Length >= 32 &&
        token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenByteLength))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
