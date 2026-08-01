// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace AgentKick75.Protocol.Tests;

public sealed class ProtocolFixtureTests
{
    private const string AdoptedCommit = "e32648ee86a8a729734060ac09bd7f8a1213876f";
    private static readonly byte[] AllowedDirections = { 0x55, 0xAA };
    private static readonly byte[] AllowedCommands = { 0xEE, 0xA0, 0xD5, 0xD6 };

    [Fact]
    public void Fixture_Provenance_IsPinnedAndExplicitlySourceDerived()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement root = fixture.RootElement;
        JsonElement source = root.GetProperty("source");
        JsonElement officialEvidence = root.GetProperty("officialNuPhyIoEvidence");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("source-derived", root.GetProperty("reportClassification").GetString());
        Assert.Equal(
            "deterministic-derived-not-hardware-captured",
            root.GetProperty("responseVectorClassification").GetString());
        Assert.False(root.GetProperty("capturedDuringM0").GetBoolean());
        Assert.Equal(AdoptedCommit, source.GetProperty("commit").GetString());
        Assert.Equal("v0.2.0", source.GetProperty("tag").GetString());
        Assert.Equal(
            new[]
            {
                "src/kick75_ledctl.c",
                "src/codex_kick75_common.py",
                "docs/PROTOCOL.md",
            },
            source.GetProperty("pathsAtPinnedCommit")
                .EnumerateArray()
                .Select(path => path.GetString()!)
                .ToArray());
        Assert.Equal(
            "https://drive.nuphy.io/static/js/main.f6f60294.js",
            officialEvidence.GetProperty("mainBundleUrl").GetString());
        Assert.Equal(
            "https://drive.nuphy.io/static/js/686.189b2dd0.chunk.js",
            officialEvidence.GetProperty("lightingChunkUrl").GetString());
        Assert.Equal("1026", officialEvidence.GetProperty("keyboardApiProductIdHex").GetString());
        Assert.Equal("2620", officialEvidence.GetProperty("u1CapabilityProductIdHex").GetString());
        Assert.Equal("A0", officialEvidence.GetProperty("getBaseInfoCommandHex").GetString());
        Assert.Equal(
            "address 0 length 8 handle 0",
            officialEvidence.GetProperty("getBaseInfoRequest").GetString());
        Assert.Equal(
            "decoded response payload byte 0; allowed values 0 or 1",
            officialEvidence.GetProperty("currentModeSource").GetString());
        Assert.Equal(
            "report byte 7 = currentMode XOR session key",
            officialEvidence.GetProperty("lightingHandleEncoding").GetString());
        Assert.Equal("4.0.18", officialEvidence.GetProperty("firmwareVersionChecked").GetString());
        Assert.False(officialEvidence.GetProperty("firmwareVersionBranchObserved").GetBoolean());
    }

    [Fact]
    public void Fixture_Reports_AreAllowedAndInternallyConsistent()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement root = fixture.RootElement;
        byte sessionKey = ParseByte(root.GetProperty("sessionKeyHex").GetString());
        JsonElement reports = root.GetProperty("reports");
        Dictionary<string, (byte Direction, byte Command, int Length, int Address, byte CurrentMode)>
            expectedReports = new(StringComparer.Ordinal)
            {
                ["get-base-info"] = (0x55, 0xA0, 8, 0, 0),
                ["get-light-state"] = (0x55, 0xD5, 17, 0, 1),
                ["set-side-green"] = (0x55, 0xD6, 8, 9, 1),
                ["restore-baseline-example"] = (0x55, 0xD6, 8, 9, 1),
                ["set-side-brightness-100"] = (0x55, 0xD6, 1, 10, 1),
                ["restore-side-brightness-zero"] = (0x55, 0xD6, 1, 10, 1),
            };

        Assert.Equal(
            AllowedDirections,
            root.GetProperty("allowedDirections")
                .EnumerateArray()
                .Select(value => ParseByte(value.GetString()))
                .ToArray());
        Assert.Equal(
            AllowedCommands,
            root.GetProperty("allowedCommands")
                .EnumerateArray()
                .Select(value => ParseByte(value.GetString()))
                .ToArray());
        Assert.Equal(expectedReports.Count, reports.GetArrayLength());

        foreach (JsonElement report in reports.EnumerateArray())
        {
            string name = report.GetProperty("name").GetString()!;
            byte[] frame = ParseHex(report.GetProperty("frameHex").GetString());
            int logicalLength = report.GetProperty("logicalLength").GetInt32();
            int address = report.GetProperty("address").GetInt32();
            byte currentMode = report.GetProperty("currentMode").GetByte();
            Assert.True(
                expectedReports.TryGetValue(name, out var expected),
                $"Unexpected protocol report: {name}");

            Assert.Equal(64, frame.Length);
            Assert.Contains(frame[0], AllowedDirections);
            Assert.Contains(frame[1], AllowedCommands);
            Assert.Equal(expected.Direction, frame[0]);
            Assert.Equal(expected.Command, frame[1]);
            Assert.Equal(expected.Length, logicalLength);
            Assert.Equal(expected.Address, address);
            Assert.Equal(expected.CurrentMode, currentMode);
            Assert.Equal(ParseByte(report.GetProperty("directionHex").GetString()), frame[0]);
            Assert.Equal(ParseByte(report.GetProperty("commandHex").GetString()), frame[1]);
            Assert.Equal(0, frame[2]);
            Assert.Equal(Checksum(frame), frame[3]);
            Assert.Equal((byte)(logicalLength ^ sessionKey), frame[4]);
            Assert.Equal(address, DecodeAddress(frame, sessionKey));
            Assert.Equal(currentMode, frame[7] ^ sessionKey);

            byte[] payload = ParseHex(report.GetProperty("payloadHex").GetString());
            if (payload.Length > 0)
            {
                Assert.Equal(logicalLength, payload.Length);
                Assert.Equal(payload, DecodePayload(frame, payload.Length, sessionKey));
            }
        }
    }

    [Fact]
    public void Fixture_SetLightReports_TargetOnlyTheTwoAllowlistedSideLightSlices()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement root = fixture.RootElement;
        JsonElement reports = root.GetProperty("reports");
        (int Address, int Length)[] allowedSlices = root
            .GetProperty("allowedSetLightSlices")
            .EnumerateArray()
            .Select(slice => (
                slice.GetProperty("address").GetInt32(),
                slice.GetProperty("logicalLength").GetInt32()))
            .ToArray();
        int setLightCount = 0;

        Assert.Equal(new[] { (9, 8), (10, 1) }, allowedSlices);

        foreach (JsonElement report in reports.EnumerateArray())
        {
            if (report.GetProperty("commandHex").GetString() != "D6")
            {
                continue;
            }

            setLightCount++;
            var slice = (
                report.GetProperty("address").GetInt32(),
                report.GetProperty("logicalLength").GetInt32());
            Assert.Contains(slice, allowedSlices);
        }

        Assert.Equal(4, setLightCount);
    }

    [Fact]
    public void Fixture_SideStates_MatchPinnedBehaviorVectors()
    {
        using JsonDocument fixture = LoadFixture();
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["settings-custom"] = "022a010000123abc",
            ["baseline-example"] = "0064010100e9fffb",
            ["running-default"] = "0264010000ffb400",
            ["permission-default"] = "0264010000ff0000",
            ["failure-default"] = "0264010000ff0000",
            ["completed-default"] = "026401000000ff00",
        };

        Dictionary<string, string> actual = fixture.RootElement
            .GetProperty("sideStates")
            .EnumerateArray()
            .ToDictionary(
                state => state.GetProperty("name").GetString()!,
                state => state.GetProperty("hex").GetString()!,
                StringComparer.Ordinal);

        Assert.Equal(expected, actual);
        Assert.All(actual.Values, value => Assert.Equal(8, ParseHex(value).Length));

        JsonElement baselineExample = fixture.RootElement
            .GetProperty("sideStates")
            .EnumerateArray()
            .Single(state => state.GetProperty("name").GetString() == "baseline-example");
        Assert.Equal(
            "upstream-documented-device-read-example",
            baselineExample.GetProperty("origin").GetString());
        Assert.False(baselineExample.GetProperty("universalDefault").GetBoolean());
    }

    private static JsonDocument LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "protocol-v1.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static byte ParseByte(string? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToByte(value, 16);
    }

    private static byte[] ParseHex(string? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.FromHexString(value);
    }

    private static byte Checksum(ReadOnlySpan<byte> frame)
    {
        int sum = 0;
        for (int index = 4; index < frame.Length; index++)
        {
            sum += frame[index];
        }

        return (byte)sum;
    }

    private static int DecodeAddress(ReadOnlySpan<byte> frame, byte sessionKey)
    {
        int low = frame[5] ^ sessionKey;
        int high = frame[6] ^ sessionKey;
        return low | (high << 8);
    }

    private static byte[] DecodePayload(
        ReadOnlySpan<byte> frame,
        int payloadLength,
        byte sessionKey)
    {
        byte[] payload = new byte[payloadLength];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(frame[index + 8] ^ sessionKey);
        }

        return payload;
    }
}
