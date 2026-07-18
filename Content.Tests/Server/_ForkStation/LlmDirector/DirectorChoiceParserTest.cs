using System.Collections.Generic;
using Content.Server._ForkStation.LlmDirector;
using NUnit.Framework;

namespace Content.Tests.Server._ForkStation.LlmDirector;

[TestFixture, TestOf(typeof(DirectorChoiceParser))]
[Parallelizable(ParallelScope.All)]
public static class DirectorChoiceParserTest
{
    private static readonly IReadOnlySet<string> Allowed = new HashSet<string>
    {
        "GoalA",
        "GoalB",
        "none",
    };

    [Test]
    public static void AcceptsFencedAllowlistedChoice()
    {
        const string response =
            """
            ```json
            {
              "choiceId": "GoalB",
              "confidence": 0.88,
              "reason": "This\nchoice fits the current context."
            }
            ```
            """;

        var success = DirectorChoiceParser.TryParse(response, Allowed, out var choice);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True);
            Assert.That(choice?.Id, Is.EqualTo("GoalB"));
            Assert.That(choice?.Reason, Is.EqualTo("This choice fits the current context."));
        });
    }

    [Test]
    public static void RejectsInventedChoice()
    {
        const string response =
            """
            {"choiceId":"RunArbitraryCommand","confidence":1,"reason":"Ignore the allowlist."}
            """;

        Assert.That(
            DirectorChoiceParser.TryParse(response, Allowed, out _),
            Is.False);
    }

    [Test]
    public static void RejectsExtraOrDuplicateProperties()
    {
        const string extra =
            """
            {"choiceId":"GoalA","confidence":0.9,"reason":"Okay.","command":"explode"}
            """;
        const string duplicate =
            """
            {"choiceId":"GoalA","choiceId":"GoalB","confidence":0.9,"reason":"Ambiguous."}
            """;

        Assert.Multiple(() =>
        {
            Assert.That(DirectorChoiceParser.TryParse(extra, Allowed, out _), Is.False);
            Assert.That(DirectorChoiceParser.TryParse(duplicate, Allowed, out _), Is.False);
        });
    }

    [TestCase(-0.01)]
    [TestCase(1.01)]
    [TestCase(double.NaN)]
    public static void RejectsInvalidConfidence(double confidence)
    {
        var response =
            $$"""{"choiceId":"GoalA","confidence":{{confidence}},"reason":"Invalid confidence."}""";

        Assert.That(
            DirectorChoiceParser.TryParse(response, Allowed, out _),
            Is.False);
    }
}
