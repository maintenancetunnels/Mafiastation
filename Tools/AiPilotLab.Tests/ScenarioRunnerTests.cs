using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class ScenarioRunnerTests
{
    [Test]
    public async Task RetriesUntilExpectationMatchesAndComputesMetrics()
    {
        var calls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scenario = new PilotScenario
        {
            Name = "retry",
            Bots = new[]
            {
                new ScenarioBot { Name = "one", Pipe = "pilot-one" },
                new ScenarioBot { Name = "two", Pipe = "pilot-two" },
            },
            Steps = new[]
            {
                new ScenarioStep
                {
                    Action = "status",
                    Until = true,
                    PollMs = 50,
                    TimeoutSeconds = 2,
                    Expect = new Dictionary<string, JsonElement>
                    {
                        ["data.ready"] = JsonSerializer.SerializeToElement(true),
                    },
                },
            },
        };
        var runner = new ScenarioRunner(bot => new FakeTransport(bot.Pipe, request =>
        {
            calls[bot.Name] = calls.GetValueOrDefault(bot.Name) + 1;
            return Exchange(request, new { ready = calls[bot.Name] >= 2 });
        }));

        var result = await runner.RunAsync(scenario);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Metrics.Bots, Is.EqualTo(2));
        Assert.That(result.Metrics.Passed, Is.EqualTo(2));
        Assert.That(result.Metrics.Attempts, Is.EqualTo(4));
        Assert.That(result.Metrics.PassRate, Is.EqualTo(1));
    }

    [Test]
    public void ValidationRejectsUnknownBotSelector()
    {
        var scenario = new PilotScenario
        {
            Name = "bad selector",
            Bots = new[] { new ScenarioBot { Name = "one", Pipe = "pilot-one" } },
            Steps = new[] { new ScenarioStep { Bot = "missing", Action = "status" } },
        };

        Assert.That(() => ScenarioLoader.Validate(scenario), Throws.TypeOf<InvalidDataException>());
    }

    private static PilotExchange Exchange(PilotRequest request, object data, bool ok = true)
    {
        var response = new PilotResponse
        {
            Version = 1,
            Id = request.Id,
            Ok = ok,
            Data = JsonSerializer.SerializeToElement(data),
        };
        var raw = JsonSerializer.SerializeToElement(new
        {
            version = 1,
            id = request.Id,
            ok,
            data,
        });
        return new PilotExchange(request, response, raw);
    }

    private sealed class FakeTransport : IPilotTransport
    {
        private readonly Func<PilotRequest, PilotExchange> _handler;

        public string PipeName { get; }

        public FakeTransport(string pipeName, Func<PilotRequest, PilotExchange> handler)
        {
            PipeName = pipeName;
            _handler = handler;
        }

        public Task<PilotExchange> SendAsync(PilotRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(request));
    }
}
