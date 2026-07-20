using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mafiastation.AiPilotLab;

/// <summary>
/// Declarative roster for ordinary connected-client crew agents. It intentionally contains no
/// free-form mission field or antagonist metadata: every job brief comes from the reviewed local
/// catalog below.
/// </summary>
public sealed class CrewRoster
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; } = 600;

    [JsonPropertyName("decisionIntervalMs")]
    public int DecisionIntervalMs { get; init; } = 5000;

    [JsonPropertyName("joinTimeoutSeconds")]
    public int JoinTimeoutSeconds { get; init; } = 60;

    [JsonPropertyName("agents")]
    public IReadOnlyList<CrewAgent> Agents { get; init; } = Array.Empty<CrewAgent>();
}

public sealed class CrewAgent
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("pipe")]
    public string Pipe { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("job")]
    public string Job { get; init; } = string.Empty;

    [JsonPropertyName("temperament")]
    public string Temperament { get; init; } = "steady";
}

public sealed record CrewRoleProfile(
    string JobId,
    string DisplayName,
    string DutyBrief);

/// <summary>
/// Reviewed, non-antagonist role briefs for the first playable crew slice. These prompts describe
/// mundane work and evidence-based conduct; a roster cannot smuggle in hidden round knowledge.
/// </summary>
public static class CrewRoleCatalog
{
    private static readonly IReadOnlyDictionary<string, CrewRoleProfile> Profiles =
        new Dictionary<string, CrewRoleProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Passenger"] = new(
                "Passenger",
                "assistant",
                "Help nearby departments with simple errands, carry clearly requested supplies, keep public areas orderly, report hazards, and ask crew what needs doing. Do not operate specialist or dangerous machinery without clear instruction."),
            ["Janitor"] = new(
                "Janitor",
                "janitor",
                "Keep public areas safe and clean. Look for visible spills, trash, cleaning tools, buckets, carts, and blocked walkways. Collect or use the appropriate nearby equipment, warn people about hazards, and request supplies when the correct tool is unavailable."),
            ["SecurityOfficer"] = new(
                "SecurityOfficer",
                "security officer",
                "Patrol in short routes, answer witnessed calls for help, de-escalate disputes, protect people facing immediate danger, and report concrete suspicious conduct. Ask questions and seek corroboration before accusations. Use force only when proportionate to an observed threat."),
            ["MedicalDoctor"] = new(
                "MedicalDoctor",
                "medical doctor",
                "Look for visibly critical or incapacitated crew, nearby medical supplies, and requests for treatment. Bring appropriate help or supplies, communicate triage priorities, guide patients toward medical care, and avoid experimenting with unknown items."),
            ["StationEngineer"] = new(
                "StationEngineer",
                "station engineer",
                "Inspect visible station infrastructure and respond to reported power, atmosphere, fire, or structural problems. Find and use an appropriate tool before interacting with machinery, isolate obvious hazards, communicate status, and do not randomly toggle unfamiliar controls."),
            ["CargoTechnician"] = new(
                "CargoTechnician",
                "cargo technician",
                "Handle visible crates, packages, delivery requests, and cargo equipment. Move supplies toward their requested recipients, keep cargo paths clear, coordinate orders, and do not open, take, or activate suspicious cargo without an in-character reason."),
            ["Botanist"] = new(
                "Botanist",
                "botanist",
                "Maintain the visible hydroponics workspace, find plants, produce, trays, water, and gardening tools, and respond to food or produce requests. Use appropriate tools deliberately and keep hazards out of public food supplies."),
            ["Bartender"] = new(
                "Bartender",
                "bartender",
                "Remain around public service areas when practical, answer ordinary drink and information requests, keep the bar orderly, and report escalating danger. Do not hand out dangerous chemicals or weapons merely because someone asks."),
        };

    private static readonly IReadOnlyDictionary<string, string> Temperaments =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["steady"] = "Be calm, practical, and moderately sociable.",
            ["sociable"] = "Be friendly and conversational, but do not chatter when work is urgent.",
            ["cautious"] = "Be careful, seek confirmation, and favor retreat or assistance when danger is unclear.",
            ["blunt"] = "Be concise and direct without becoming needlessly hostile.",
            ["curious"] = "Ask sensible in-character questions and investigate benign anomalies without taking reckless risks.",
        };

    public static bool TryGet(string? jobId, out CrewRoleProfile profile)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            profile = null!;
            return false;
        }

        return Profiles.TryGetValue(jobId.Trim(), out profile!);
    }

    public static bool IsTemperament(string? temperament)
    {
        return !string.IsNullOrWhiteSpace(temperament) &&
               Temperaments.ContainsKey(temperament.Trim());
    }

    public static string BuildStandingGoal(CrewAgent agent)
    {
        if (!TryGet(agent.Job, out var profile))
            throw new InvalidDataException($"Unsupported crew job '{agent.Job}'.");
        if (string.IsNullOrWhiteSpace(agent.Temperament) ||
            !Temperaments.TryGetValue(agent.Temperament.Trim(), out var temperament))
            throw new InvalidDataException($"Unsupported crew temperament '{agent.Temperament}'.");

        return
            $"You are serving as a normal {profile.DisplayName}. {temperament} " +
            $"Your standing duties are: {profile.DutyBrief} " +
            "Continue doing plausible routine work for the whole shift. Respond to speech that your client actually received, cooperate with other crew, and take short safe patrol steps when no relevant work is visible. " +
            "You have no access to hidden roles or objectives. Judge danger only from concrete in-character events in the bounded observation, never from account names, internal identifiers, or assumptions about who the human controls.";
    }
}

