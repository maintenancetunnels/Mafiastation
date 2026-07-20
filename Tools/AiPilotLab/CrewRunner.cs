using System.Diagnostics;
using System.Text.Json;

namespace Mafiastation.AiPilotLab;

public sealed record CrewAgentRunSummary(
    string Agent,
    string Job,
    bool Joined,
    string? SetupError,
    LlmAgentSummary? Run);

public sealed record CrewRunSummary(
    string Roster,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Success,
    IReadOnlyList<CrewAgentRunSummary> Agents);

/// <summary>
/// Joins real connected clients into reviewed jobs and then runs the ordinary bounded model policy
/// independently for each one. Joining is deterministic and outside model control.
/// </summary>
public sealed class CrewRunner
{
    private static readonly TimeSpan PreparationPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly Func<CrewAgent, IPilotTransport> _transportFactory;
    private readonly Func<CrewAgent, IPilotPolicy> _policyFactory;

    public CrewRunner(
        Func<CrewAgent, IPilotTransport> transportFactory,
        Func<CrewAgent, IPilotPolicy> policyFactory)
    {
        _transportFactory = transportFactory;
        _policyFactory = policyFactory;
    }

    public async Task<CrewRunSummary> RunAsync(
        CrewRoster roster,
        PilotRecorder? recorder = null,
        CancellationToken cancellationToken = default)
    {
        CrewRosterLoader.Validate(roster);
        var startedAt = DateTimeOffset.UtcNow;
        var results = await Task.WhenAll(roster.Agents.Select(agent =>
            RunAgentAsync(agent, roster, recorder, cancellationToken)));
        return new CrewRunSummary(
            roster.Name,
            startedAt,
            DateTimeOffset.UtcNow,
            results.All(result => result.Joined && result.Run?.Success == true),
            results);
    }

    private async Task<CrewAgentRunSummary> RunAgentAsync(
        CrewAgent agent,
        CrewRoster roster,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        var transport = _transportFactory(agent);
        try
        {
            await PrepareAsync(
                agent,
                transport,
                TimeSpan.FromSeconds(roster.JoinTimeoutSeconds),
                recorder,
                cancellationToken);
            var runner = new LlmAgentRunner(transport, _policyFactory(agent));
            var summary = await runner.RunAsync(
                agent.Name,
                new LlmAgentOptions(
                    CrewRoleCatalog.BuildStandingGoal(agent),
                    TimeSpan.FromSeconds(roster.DurationSeconds),
                    TimeSpan.FromMilliseconds(roster.DecisionIntervalMs)),
                recorder,
                cancellationToken);
            return new CrewAgentRunSummary(agent.Name, agent.Job, true, null, summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await StopBestEffortAsync(transport, recorder);
            var error = $"Crew setup timed out after {roster.JoinTimeoutSeconds} seconds.";
            if (recorder != null)
            {
                await recorder.RecordErrorAsync(agent.Name, new
                {
                    stage = "crew-setup",
                    error = "Timeout",
                    message = error,
                }, CancellationToken.None);
            }
            return new CrewAgentRunSummary(agent.Name, agent.Job, false, error, null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           TimeoutException or InvalidOperationException)
        {
            await StopBestEffortAsync(transport, recorder);
            if (recorder != null)
            {
                await recorder.RecordErrorAsync(agent.Name, new
                {
                    stage = "crew-setup",
                    error = exception.GetType().Name,
                    message = exception.Message,
                }, CancellationToken.None);
            }
            return new CrewAgentRunSummary(
                agent.Name,
                agent.Job,
                false,
                exception.Message,
                null);
        }
    }

    private static async Task PrepareAsync(
        CrewAgent agent,
        IPilotTransport transport,
        TimeSpan timeout,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var joinArguments = JsonSerializer.SerializeToElement(new { job = agent.Job });
        string? lastError = null;
        var joined = false;
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var exchange = await SendRecordedAsync(
                    agent.Name,
                    transport,
                    PilotRequest.Create("join", joinArguments),
                    recorder,
                    deadline.Token);
                if (exchange.Response.Ok)
                {
                    VerifyAssignedJob(agent.Job, exchange.Response);
                    joined = true;
                    break;
                }
                lastError = exchange.Response.Error;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                lastError = exception.Message;
            }

            await Task.Delay(PreparationPollInterval, deadline.Token);
        }

        if (!joined)
            throw new TimeoutException($"Crew agent could not join as {agent.Job}: {lastError ?? "timed out"}");

        while (!deadline.IsCancellationRequested)
        {
            var exchange = await SendRecordedAsync(
                agent.Name,
                transport,
                PilotRequest.Create("observe"),
                recorder,
                deadline.Token);
            if (exchange.Response.Ok && IsAttached(exchange.Response))
                return;
            lastError = exchange.Response.Error;
            await Task.Delay(PreparationPollInterval, deadline.Token);
        }

        throw new TimeoutException(
            $"Crew agent joined as {agent.Job} but did not attach to a character: {lastError ?? "timed out"}");
    }

    private static void VerifyAssignedJob(string expectedJob, PilotResponse response)
    {
        if (response.Data.ValueKind != JsonValueKind.Object ||
            !response.Data.TryGetProperty("assignedJob", out var assigned) ||
            assigned.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(assigned.GetString()))
        {
            throw new InvalidDataException(
                $"Pilot join did not verify the assigned job '{expectedJob}'.");
        }

        if (!string.Equals(assigned.GetString(), expectedJob, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Pilot joined with unexpected job '{assigned}' instead of '{expectedJob}'.");
        }
    }

    private static bool IsAttached(PilotResponse response)
    {
        return response.Data.ValueKind == JsonValueKind.Object &&
               response.Data.TryGetProperty("attached", out var attached) &&
               attached.ValueKind == JsonValueKind.True;
    }

    private static async Task<PilotExchange> SendRecordedAsync(
        string agent,
        IPilotTransport transport,
        PilotRequest request,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        if (recorder != null)
            await recorder.RecordRequestAsync(agent, request, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();
        var exchange = await transport.SendAsync(request, cancellationToken);
        stopwatch.Stop();
        if (recorder != null)
        {
            await recorder.RecordResponseAsync(
                agent,
                exchange.Response,
                stopwatch.Elapsed.TotalMilliseconds,
                CancellationToken.None);
        }
        return exchange;
    }

    private static async Task StopBestEffortAsync(
        IPilotTransport transport,
        PilotRecorder? recorder)
    {
        try
        {
            await SendRecordedAsync(
                transport.PipeName,
                transport,
                PilotRequest.Create("stop"),
                recorder,
                CancellationToken.None);
        }
        catch (Exception)
        {
            // Client movement has its own deadlines and launcher disposal still terminates only
            // the clients it created.
        }
    }
}
