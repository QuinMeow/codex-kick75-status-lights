// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AgentKick75.App.Infrastructure;

public static class UserScope
{
    private const string ProductName = "AgentKick75";

    public static string CurrentUserKey => CreateUserKey(GetCurrentIdentity());

    public static string PipeName => $"{ProductName}.v1.{CurrentUserKey}";

    public static string MutexName => $"Local\\{ProductName}.Host.{CurrentUserKey}";

    public static string CreateUserKey(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string GetCurrentIdentity()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            return sid;
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }
}
