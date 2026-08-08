using System.Diagnostics;
using System.Text.Json;

namespace Mafiastation.AiPilotLab;

public sealed record LlmAgentOptions(
    string Goal,
    TimeSpan Duration,
    TimeSpan DecisionInterval,
    // 3 was too tight for a live session: a pilot that names an entity outside its bounded
    // observation trips the validator, and three of those in a row retired two of five crew in
    // the first minutes. Still bounded, so a genuinely stuck pilot stops instead of spinning.
    int MaximumConsecutiveErrors = 8);

public sealed record LlmAgentSummary(
    string Bot,
    int ModelDecisions,
    int Actions,
    int Errors,
    string CompletionReason,
    double DurationMilliseconds,
    bool Success,
    int RoutinePolls = 0,
    int Escalations = 0,
    int RejectedActions = 0);

public sealed class LlmAgentRunner
{
    private static readonly TimeSpan AuthorizationReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AuthorizationPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InitializationRetryInterval = TimeSpan.FromMilliseconds(250);
    private const int InitializationTransportAttempts = 3;
    private const string AuthorizationPendingReason = "Authorization has not been checked.";

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
        var rejectedActions = 0;
        var consecutiveErrors = 0;
        var consecutiveEndpointFailures = 0;
        var endpointBackoff = TimeSpan.Zero;
        var completionReason = "duration";
        PilotResponse observation;
        try
        {
            var initial = await SendInitialObservationAsync(
                bot,
                recorder,
                deadline.Token);
            if (!initial.Response.Ok)
                throw new InvalidDataException(initial.Response.Error ?? "Initial pilot observation failed.");
            observation = await EnsureAuthorizedAsync(bot, initial.Response, recorder, deadline.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
        {
            stopwatch.Stop();
            return new LlmAgentSummary(bot, 0, 0, 1, $"initialization failed: {exception.Message}", stopwatch.Elapsed.TotalMilliseconds, false, 0, 0);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new LlmAgentSummary(bot, 0, 0, 1, "initialization failed: agent deadline elapsed before authorization was ready.", stopwatch.Elapsed.TotalMilliseconds, false, 0, 0);
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

                    // Someone spoke to us, or something happened to us (damage, an incident).
                    // Interrupt the running goal so the next iteration consults the model with the
                    // fresh perception. Without this the pilot walks out its goal like a scripted
                    // robot: it cannot answer a player who talks to it, and it will not react to
                    // being attacked, because the model is simply never asked while a goal runs.
                    if (HasUnreadPerception(status.Response))
                    {
                        await SendAsync(bot, PilotRequest.Create("stop"), recorder, deadline.Token);
                        actions++;
                        var interrupted = await SendAsync(
                            bot,
                            PilotRequest.Create("observe"),
                            recorder,
                            deadline.Token);
                        actions++;
                        observation = interrupted.Response.Ok ? interrupted.Response : status.Response;
                    }
                    else if (IsActiveGoal(PilotJson.GoalState(status.Response)))
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
                    if (!HasUnreadPerception(observation))
                    {
                        var refreshed = await SendAsync(
                            bot,
                            PilotRequest.Create("observe"),
                            recorder,
                            deadline.Token);
                        actions++;
                        if (!refreshed.Response.Ok)
                        {
                            throw new InvalidDataException(
                                refreshed.Response.Error ?? "Pilot pre-decision observation failed.");
                        }
                        observation = refreshed.Response;
                    }

                    escalations++;
                    var decision = await _policy.DecideAsync(options.Goal, observation, deadline.Token);
                    consecutiveEndpointFailures = 0;
                    endpointBackoff = TimeSpan.Zero;
                    decisions++;
                    if (recorder != null)
                        await recorder.RecordModelAsync(bot, decision, CancellationToken.None);
                    var exchange = await SendAsync(bot, decision.Request, recorder, deadline.Token);
                    actions++;
                    if (!exchange.Response.Ok)
                    {
                        rejectedActions++;
                        if (recorder != null)
                        {
                            await recorder.RecordErrorAsync(bot, new
                            {
                                stage = "action-rejected",
                                error = "PilotActionRejected",
                                message = exchange.Response.Error ?? "Pilot action was rejected.",
                                transient = true,
                            }, CancellationToken.None);
                        }

                        var refreshed = await SendAsync(
                            bot,
                            PilotRequest.Create("observe"),
                            recorder,
                            deadline.Token);
                        actions++;
                        if (!refreshed.Response.Ok)
                        {
                            throw new InvalidDataException(
                                refreshed.Response.Error ?? "Pilot observation after rejected action failed.");
                        }
                        observation = refreshed.Response;
                        consecutiveErrors = 0;
                    }
                    else
                    {
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
                            if (!observed.Response.Ok)
                            {
                                throw new InvalidDataException(
                                    observed.Response.Error ?? "Pilot observation after action failed.");
                            }
                            observation = observed.Response;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                                               InvalidOperationException or JsonException or
                                               HttpRequestException or TimeoutException)
            {
                errors++;
                var transientEndpointFailure = IsTransientEndpointFailure(exception);
                if (transientEndpointFailure)
                {
                    // Provider throttling and temporarily incomplete Responses payloads do not
                    // mean the connected player is broken. Back off without retiring its loop.
                    consecutiveEndpointFailures++;
                    endpointBackoff = GetEndpointBackoff(bot, consecutiveEndpointFailures);
                    consecutiveErrors = 0;
                }
                else
                {
                    consecutiveEndpointFailures = 0;
                    endpointBackoff = TimeSpan.Zero;
                    consecutiveErrors++;
                }
                if (recorder != null)
                {
                    await recorder.RecordErrorAsync(bot, new
                    {
                        stage = "agent",
                        error = exception.GetType().Name,
                        message = exception.Message,
                        consecutiveErrors,
                        transientEndpointFailure,
                        retryAfterMilliseconds = transientEndpointFailure
                            ? endpointBackoff.TotalMilliseconds
                            : 0d,
                    }, CancellationToken.None);
                }
                if (!transientEndpointFailure &&
                    consecutiveErrors >= options.MaximumConsecutiveErrors)
                {
                    completionReason = $"{consecutiveErrors} consecutive errors";
                    break;
                }
            }

            iteration.Stop();
            var targetDelay = endpointBackoff > options.DecisionInterval
                ? endpointBackoff
                : options.DecisionInterval;
            var remaining = targetDelay - iteration.Elapsed;
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
            escalations,
            rejectedActions);
    }

    private static bool IsTransientEndpointFailure(Exception exception)
    {
        if (exception is TimeoutException)
            return true;

        if (exception is HttpRequestException)
        {
            var message = exception.Message;
            return !message.Contains("HTTP ", StringComparison.Ordinal) ||
                   message.Contains("HTTP 408", StringComparison.Ordinal) ||
                   message.Contains("HTTP 425", StringComparison.Ordinal) ||
                   message.Contains("HTTP 429", StringComparison.Ordinal) ||
                   message.Contains("HTTP 5", StringComparison.Ordinal);
        }

        return exception is InvalidDataException &&
               exception.Message.Contains(
                   "OpenAI Responses result did not contain output text",
                   StringComparison.Ordinal);
    }

    private static TimeSpan GetEndpointBackoff(string bot, int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures, 1, 4);
        var seconds = Math.Min(30d, Math.Pow(2d, exponent));
        var hash = StringComparer.Ordinal.GetHashCode(bot) & int.MaxValue;
        var jitter = TimeSpan.FromMilliseconds(hash % 1000);
        return TimeSpan.FromSeconds(seconds) + jitter;
    }

