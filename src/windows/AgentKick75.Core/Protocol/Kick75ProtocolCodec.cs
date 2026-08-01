// SPDX-License-Identifier: MIT
using System.Security.Cryptography;

namespace AgentKick75.Core.Protocol;

/// <summary>
/// Builds and validates the allowlisted 64-byte Kick75 lighting reports.
/// </summary>
public static class Kick75ProtocolCodec
{
    public const int ReportSize = 64;
    public const int HeaderSize = 8;
    public const int MaximumPayloadLength = ReportSize - HeaderSize;
    public const int BaseInfoLength = 8;
    public const int LightStateLength = 17;
    public const int SideLightAddress = 9;
    public const int SideLightLength = 8;
    public const int SideLightOffsetInLightState = 9;
    public const int SideLightBrightnessOffset = 1;
    public const int SideLightBrightnessAddress = SideLightAddress + SideLightBrightnessOffset;
    public const int SideLightBrightnessLength = 1;

    public const byte HostDirection = 0x55;
    public const byte DeviceDirection = 0xAA;
    public const byte ZeroSessionKeyFallback = 0xAA;

    private const int SessionKeyReportOffset = 28;
    private const int SessionKeyChallengeOffset = SessionKeyReportOffset - HeaderSize;

    /// <summary>
    /// Creates a key-exchange report using cryptographically strong random challenge bytes.
    /// </summary>
    public static Kick75SessionRequest BuildSessionRequest()
    {
        Span<byte> challenge = stackalloc byte[MaximumPayloadLength];
        RandomNumberGenerator.Fill(challenge);
        return BuildSessionRequest(challenge);
    }

    /// <summary>
    /// Creates a deterministic key-exchange report from exactly 56 challenge bytes.
    /// </summary>
    public static Kick75SessionRequest BuildSessionRequest(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != MaximumPayloadLength)
        {
            throw new ArgumentException(
                $"The session challenge must contain exactly {MaximumPayloadLength} bytes.",
                nameof(challenge));
        }

        byte[] challengeCopy = challenge.ToArray();
        byte sessionKey = challengeCopy[SessionKeyChallengeOffset];
        if (sessionKey == 0)
        {
            sessionKey = ZeroSessionKeyFallback;
            challengeCopy[SessionKeyChallengeOffset] = sessionKey;
        }

