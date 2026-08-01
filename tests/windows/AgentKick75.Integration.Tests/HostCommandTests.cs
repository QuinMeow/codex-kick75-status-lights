// SPDX-License-Identifier: MIT
using AgentKick75.App.Commands;
using AgentKick75.App.Hosting;
using AgentKick75.App.Infrastructure;

namespace AgentKick75.Integration.Tests;

public sealed class HostCommandTests
{
    [Theory]
    [InlineData("auto", HardwareTransportChoice.Auto)]
    [InlineData("usb", HardwareTransportChoice.Usb)]
    [InlineData("dongle", HardwareTransportChoice.Dongle)]
    public void Parse_HardwareTestTransport_IsReadyToRun(
        string transport,
        HardwareTransportChoice expected)
    {
        ParsedCommand parsed = CommandLine.Parse(
            ["hardware-test", "--transport", transport]);

        Assert.Equal(AppCommandKind.HardwareTest, parsed.Kind);
        Assert.Equal(expected, parsed.HardwareTest!.Transport);
    }

    [Fact]
    public void Parse_HardwareTestOptions_AcceptsCyclesAndDuration()
    {
        ParsedCommand parsed = CommandLine.Parse(
        [
            "hardware-test",
            "--transport",
            "usb",
            "--cycles",
            "20",
            "--green-seconds",
            "5",
        ]);

        Assert.Equal(AppCommandKind.HardwareTest, parsed.Kind);
        Assert.Equal(20, parsed.HardwareTest!.Cycles);
        Assert.Equal(TimeSpan.FromSeconds(5), parsed.HardwareTest.GreenDuration);
    }

    [Fact]
    public void SingleInstance_SameUserName_AllowsOnlyOneLease()
    {
        string name = $"Local\\AgentKick75.tests.{Guid.NewGuid():N}";

        Assert.True(SingleInstanceLease.TryAcquire(out SingleInstanceLease? first, name));
        using (first)
        {
            Assert.False(SingleInstanceLease.TryAcquire(out SingleInstanceLease? second, name));
            Assert.Null(second);
        }
    }

    [Fact]
    public void UserScope_DoesNotExposeRawIdentityInPipeName()
    {
        const string identity = "S-1-5-21-123456789-sensitive";

        string key = UserScope.CreateUserKey(identity);

        Assert.Equal(24, key.Length);
        Assert.DoesNotContain("S-1-5", key, StringComparison.Ordinal);
    }

}
