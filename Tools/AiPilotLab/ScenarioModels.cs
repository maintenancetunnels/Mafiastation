using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mafiastation.AiPilotLab;

public sealed class PilotScenario
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 120;

    [JsonPropertyName("bots")]
    public IReadOnlyList<ScenarioBot> Bots { get; init; } = Array.Empty<ScenarioBot>();

    [JsonPropertyName("steps")]
    public IReadOnlyList<ScenarioStep> Steps { get; init; } = Array.Empty<ScenarioStep>();
}

public sealed class ScenarioBot
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("pipe")]
    public string Pipe { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string? Username { get; init; }
}

public sealed class ScenarioStep
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("bot")]
    public string? Bot { get; init; }

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; } = JsonSerializer.SerializeToElement(new { });

    [JsonPropertyName("expect")]
    public IReadOnlyDictionary<string, JsonElement> Expect { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    [JsonPropertyName("until")]
    public bool Until { get; init; }

    [JsonPropertyName("pollMs")]
    public int PollMs { get; init; } = 250;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 15;

    [JsonPropertyName("delayAfterMs")]
    public int DelayAfterMs { get; init; }

    [JsonPropertyName("capture")]
    public string? Capture { get; init; }
}

public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<PilotScenario> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var scenario = await JsonSerializer.DeserializeAsync<PilotScenario>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Scenario file was empty.");
        Validate(scenario);
        return scenario;
    }

    public static void Validate(PilotScenario scenario)
    {
        if (scenario.Version != 1)
            throw new InvalidDataException($"Unsupported scenario version {scenario.Version}; expected version 1.");
        if (string.IsNullOrWhiteSpace(scenario.Name) || scenario.Name.Length > 128)
            throw new InvalidDataException("Scenario name is required and must be at most 128 characters.");
        if (scenario.TimeoutSeconds is < 1 or > 3600)
            throw new InvalidDataException("Scenario timeoutSeconds must be from 1 through 3600.");
        if (scenario.Bots.Count is < 1 or > 32)
            throw new InvalidDataException("A scenario must define from 1 through 32 bots.");
        if (scenario.Steps.Count is < 1 or > 1000)
            throw new InvalidDataException("A scenario must define from 1 through 1000 steps.");

        var botNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pipeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bot in scenario.Bots)
        {
            if (string.IsNullOrWhiteSpace(bot.Name) || bot.Name.Length > 64 || !botNames.Add(bot.Name))
                throw new InvalidDataException("Bot names are required, must be unique, and must be at most 64 characters.");
            if (string.IsNullOrWhiteSpace(bot.Pipe) || !pipeNames.Add(bot.Pipe))
                throw new InvalidDataException("Bot pipe names are required and must be unique.");
            _ = new PilotPipeClient(bot.Pipe, TimeSpan.FromSeconds(1));
        }

        for (var index = 0; index < scenario.Steps.Count; index++)
        {
            var step = scenario.Steps[index];
            if (string.IsNullOrWhiteSpace(step.Action) || step.Action.Length > 64)
                throw new InvalidDataException($"Step {index + 1} must have an action of at most 64 characters.");
            if (step.Arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                throw new InvalidDataException($"Step {index + 1} arguments must be a JSON object.");
            if (step.PollMs is < 50 or > 60_000)
                throw new InvalidDataException($"Step {index + 1} pollMs must be from 50 through 60000.");
            if (step.TimeoutSeconds is < 1 or > 600)
                throw new InvalidDataException($"Step {index + 1} timeoutSeconds must be from 1 through 600.");
            if (step.DelayAfterMs is < 0 or > 60_000)
                throw new InvalidDataException($"Step {index + 1} delayAfterMs must be from 0 through 60000.");
            if (step.Expect.Count > 32)
                throw new InvalidDataException($"Step {index + 1} may contain at most 32 expectations.");
            if (step.Bot is { Length: > 0 } bot && bot != "*" && !botNames.Contains(bot))
                throw new InvalidDataException($"Step {index + 1} refers to unknown bot '{bot}'.");
        }
    }
}

public sealed record ScenarioStepResult(
    int Step,
    string Name,
    string Action,
    bool Success,
    int Attempts,
    double ElapsedMilliseconds,
    string? Error,
    JsonElement? Capture);

public sealed record ScenarioBotResult(
    string Bot,
    string Pipe,
    bool Success,
    IReadOnlyList<ScenarioStepResult> Steps);

public sealed record ScenarioMetrics(
    int Bots,
    int Steps,
    int Passed,
    int Failed,
    int Attempts,
    double PassRate,
    double MedianLatencyMilliseconds,
    double P95LatencyMilliseconds,
    double DurationMilliseconds);

public sealed record ScenarioRunSummary(
    string Scenario,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Success,
    ScenarioMetrics Metrics,
    IReadOnlyList<ScenarioBotResult> Bots);