public static class CrewRosterLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<CrewRoster> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var roster = await JsonSerializer.DeserializeAsync<CrewRoster>(
                         stream,
                         Options,
                         cancellationToken)
                     ?? throw new InvalidDataException("Crew roster file was empty.");
        Validate(roster);
        return roster;
    }

    public static void Validate(CrewRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (roster.Version != 1)
            throw new InvalidDataException($"Unsupported crew roster version {roster.Version}; expected version 1.");
        if (string.IsNullOrWhiteSpace(roster.Name) || roster.Name.Length > 128)
            throw new InvalidDataException("Crew roster name is required and must be at most 128 characters.");
        if (roster.DurationSeconds is < 30 or > 3600)
            throw new InvalidDataException("Crew durationSeconds must be from 30 through 3600.");
        if (roster.DecisionIntervalMs is < 2000 or > 60_000)
            throw new InvalidDataException("Crew decisionIntervalMs must be from 2000 through 60000.");
        if (roster.JoinTimeoutSeconds is < 5 or > 120)
            throw new InvalidDataException("Crew joinTimeoutSeconds must be from 5 through 120.");
        if (roster.Agents == null || roster.Agents.Count is < 1 or > 16)
            throw new InvalidDataException("A crew roster must define from 1 through 16 agents.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in roster.Agents)
        {
            if (agent == null)
                throw new InvalidDataException("Crew roster entries cannot be null.");

            if (string.IsNullOrWhiteSpace(agent.Name) ||
                agent.Name.Length > 64 ||
                agent.Name.Any(char.IsControl) ||
                !names.Add(agent.Name))
            {
                throw new InvalidDataException("Crew agent names must be unique and at most 64 non-control characters.");
            }

            if (string.IsNullOrWhiteSpace(agent.Username) ||
                agent.Username.Length > 32 ||
                agent.Username.Any(char.IsControl) ||
                !usernames.Add(agent.Username))
            {
                throw new InvalidDataException("Crew usernames must be unique and at most 32 non-control characters.");
            }

            if (string.IsNullOrWhiteSpace(agent.Pipe) || !pipes.Add(agent.Pipe))
                throw new InvalidDataException("Crew pipe names must be present and unique.");
            _ = new PilotPipeClient(agent.Pipe, TimeSpan.FromSeconds(1));

            if (!CrewRoleCatalog.TryGet(agent.Job, out _))
                throw new InvalidDataException($"Crew job '{agent.Job}' is not in the reviewed role catalog.");
            if (!CrewRoleCatalog.IsTemperament(agent.Temperament))
                throw new InvalidDataException($"Crew temperament '{agent.Temperament}' is not supported.");
        }
    }
}
