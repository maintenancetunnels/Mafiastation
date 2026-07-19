using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class ReplayRunnerTests
{
    [Test]
    public async Task SafeReplaySkipsSpeechAndLobbyLifecycleByDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-pilot-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var recorder = new PilotRecorder(path))
            {
                await recorder.RecordRequestAsync("pilot", PilotRequest.Create("join"));
                await recorder.RecordRequestAsync("pilot", PilotRequest.Create(
                    "say",
                    JsonSerializer.SerializeToElement(new { text = "hello" })));
                await recorder.RecordRequestAsync("pilot", PilotRequest.Create(
                    "move",
                    JsonSerializer.SerializeToElement(new { direction = "east", durationMs = 100 })));
            }
            var transport = new RecordingTransport("pilot-pipe");

            var summary = await new ReplayRunner().RunAsync(
                path,
                _ => transport,
                new ReplayOptions(TimeScale: 0));

            Assert.That(summary.RecordedRequests, Is.EqualTo(3));
            Assert.That(summary.Skipped, Is.EqualTo(2));
            Assert.That(summary.Sent, Is.EqualTo(1));
            Assert.That(summary.Success, Is.True);
            Assert.That(transport.Actions, Is.EqualTo(new[] { "move" }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReplayRejectsActionsOutsideBridgeAllowlist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-pilot-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var recorder = new PilotRecorder(path))
                await recorder.RecordRequestAsync("pilot", PilotRequest.Create("execute_console"));
            var transport = new RecordingTransport("pilot-pipe");

            Assert.That(
                async () => await new ReplayRunner().RunAsync(path, _ => transport, new ReplayOptions(TimeScale: 0)),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("unsupported action"));
            Assert.That(transport.Actions, Is.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingTransport : IPilotTransport
    {
        public string PipeName { get; }
        public List<string> Actions { get; } = new();

        public RecordingTransport(string pipeName)
        {
            PipeName = pipeName;
        }

        public Task<PilotExchange> SendAsync(PilotRequest request, CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            var response = new PilotResponse
            {
                Version = 1,
                Id = request.Id,
                Ok = true,
                Data = JsonSerializer.SerializeToElement(new { }),
            };
            var raw = JsonSerializer.SerializeToElement(new
            {
                version = 1,
                id = request.Id,
                ok = true,
                data = new { },
            });
            return Task.FromResult(new PilotExchange(request, response, raw));
        }
    }
}
