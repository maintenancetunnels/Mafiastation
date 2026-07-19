using System.Diagnostics;
using System.Text.Json;

namespace Mafiastation.AiPilotLab;

public sealed record ReplayOptions(
    double TimeScale = 1,
    bool AllowSpeech = false,
    bool AllowLifecycle = false,
    int MaximumActions = 10_000);

public sealed record ReplaySummary(
    int RecordedRequests,
    int Sent,
    int Skipped,
    int Failed,
    double DurationMilliseconds,
    bool Success);

public sealed class ReplayRunner
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "status",
        "observe",
        "move",
        "interact",
        "use",
        "pickup",
        "drop",
        "swap_hands",
        "ready",
        "join",
        "goal",
        "goal_status",
        "stop",
        "say",
        "whisper",
    };

    private static readonly HashSet<string> SpeechActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "say",
        "whisper",
    };

    private static readonly HashSet<string> LifecycleActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "join",
        "ready",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    public async Task<ReplaySummary> RunAsync(
        string logPath,
        Func<string, IPilotTransport> transportFactory,
        ReplayOptions options,
        string? defaultBot = null,
        PilotRecorder? recorder = null,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(options.TimeScale) || options.TimeScale < 0 || options.TimeScale > 100)
            throw new ArgumentOutOfRangeException(nameof(options), "Replay time scale must be from 0 through 100.");
        if (options.MaximumActions is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Replay maximum actions must be from 1 through 100000.");

        var requests = await LoadRequestsAsync(logPath, options.MaximumActions, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var sent = 0;
        var skipped = 0;
        var failed = 0;
        double? previousElapsed = null;

        foreach (var entry in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previousElapsed != null && options.TimeScale > 0)
            {
                var delay = Math.Max(0, entry.ElapsedMilliseconds - previousElapsed.Value) / options.TimeScale;
                if (delay > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 60_000)), cancellationToken);
            }
            previousElapsed = entry.ElapsedMilliseconds;

            var recorded = entry.Payload.Deserialize<PilotRequest>(JsonOptions)
                ?? throw new InvalidDataException("Replay log contained an empty pilot request.");
            if (!AllowedActions.Contains(recorded.Action))
                throw new InvalidDataException($"Replay log contained unsupported action '{recorded.Action}'.");
            if ((!options.AllowSpeech && SpeechActions.Contains(recorded.Action)) ||
                (!options.AllowLifecycle && LifecycleActions.Contains(recorded.Action)))
            {
                skipped++;
                continue;
            }

            var bot = string.IsNullOrWhiteSpace(entry.Bot) ? defaultBot : entry.Bot;
            if (string.IsNullOrWhiteSpace(bot))
                throw new InvalidDataException("Replay entry had no bot and no default bot was supplied.");
            var request = PilotRequest.Create(recorded.Action, recorded.Arguments);
            try
            {
                if (recorder != null)
                    await recorder.RecordRequestAsync(bot, request, cancellationToken);
                var attempt = Stopwatch.StartNew();
                var exchange = await transportFactory(bot).SendAsync(request, cancellationToken);
                attempt.Stop();
                if (recorder != null)
                    await recorder.RecordResponseAsync(bot, exchange.Response, attempt.Elapsed.TotalMilliseconds, cancellationToken);
                sent++;
                if (!exchange.Response.Ok)
                    failed++;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
            {
                sent++;
                failed++;
                if (recorder != null)
                {
                    await recorder.RecordErrorAsync(bot, new
                    {
                        action = recorded.Action,
                        error = exception.GetType().Name,
                        message = exception.Message,
                    }, cancellationToken);
                }
            }
        }

        stopwatch.Stop();
        return new ReplaySummary(requests.Count, sent, skipped, failed, stopwatch.Elapsed.TotalMilliseconds, failed == 0);
    }

    private static async Task<List<PilotLogEntry>> LoadRequestsAsync(
        string path,
        int maximumActions,
        CancellationToken cancellationToken)
    {
        var result = new List<PilotLogEntry>();
        using var reader = new StreamReader(Path.GetFullPath(path));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var entry = JsonSerializer.Deserialize<PilotLogEntry>(line, JsonOptions)
                ?? throw new InvalidDataException("Replay log contained an empty entry.");
            if (entry.Version != 1)
                throw new InvalidDataException($"Unsupported replay log version {entry.Version}.");
            if (!entry.Type.Equals("request", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(entry);
            if (result.Count > maximumActions)
                throw new InvalidDataException($"Replay log exceeds the configured maximum of {maximumActions} actions.");
        }
        return result;
    }
}
