using NUnit.Framework;
using Robust.Client.Mafiastation.AiPilot;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class TrustedBridgeAuthorizationTests
{
    [TestCase("127.0.0.1:1212")]
    [TestCase("127.42.0.8:65535")]
    [TestCase("localhost:1212")]
    [TestCase("udp://localhost:1212")]
    [TestCase("udp://[::1]:1212")]
    public void AcceptsStrictLoopbackServerAddresses(string address)
    {
        Assert.That(AiPilotPipeBridge.IsLoopbackAddress(address), Is.True);
    }

    [TestCase("example.com:1212")]
    [TestCase("http://127.0.0.1:1212")]
    [TestCase("udp://127.0.0.1:1212/path")]
    [TestCase("udp://127.0.0.1:1212?host=example.com")]
    [TestCase("udp://127.0.0.1:1212@evil.example")]
    [TestCase("localhost:0")]
    [TestCase("localhost:not-a-port")]
    public void RejectsRemoteOrAmbiguousServerAddresses(string address)
    {
        Assert.That(AiPilotPipeBridge.IsLoopbackAddress(address), Is.False);
    }

    [Test]
    public void UsesFinalEffectiveRepeatedArgument()
    {
        var args = new[]
        {
            "client.dll",
            "--connect-address",
            "127.0.0.1:1212",
            "--connect-address",
            "example.com:1212",
        };

        Assert.That(
            AiPilotPipeBridge.TryGetLastArgument(args, "--connect-address", out var value),
            Is.True);
        Assert.That(value, Is.EqualTo("example.com:1212"));
        Assert.That(AiPilotPipeBridge.IsLoopbackAddress(value), Is.False);
    }

    [Test]
    public void UsesFinalEffectiveRepeatedCvar()
    {
        var args = new[]
        {
            "client.dll",
            "--cvar",
            "mafia.ai_pilot.client_enabled=true",
            "--cvar",
            "mafia.ai_pilot.client_enabled=false",
        };

        Assert.That(
            AiPilotPipeBridge.TryGetLastCvar(
                args,
                "mafia.ai_pilot.client_enabled",
                out var value),
            Is.True);
        Assert.That(value, Is.EqualTo("false"));
    }

    [TestCase("mafiastation-pilot-1", true)]
    [TestCase("pilot.with_safe_chars", true)]
    [TestCase("pilot/escape", false)]
    [TestCase("pilot\\escape", false)]
    [TestCase("", false)]
    public void ValidatesCurrentUserPipeNames(string pipeName, bool expected)
    {
        Assert.That(AiPilotPipeBridge.IsValidPipeName(pipeName), Is.EqualTo(expected));
    }
}
