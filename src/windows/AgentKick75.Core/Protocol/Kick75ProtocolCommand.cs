// SPDX-License-Identifier: MIT
namespace AgentKick75.Core.Protocol;

/// <summary>
/// Commands that the Windows MVP is allowed to send to a Kick75 device.
/// </summary>
public enum Kick75ProtocolCommand : byte
{
    GetBaseInfo = 0xA0,
    GetLightState = 0xD5,
    SetLightState = 0xD6,
    SetSecretKey = 0xEE,
}
