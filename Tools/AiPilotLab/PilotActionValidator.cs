using System.Text.Json;

namespace Mafiastation.AiPilotLab;

public sealed record PilotActionValidation(bool IsValid, PilotRequest? Request, string? Error)
{
    public static PilotActionValidation Valid(PilotRequest request) => new(true, request, null);
    public static PilotActionValidation Invalid(string error) => new(false, null, error);
}

public sealed class PilotActionValidator
{
    private static readonly HashSet<string> Directions = new(StringComparer.OrdinalIgnoreCase)
    {
        "north",
        "south",
        "east",
        "west",
        "northeast",
        "northwest",
        "southeast",
        "southwest",
    };

    private static readonly HashSet<string> TargetActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "interact",
        "use",
        "pickup",
    };

    private static readonly HashSet<string> ArgumentlessActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "status",
        "observe",
        "drop",
        "swap_hands",
        "goal_status",
        "stop",
    };

    public PilotActionValidation Validate(
        JsonElement modelAction,
        PilotResponse? latestObservation,
        bool allowSpeech,
        double maximumGoalDistance = 20)
    {
        if (modelAction.ValueKind != JsonValueKind.Object ||
            !modelAction.TryGetProperty("action", out var actionElement) ||
            actionElement.ValueKind != JsonValueKind.String)
        {
            return PilotActionValidation.Invalid("Model output must be an object with a string action property.");
        }

        var action = actionElement.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
        var arguments = modelAction.TryGetProperty("arguments", out var argumentElement)
            ? argumentElement
            : JsonSerializer.SerializeToElement(new { });
        if (arguments.ValueKind != JsonValueKind.Object)
            return PilotActionValidation.Invalid("Action arguments must be a JSON object.");

        if (ArgumentlessActions.Contains(action))
        {
            if (action == "drop" && !PilotJson.CapabilityAllows(latestObservation, "canDrop"))
                return PilotActionValidation.Invalid("Latest pilot capacity snapshot does not allow dropping.");
            if (action == "swap_hands" && !PilotJson.CapabilityAllows(latestObservation, "canSwapHands"))
                return PilotActionValidation.Invalid("Latest pilot capacity snapshot does not allow swapping hands.");
            return PilotActionValidation.Valid(PilotRequest.Create(action));
        }
        if (action == "move")
        {
            if (!PilotJson.CapabilityAllows(latestObservation, "canMove"))
                return PilotActionValidation.Invalid("Latest pilot capacity snapshot does not allow movement.");
            return ValidateMove(arguments);
        }
        if (TargetActions.Contains(action))
        {
            var capability = action == "pickup" ? "canPickup" : "canInteract";
            if (!PilotJson.CapabilityAllows(latestObservation, capability))
                return PilotActionValidation.Invalid($"Latest pilot capacity snapshot does not allow {action}.");
            return ValidateTargetAction(action == "use" ? "interact" : action, arguments, latestObservation);
        }
        if (action == "goal")
        {
            if (!PilotJson.CapabilityAllows(latestObservation, "canUseGoals"))
                return PilotActionValidation.Invalid("Latest pilot capacity snapshot does not allow deterministic goals.");
            if (arguments.TryGetProperty("kind", out var goalKind) &&
                goalKind.ValueKind == JsonValueKind.String)
            {
                var kind = goalKind.GetString()?.Trim().ToLowerInvariant();
                if (kind == "interact" &&
                    !PilotJson.CapabilityAllows(latestObservation, "canInteract"))
                {
                    return PilotActionValidation.Invalid(
                        "Latest pilot capacity snapshot does not allow interaction goals.");
                }
                if (kind == "pickup" &&
                    !PilotJson.CapabilityAllows(latestObservation, "canPickup"))
                {
                    return PilotActionValidation.Invalid(
                        "Latest pilot capacity snapshot does not allow pickup goals.");
                }
            }
            return ValidateGoal(arguments, latestObservation, maximumGoalDistance);
        }
        if (action == "say")
        {
            if (!PilotJson.CapabilityAllows(latestObservation, "canSpeak"))
                return PilotActionValidation.Invalid("Latest pilot capacity snapshot does not allow speech.");
            return ValidateSpeech(arguments, allowSpeech);
        }
        return PilotActionValidation.Invalid($"Action '{action}' is not in the pilot allowlist.");
    }

    private static PilotActionValidation ValidateMove(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("direction", out var directionElement) ||
            directionElement.ValueKind != JsonValueKind.String ||
            !Directions.Contains(directionElement.GetString() ?? string.Empty))
        {
            return PilotActionValidation.Invalid("Move direction must be a cardinal or diagonal direction.");
        }

        var duration = 250;
        if (arguments.TryGetProperty("durationMs", out var durationElement))
        {
            if (!durationElement.TryGetInt32(out duration))
                return PilotActionValidation.Invalid("Move durationMs must be an integer.");
            duration = Math.Clamp(duration, 50, 1000);
        }

        var normalized = JsonSerializer.SerializeToElement(new
        {
            direction = directionElement.GetString()!.ToLowerInvariant(),
            durationMs = duration,
        });
        return PilotActionValidation.Valid(PilotRequest.Create("move", normalized));
    }

    private static PilotActionValidation ValidateTargetAction(
        string action,
        JsonElement arguments,
        PilotResponse? observation)
    {
        if (!TryGetObservedTarget(arguments, observation, out var targetId, out var error))
            return PilotActionValidation.Invalid(error);
        return PilotActionValidation.Valid(PilotRequest.Create(action, JsonSerializer.SerializeToElement(new { targetId })));
    }

    private static PilotActionValidation ValidateGoal(
        JsonElement arguments,
        PilotResponse? observation,
        double maximumGoalDistance)
    {
        if (!arguments.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            return PilotActionValidation.Invalid("Goal kind is required.");
        var kind = kindElement.GetString()?.Trim().ToLowerInvariant();
        if (kind is "interact" or "pickup" or "move_to_entity")
        {
            if (!TryGetObservedTarget(arguments, observation, out var targetId, out var error))
                return PilotActionValidation.Invalid(error);
            var range = ReadRange(arguments);
            if (range == null)
                return PilotActionValidation.Invalid("Goal range must be a finite number from 0.25 through 5.");
            return PilotActionValidation.Valid(PilotRequest.Create("goal", JsonSerializer.SerializeToElement(new
            {
                kind,
                targetId,
                range,
            })));
        }

        if (kind == "move_relative")
        {
            if (!TryReadFinite(arguments, "x", out var deltaX) || !TryReadFinite(arguments, "y", out var deltaY))
                return PilotActionValidation.Invalid("Relative movement goals require finite x and y offsets.");
            if (Math.Sqrt(Math.Pow(deltaX, 2) + Math.Pow(deltaY, 2)) > maximumGoalDistance)
                return PilotActionValidation.Invalid($"Relative movement goal exceeds the {maximumGoalDistance:0.##}-tile limit.");
            var relativeRange = ReadRange(arguments);
            if (relativeRange == null)
                return PilotActionValidation.Invalid("Goal range must be a finite number from 0.25 through 5.");
            return PilotActionValidation.Valid(PilotRequest.Create("goal", JsonSerializer.SerializeToElement(new
            {
                kind,
                x = deltaX,
                y = deltaY,
                range = relativeRange,
            })));
        }

        if (kind != "move_to")
            return PilotActionValidation.Invalid("Goal kind must be move_to, move_relative, move_to_entity, interact, or pickup.");
        if (!TryReadFinite(arguments, "x", out var x) || !TryReadFinite(arguments, "y", out var y))
            return PilotActionValidation.Invalid("Coordinate goals require finite x and y values.");
        if (!TryGetSelfPosition(observation, out var selfX, out var selfY))
            return PilotActionValidation.Invalid("A recent observation with self position is required for coordinate goals.");
        if (Math.Sqrt(Math.Pow(x - selfX, 2) + Math.Pow(y - selfY, 2)) > maximumGoalDistance)
            return PilotActionValidation.Invalid($"Coordinate goal exceeds the {maximumGoalDistance:0.##}-tile limit.");
        var coordinateRange = ReadRange(arguments);
        if (coordinateRange == null)
            return PilotActionValidation.Invalid("Goal range must be a finite number from 0.25 through 5.");
        return PilotActionValidation.Valid(PilotRequest.Create("goal", JsonSerializer.SerializeToElement(new
        {
            kind,
            x,
            y,
            range = coordinateRange,
        })));
    }

    private static PilotActionValidation ValidateSpeech(JsonElement arguments, bool allowSpeech)
    {
        if (!allowSpeech)
            return PilotActionValidation.Invalid("Speech is disabled for this model agent.");
        if (!arguments.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            return PilotActionValidation.Invalid("Say requires a string text argument.");
        var text = textElement.GetString()?.Trim() ?? string.Empty;
        if (text.Length is < 1 or > 160 || text.StartsWith('/') || text.Any(char.IsControl))
            return PilotActionValidation.Invalid("Speech must be 1-160 non-control characters and may not begin with '/'.");
        return PilotActionValidation.Valid(PilotRequest.Create("say", JsonSerializer.SerializeToElement(new { text })));
    }

    private static bool TryGetObservedTarget(
        JsonElement arguments,
        PilotResponse? observation,
        out int targetId,
        out string error)
    {
        targetId = default;
        if (!arguments.TryGetProperty("targetId", out var targetElement) || !targetElement.TryGetInt32(out targetId))
        {
            error = "Target action requires an integer targetId.";
            return false;
        }
        if (observation == null || !PilotJson.ObservedEntityIds(observation).Contains(targetId))
        {
            error = $"Target {targetId} is not present in the latest bounded observation.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static double? ReadRange(JsonElement arguments)
    {
        var range = 1.25;
        if (arguments.TryGetProperty("range", out var rangeElement) &&
            (!rangeElement.TryGetDouble(out range) || !double.IsFinite(range)))
        {
            return null;
        }
        return range is >= 0.25 and <= 5 ? range : null;
    }

    private static bool TryReadFinite(JsonElement arguments, string property, out double value)
    {
        value = default;
        return arguments.TryGetProperty(property, out var element) &&
               element.TryGetDouble(out value) &&
               double.IsFinite(value);
    }

    private static bool TryGetSelfPosition(PilotResponse? observation, out double x, out double y)
    {
        x = default;
        y = default;
        if (observation?.Data.ValueKind != JsonValueKind.Object ||
            !observation.Data.TryGetProperty("self", out var self) ||
            !self.TryGetProperty("position", out var position))
        {
            return false;
        }
        return TryReadFinite(position, "x", out x) && TryReadFinite(position, "y", out y);
    }
}
