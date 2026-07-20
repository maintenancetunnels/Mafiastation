using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class LlmAgentRunnerTests
{
    [Test]
    public async Task PermanentAuthorizationDenialFailsBeforeModelCall()
    {
        var policy = new CountingPolicy((_, _, _) =>
            throw new AssertionException("Denied pilot unexpectedly called the model."));
        var transport = new ScriptedTransport(request => request.Action switch
        {
            "observe" => Exchange(request, new
            {
                authorized = false,
                capabilities = new { canMove = false, canSpeak = false },
            }),
            "status" => Exchange(request, new
            {
                authorized = false,
                authorizationReason = "AI pilot account is not allowlisted.",
            }),
            _ => throw new AssertionException($"Unexpected action {request.Action}."),
        });

        var summary = await new LlmAgentRunner(transport, policy).RunAsync(
            "denied",
            new LlmAgentOptions(
                "Report for duty.",
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1)));

        Assert.That(policy.Calls, Is.Zero);
        Assert.That(summary.Success, Is.False);
        Assert.That(summary.Errors, Is.EqualTo(1));
        Assert.That(summary.CompletionReason, Does.Contain("authorization denied"));
        Assert.That(transport.Actions, Is.EqualTo(new[] { "observe", "status" }));
    }

    [Test]
    public async Task PendingAuthorizationRefreshesObservationBeforeModelCall()
    {
        var observeCalls = 0;
        var statusCalls = 0;
        var policy = new CountingPolicy((_, observation, _) =>
        {
            Assert.That(observation.Data.GetProperty("authorized").GetBoolean(), Is.True);
            return Task.FromResult(new PilotPolicyDecision(
                "test",
                "test",
                """{"action":"stop","arguments":{}}""",
                PilotRequest.Create("stop")));
        });
        var transport = new ScriptedTransport(request => request.Action switch
        {
            "observe" => ++observeCalls == 1
                ? Exchange(request, new
                {
                    authorized = false,
                    capabilities = new { canMove = false, canSpeak = false },
                })
                : Exchange(request, new
                {
                    authorized = true,
                    goal = new { state = "none" },
                    capabilities = new { canMove = true, canSpeak = true },
                }),
            "status" => ++statusCalls == 1
                ? Exchange(request, new
                {
                    authorized = false,
                    authorizationReason = "Authorization has not been checked.",
                })
                : Exchange(request, new
                {
                    authorized = true,
                    authorizationReason = "",
                }),
            "stop" => Exchange(request, new { accepted = true }),
            _ => throw new AssertionException($"Unexpected action {request.Action}."),
        });

        var summary = await new LlmAgentRunner(transport, policy).RunAsync(
            "pending",
            new LlmAgentOptions(
                "Report for duty.",
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1)));

        Assert.That(policy.Calls, Is.EqualTo(1));
        Assert.That(statusCalls, Is.EqualTo(2));
        Assert.That(observeCalls, Is.EqualTo(2));
        Assert.That(summary.Success, Is.True);
        Assert.That(summary.CompletionReason, Is.EqualTo("model requested stop"));
    }

    private static PilotExchange Exchange(PilotRequest request, object data)
    {
        var response = new PilotResponse
        {
            Version = 1,
            Id = request.Id,
            Ok = true,
            Data = JsonSerializer.SerializeToElement(data),
        };
        return new PilotExchange(
            request,
            response,
            JsonSerializer.SerializeToElement(new
            {
                version = 1,
                id = request.Id,
                ok = true,
                data,
            }));
    }

    private sealed class ScriptedTransport(
        Func<PilotRequest, PilotExchange> handler) : IPilotTransport
    {
        public string PipeName => "authorization-test";
        public List<string> Actions { get; } = new();

        public Task<PilotExchange> SendAsync(
            PilotRequest request,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            return Task.FromResult(handler(request));
        }
    }

    private sealed class CountingPolicy(
        Func<string, PilotResponse, CancellationToken, Task<PilotPolicyDecision>> handler)
        : IPilotPolicy
    {
        public int Calls { get; private set; }

        public Task<PilotPolicyDecision> DecideAsync(
            string goal,
            PilotResponse observation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return handler(goal, observation, cancellationToken);
        }
    }
}
