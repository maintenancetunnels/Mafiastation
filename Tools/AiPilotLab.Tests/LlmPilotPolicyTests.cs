using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class LlmPilotPolicyTests
{
    [Test]
    public void SystemPromptDefinesAuthoritativeSelfContext()
    {
        var prompt = LlmPilotPolicy.BuildSystemPrompt(false);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("self object is authoritative current character state"));
            Assert.That(prompt, Does.Contain("null equipment item"));
            Assert.That(prompt, Does.Contain("appearance"));
            Assert.That(prompt, Does.Contain("hidden roles or objectives"));
        });
    }

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
    public void ContextualSpeechTriggerIgnoresElapsedSilenceAndDetectsEvents()
    {
        var firstTurn = PilotJsonTests.Response(new
        {
            speech = new { lastSpokeSecondsAgo = (int?) null, lastChannel = (string?) null },
        });
        var quiet = PilotJsonTests.Response(new
        {
            speech = new { lastSpokeSecondsAgo = 120, lastChannel = "radio" },
        });
        var conversation = PilotJsonTests.Response(new
        {
            recentSpeech = new[] { new { speaker = "Alex", text = "Cargo needs help.", channel = "radio" } },
        });
        var completed = PilotJsonTests.Response(new
        {
            goal = new { state = "completed" },
        });

        Assert.Multiple(() =>
        {
            Assert.That(LlmPilotPolicy.HasContextualSpeechTrigger(firstTurn), Is.False);
            Assert.That(LlmPilotPolicy.HasContextualSpeechTrigger(quiet), Is.False);
            Assert.That(LlmPilotPolicy.HasContextualSpeechTrigger(conversation), Is.True);
            Assert.That(LlmPilotPolicy.HasContextualSpeechTrigger(completed), Is.True);
        });
    }

    [Test]
    public async Task OpenAiCompatibleRequestsJsonObjectMode()
    {
        using var handler = new RecordingHandler(OpenAiResponse(
            """{"action":"observe","arguments":{}}"""));
        using var client = new HttpClient(handler);
        var decision = await Policy(client).DecideAsync("Do ordinary station work.", Observation());

        using var request = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Multiple(() =>
        {
            Assert.That(
                request.RootElement.GetProperty("response_format").GetProperty("type").GetString(),
                Is.EqualTo("json_object"));
            Assert.That(decision.Request.Action, Is.EqualTo("observe"));
            Assert.That(decision.RepairAttempts, Is.Zero);
        });
    }

    [Test]
    public async Task JsonObjectModeCanBeDisabledForLegacyEndpoint()
    {
        using var handler = new RecordingHandler(OpenAiResponse(
            """{"action":"observe","arguments":{}}"""));
        using var client = new HttpClient(handler);
        await Policy(client, useJsonObjectResponseFormat: false)
            .DecideAsync("Do ordinary station work.", Observation());

        using var request = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.That(request.RootElement.TryGetProperty("response_format", out _), Is.False);
    }

    [Test]
    public async Task ExtractsBalancedActionObjectFromProseWrapper()
    {
        using var handler = new RecordingHandler(OpenAiResponse(
            """
            Here is the requested action:
            ```json
            {"action":"observe","arguments":{"note":"brace } remains inside the string"}}
            ```
            Ready.
            """));
        using var client = new HttpClient(handler);
        var decision = await Policy(client).DecideAsync("Do ordinary station work.", Observation());

        Assert.Multiple(() =>
        {
            Assert.That(decision.Request.Action, Is.EqualTo("observe"));
            Assert.That(decision.RepairAttempts, Is.Zero);
            Assert.That(handler.RequestBodies, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task RepairsMalformedJsonOnce()
    {
        using var handler = new RecordingHandler(
            OpenAiResponse("I should look around before acting."),
            OpenAiResponse("""{"action":"observe","arguments":{}}"""));
        using var client = new HttpClient(handler);
        var decision = await Policy(client).DecideAsync("Do ordinary station work.", Observation());

        using var retry = JsonDocument.Parse(handler.RequestBodies[1]);
        var retryPrompt = retry.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Multiple(() =>
        {
            Assert.That(decision.Request.Action, Is.EqualTo("observe"));
            Assert.That(decision.RepairAttempts, Is.EqualTo(1));
            Assert.That(handler.RequestBodies, Has.Count.EqualTo(2));
            Assert.That(retryPrompt, Does.Contain("Correction:"));
            Assert.That(retryPrompt, Does.Contain("Emit ONLY one JSON object"));
        });
    }

    [Test]
    public async Task RepairsAllowlistFailureOnce()
    {
        using var handler = new RecordingHandler(
            OpenAiResponse("""{"action":"move_relative","arguments":{"x":1,"y":0}}"""),
            OpenAiResponse("""{"action":"observe","arguments":{}}"""));
        using var client = new HttpClient(handler);
        var decision = await Policy(client).DecideAsync("Do ordinary station work.", Observation());

        using var retry = JsonDocument.Parse(handler.RequestBodies[1]);
        var retryPrompt = retry.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Multiple(() =>
        {
            Assert.That(decision.Request.Action, Is.EqualTo("observe"));
            Assert.That(decision.RepairAttempts, Is.EqualTo(1));
            Assert.That(retryPrompt, Does.Contain("move_relative"));
            Assert.That(retryPrompt, Does.Contain("belong inside arguments of action goal"));
        });
    }

    [Test]
    public void RepairAttemptLimitIsBounded()
    {
        using var handler = new RecordingHandler(
            OpenAiResponse("not json"),
            OpenAiResponse("still not json"),
            OpenAiResponse("""{"action":"observe","arguments":{}}"""));
        using var client = new HttpClient(handler);

        Assert.That(
            async () => await Policy(client).DecideAsync("Do ordinary station work.", Observation()),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("single JSON action object"));
        Assert.That(handler.RequestBodies, Has.Count.EqualTo(2));
    }

    private static LlmPilotPolicy Policy(
        HttpClient client,
        bool useJsonObjectResponseFormat = true,
        int maximumDecisionRepairAttempts = 1) =>
        new(client, new LlmPilotPolicyOptions(
            "openai-compatible",
            new Uri("http://127.0.0.1:11434/v1/chat/completions"),
            "local-test",
            null,
            false,
            UseJsonObjectResponseFormat: useJsonObjectResponseFormat,
            MaximumDecisionRepairAttempts: maximumDecisionRepairAttempts));

    private static PilotResponse Observation() => PilotJsonTests.Response(new
    {
        attached = true,
        capabilities = new { canObserve = true },
    });

    private static string OpenAiResponse(string content) => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new { message = new { content } },
        },
    });

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0)
                throw new InvalidOperationException("No fake model response remains.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
