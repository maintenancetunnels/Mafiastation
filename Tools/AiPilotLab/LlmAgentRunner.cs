using System.Diagnostics;

namespace Mafiastation.AiPilotLab;

public sealed record LlmAgentOptions(
    string Goal,
    TimeSpan Duration,
    TimeSpan DecisionInterval,
    int MaximumConsecutiveErrors = 3);

public sealed record LlmAgentSummary(
    string Bot,
    int ModelDecisions,
    int Actions,
    int Errors,
    string CompletionReason,
    double DurationMilliseconds,
    bool Success,
    int RoutinePolls = 0,
    int Escalations = 0);

public sealed class LlmAgentRunner
{
    private readonly IPilotTransport _transport;
    private readonly IPilotPolicy _policy;

    public LlmAgentRunner(IPilotTransport transport, IPilotPolicy policy)
    {
        _transport = transport;
        _policy = policy;
    }

    public async Task<LlmAgentSummary> RunAsync(
        string bot,
        LlmAgentOptions options,
        PilotRecorder? recorder = null,
        CancellationToken cancellationToken = default)
    {
        if (options.Duration < TimeSpan.FromSeconds(1) || options.Duration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options), "Agent duration must be from 1 second through 1 hour.");
        if (options.DecisionInterval < TimeSpan.FromSeconds(1) || options.DecisionInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options), "Decision interval must be from 1 through 60 seconds.");

        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Duration);
        var decisions = 0;
        var actions = 0;
        var errors = 0;
        var routinePolls = 0;
        var escalations = 0;
        var consecutiveErrors = 0;
        var completionReason = "duration";
        PilotResponse observation;
        try
        {
            observation = (await SendAsync(bot, PilotRequest.Create("observe"), recorder, deadline.Token)).Response;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
        {
            stopwatch.Stop();
            return new LlmAgentSummary(bot, 0, 0, 1, $"initial observation failed: {exception.Message}", stopwatch.Elapsed.TotalMilliseconds, false, 0, 0);
        }

        while (!deadline.IsCancellationRequested)
        {
            var iteration = Stopwatch.StartNew();
            try
            {
                if (IsActiveGoal(PilotJson.GoalState(observation)))
                {
                    var status = await SendAsync(
                        bot,
                        PilotRequest.Create("goal_status"),
                        recorder,
                        deadline.Token);
                    actions++;
                    routinePolls++;
                    if (!status.Response.Ok)
                        throw new InvalidDataException(status.Response.Error ?? "Pilot goal status failed.");

                    if (IsActiveGoal(PilotJson.GoalState(status.Response)))
                    {
                        observation = status.Response;
                    }
                    else
                    {
                        var observed = await SendAsync(
                            bot,
                            PilotRequest.Create("observe"),
                            recorder,
                            deadline.Token);
                        actions++;
                        observation = observed.Response;
                    }
                    consecutiveErrors = 0;
                }
                else
                {
                    escalations++;
                    var decision = await _policy.DecideAsync(options.Goal, observation, deadline.Token);
                    decisions++;
                    if (recorder != null)
                        await recorder.RecordModelAsync(bot, decision, CancellationToken.None);
                    var exchange = await SendAsync(bot, decision.Request, recorder, deadline.Token);
                    actions++;
                    if (!exchange.Response.Ok)
                        throw new InvalidDataException(exchange.Response.Error ?? "Pilot action failed.");
                    consecutiveErrors = 0;

                    if (decision.Request.Action == "stop")
                    {
                        completionReason = "model requested stop";
                        break;
                    }

                    if (decision.Request.Action == "observe")
                    {
                        observation = exchange.Response;
                    }
                    else
                    {
                        var observed = await SendAsync(bot, PilotRequest.Create("observe"), recorder, deadline.Token);
                        actions++;
                        observation = observed.Response;
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or TimeoutException)
            {
                errors++;
                consecutiveErrors++;
                if (recorder != null)
                {
                    await recorder.RecordErrorAsync(bot, new
                    {
                        stage = "agent",
                        error = exception.GetType().Name,
                        message = exception.Message,
                        consecutiveErrors,
                    }, CancellationToken.None);
                }
                if (consecutiveErrors >= options.MaximumConsecutiveErrors)
                {
                    completionReason = $"{consecutiveErrors} consecutive errors";
                    break;
                }
            }

            iteration.Stop();
            var remaining = options.DecisionInterval - iteration.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        try
        {
            await SendAsync(bot, PilotRequest.Create("stop"), recorder, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best-effort release. The in-client bridge independently auto-releases held inputs.
        }
        stopwatch.Stop();
        var success = errors == 0;
        return new LlmAgentSummary(
            bot,
            decisions,
            actions,
            errors,
            completionReason,
            stopwatch.Elapsed.TotalMilliseconds,
            success,
            routinePolls,
            escalations);
    }

    private async Task<PilotExchange> SendAsync(
        string bot,
        PilotRequest request,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        if (recorder != null)
            await recorder.RecordRequestAsync(bot, request, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();
        var exchange = await _transport.SendAsync(request, cancellationToken);
        stopwatch.Stop();
        if (recorder != null)
            await recorder.RecordResponseAsync(bot, exchange.Response, stopwatch.Elapsed.TotalMilliseconds, CancellationToken.None);
        return exchange;
    }

    private static bool IsActiveGoal(string? state)
    {
        return state is "planning" or "moving";
    }
}