        byte[] frame = CreateEmptyRequest(Kick75ProtocolCommand.SetSecretKey);
        challengeCopy.CopyTo(frame, HeaderSize);
        SetChecksum(frame);
        return new Kick75SessionRequest(frame, sessionKey);
    }

    /// <summary>
    /// Validates the complete key-exchange challenge response.
    /// </summary>
    public static byte ValidateSessionResponse(
        ReadOnlySpan<byte> response,
        Kick75SessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateResponseEnvelope(response, Kick75ProtocolCommand.SetSecretKey);
        ValidateEncodedFields(
            response,
            request.SessionKey,
            expectedLength: 0,
            expectedAddress: 0,
            expectedCurrentMode: 0);

        ReadOnlySpan<byte> requestFrame = request.Frame.Span;
        for (int index = HeaderSize; index < ReportSize; index++)
        {
            if ((response[index] ^ requestFrame[index]) != request.SessionKey)
            {
                throw new Kick75ProtocolException(
                    $"The key-exchange response payload does not match the challenge at payload offset {index - HeaderSize}.");
            }
        }

        return request.SessionKey;
    }

    /// <summary>
    /// Builds the official read-only base-info request used to discover the
    /// current mode handle: address 0, length 8, handle 0.
    /// </summary>
    public static byte[] BuildGetBaseInfoRequest(byte sessionKey)
    {
        ValidateSessionKey(sessionKey);
        return BuildDataRequest(
            Kick75ProtocolCommand.GetBaseInfo,
            sessionKey,
            BaseInfoLength,
            address: 0,
            currentMode: 0,
            ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Decodes the current mode handle from byte zero of an eight-byte A0 response.
    /// Only the official values 0 and 1 are accepted.
    /// </summary>
    public static byte DecodeGetBaseInfoResponse(
        ReadOnlySpan<byte> response,
        byte sessionKey)
    {
        ValidateSessionKey(sessionKey);
        ValidateResponseEnvelope(response, Kick75ProtocolCommand.GetBaseInfo);
        ValidateEncodedFields(
            response,
            sessionKey,
            expectedLength: BaseInfoLength,
            expectedAddress: 0,
            expectedCurrentMode: 0);

        byte currentMode = (byte)(response[HeaderSize] ^ sessionKey);
        if (currentMode > 1)
        {
            throw new Kick75ProtocolException(
                $"The decoded A0 current-mode handle is {currentMode}; expected 0 or 1.");
        }

        return currentMode;
    }

    /// <summary>
    /// Builds the only allowlisted light-state read: address 0, length 17.
    /// </summary>
    public static byte[] BuildGetLightStateRequest(
        byte sessionKey,
        byte currentMode)
    {
        ValidateSessionKey(sessionKey);
        ValidateCurrentMode(currentMode);
        return BuildDataRequest(
            Kick75ProtocolCommand.GetLightState,
            sessionKey,
            LightStateLength,
            address: 0,
            currentMode,
            ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds the only allowlisted light-state write: side-light address 9, length 8.
    /// </summary>
    public static byte[] BuildSetSideLightRequest(
        byte sessionKey,
        byte currentMode,
        ReadOnlySpan<byte> sideLightState)
    {
        ValidateSessionKey(sessionKey);
        ValidateCurrentMode(currentMode);
        if (sideLightState.Length != SideLightLength)
        {
            throw new ArgumentException(
                $"The side-light state must contain exactly {SideLightLength} bytes.",
                nameof(sideLightState));
        }

        return BuildDataRequest(
            Kick75ProtocolCommand.SetLightState,
            sessionKey,
            SideLightLength,
            SideLightAddress,
            currentMode,
            sideLightState);
    }

    /// <summary>
    /// Builds the narrowly allowlisted brightness refresh used by the pinned USB
    /// hardware-test sequence: side-light address 10, length 1.
    /// </summary>
    public static byte[] BuildSetSideLightBrightnessRequest(
        byte sessionKey,
        byte currentMode,
        byte brightness)
    {
        ValidateSessionKey(sessionKey);
        ValidateCurrentMode(currentMode);
        Span<byte> payload = stackalloc byte[SideLightBrightnessLength];
        payload[0] = brightness;
        return BuildDataRequest(
            Kick75ProtocolCommand.SetLightState,
            sessionKey,
            SideLightBrightnessLength,
            SideLightBrightnessAddress,
            currentMode,
            payload);
    }

    /// <summary>
    /// Validates and decodes a complete 17-byte light-state response.
    /// </summary>
    public static byte[] DecodeGetLightStateResponse(
        ReadOnlySpan<byte> response,
        byte sessionKey,
        byte currentMode)
    {
        ValidateSessionKey(sessionKey);
        ValidateCurrentMode(currentMode);
        ValidateResponseEnvelope(response, Kick75ProtocolCommand.GetLightState);
        ValidateEncodedFields(
            response,
            sessionKey,
            expectedLength: LightStateLength,
            expectedAddress: 0,
            expectedCurrentMode: currentMode);

        byte[] lightState = new byte[LightStateLength];
        XorCopy(response.Slice(HeaderSize, LightStateLength), lightState, sessionKey);
        return lightState;
    }

    /// <summary>
    /// Copies the eight side-light bytes from a decoded 17-byte light state.
    /// </summary>
    public static byte[] ExtractSideLightState(ReadOnlySpan<byte> lightState)
    {
        if (lightState.Length != LightStateLength)
        {
            throw new ArgumentException(
                $"The light state must contain exactly {LightStateLength} bytes.",
                nameof(lightState));
        }

        return lightState.Slice(SideLightOffsetInLightState, SideLightLength).ToArray();
    }

    /// <summary>
    /// Validates the documented D6 acknowledgement envelope and encoded header fields.
    /// </summary>
    public static void ValidateSetSideLightResponse(
        ReadOnlySpan<byte> response,
        byte sessionKey,
        byte currentMode)
    {
        ValidateSessionKey(sessionKey);
        ValidateCurrentMode(currentMode);
        ValidateResponseEnvelope(response, Kick75ProtocolCommand.SetLightState);
        ValidateEncodedFields(
            response,
            sessionKey,
            expectedLength: SideLightLength,
            expectedAddress: SideLightAddress,
            expectedCurrentMode: currentMode);
    }

    /// <summary>
    /// Validates the dedicated address-10, length-1 brightness acknowledgement.
    /// </summary>
    public static void ValidateSetSideLightBrightnessResponse(
        ReadOnlySpan<byte> response,
        byte sessionKey,
        byte currentMode)
    {
        ValidateSessionKey(sessionKey);
        ValidateCurrentMode(currentMode);
        ValidateResponseEnvelope(response, Kick75ProtocolCommand.SetLightState);
        ValidateEncodedFields(
            response,
            sessionKey,
            expectedLength: SideLightBrightnessLength,
            expectedAddress: SideLightBrightnessAddress,
            expectedCurrentMode: currentMode);
    }

    /// <summary>
    /// Calculates the wrapping checksum over report bytes 4 through 63.
    /// </summary>
    public static byte CalculateChecksum(ReadOnlySpan<byte> frame)
    {
        ValidateReportLength(frame);

        byte checksum = 0;
        for (int index = 4; index < frame.Length; index++)
        {
            checksum = unchecked((byte)(checksum + frame[index]));
        }

        return checksum;
    }

    /// <summary>
    /// Returns whether an opcode is within the Windows MVP protocol allowlist.
    /// </summary>
    public static bool IsAllowedOpcode(byte opcode)
    {
        return opcode is
            (byte)Kick75ProtocolCommand.GetBaseInfo or
            (byte)Kick75ProtocolCommand.SetSecretKey or
            (byte)Kick75ProtocolCommand.GetLightState or
            (byte)Kick75ProtocolCommand.SetLightState;
    }

    private static byte[] BuildDataRequest(
        Kick75ProtocolCommand command,
        byte sessionKey,
        int logicalLength,
        ushort address,
        byte currentMode,
        ReadOnlySpan<byte> payload)
    {
        byte[] frame = CreateEmptyRequest(command);
        frame[4] = (byte)(logicalLength ^ sessionKey);
        frame[5] = (byte)((address & 0xFF) ^ sessionKey);
        frame[6] = (byte)((address >> 8) ^ sessionKey);
        // Byte 7 is the A0-discovered current-mode handle XOR the session key.
        // It is not a side-light mode flag or a transfer-window size.
        frame[7] = (byte)(currentMode ^ sessionKey);
        XorCopy(payload, frame.AsSpan(HeaderSize), sessionKey);
        SetChecksum(frame);
        return frame;
    }

    private static byte[] CreateEmptyRequest(Kick75ProtocolCommand command)
    {
        if (!IsAllowedOpcode((byte)command))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "The opcode is not allowed.");
        }

        byte[] frame = new byte[ReportSize];
        frame[0] = HostDirection;
        frame[1] = (byte)command;
        return frame;
    }

    private static void ValidateResponseEnvelope(
        ReadOnlySpan<byte> response,
        Kick75ProtocolCommand expectedCommand)
    {
        ValidateResponseHeader(response, expectedCommand);

        byte checksum = CalculateChecksum(response);
        if (response[3] != checksum)
        {
            throw new Kick75ProtocolException(
                $"The response checksum is 0x{response[3]:X2}; expected 0x{checksum:X2}.");
        }
    }

    private static void ValidateResponseHeader(
        ReadOnlySpan<byte> response,
        Kick75ProtocolCommand expectedCommand)
    {
        ValidateReportLength(response);
        if (response[0] != DeviceDirection)
        {
            throw new Kick75ProtocolException(
                $"The device response direction must be 0x{DeviceDirection:X2}.");
        }

        if (!IsAllowedOpcode(response[1]))
        {
            throw new Kick75ProtocolException(
                $"The device response opcode 0x{response[1]:X2} is not allowed.");
        }

        if (response[1] != (byte)expectedCommand)
        {
            throw new Kick75ProtocolException(
                $"Expected opcode 0x{(byte)expectedCommand:X2}, but received 0x{response[1]:X2}.");
        }
    }

    private static void ValidateEncodedFields(
        ReadOnlySpan<byte> response,
        byte sessionKey,
        int expectedLength,
        ushort expectedAddress,
        byte expectedCurrentMode)
    {
        int decodedLength = response[4] ^ sessionKey;
        if (decodedLength != expectedLength)
        {
            throw new Kick75ProtocolException(
                $"The decoded response length is {decodedLength}; expected {expectedLength}.");
        }

        ushort decodedAddress = (ushort)(
            (response[5] ^ sessionKey) |
            ((response[6] ^ sessionKey) << 8));
        if (decodedAddress != expectedAddress)
        {
            throw new Kick75ProtocolException(
                $"The decoded response address is {decodedAddress}; expected {expectedAddress}.");
        }

        byte decodedCurrentMode = (byte)(response[7] ^ sessionKey);
        if (decodedCurrentMode != expectedCurrentMode)
        {
            throw new Kick75ProtocolException(
                $"The decoded response current-mode handle is {decodedCurrentMode}; expected {expectedCurrentMode}.");
        }
    }

    private static void ValidateCurrentMode(byte currentMode)
    {
        if (currentMode > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentMode),
                currentMode,
                "The current-mode handle must be 0 or 1.");
        }
    }

    private static void ValidateSessionKey(byte sessionKey)
    {
        if (sessionKey == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionKey),
                sessionKey,
                "The session key must be non-zero.");
        }
    }

    private static void ValidateReportLength(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != ReportSize)
        {
            throw new Kick75ProtocolException(
                $"A Kick75 protocol report must contain exactly {ReportSize} bytes; received {frame.Length}.");
        }
    }

    private static void SetChecksum(Span<byte> frame)
    {
        frame[3] = CalculateChecksum(frame);
    }

    private static void XorCopy(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        byte sessionKey)
    {
        for (int index = 0; index < source.Length; index++)
        {
            destination[index] = (byte)(source[index] ^ sessionKey);
        }
    }
}
