using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Server._ForkStation.Moderation;

public enum ModerationMessageKind : byte
{
    Say,
    Whisper,
    Radio,
    Emote,
    Ooc,
    Looc,
    Dead,
    SecurityPenalty,
}

/// <summary>
/// Broadcast by core chat delivery paths after a player message is accepted. IC speech and audible
/// emotes use their existing engine events and do not need this bridge.
/// </summary>
public sealed class ModerationChatMessageEvent : EntityEventArgs
{
    public ModerationMessageKind Kind { get; }
    public string SpeakerName { get; }
    public string AccountName { get; }
    public string UserId { get; }
    public string Message { get; }
    public EntityUid Source { get; }

    public ModerationChatMessageEvent(
        ModerationMessageKind kind,
        string speakerName,
        string accountName,
        string userId,
        string message,
        EntityUid source)
    {
        Kind = kind;
        SpeakerName = speakerName;
        AccountName = accountName;
        UserId = userId;
        Message = message;
        Source = source;
    }
}

public sealed record ModerationMessageRecord(
    long Id,
    DateTimeOffset Timestamp,
    ModerationMessageKind Kind,
    string SpeakerName,
    string AccountName,
    string UserId,
    string Message);

public sealed record ModerationVerdict(
    string Category,
    int Severity,
    double Confidence,
    string UserId,
    IReadOnlyList<long> EvidenceMessageIds,
    string Summary,
    string RuleCitation,
    string RecommendedAction);

public sealed class ModerationResponseEnvelope
{
    [JsonPropertyName("verdicts")]
    public List<ModerationVerdictDto> Verdicts { get; set; } = new();
}

public sealed class ModerationVerdictDto
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public int Severity { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("evidenceMessageIds")]
    public List<long> EvidenceMessageIds { get; set; } = new();

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("ruleCitation")]
    public string RuleCitation { get; set; } = string.Empty;

    [JsonPropertyName("recommendedAction")]
    public string RecommendedAction { get; set; } = string.Empty;
}

