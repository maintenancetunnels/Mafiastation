using System.Text.Json;

namespace Mafiastation.AiPilotLab;

internal static class Program
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] rawArguments)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var arguments = CommandLineArguments.Parse(rawArguments);
            return arguments.Command switch
            {
                "help" or "--help" or "-h" => Help(),
                "send" => await SendAsync(arguments, cancellation.Token),
                "scenario" => await ScenarioAsync(arguments, cancellation.Token),
                "validate-scenario" => await ValidateScenarioAsync(arguments, cancellation.Token),
                "crew" => await CrewAsync(arguments, cancellation.Token),
                "validate-crew" => await ValidateCrewAsync(arguments, cancellation.Token),
                "replay" => await ReplayAsync(arguments, cancellation.Token),
                "agent" => await AgentAsync(arguments, cancellation.Token),
                "launch" => await LaunchAsync(arguments, cancellation.Token),
                "moderation" => await ModerationAsync(arguments, cancellation.Token),
                _ => throw new ArgumentException($"Unknown command '{arguments.Command}'. Run 'help' for usage."),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or
                                          HttpRequestException or JsonException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            Mafiastation AI Pilot Lab

              send --pipe NAME --action ACTION [--args JSON]
              scenario --file PATH [--output DIR]
              validate-scenario --file PATH
              crew --file ROSTER --provider openai-compatible|anthropic
                   --endpoint URI --model MODEL [--allow-speech] [--output DIR]
              validate-crew --file ROSTER
              replay --file JSONL [--pipe NAME] [--map BOT=PIPE] [--time-scale N]
                     [--allow-speech] [--allow-lifecycle]
              agent --pipe NAME --goal TEXT --provider openai-compatible|anthropic
                    --endpoint URI --model MODEL [--api-key-env NAME] [--allow-speech]
              launch --client PATH --server ADDRESS
                     [--count N] [--scenario PATH | --goal TEXT | --crew ROSTER]
                     [agent model options] [--startup-timeout-seconds N]
                     [--pipe-prefix NAME] [--username-prefix NAME]
              moderation --action list|show|label|summary|export --incidents PATH [options]

            Model API keys are read only from an environment variable (MAFIA_LLM_KEY by default).
            Replay skips speech and join/ready actions unless their explicit gates are supplied.
            """);
        return 0;
    }

    private static async Task<int> SendAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        var pipe = arguments.Require("pipe");
        var action = arguments.Require("action");
        var requestArguments = ParseArguments(arguments.Get("args"));
        var timeout = arguments.GetInt("timeout-ms", 10_000, 100, 300_000);
        var client = new PilotPipeClient(pipe, TimeSpan.FromMilliseconds(timeout));
        var exchange = await client.SendAsync(PilotRequest.Create(action, requestArguments), cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(exchange.RawResponse, OutputJson));
        return exchange.Response.Ok ? 0 : 1;
    }

    private static async Task<int> ValidateScenarioAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        var scenario = await ScenarioLoader.LoadAsync(arguments.Require("file"), cancellationToken);
        Console.WriteLine($"Valid scenario '{scenario.Name}': {scenario.Bots.Count} bot(s), {scenario.Steps.Count} step(s).");
        return 0;
    }

    private static async Task<int> ScenarioAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        var scenario = await ScenarioLoader.LoadAsync(arguments.Require("file"), cancellationToken);
        var output = ResolveOutputDirectory(arguments.Get("output"), scenario.Name);
        Directory.CreateDirectory(output);
        await using var recorder = new PilotRecorder(Path.Combine(output, "actions.jsonl"));
        var summary = await new ScenarioRunner().RunAsync(scenario, recorder, cancellationToken);
        await ScenarioRunner.SaveSummaryAsync(summary, Path.Combine(output, "summary.json"), cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(summary, OutputJson));
        Console.WriteLine($"Artifacts: {output}");
        return summary.Success ? 0 : 1;
    }

    private static async Task<int> ValidateCrewAsync(
        CommandLineArguments arguments,
        CancellationToken cancellationToken)
    {
        var roster = await CrewRosterLoader.LoadAsync(arguments.Require("file"), cancellationToken);
        Console.WriteLine(
            $"Valid crew roster '{roster.Name}': {roster.Agents.Count} connected player agent(s).");
        return 0;
    }

    private static async Task<int> CrewAsync(
        CommandLineArguments arguments,
        CancellationToken cancellationToken)
    {
        var roster = await CrewRosterLoader.LoadAsync(arguments.Require("file"), cancellationToken);
        var output = ResolveOutputDirectory(arguments.Get("output"), $"crew-{roster.Name}");
        Directory.CreateDirectory(output);
        using var httpClient = NewModelHttpClient(arguments);
        var policy = CreatePolicy(arguments, httpClient);
        return await RunCrewAsync(roster, output, policy, cancellationToken);
    }

    private static async Task<int> ReplayAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        var input = arguments.Require("file");
        var output = ResolveOutputDirectory(arguments.Get("output"), "replay");
        Directory.CreateDirectory(output);
        var mapping = ParsePipeMappings(arguments.GetMany("map"));
        var defaultPipe = arguments.Get("pipe");
        if (mapping.Count == 0 && string.IsNullOrWhiteSpace(defaultPipe))
            throw new ArgumentException("Replay requires --pipe or at least one --map BOT=PIPE.");
        var transports = new Dictionary<string, IPilotTransport>(StringComparer.OrdinalIgnoreCase);
        IPilotTransport Factory(string bot)
        {
            var pipe = mapping.TryGetValue(bot, out var mapped) ? mapped : defaultPipe;
            if (string.IsNullOrWhiteSpace(pipe))
                throw new InvalidDataException($"No replay pipe mapping was supplied for bot '{bot}'.");
            if (!transports.TryGetValue(pipe, out var transport))
            {
                transport = new PilotPipeClient(pipe, TimeSpan.FromSeconds(10));
                transports.Add(pipe, transport);
            }
            return transport;
        }

        await using var recorder = new PilotRecorder(Path.Combine(output, "replay.jsonl"));
        var options = new ReplayOptions(
            arguments.GetDouble("time-scale", 1, 0, 100),
            arguments.GetFlag("allow-speech"),
            arguments.GetFlag("allow-lifecycle"),
            arguments.GetInt("max-actions", 10_000, 1, 100_000));
        var summary = await new ReplayRunner().RunAsync(
            input,
            Factory,
            options,
            defaultPipe == null ? null : "default",
            recorder,
            cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(summary, OutputJson));
        Console.WriteLine($"Artifacts: {output}");
        return summary.Success ? 0 : 1;
    }

    private static async Task<int> AgentAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        var pipe = arguments.Require("pipe");
        var bot = arguments.Get("bot") ?? pipe;
        var output = ResolveOutputDirectory(arguments.Get("output"), $"agent-{bot}");
        Directory.CreateDirectory(output);
        using var httpClient = NewModelHttpClient(arguments);
        var policy = CreatePolicy(arguments, httpClient);
        var transport = new PilotPipeClient(pipe, TimeSpan.FromSeconds(10));
        var runner = new LlmAgentRunner(transport, policy);
        await using var recorder = new PilotRecorder(Path.Combine(output, "agent.jsonl"));
        var summary = await runner.RunAsync(bot, AgentOptions(arguments), recorder, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(summary, OutputJson));
        Console.WriteLine($"Artifacts: {output}");
        return summary.Success ? 0 : 1;
    }

    private static async Task<int> LaunchAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        PilotScenario? scenario = null;
        if (arguments.Get("scenario") is { } scenarioPath)
            scenario = await ScenarioLoader.LoadAsync(scenarioPath, cancellationToken);
        CrewRoster? crew = null;
        if (arguments.Get("crew") is { } crewPath)
            crew = await CrewRosterLoader.LoadAsync(crewPath, cancellationToken);
        var goal = arguments.Get("goal");
        var selectedModes = (scenario != null ? 1 : 0) +
                            (crew != null ? 1 : 0) +
                            (!string.IsNullOrWhiteSpace(goal) ? 1 : 0);
        if (selectedModes > 1)
            throw new ArgumentException("Launch accepts only one of --scenario, --goal, or --crew.");

        var pipePrefix = arguments.Get("pipe-prefix") ?? "mafiastation-pilot";
        var usernamePrefix = arguments.Get("username-prefix") ?? "Pilot";
        var specs = crew != null
            ? crew.Agents.Select(agent =>
                new ClientLaunchSpec(agent.Name, agent.Pipe, agent.Username)).ToArray()
            : scenario != null
                ? scenario.Bots.Select((bot, index) =>
                    new ClientLaunchSpec(
                        bot.Name,
                        bot.Pipe,
                        bot.Username ?? $"{usernamePrefix}{index + 1}")).ToArray()
                : Enumerable.Range(1, arguments.GetInt("count", 1, 1, 32))
                    .Select(index =>
                        new ClientLaunchSpec(
                            $"pilot{index}",
                            $"{pipePrefix}-{index}",
                            $"{usernamePrefix}{index}"))
                    .ToArray();
        var output = ResolveOutputDirectory(arguments.Get("output"), "launch");
        Directory.CreateDirectory(output);
        var launcher = new ClientLauncher();
        if (arguments.Has("startup-timeout-seconds") && arguments.Has("startup-seconds"))
        {
            throw new ArgumentException(
                "Use only --startup-timeout-seconds; --startup-seconds is its legacy alias.");
        }
        var startupTimeoutOption = arguments.Has("startup-timeout-seconds")
            ? "startup-timeout-seconds"
            : "startup-seconds";
        var launcherOptions = new ClientLauncherOptions(
            arguments.Require("client"),
            arguments.Require("server"),
            Path.Combine(output, "clients"),
            arguments.Get("dotnet"),
            TimeSpan.FromSeconds(arguments.GetInt(startupTimeoutOption, 60, 1, 600)),
            arguments.GetMany("client-cvar"));
        await using var clients = await launcher.LaunchAsync(specs, launcherOptions, cancellationToken);

        if (scenario != null)
        {
            await using var recorder = new PilotRecorder(Path.Combine(output, "actions.jsonl"));
            var summary = await new ScenarioRunner().RunAsync(scenario, recorder, cancellationToken);
            await ScenarioRunner.SaveSummaryAsync(summary, Path.Combine(output, "summary.json"), cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(summary, OutputJson));
            Console.WriteLine($"Artifacts: {output}");
            return summary.Success ? 0 : 1;
        }

        if (crew != null)
        {
            using var httpClient = NewModelHttpClient(arguments);
            var policy = CreatePolicy(arguments, httpClient);
            return await RunCrewAsync(crew, output, policy, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(goal))
        {
            using var httpClient = NewModelHttpClient(arguments);
            var policy = CreatePolicy(arguments, httpClient);
            await using var recorder = new PilotRecorder(Path.Combine(output, "agents.jsonl"));
            var summaries = await Task.WhenAll(specs.Select(spec =>
            {
                var runner = new LlmAgentRunner(
                    new PilotPipeClient(spec.Pipe, TimeSpan.FromSeconds(10)),
                    policy);
                return runner.RunAsync(spec.Name, AgentOptions(arguments), recorder, cancellationToken);
            }));
            Console.WriteLine(JsonSerializer.Serialize(summaries, OutputJson));
            Console.WriteLine($"Artifacts: {output}");
            return summaries.All(summary => summary.Success) ? 0 : 1;
        }

        var statuses = new List<object>();
        var allHealthy = true;
        foreach (var spec in specs)
        {
            var exchange = await new PilotPipeClient(spec.Pipe, TimeSpan.FromSeconds(5))
                .SendAsync(PilotRequest.Create("status"), cancellationToken);
            statuses.Add(new { spec.Name, spec.Pipe, exchange.Response.Ok, exchange.Response.Data });
            allHealthy &= exchange.Response.Ok;
        }
        Console.WriteLine(JsonSerializer.Serialize(statuses, OutputJson));
        Console.WriteLine("No scenario or goal was supplied; clients were probed and cleanly stopped.");
        return allHealthy ? 0 : 1;
    }

    private static async Task<int> RunCrewAsync(
        CrewRoster roster,
        string output,
        IPilotPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var recorder = new PilotRecorder(Path.Combine(output, "crew.jsonl"));
        var runner = new CrewRunner(
            agent => new PilotPipeClient(agent.Pipe, TimeSpan.FromSeconds(10)),
            _ => policy);
        var summary = await runner.RunAsync(roster, recorder, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(output, "crew-summary.json"),
            JsonSerializer.Serialize(summary, OutputJson),
            cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(summary, OutputJson));
        Console.WriteLine($"Artifacts: {output}");
        return summary.Success ? 0 : 1;
    }

    private static async Task<int> ModerationAsync(
        CommandLineArguments arguments,
        CancellationToken cancellationToken)
    {
        var action = (arguments.Get("action") ?? "summary").Trim().ToLowerInvariant();
        var incidentsPath = Path.GetFullPath(arguments.Get("incidents") ?? "moderation_incidents.json");
        var reviewsPath = Path.GetFullPath(arguments.Get("reviews") ??
                                           Path.Combine(Path.GetDirectoryName(incidentsPath)!, "moderation_reviews.json"));
        var store = new ModerationEvaluationStore(incidentsPath, reviewsPath);
        var incidents = await store.LoadIncidentsAsync(cancellationToken);
        var reviews = await store.LoadReviewsAsync(cancellationToken);

        switch (action)
        {
            case "list":
            {
                var limit = arguments.GetInt("limit", 50, 1, 1000);
                var unreviewedOnly = arguments.GetFlag("unreviewed");
                var rows = ModerationEvaluationService.JoinRows(incidents, reviews)
                    .Where(row => !unreviewedOnly || row.HumanVerdict == null)
                    .OrderByDescending(row => row.CreatedAt)
                    .Take(limit)
                    .ToArray();
                Console.WriteLine(JsonSerializer.Serialize(rows, OutputJson));
                return 0;
            }
            case "show":
            {
                var incidentId = RequireGuid(arguments, "incident");
                var incident = incidents.SingleOrDefault(value => value.IncidentId == incidentId)
                    ?? throw new ArgumentException($"Incident {incidentId} was not found.");
                reviews.TryGetValue(incidentId, out var review);
                Console.WriteLine(JsonSerializer.Serialize(new { incident, review }, OutputJson));
                return 0;
            }
            case "label":
            {
                var incidentId = RequireGuid(arguments, "incident");
                if (incidents.All(value => value.IncidentId != incidentId))
                    throw new ArgumentException($"Incident {incidentId} was not found.");
                var verdict = ModerationEvaluationStore.ParseVerdict(arguments.Require("verdict"));
                var reviewer = ModerationEvaluationStore.NormalizeOptional(
                                   arguments.Get("reviewer") ?? "local-reviewer",
                                   128,
                                   "Reviewer")
                               ?? "local-reviewer";
                int? correctedSeverity = null;
                if (arguments.Has("severity"))
                    correctedSeverity = arguments.GetInt("severity", 1, 1, 4);
                var review = new ModerationHumanReview(
                    incidentId,
                    verdict,
                    reviewer,
                    DateTimeOffset.UtcNow,
                    ModerationEvaluationStore.NormalizeOptional(arguments.Get("category"), 64, "Corrected category"),
                    correctedSeverity,
                    ModerationEvaluationStore.NormalizeOptional(arguments.Get("notes"), 1000, "Notes"));
                await store.UpsertReviewAsync(review, cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(review, OutputJson));
                return 0;
            }
            case "summary":
            {
                var metrics = ModerationEvaluationService.ComputeMetrics(incidents, reviews);
                Console.WriteLine(JsonSerializer.Serialize(metrics, OutputJson));
                return 0;
            }
            case "export":
            {
                var output = Path.GetFullPath(arguments.Require("output"));
                var format = arguments.Get("format") ??
                             (Path.GetExtension(output).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json");
                var metrics = ModerationEvaluationService.ComputeMetrics(incidents, reviews);
                var export = new ModerationEvaluationExport(
                    1,
                    DateTimeOffset.UtcNow,
                    incidentsPath,
                    metrics,
                    ModerationEvaluationService.JoinRows(incidents, reviews));
                await ModerationEvaluationService.WriteExportAsync(export, output, format, cancellationToken);
                Console.WriteLine($"Exported {export.Rows.Count} incident row(s) to {output}.");
                return 0;
            }
            default:
                throw new ArgumentException("Moderation action must be list, show, label, summary, or export.");
        }
    }

    private static LlmAgentOptions AgentOptions(CommandLineArguments arguments) => new(
        arguments.Require("goal"),
        TimeSpan.FromSeconds(arguments.GetInt("duration-seconds", 120, 1, 3600)),
        TimeSpan.FromMilliseconds(arguments.GetInt("interval-ms", 1500, 1000, 60_000)),
        arguments.GetInt("max-errors", 3, 1, 20));

    private static HttpClient NewModelHttpClient(CommandLineArguments arguments) => new()
    {
        Timeout = TimeSpan.FromSeconds(arguments.GetInt("model-timeout-seconds", 30, 1, 300)),
    };

    private static LlmPilotPolicy CreatePolicy(CommandLineArguments arguments, HttpClient httpClient)
    {
        var provider = arguments.Get("provider") ?? "openai-compatible";
        if (!Uri.TryCreate(arguments.Require("endpoint"), UriKind.Absolute, out var endpoint))
            throw new ArgumentException("--endpoint must be an absolute URI.");
        var keyEnvironment = arguments.Get("api-key-env") ?? "MAFIA_LLM_KEY";
        if (string.IsNullOrWhiteSpace(keyEnvironment) || keyEnvironment.Length > 128)
            throw new ArgumentException("--api-key-env must name an environment variable.");
        var key = Environment.GetEnvironmentVariable(keyEnvironment);
        return new LlmPilotPolicy(httpClient, new LlmPilotPolicyOptions(
            provider,
            endpoint,
            arguments.Require("model"),
            key,
            arguments.GetFlag("allow-speech"),
            arguments.GetDouble("max-goal-distance", 20, 1, 100),
            arguments.GetDouble("temperature", 0.1, 0, 1),
            arguments.GetInt("max-tokens", 300, 32, 2000),
            !arguments.GetFlag("disable-json-object-mode"),
            arguments.GetInt("model-repair-attempts", 1, 0, 2)));
    }

    private static JsonElement? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("--args must be a JSON object.");
        return document.RootElement.Clone();
    }

    private static Dictionary<string, string> ParsePipeMappings(IReadOnlyList<string> mappings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            var separator = mapping.IndexOf('=');
            if (separator <= 0 || separator == mapping.Length - 1)
                throw new ArgumentException("--map values must use BOT=PIPE syntax.");
            var bot = mapping[..separator];
            var pipe = mapping[(separator + 1)..];
            _ = new PilotPipeClient(pipe, TimeSpan.FromSeconds(1));
            if (!result.TryAdd(bot, pipe))
                throw new ArgumentException($"Duplicate replay mapping for bot '{bot}'.");
        }
        return result;
    }

    private static Guid RequireGuid(CommandLineArguments arguments, string key)
    {
        if (!Guid.TryParse(arguments.Require(key), out var value) || value == Guid.Empty)
            throw new ArgumentException($"--{key} must be a non-empty GUID.");
        return value;
    }

    private static string ResolveOutputDirectory(string? configured, string label)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        var safe = string.Concat(label.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return Path.GetFullPath(Path.Combine("artifacts", "ai-pilot", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}"));
    }
}
