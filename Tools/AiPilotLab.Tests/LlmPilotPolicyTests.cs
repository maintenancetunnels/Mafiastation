using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class LlmPilotPolicyTests
{
    [Test]
    public void AllowsUnauthenticatedHttpOnlyForLoopback()
    {
        using var client = new HttpClient();
        Assert.That(
            () => new LlmPilotPolicy(client, new LlmPilotPolicyOptions(
                "openai-compatible",
                new Uri("http://127.0.0.1:11434/v1/chat/completions"),
                "cheap-local",
                null,
                false)),
            Throws.Nothing);
        Assert.That(
            () => new LlmPilotPolicy(client, new LlmPilotPolicyOptions(
                "openai-compatible",
                new Uri("http://example.invalid/v1/chat/completions"),
                "cheap-remote",
                null,
                false)),
            Throws.ArgumentException);
    }

    [Test]
    public void RequiresEnvironmentStyleKeyForRemoteHttps()
    {
        using var client = new HttpClient();
        Assert.That(
            () => new LlmPilotPolicy(client, new LlmPilotPolicyOptions(
                "openai-compatible",
                new Uri("https://example.invalid/v1/chat/completions"),
                "cheap-remote",
                null,
                false)),
            Throws.ArgumentException.With.Message.Contains("API key"));
    }

    [Test]
    public void ChatterCueBecomesDueAfterQuietPeriod()
    {
        var firstTurn = PilotJsonTests.Response(new
        {
            speech = new { lastSpokeSecondsAgo = (int?) null, lastChannel = (string?) null },
        });
        var recent = PilotJsonTests.Response(new
        {
            speech = new { lastSpokeSecondsAgo = 12, lastChannel = "local" },
        });
        var quiet = PilotJsonTests.Response(new
        {
            speech = new { lastSpokeSecondsAgo = 45, lastChannel = "radio" },
        });

        Assert.Multiple(() =>
        {
            Assert.That(LlmPilotPolicy.IsChatterDue(firstTurn), Is.True);
            Assert.That(LlmPilotPolicy.IsChatterDue(recent), Is.False);
            Assert.That(LlmPilotPolicy.IsChatterDue(quiet), Is.True);
        });
    }
}
