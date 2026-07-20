using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class HybridPilotRunnerTests
{
    [Test]
    public async Task ActiveGoalRunsForWholeLeaseWithoutModelCall()
    {
        var policy = new FakePolicy((_, _, _) =>
            throw new AssertionException("Routine goal execution unexpectedly called the model."));
        var transport = new FakeTransport(request => request.Action switch
        {
            "observe" => Exchange(request, new
            {
                goal = new { state = "moving" },
                capabilities = new { canMove = true, canUseGoals = true },
            }),
            "goal_status" => Exchange(request, new { state = "moving" }),
            "stop" => Exchange(request, new { accepted = true }),
            _ => throw new AssertionException($"Unexpected action {request.Action}."),
        });
        var runner = new LlmAgentRunner(transport, policy);

        var summary = await runner.RunAsync(
            "routine",
            new LlmAgentOptions(
                "Walk to the marked work station.",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)));

        Assert.That(policy.Calls, Is.Zero);
        Assert.That(summary.ModelDecisions, Is.Zero);
        Assert.That(summary.RoutinePolls, Is.GreaterThanOrEqualTo(1));
        Assert.That(summary.Escalations, Is.Zero);
    }

    [Test]
    public async Task TerminalGoalEscalatesBackToModel()
    {
        var policy = new FakePolicy((_, _, _) =>
            Task.FromResult(new PilotPolicyDecision(
                "test",
                "test",
                """{"action":"stop","arguments":{}}""",
                PilotRequest.Create("stop"))));
        var transport = new FakeTransport(request => request.Action switch
        {
            "observe" => Exchange(request, new
            {
                goal = new { state = "failed", error = "blocked" },
                capabilities = new { canMove = true, canUseGoals = true },
            }),
            "stop" => Exchange(request, new { accepted = true }),
            _ => throw new AssertionException($"Unexpected action {request.Action}."),
        });
        var runner = new LlmAgentRunner(transport, policy);

        var summary = await runner.RunAsync(
            "complex recovery",
            new LlmAgentOptions(
                "Find another safe way around the obstruction.",
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1)));

        Assert.That(policy.Calls, Is.EqualTo(1));
        Assert.That(summary.ModelDecisions, Is.EqualTo(1));
        Assert.That(summary.Escalations, Is.EqualTo(1));
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

    private sealed class FakeTransport(
        Func<PilotRequest, PilotExchange> handler) : IPilotTransport
    {
        public string PipeName => "hybrid-test";

        public Task<PilotExchange> SendAsync(
            PilotRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakePolicy(
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
