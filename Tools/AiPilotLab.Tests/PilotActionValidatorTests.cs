using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class PilotActionValidatorTests
{
    private readonly PilotActionValidator _validator = new();

    [Test]
    public void RejectsTargetOutsideLatestObservation()
    {
        var observation = PilotJsonTests.Response(new
        {
            entities = new[] { new { id = 12 } },
        });
        var action = JsonSerializer.SerializeToElement(new
        {
            action = "interact",
            arguments = new { targetId = 13 },
        });

        var result = _validator.Validate(action, observation, false);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Does.Contain("not present"));
    }

    [Test]
    public void NormalizesObservedTargetAndDropsExtraArguments()
    {
        var observation = PilotJsonTests.Response(new
        {
            entities = new[] { new { id = 12 } },
        });
        var action = JsonSerializer.SerializeToElement(new
        {
            action = "use",
            arguments = new { targetId = 12, ignored = "value" },
        });

        var result = _validator.Validate(action, observation, false);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Request!.Action, Is.EqualTo("interact"));
        Assert.That(result.Request.Arguments.TryGetProperty("targetId", out var target), Is.True);
        Assert.That(target.GetInt32(), Is.EqualTo(12));
        Assert.That(result.Request.Arguments.TryGetProperty("ignored", out _), Is.False);
    }

    [Test]
    public void ClampsMovementDuration()
    {
        var action = JsonSerializer.SerializeToElement(new
        {
            action = "move",
            arguments = new { direction = "NORTH", durationMs = 50_000 },
        });

        var result = _validator.Validate(action, null, false);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Request!.Arguments.GetProperty("direction").GetString(), Is.EqualTo("north"));
        Assert.That(result.Request.Arguments.GetProperty("durationMs").GetInt32(), Is.EqualTo(1000));
    }

    [Test]
    public void SpeechRequiresExplicitGateAndRejectsCommandPrefix()
    {
        var speech = JsonSerializer.SerializeToElement(new
        {
            action = "say",
            arguments = new { text = "hello" },
        });
        var command = JsonSerializer.SerializeToElement(new
        {
            action = "say",
            arguments = new { text = "/admin" },
        });

        Assert.That(_validator.Validate(speech, null, false).IsValid, Is.False);
        Assert.That(_validator.Validate(speech, null, true).IsValid, Is.True);
        Assert.That(_validator.Validate(command, null, true).IsValid, Is.False);
    }

    [Test]
    public void CoordinateGoalIsBoundedFromObservedSelf()
    {
        var observation = PilotJsonTests.Response(new
        {
            self = new { position = new { x = 5, y = 8 } },
            entities = Array.Empty<object>(),
        });
        var near = JsonSerializer.SerializeToElement(new
        {
            action = "goal",
            arguments = new { kind = "move_to", x = 8, y = 8 },
        });
        var far = JsonSerializer.SerializeToElement(new
        {
            action = "goal",
            arguments = new { kind = "move_to", x = 50, y = 8 },
        });

        Assert.That(_validator.Validate(near, observation, false, 10).IsValid, Is.True);
        Assert.That(_validator.Validate(far, observation, false, 10).IsValid, Is.False);
    }

    [Test]
    public void RelativeGoalIsBoundedWithoutInventingAnAbsolutePosition()
    {
        var near = JsonSerializer.SerializeToElement(new
        {
            action = "goal",
            arguments = new { kind = "move_relative", x = 3, y = -1 },
        });
        var far = JsonSerializer.SerializeToElement(new
        {
            action = "goal",
            arguments = new { kind = "move_relative", x = 30, y = 0 },
        });

        Assert.That(_validator.Validate(near, null, false, 10).IsValid, Is.True);
        Assert.That(_validator.Validate(far, null, false, 10).IsValid, Is.False);
    }
}
