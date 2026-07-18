using System;
using System.Collections.Generic;
using Content.Server._ForkStation.Moderation;
using NUnit.Framework;

namespace Content.Tests.Server._ForkStation.Moderation;

[TestFixture, TestOf(typeof(ModerationResponseParser))]
[Parallelizable(ParallelScope.All)]
public static class ModerationResponseParserTest
{
    private static IReadOnlyDictionary<long, ModerationMessageRecord> Messages()
    {
        return new Dictionary<long, ModerationMessageRecord>
        {
            [11] = new(
                11,
                DateTimeOffset.UnixEpoch,
                ModerationMessageKind.Ooc,
                "Speaker A",
                "Account A",
                "user-a",
                "first message"),
            [12] = new(
                12,
                DateTimeOffset.UnixEpoch,
                ModerationMessageKind.Say,
                "Speaker B",
                "Account B",
                "user-b",
                "second message"),
        };
    }

    [Test]
    public static void AcceptsValidFencedResponseAndNormalizesText()
    {
        const string response =
            """
            ```json
            {
              "verdicts": [{
                "category": "ooc_in_ic",
                "severity": 3,
                "confidence": 0.91,
                "userId": "user-b",
                "evidenceMessageIds": [12, 12],
                "summary": "Round-external\ninformation was stated in character.",
                "ruleCitation": "OOC / IC separation",
                "recommendedAction": "admin_review"
              }]
            }
            ```
            """;

        var verdicts = ModerationResponseParser.Parse(response, Messages());

        Assert.Multiple(() =>
        {
            Assert.That(verdicts, Has.Count.EqualTo(1));
            Assert.That(verdicts[0].EvidenceMessageIds, Is.EqualTo(new long[] { 12 }));
            Assert.That(
                verdicts[0].Summary,
                Is.EqualTo("Round-external information was stated in character."));
        });
    }

    [Test]
    public static void RejectsInventedEvidenceId()
    {
        const string response =
            """
            {
              "verdicts": [{
                "category": "metacomms_or_metafriending",
                "severity": 3,
                "confidence": 0.95,
                "userId": "user-a",
                "evidenceMessageIds": [999],
                "summary": "Claimed outside-game coordination.",
                "ruleCitation": "No metacommunications",
                "recommendedAction": "admin_review"
              }]
            }
            """;

        Assert.That(
            ModerationResponseParser.Parse(response, Messages()),
            Is.Empty);
    }

    [Test]
    public static void RejectsEvidenceBelongingToAnotherUser()
    {
        const string response =
            """
            {
              "verdicts": [{
                "category": "extreme_bigotry_or_harassment",
                "severity": 4,
                "confidence": 0.99,
                "userId": "user-a",
                "evidenceMessageIds": [12],
                "summary": "Evidence was attributed to the wrong account.",
                "ruleCitation": "Extreme harassment",
                "recommendedAction": "urgent_admin_review"
              }]
            }
            """;

        Assert.That(
            ModerationResponseParser.Parse(response, Messages()),
            Is.Empty);
    }

    [Test]
    public static void RejectsUnknownUserAndUnapprovedEnums()
    {
        const string response =
            """
            {
              "verdicts": [
                {
                  "category": "general_bad_behavior",
                  "severity": 3,
                  "confidence": 0.9,
                  "userId": "user-a",
                  "evidenceMessageIds": [11],
                  "summary": "Category is not in the allowlist.",
                  "ruleCitation": "Unknown",
                  "recommendedAction": "admin_review"
                },
                {
                  "category": "ooc_in_ic",
                  "severity": 3,
                  "confidence": 0.9,
                  "userId": "invented-user",
                  "evidenceMessageIds": [11],
                  "summary": "User was invented.",
                  "ruleCitation": "OOC / IC separation",
                  "recommendedAction": "ban"
                }
              ]
            }
            """;

        Assert.That(
            ModerationResponseParser.Parse(response, Messages()),
            Is.Empty);
    }

    [Test]
    public static void RejectsExtraDuplicateOrNullFields()
    {
        const string extra =
            """
            {"verdicts":[],"command":"ban"}
            """;
        const string duplicate =
            """
            {"verdicts":[],"verdicts":[]}
            """;
        const string nullEvidence =
            """
            {"verdicts":[{"category":"ooc_in_ic","severity":3,"confidence":0.9,"userId":"user-a","evidenceMessageIds":null,"summary":"Invalid.","ruleCitation":"Rule","recommendedAction":"admin_review"}]}
            """;

        Assert.Multiple(() =>
        {
            Assert.That(ModerationResponseParser.Parse(extra, Messages()), Is.Empty);
            Assert.That(ModerationResponseParser.Parse(duplicate, Messages()), Is.Empty);
            Assert.That(ModerationResponseParser.Parse(nullEvidence, Messages()), Is.Empty);
        });
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{\"verdicts\":")]
    public static void MalformedResponsesAreSafeEmpty(string response)
    {
        Assert.That(
            ModerationResponseParser.Parse(response, Messages()),
            Is.Empty);
    }
}
