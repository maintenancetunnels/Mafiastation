using System.IO.Pipes;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class PilotProtocolTests
{
    [Test]
    public async Task InternalPipeDeadlineIsReportedAsRetryableTimeout()
    {
        var pipeName = $"pilot-timeout-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new PilotPipeClient(pipeName, TimeSpan.FromMilliseconds(250));
        var exchange = client.SendAsync(PilotRequest.Create("status"));

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using var reader = new StreamReader(server, leaveOpen: true);
        Assert.That(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.Not.Empty);

        var exception = Assert.ThrowsAsync<TimeoutException>(async () => await exchange);
        Assert.That(exception!.Message, Does.Contain(pipeName));
    }

    [Test]
    public void CallerCancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new PilotPipeClient(
            $"pilot-cancel-{Guid.NewGuid():N}",
            TimeSpan.FromSeconds(5));

        Assert.CatchAsync<OperationCanceledException>(
            async () => await client.SendAsync(PilotRequest.Create("status"), cancellation.Token));
    }
}
