using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class ClientLauncherTests
{
    [TestCase("127.0.0.1:1212")]
    [TestCase("localhost:1212")]
    [TestCase("ss14://localhost:1212")]
    [TestCase("[::1]:1212")]
    public void AcceptsLoopbackServerAddresses(string address)
    {
        Assert.That(ClientLauncher.IsLoopbackServerAddress(address), Is.True);
    }

    [TestCase("example.com:1212")]
    [TestCase("192.0.2.5:1212")]
    [TestCase("not an address")]
    public void RejectsNonLoopbackServerAddresses(string address)
    {
        Assert.That(ClientLauncher.IsLoopbackServerAddress(address), Is.False);
    }
}
