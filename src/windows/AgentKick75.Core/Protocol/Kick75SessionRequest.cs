// SPDX-License-Identifier: MIT
namespace AgentKick75.Core.Protocol;

/// <summary>
/// A key-exchange request and the session key selected by its challenge.
/// </summary>
public sealed class Kick75SessionRequest
{
    private readonly byte[] frame;

    internal Kick75SessionRequest(byte[] frame, byte sessionKey)
    {
        this.frame = frame;
        SessionKey = sessionKey;
    }

    /// <summary>
    /// Gets the exact 64-byte output report to send with report ID 0.
    /// </summary>
    public ReadOnlyMemory<byte> Frame => frame;

    /// <summary>
    /// Gets the non-zero key encoded by the challenge at report offset 28.
    /// </summary>
    public byte SessionKey { get; }
}
