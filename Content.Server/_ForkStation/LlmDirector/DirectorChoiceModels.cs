using System.Text.Json;

namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// A game-owned choice exposed to the LLM. IDs are authoritative; descriptions are untrusted
/// prompt context and never interpreted as executable instructions.
/// </summary>
public sealed record DirectorChoiceOption(string Id, string Description);

public sealed record DirectorChoice(string Id, double Confidence, string Reason);

/// <summary>
/// Server-authored disposition for one bounded model choice. Consumers may remember this result,
/// but must still validate all game state immediately before taking any action.
/// </summary>
public enum LlmDirectorOutcomeStatus : byte
{
    Applied,
    Retained,
    Previewed,
    Selected,
    Abstained,
    Rejected,
    Failed,
    Cancelled,
}

public sealed record LlmDirectorOutcome(
    LlmDirectorOutcomeStatus Status,
    DirectorChoice? Choice,
    string Summary);

public static class DirectorChoiceParser
{
    public const string JsonSchema =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["choiceId", "confidence", "reason"],
          "properties": {
            "choiceId": { "type": "string", "minLength": 1, "maxLength": 128 },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "reason": { "type": "string", "minLength": 1, "maxLength": 300 }
          }
        }
        """;

    public static bool TryParse(
        string response,
        IReadOnlySet<string> allowedIds,
        out DirectorChoice? choice)
    {
        choice = null;
        var json = ExtractJsonObject(response);
        if (json == null)
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("choiceId" or "confidence" or "reason") ||
                    !properties.TryAdd(property.Name, property.Value))
                {
                    return false;
                }
            }

            if (properties.Count != 3 ||
                properties["choiceId"].ValueKind != JsonValueKind.String ||
                properties["confidence"].ValueKind != JsonValueKind.Number ||
                properties["reason"].ValueKind != JsonValueKind.String ||
                !properties["confidence"].TryGetDouble(out var confidence) ||
                !double.IsFinite(confidence) ||
                confidence is < 0 or > 1)
            {
                return false;
            }

            var id = properties["choiceId"].GetString();
            var reason = properties["reason"].GetString();
            if (string.IsNullOrWhiteSpace(id) ||
                id.Length > 128 ||
                !allowedIds.Contains(id) ||
                string.IsNullOrWhiteSpace(reason) ||
                reason.Length > 300)
            {
                return false;
            }

            choice = new DirectorChoice(id, confidence, NormalizeText(reason));
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

    private static string NormalizeText(string text)
    {
        return string.Join(
            " ",
            text.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
    }
}
