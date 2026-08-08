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
        var valid = _validator.Validate(speech, null, true);
        Assert.That(valid.IsValid, Is.True);
        Assert.That(valid.Request!.Arguments.GetProperty("channel").GetString(), Is.EqualTo("local"));
        Assert.That(_validator.Validate(command, null, true).IsValid, Is.False);
    }

    [Test]
    public void SpeechAllowsOnlyExplicitLocalOrCommonRadioChannels()
    {
        var radio = JsonSerializer.SerializeToElement(new
        {
            action = "say",
            arguments = new { text = "Engineering, power is stable.", channel = "RADIO", ignored = true },
        });
        var department = JsonSerializer.SerializeToElement(new
        {
            action = "say",
            arguments = new { text = "Status report", channel = "engineering" },
        });
        var implicitPrefix = JsonSerializer.SerializeToElement(new
        {
            action = "say",
            arguments = new { text = ";Security to arrivals", channel = "local" },
        });

        var valid = _validator.Validate(radio, null, true);

        Assert.Multiple(() =>
        {
            Assert.That(valid.IsValid, Is.True);
            Assert.That(valid.Request!.Arguments.GetProperty("channel").GetString(), Is.EqualTo("radio"));
            Assert.That(valid.Request.Arguments.TryGetProperty("ignored", out _), Is.False);
            Assert.That(_validator.Validate(department, null, true).IsValid, Is.False);
            Assert.That(_validator.Validate(implicitPrefix, null, true).IsValid, Is.False);
        });
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

    [Test]
    public void WrongJsonScalarTypesAreRejectedWithoutThrowing()
    {
        var observation = PilotJsonTests.Response(new
        {
            self = new { position = new { x = 0, y = 0 } },
            capabilities = new
            {
                canMove = true,
                canUseGoals = true,
                canInteract = true,
            },
            entities = new[] { new { id = 12 } },
        });
        var actions = new[]
        {
            """{"action":"move","arguments":{"direction":"north","durationMs":"250"}}""",
            """{"action":"goal","arguments":{"kind":"move_relative","x":"3","y":0}}""",
            """{"action":"goal","arguments":{"kind":"move_relative","x":3,"y":0,"range":"1.25"}}""",
            """{"action":"interact","arguments":{"targetId":"12"}}""",
        };

        foreach (var json in actions)
        {
            using var document = JsonDocument.Parse(json);
            PilotActionValidation? result = null;
            Assert.That(
                () => result = _validator.Validate(document.RootElement, observation, false),
                Throws.Nothing,
                json);
            Assert.That(result!.IsValid, Is.False, json);
        }
    }

    [Test]
    public void RejectsActionWhenCurrentCapacityIsFalse()
    {
        var observation = PilotJsonTests.Response(new
        {
            self = new { position = new { x = 0, y = 0 } },
            capabilities = new
            {
                canMove = false,
                canUseGoals = false,
                canInteract = true,
            },
        });
        var movement = JsonSerializer.SerializeToElement(new
        {
            action = "move",
            arguments = new { direction = "north" },
        });
        var goal = JsonSerializer.SerializeToElement(new
        {
            action = "goal",
            arguments = new { kind = "move_relative", x = 1, y = 0 },
        });

        Assert.That(_validator.Validate(movement, observation, false).IsValid, Is.False);
        Assert.That(_validator.Validate(goal, observation, false).IsValid, Is.False);
    }

    [Test]
    public void RejectsPickupGoalWhenFinalActionCapacityIsFalse()
    {
        var observation = PilotJsonTests.Response(new
        {
            self = new { position = new { x = 0, y = 0 } },
            capabilities = new
            {
                canUseGoals = true,
                canPickup = false,
            },
            entities = new[]
            {
                new { id = 7 },
            },
        });
        var goal = JsonSerializer.SerializeToElement(new
        {
            action = "goal",
            arguments = new { kind = "pickup", targetId = 7 },
        });

        var validation = _validator.Validate(goal, observation, false);

        Assert.That(validation.IsValid, Is.False);
        Assert.That(validation.Error, Does.Contain("pickup goals"));
    }
}
