using System.Diagnostics;
using System.Text.Json;

namespace Mafiastation.AiPilotLab;

public sealed class ScenarioRunner
{
    private readonly Func<ScenarioBot, IPilotTransport> _transportFactory;

    public ScenarioRunner(Func<ScenarioBot, IPilotTransport>? transportFactory = null)
    {
        _transportFactory = transportFactory ?? (bot => new PilotPipeClient(bot.Pipe, TimeSpan.FromSeconds(10)));
    }

    public async Task<ScenarioRunSummary> RunAsync(
        PilotScenario scenario,
        PilotRecorder? recorder = null,
        CancellationToken cancellationToken = default)
    {
        ScenarioLoader.Validate(scenario);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(scenario.TimeoutSeconds));

        var tasks = scenario.Bots
            .Select(bot => RunBotAsync(scenario, bot, _transportFactory(bot), recorder, deadline.Token))
            .ToArray();
        var bots = await Task.WhenAll(tasks);
        stopwatch.Stop();

        var stepResults = bots.SelectMany(bot => bot.Steps).ToArray();
        var latencies = stepResults.Select(step => step.ElapsedMilliseconds).Order().ToArray();
        var passed = stepResults.Count(step => step.Success);
        var failed = stepResults.Length - passed;
        var metrics = new ScenarioMetrics(
            bots.Length,
            stepResults.Length,
            passed,
            failed,
            stepResults.Sum(step => step.Attempts),
            stepResults.Length == 0 ? 0 : (double)passed / stepResults.Length,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.95),
            stopwatch.Elapsed.TotalMilliseconds);
        return new ScenarioRunSummary(
            scenario.Name,
            startedAt,
            DateTimeOffset.UtcNow,
            bots.All(bot => bot.Success) && failed == 0,
            metrics,
            bots);
    }

    public static async Task SaveSummaryAsync(
        ScenarioRunSummary summary,
        string path,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, summary, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }, cancellationToken);
    }

    private static async Task<ScenarioBotResult> RunBotAsync(
        PilotScenario scenario,
        ScenarioBot bot,
        IPilotTransport transport,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        var results = new List<ScenarioStepResult>();
        for (var index = 0; index < scenario.Steps.Count; index++)
        {
            var step = scenario.Steps[index];
            if (!AppliesTo(step, bot))
                continue;

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(FailedWithoutAttempt(index, step, "Scenario deadline or cancellation was reached."));
                break;
            }

            var result = await RunStepAsync(index, step, bot, transport, recorder, cancellationToken);
            results.Add(result);
            if (!result.Success)
                break;
            if (step.DelayAfterMs > 0)
            {
                try
                {
                    await Task.Delay(step.DelayAfterMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        var expected = scenario.Steps.Count(step => AppliesTo(step, bot));
        return new ScenarioBotResult(
            bot.Name,
            bot.Pipe,
            results.Count == expected && results.All(step => step.Success),
            results);
    }

    private static async Task<ScenarioStepResult> RunStepAsync(
        int index,
        ScenarioStep step,
        ScenarioBot bot,
        IPilotTransport transport,
        PilotRecorder? recorder,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        string? lastError = null;
        JsonElement? capture = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

        do
        {
            attempts++;
            var request = PilotRequest.Create(step.Action, NormalizeArguments(step.Arguments));
            try
            {
                if (recorder != null)
                    await recorder.RecordRequestAsync(bot.Name, request, CancellationToken.None);
                var attempt = Stopwatch.StartNew();
                var exchange = await transport.SendAsync(request, deadline.Token);
                attempt.Stop();
                if (recorder != null)
                    await recorder.RecordResponseAsync(bot.Name, exchange.Response, attempt.Elapsed.TotalMilliseconds, CancellationToken.None);

                if (Matches(exchange, step.Expect, out lastError))
                {
                    if (!string.IsNullOrWhiteSpace(step.Capture) &&
                        PilotJson.TryGetPath(exchange.RawResponse, step.Capture, out var captured))
                    {
                        capture = captured.Clone();
                    }
                    stopwatch.Stop();
                    return new ScenarioStepResult(
                        index + 1,
                        StepName(index, step),
                        step.Action,
                        true,
                        attempts,
                        stopwatch.Elapsed.TotalMilliseconds,
                        null,
                        capture);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                lastError = "Step deadline or scenario cancellation was reached.";
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
            {
                lastError = exception.Message;
                if (recorder != null)
                {
                    await recorder.RecordErrorAsync(bot.Name, new
                    {
                        action = step.Action,
                        attempt = attempts,
                        error = exception.GetType().Name,
                        message = exception.Message,
                    }, CancellationToken.None);
                }
            }

            if (!step.Until || deadline.IsCancellationRequested)
                break;
            try
            {
                await Task.Delay(step.PollMs, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                lastError = "Step deadline or scenario cancellation was reached.";
                break;
            }
        } while (!deadline.IsCancellationRequested);

        stopwatch.Stop();
        return new ScenarioStepResult(
            index + 1,
            StepName(index, step),
            step.Action,
            false,
            attempts,
            stopwatch.Elapsed.TotalMilliseconds,
            lastError ?? "Expectation did not match.",
            capture);
    }

    private static bool Matches(
        PilotExchange exchange,
        IReadOnlyDictionary<string, JsonElement> expectations,
        out string? error)
    {
        if (expectations.Count == 0)
        {
            error = exchange.Response.Ok ? null : exchange.Response.Error ?? "Pilot action failed.";
            return exchange.Response.Ok;
        }

        foreach (var (path, expected) in expectations)
        {
            if (!PilotJson.TryGetPath(exchange.RawResponse, path, out var actual))
            {
                error = $"Response did not contain expected path '{path}'.";
                return false;
            }
            if (!PilotJson.Equivalent(actual, expected))
            {
                error = $"Response path '{path}' was {actual.GetRawText()}, expected {expected.GetRawText()}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static JsonElement? NormalizeArguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Undefined
            ? null
            : arguments;

    private static bool AppliesTo(ScenarioStep step, ScenarioBot bot) =>
        string.IsNullOrWhiteSpace(step.Bot) ||
        step.Bot == "*" ||
        string.Equals(step.Bot, bot.Name, StringComparison.OrdinalIgnoreCase);

    private static string StepName(int index, ScenarioStep step) =>
        string.IsNullOrWhiteSpace(step.Name) ? $"{index + 1}:{step.Action}" : step.Name;

    private static ScenarioStepResult FailedWithoutAttempt(int index, ScenarioStep step, string error) =>
        new(index + 1, StepName(index, step), step.Action, false, 0, 0, error, null);

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
