// SPDX-License-Identifier: MIT
namespace AgentKick75.Core.Protocol;

/// <summary>
/// Indicates that a device report violated the allowlisted Kick75 protocol.
/// </summary>
public sealed class Kick75ProtocolException : Exception
{
    public Kick75ProtocolException()
        : base("The Kick75 protocol report is invalid.")
    {
    }

    public Kick75ProtocolException(string message)
        : base(message)
    {
    }

    public Kick75ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
