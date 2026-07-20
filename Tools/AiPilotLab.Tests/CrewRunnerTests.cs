using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class CrewRunnerTests
{
    [Test]
    public async Task JoinsRequestedJobBeforeStartingRolePolicy()
    {
        string? requestedJob = null;
        string? standingGoal = null;
        var transport = new FakeTransport(request => request.Action switch
        {
            "join" => Join(request, requestedJob = request.Arguments.GetProperty("job").GetString()),
            "observe" => Exchange(request, new
            {
                attached = true,
                goal = new { state = "none" },
                capabilities = new { canMove = true, canUseGoals = true },
                entities = Array.Empty<object>(),
                recentSpeech = Array.Empty<object>(),
            }),
            "stop" => Exchange(request, new { accepted = true }),
            _ => throw new AssertionException($"Unexpected action {request.Action}."),
        });
        var policy = new FakePolicy((goal, _, _) =>
        {
            standingGoal = goal;
            return Task.FromResult(new PilotPolicyDecision(
                "test",
                "test",
                """{"action":"stop","arguments":{}}""",
                PilotRequest.Create("stop")));
        });
        var roster = Roster();
        var runner = new CrewRunner(_ => transport, _ => policy);

        var summary = await runner.RunAsync(roster);

        Assert.Multiple(() =>
        {
            Assert.That(summary.Success, Is.True);
            Assert.That(summary.Agents[0].Joined, Is.True);
            Assert.That(summary.Agents[0].Run?.ModelDecisions, Is.EqualTo(1));
            Assert.That(requestedJob, Is.EqualTo("Janitor"));
            Assert.That(standingGoal, Does.Contain("normal janitor"));
            Assert.That(standingGoal, Does.Contain("no access to hidden roles"));
        });
    }

    [Test]
    public async Task FailsClosedWhenServerAssignsDifferentJob()
    {
        var transport = new FakeTransport(request => request.Action switch
        {
            "join" => Exchange(request, new { accepted = true, assignedJob = "Passenger" }),
            "stop" => Exchange(request, new { accepted = true }),
            _ => throw new AssertionException($"Unexpected action {request.Action}."),
        });
        var policy = new FakePolicy((_, _, _) =>
            throw new AssertionException("Policy must not run after a mismatched job assignment."));
        var runner = new CrewRunner(_ => transport, _ => policy);

        var summary = await runner.RunAsync(Roster());

        Assert.Multiple(() =>
        {
            Assert.That(summary.Success, Is.False);
            Assert.That(summary.Agents[0].Joined, Is.False);
            Assert.That(summary.Agents[0].SetupError, Does.Contain("unexpected job"));
            Assert.That(policy.Calls, Is.Zero);
        });
    }

    [Test]
    public async Task FailsClosedWhenServerDoesNotVerifyAssignedJob()
    {
        var transport = new FakeTransport(request => request.Action switch
        {
            "join" => Exchange(request, new { accepted = true }),
            "stop" => Exchange(request, new { accepted = true }),
            _ => throw new AssertionException($"Unexpected action {request.Action}."),
        });
        var policy = new FakePolicy((_, _, _) =>
            throw new AssertionException("Policy must not run without a verified job assignment."));
        var runner = new CrewRunner(_ => transport, _ => policy);

        var summary = await runner.RunAsync(Roster());

        Assert.Multiple(() =>
        {
            Assert.That(summary.Success, Is.False);
            Assert.That(summary.Agents[0].Joined, Is.False);
            Assert.That(summary.Agents[0].SetupError, Does.Contain("did not verify"));
            Assert.That(policy.Calls, Is.Zero);
        });
    }

    private static CrewRoster Roster() => new()
    {
        Name = "test shift",
        DurationSeconds = 30,
        DecisionIntervalMs = 2000,
        JoinTimeoutSeconds = 5,
        Agents = new[]
        {
            new CrewAgent
            {
                Name = "janitor",
                Pipe = "crew-janitor",
                Username = "CrewJanitor",
                Job = "Janitor",
                Temperament = "steady",
            },
        },
    };

    private static PilotExchange Join(PilotRequest request, string? requestedJob)
    {
        return Exchange(request, new { accepted = true, assignedJob = requestedJob });
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

    private sealed class FakeTransport(Func<PilotRequest, PilotExchange> handler) : IPilotTransport
    {
        public string PipeName => "crew-janitor";

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