public static class ModerationResponseParser
{
    public const string JsonSchema =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["verdicts"],
          "properties": {
            "verdicts": {
              "type": "array",
              "maxItems": 12,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": [
                  "category",
                  "severity",
                  "confidence",
                  "userId",
                  "evidenceMessageIds",
                  "summary",
                  "ruleCitation",
                  "recommendedAction"
                ],
                "properties": {
                  "category": {
                    "type": "string",
                    "enum": [
                      "extreme_bigotry_or_harassment",
                      "metacomms_or_metafriending",
                      "ooc_in_ic",
                      "security_penalty_abuse"
                    ]
                  },
                  "severity": { "type": "integer", "minimum": 1, "maximum": 4 },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
                  "userId": { "type": "string", "minLength": 1, "maxLength": 128 },
                  "evidenceMessageIds": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 12,
                    "items": { "type": "integer" }
                  },
                  "summary": { "type": "string", "minLength": 1, "maxLength": 400 },
                  "ruleCitation": { "type": "string", "minLength": 1, "maxLength": 200 },
                  "recommendedAction": {
                    "type": "string",
                    "enum": ["monitor", "admin_review", "urgent_admin_review"]
                  }
                }
              }
            }
          }
        }
        """;

    private static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    {
        "extreme_bigotry_or_harassment",
        "metacomms_or_metafriending",
        "ooc_in_ic",
        "security_penalty_abuse",
    };

    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "monitor",
        "admin_review",
        "urgent_admin_review",
    };

    private static readonly HashSet<string> RequiredVerdictProperties = new(StringComparer.Ordinal)
    {
        "category",
        "severity",
        "confidence",
        "userId",
        "evidenceMessageIds",
        "summary",
        "ruleCitation",
        "recommendedAction",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Parses and independently validates model output. Unknown users, invented evidence IDs,
    /// invalid enums, invalid ranges, and oversized fields are rejected.
    /// </summary>
    public static IReadOnlyList<ModerationVerdict> Parse(
        string response,
        IReadOnlyDictionary<long, ModerationMessageRecord> messages)
    {
        var json = ExtractJsonObject(response);
        if (json == null)
            return Array.Empty<ModerationVerdict>();

        if (!HasExactJsonShape(json))
            return Array.Empty<ModerationVerdict>();

        ModerationResponseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ModerationResponseEnvelope>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return Array.Empty<ModerationVerdict>();
        }

        if (envelope == null ||
            envelope.Verdicts == null ||
            envelope.Verdicts.Count > 12)
            return Array.Empty<ModerationVerdict>();

        var knownUsers = messages.Values
            .Select(message => message.UserId)
            .ToHashSet(StringComparer.Ordinal);
        var validated = new List<ModerationVerdict>(envelope.Verdicts.Count);

        foreach (var candidate in envelope.Verdicts)
        {
            if (!AllowedCategories.Contains(candidate.Category) ||
                !AllowedActions.Contains(candidate.RecommendedAction) ||
                candidate.Severity is < 1 or > 4 ||
                !double.IsFinite(candidate.Confidence) ||
                candidate.Confidence is < 0 or > 1 ||
                string.IsNullOrWhiteSpace(candidate.UserId) ||
                candidate.UserId.Length > 128 ||
                !knownUsers.Contains(candidate.UserId) ||
                string.IsNullOrWhiteSpace(candidate.Summary) ||
                candidate.Summary.Length > 400 ||
                string.IsNullOrWhiteSpace(candidate.RuleCitation) ||
                candidate.RuleCitation.Length > 200 ||
                candidate.EvidenceMessageIds == null ||
                candidate.EvidenceMessageIds.Count is < 1 or > 12)
            {
                continue;
            }

            var evidence = candidate.EvidenceMessageIds
                .Distinct()
                .Where(messages.ContainsKey)
                .ToArray();
            if (evidence.Length == 0 ||
                evidence.Any(id => messages[id].UserId != candidate.UserId))
            {
                continue;
            }

            validated.Add(new ModerationVerdict(
                candidate.Category,
                candidate.Severity,
                candidate.Confidence,
                candidate.UserId,
                evidence,
                NormalizeText(candidate.Summary),
                NormalizeText(candidate.RuleCitation),
                candidate.RecommendedAction));
        }

        return validated;
    }

    private static bool HasExactJsonShape(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var rootProperties = new HashSet<string>(StringComparer.Ordinal);
            JsonElement verdicts = default;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name != "verdicts" ||
                    !rootProperties.Add(property.Name))
                {
                    return false;
                }

                verdicts = property.Value;
            }

            if (rootProperties.Count != 1 || verdicts.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in verdicts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return false;

                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!RequiredVerdictProperties.Contains(property.Name) ||
                        !properties.Add(property.Name))
                    {
                        return false;
                    }
                }

                if (properties.Count != RequiredVerdictProperties.Count)
                    return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonObject(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        return response[start..(end + 1)];
    }

    public static string NormalizeText(string text)
    {
        return string.Join(
            " ",
            text.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed class ModerationIncidentDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("incidents")]
    public List<ModerationIncident> Incidents { get; set; } = new();
}

public sealed record ModerationIncident(
    Guid IncidentId,
    DateTimeOffset CreatedAt,
    int RoundId,
    string Category,
    int Severity,
    double Confidence,
    string UserId,
    string SpeakerName,
    string Summary,
    string RuleCitation,
    string RecommendedAction,
    string Provider,
    string Model,
    bool AutomatedActionTaken,
    IReadOnlyList<ModerationIncidentEvidence> Evidence);

public sealed record ModerationIncidentEvidence(
    long MessageId,
    DateTimeOffset Timestamp,
    ModerationMessageKind Kind,
    string SpeakerName,
    string AccountName,
    string UserId,
    string Message);