    private async Task<PilotExchange> SendInitialObservationAsync(
        string bot,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= InitializationTransportAttempts; attempt++)
        {
            try
            {
                return await SendAsync(
                    bot,
                    PilotRequest.Create("observe"),
                    recorder,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                lastException = exception;
                if (attempt == InitializationTransportAttempts)
                    break;

                await Task.Delay(InitializationRetryInterval, cancellationToken);
            }
        }

        throw lastException ??
              new TimeoutException("Initial pilot observation did not complete.");
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

    private async Task<PilotResponse> EnsureAuthorizedAsync(
        string bot,
        PilotResponse observation,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        if (ReadAuthorizationState(observation) is not false)
            return observation;

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < AuthorizationReadyTimeout)
        {
            var status = await SendAsync(
                bot,
                PilotRequest.Create("status"),
                recorder,
                cancellationToken);
            if (!status.Response.Ok)
                throw new InvalidDataException(status.Response.Error ?? "Pilot authorization status failed.");

            if (ReadAuthorizationState(status.Response) == true)
            {
                var refreshed = await SendAsync(
                    bot,
                    PilotRequest.Create("observe"),
                    recorder,
                    cancellationToken);
                if (!refreshed.Response.Ok)
                    throw new InvalidDataException(refreshed.Response.Error ?? "Authorized pilot observation failed.");
                if (ReadAuthorizationState(refreshed.Response) is not false)
                    return refreshed.Response;
            }
            else if (ReadAuthorizationReason(status.Response) is { Length: > 0 } reason &&
                     !string.Equals(reason, AuthorizationPendingReason, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Pilot authorization denied: {reason}");
            }

            await Task.Delay(AuthorizationPollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"Pilot authorization was not confirmed within {AuthorizationReadyTimeout.TotalSeconds:0} seconds.");
    }

    private static bool? ReadAuthorizationState(PilotResponse response)
    {
        if (response.Data.ValueKind != JsonValueKind.Object ||
            !response.Data.TryGetProperty("authorized", out var authorized))
        {
            return null;
        }

        return authorized.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? ReadAuthorizationReason(PilotResponse response)
    {
        if (response.Data.ValueKind != JsonValueKind.Object ||
            !response.Data.TryGetProperty("authorizationReason", out var reason) ||
            reason.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return reason.GetString()?.Trim();
    }

    private static bool IsActiveGoal(string? state)
    {
        return state is "planning" or "moving";
    }

    private static bool HasUnreadPerception(PilotResponse observation)
    {
        return HasUnreadItems(observation, "recentSpeech") ||
               HasUnreadItems(observation, "recentIncidents");
    }

    private static bool HasUnreadItems(PilotResponse observation, string property)
    {
        if (observation.Data.ValueKind != JsonValueKind.Object ||
            !observation.Data.TryGetProperty(property, out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("unread", out var unread) ||
                unread.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }
}
