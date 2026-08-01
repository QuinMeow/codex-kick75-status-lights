// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.Core.Protocol;

namespace AgentKick75.Protocol.Tests;

public sealed class Kick75ProtocolCodecTests
{
    [Fact]
    public void BuildSessionRequest_GoldenChallenge_MatchesFixture()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement exchange = fixture.RootElement.GetProperty("sessionExchange");
        byte[] challenge = ParseHex(exchange.GetProperty("challengeHex"));
        byte[] expected = ParseHex(exchange.GetProperty("requestFrameHex"));

        Kick75SessionRequest request = Kick75ProtocolCodec.BuildSessionRequest(challenge);

        Assert.Equal((byte)0x5A, request.SessionKey);
        Assert.Equal(expected, request.Frame.ToArray());
    }

    [Fact]
    public void BuildSessionRequest_ZeroSelectedKey_UsesDocumentedFallback()
    {
        byte[] challenge = new byte[Kick75ProtocolCodec.MaximumPayloadLength];

        Kick75SessionRequest request = Kick75ProtocolCodec.BuildSessionRequest(challenge);
        byte[] frame = request.Frame.ToArray();

        Assert.Equal(Kick75ProtocolCodec.ZeroSessionKeyFallback, request.SessionKey);
        Assert.Equal(Kick75ProtocolCodec.ZeroSessionKeyFallback, frame[28]);
        Assert.Equal(Kick75ProtocolCodec.CalculateChecksum(frame), frame[3]);
    }

    [Theory]
    [InlineData(55)]
    [InlineData(57)]
    public void BuildSessionRequest_Non56ByteChallenge_Throws(int length)
    {
        byte[] challenge = new byte[length];

        Assert.Throws<ArgumentException>(
            () => Kick75ProtocolCodec.BuildSessionRequest(challenge));
    }

    [Fact]
    public void ValidateSessionResponse_GoldenResponse_ReturnsFixtureKey()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement exchange = fixture.RootElement.GetProperty("sessionExchange");
        Kick75SessionRequest request = Kick75ProtocolCodec.BuildSessionRequest(
            ParseHex(exchange.GetProperty("challengeHex")));
        byte[] response = ParseHex(exchange.GetProperty("responseFrameHex"));

        byte sessionKey = Kick75ProtocolCodec.ValidateSessionResponse(response, request);

        Assert.Equal((byte)0x5A, sessionKey);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("direction")]
    [InlineData("unknown-opcode")]
    [InlineData("wrong-allowed-opcode")]
    [InlineData("checksum")]
    public void ValidateSessionResponse_BadEnvelope_Throws(string mutation)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement exchange = fixture.RootElement.GetProperty("sessionExchange");
        Kick75SessionRequest request = Kick75ProtocolCodec.BuildSessionRequest(
            ParseHex(exchange.GetProperty("challengeHex")));
        byte[] response = ParseHex(exchange.GetProperty("responseFrameHex"));

        if (mutation == "short")
        {
            response = response[..^1];
        }
        else
        {
            switch (mutation)
            {
                case "direction":
                    response[0] = Kick75ProtocolCodec.HostDirection;
                    break;
                case "unknown-opcode":
                    response[1] = 0xF0;
                    break;
                case "wrong-allowed-opcode":
                    response[1] = (byte)Kick75ProtocolCommand.GetLightState;
                    break;
                case "checksum":
                    response[3] ^= 0x01;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown test mutation: {mutation}");
            }
        }

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSessionResponse(response, request));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("address-low")]
    [InlineData("address-high")]
    [InlineData("session")]
    public void ValidateSessionResponse_BadEncodedFieldWithValidChecksum_Throws(
        string mutation)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement exchange = fixture.RootElement.GetProperty("sessionExchange");
        Kick75SessionRequest request = Kick75ProtocolCodec.BuildSessionRequest(
            ParseHex(exchange.GetProperty("challengeHex")));
        byte[] response = ParseHex(exchange.GetProperty("responseFrameHex"));

        int offset = mutation switch
        {
            "length" => 4,
            "address-low" => 5,
            "address-high" => 6,
            "session" => 7,
            _ => throw new InvalidOperationException($"Unknown test mutation: {mutation}"),
        };
        response[offset] ^= 0x01;
        RefreshChecksum(response);

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSessionResponse(response, request));
    }

    [Fact]
    public void ValidateSessionResponse_ChallengePayloadMismatchWithValidChecksum_Throws()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement exchange = fixture.RootElement.GetProperty("sessionExchange");
        Kick75SessionRequest request = Kick75ProtocolCodec.BuildSessionRequest(
            ParseHex(exchange.GetProperty("challengeHex")));
        byte[] response = ParseHex(exchange.GetProperty("responseFrameHex"));
        response.AsSpan(Kick75ProtocolCodec.HeaderSize).Fill(0xC7);
        RefreshChecksum(response);

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSessionResponse(response, request));
    }

    [Fact]
    public void BuildGetBaseInfoRequest_FixtureSessionKey_MatchesGoldenFrame()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement report = FindByName(
            fixture.RootElement.GetProperty("reports"),
            "get-base-info");

        byte[] actual = Kick75ProtocolCodec.BuildGetBaseInfoRequest(0x5A);

        Assert.Equal(ParseHex(report.GetProperty("frameHex")), actual);
    }

    [Fact]
    public void DecodeGetBaseInfoResponse_GoldenResponse_ReturnsCurrentMode()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement response = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "get-base-info-mode-one");

        byte currentMode = Kick75ProtocolCodec.DecodeGetBaseInfoResponse(
            ParseHex(response.GetProperty("frameHex")),
            0x5A);

        Assert.Equal((byte)1, currentMode);
    }

    [Theory]
    [InlineData("direction", 0)]
    [InlineData("opcode", 1)]
    [InlineData("checksum", 3)]
    [InlineData("length", 4)]
    [InlineData("address-low", 5)]
    [InlineData("address-high", 6)]
    [InlineData("handle", 7)]
    public void DecodeGetBaseInfoResponse_InvalidField_Throws(
        string mutation,
        int offset)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "get-base-info-mode-one");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));
        response[offset] ^= 0x01;
        if (mutation is not "checksum")
        {
            RefreshChecksum(response);
        }

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.DecodeGetBaseInfoResponse(response, 0x5A));
    }

    [Fact]
    public void DecodeGetBaseInfoResponse_UnsupportedCurrentModeWithValidChecksum_Throws()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "get-base-info-mode-one");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));
        response[Kick75ProtocolCodec.HeaderSize] = 2 ^ 0x5A;
        RefreshChecksum(response);

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.DecodeGetBaseInfoResponse(response, 0x5A));
    }

    [Fact]
    public void BuildGetLightStateRequest_FixtureSessionKey_MatchesGoldenFrame()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement report = FindByName(
            fixture.RootElement.GetProperty("reports"),
            "get-light-state");

        byte[] actual = Kick75ProtocolCodec.BuildGetLightStateRequest(0x5A, 1);

        Assert.Equal(ParseHex(report.GetProperty("frameHex")), actual);
    }

    [Theory]
    [InlineData("set-side-green")]
    [InlineData("restore-baseline-example")]
    public void BuildSetSideLightRequest_FixtureState_MatchesGoldenFrame(string reportName)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement report = FindByName(
            fixture.RootElement.GetProperty("reports"),
            reportName);

        byte[] actual = Kick75ProtocolCodec.BuildSetSideLightRequest(
            0x5A,
            1,
            ParseHex(report.GetProperty("payloadHex")));

        Assert.Equal(ParseHex(report.GetProperty("frameHex")), actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void BuildSetSideLightRequest_Non8ByteState_Throws(int length)
    {
        byte[] sideState = new byte[length];

        Assert.Throws<ArgumentException>(
            () => Kick75ProtocolCodec.BuildSetSideLightRequest(0x5A, 1, sideState));
    }

    [Theory]
    [InlineData("set-side-brightness-100")]
    [InlineData("restore-side-brightness-zero")]
    public void BuildSetSideLightBrightnessRequest_FixtureValue_MatchesGoldenFrame(
        string reportName)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement report = FindByName(
            fixture.RootElement.GetProperty("reports"),
            reportName);
        byte brightness = Assert.Single(ParseHex(report.GetProperty("payloadHex")));

        byte[] actual = Kick75ProtocolCodec.BuildSetSideLightBrightnessRequest(
            0x5A,
            1,
            brightness);

        Assert.Equal(ParseHex(report.GetProperty("frameHex")), actual);
    }

    [Fact]
    public void DecodeGetLightStateResponse_GoldenResponse_ReturnsFullAndSideState()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement response = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "get-light-state-baseline-example");
        byte[] expected = ParseHex(response.GetProperty("payloadHex"));

        byte[] decoded = Kick75ProtocolCodec.DecodeGetLightStateResponse(
            ParseHex(response.GetProperty("frameHex")),
            0x5A,
            1);
        byte[] sideState = Kick75ProtocolCodec.ExtractSideLightState(decoded);

        Assert.Equal(expected, decoded);
        Assert.Equal(Convert.FromHexString("0064010100e9fffb"), sideState);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("direction")]
    [InlineData("unknown-opcode")]
    [InlineData("wrong-allowed-opcode")]
    [InlineData("checksum")]
    public void DecodeGetLightStateResponse_BadEnvelope_Throws(string mutation)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "get-light-state-baseline-example");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));

        if (mutation == "short")
        {
            response = response[..^1];
        }
        else
        {
            switch (mutation)
            {
                case "direction":
                    response[0] = Kick75ProtocolCodec.HostDirection;
                    break;
                case "unknown-opcode":
                    response[1] = 0xF0;
                    break;
                case "wrong-allowed-opcode":
                    response[1] = (byte)Kick75ProtocolCommand.SetLightState;
                    break;
                case "checksum":
                    response[3] ^= 0x01;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown test mutation: {mutation}");
            }
        }

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.DecodeGetLightStateResponse(response, 0x5A, 1));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("address-low")]
    [InlineData("address-high")]
    [InlineData("session")]
    public void DecodeGetLightStateResponse_BadEncodedFieldWithValidChecksum_Throws(
        string mutation)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "get-light-state-baseline-example");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));

        int offset = mutation switch
        {
            "length" => 4,
            "address-low" => 5,
            "address-high" => 6,
            "session" => 7,
            _ => throw new InvalidOperationException($"Unknown test mutation: {mutation}"),
        };
        response[offset] ^= 0x01;
        RefreshChecksum(response);

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.DecodeGetLightStateResponse(response, 0x5A, 1));
    }

    [Fact]
    public void ValidateSetSideLightResponse_GoldenAck_Succeeds()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement response = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-green-ack");

        Kick75ProtocolCodec.ValidateSetSideLightResponse(
            ParseHex(response.GetProperty("frameHex")),
            0x5A,
            1);
    }

    [Theory]
    [InlineData("direction", 0)]
    [InlineData("opcode", 1)]
    [InlineData("checksum", 3)]
    public void ValidateSetSideLightResponse_BadAck_Throws(string mutation, int offset)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-green-ack");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));

        response[offset] ^= 0x01;
        if (mutation is not "checksum")
        {
            RefreshChecksum(response);
        }

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightResponse(response, 0x5A, 1));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("address-low")]
    [InlineData("address-high")]
    [InlineData("session")]
    public void ValidateSetSideLightResponse_BadEncodedFieldWithValidChecksum_Throws(
        string mutation)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-green-ack");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));

        int offset = mutation switch
        {
            "length" => 4,
            "address-low" => 5,
            "address-high" => 6,
            "session" => 7,
            _ => throw new InvalidOperationException($"Unknown test mutation: {mutation}"),
        };
        response[offset] ^= 0x01;
        RefreshChecksum(response);

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightResponse(response, 0x5A, 1));
    }

    [Fact]
    public void ValidateSetSideLightResponse_PreviousSessionAck_Throws()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-green-ack");
        byte[] previousSessionAck = ParseHex(vector.GetProperty("frameHex"));

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightResponse(previousSessionAck, 0x6B, 1));
    }

    [Fact]
    public void ValidateSetSideLightResponse_UnknownPayloadWithValidChecksum_IsNotRejected()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-green-ack");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));
        response.AsSpan(Kick75ProtocolCodec.HeaderSize).Fill(0xC7);
        RefreshChecksum(response);

        Kick75ProtocolCodec.ValidateSetSideLightResponse(response, 0x5A, 1);
    }

    [Fact]
    public void ValidateSetSideLightBrightnessResponse_GoldenAck_Succeeds()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement response = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-brightness-ack");

        Kick75ProtocolCodec.ValidateSetSideLightBrightnessResponse(
            ParseHex(response.GetProperty("frameHex")),
            0x5A,
            1);
    }

    [Theory]
    [InlineData("direction", 0)]
    [InlineData("opcode", 1)]
    [InlineData("checksum", 3)]
    public void ValidateSetSideLightBrightnessResponse_BadEnvelope_Throws(
        string mutation,
        int offset)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-brightness-ack");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));
        response[offset] ^= 0x01;
        if (mutation is not "checksum")
        {
            RefreshChecksum(response);
        }

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightBrightnessResponse(response, 0x5A, 1));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("address-low")]
    [InlineData("address-high")]
    [InlineData("session")]
    public void ValidateSetSideLightBrightnessResponse_BadEncodedFieldWithValidChecksum_Throws(
        string mutation)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement vector = FindByName(
            fixture.RootElement.GetProperty("responses"),
            "set-side-brightness-ack");
        byte[] response = ParseHex(vector.GetProperty("frameHex"));
        int offset = mutation switch
        {
            "length" => 4,
            "address-low" => 5,
            "address-high" => 6,
            "session" => 7,
            _ => throw new InvalidOperationException($"Unknown test mutation: {mutation}"),
        };
        response[offset] ^= 0x01;
        RefreshChecksum(response);

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightBrightnessResponse(response, 0x5A, 1));
    }

    [Fact]
    public void SetSideLightValidators_OtherAllowedSliceAck_Throws()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement responses = fixture.RootElement.GetProperty("responses");
        byte[] blockAck = ParseHex(
            FindByName(responses, "set-side-green-ack").GetProperty("frameHex"));
        byte[] brightnessAck = ParseHex(
            FindByName(responses, "set-side-brightness-ack").GetProperty("frameHex"));

        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightBrightnessResponse(blockAck, 0x5A, 1));
        Assert.Throws<Kick75ProtocolException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightResponse(brightnessAck, 0x5A, 1));
    }

    [Fact]
    public void CalculateChecksum_GoldenReport_MatchesFixtureByte()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement report = FindByName(
            fixture.RootElement.GetProperty("reports"),
            "restore-baseline-example");
        byte[] frame = ParseHex(report.GetProperty("frameHex"));

        Assert.Equal(frame[3], Kick75ProtocolCodec.CalculateChecksum(frame));
    }

    [Theory]
    [InlineData(0xA0, true)]
    [InlineData(0xD5, true)]
    [InlineData(0xD6, true)]
    [InlineData(0xEE, true)]
    [InlineData(0xD2, false)]
    [InlineData(0x00, false)]
    [InlineData(0xFF, false)]
    public void IsAllowedOpcode_Value_ReturnsExpected(int opcode, bool expected)
    {
        Assert.Equal(expected, Kick75ProtocolCodec.IsAllowedOpcode((byte)opcode));
    }

    [Fact]
    public void PublicDataOperations_ZeroSessionKey_Throw()
    {
        byte[] sideState = new byte[Kick75ProtocolCodec.SideLightLength];
        byte[] response = new byte[Kick75ProtocolCodec.ReportSize];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.BuildGetBaseInfoRequest(0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.BuildGetLightStateRequest(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.BuildSetSideLightRequest(0, 1, sideState));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.BuildSetSideLightBrightnessRequest(0, 1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.DecodeGetBaseInfoResponse(response, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.DecodeGetLightStateResponse(response, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightResponse(response, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightBrightnessResponse(response, 0, 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void LightingOperations_UnsupportedCurrentMode_Throw(int currentMode)
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement responses = fixture.RootElement.GetProperty("responses");
        byte[] lightResponse = ParseHex(
            FindByName(responses, "get-light-state-baseline-example").GetProperty("frameHex"));
        byte[] blockAck = ParseHex(
            FindByName(responses, "set-side-green-ack").GetProperty("frameHex"));
        byte[] brightnessAck = ParseHex(
            FindByName(responses, "set-side-brightness-ack").GetProperty("frameHex"));
        byte[] sideState = new byte[Kick75ProtocolCodec.SideLightLength];
        byte mode = (byte)currentMode;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.BuildGetLightStateRequest(0x5A, mode));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.BuildSetSideLightRequest(0x5A, mode, sideState));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.BuildSetSideLightBrightnessRequest(0x5A, mode, 100));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.DecodeGetLightStateResponse(lightResponse, 0x5A, mode));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightResponse(blockAck, 0x5A, mode));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Kick75ProtocolCodec.ValidateSetSideLightBrightnessResponse(
                brightnessAck,
                0x5A,
                mode));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    public void ExtractSideLightState_Non17ByteState_Throws(int length)
    {
        Assert.Throws<ArgumentException>(
            () => Kick75ProtocolCodec.ExtractSideLightState(new byte[length]));
    }

    private static JsonDocument LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "protocol-v1.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement FindByName(JsonElement values, string name)
    {
        return values
            .EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == name);
    }

    private static byte[] ParseHex(JsonElement value)
    {
        string? text = value.GetString();
        ArgumentNullException.ThrowIfNull(text);
        return Convert.FromHexString(text);
    }

    private static void RefreshChecksum(Span<byte> frame)
    {
        frame[3] = Kick75ProtocolCodec.CalculateChecksum(frame);
    }
}
