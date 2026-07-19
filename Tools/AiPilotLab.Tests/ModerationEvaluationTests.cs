using System.Text.Json;
using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class ModerationEvaluationTests
{
    [Test]
    public void ComputesFalsePositiveRatesByProviderModelPolicyAndCategory()
    {
        var first = Incident("anthropic", "cheap-a", "policy-a", "ooc_in_ic", 3, 0.9);
        var second = Incident("anthropic", "cheap-b", "policy-b", "ooc_in_ic", 2, 0.8);
        var third = Incident("openai-compatible", "cheap-a", null, "harassment", 4, 0.95);
        var reviews = new Dictionary<Guid, ModerationHumanReview>
        {
            [first.IncidentId] = Review(first, HumanModerationVerdict.Confirmed),
            [second.IncidentId] = Review(second, HumanModerationVerdict.FalsePositive, correctedSeverity: 1),
            [third.IncidentId] = Review(third, HumanModerationVerdict.NeedsContext),
        };

        var metrics = ModerationEvaluationService.ComputeMetrics(new[] { first, second, third }, reviews);

        Assert.That(metrics.ReviewCoverage, Is.EqualTo(1));
        Assert.That(metrics.FalsePositiveRate, Is.EqualTo(0.5));
        Assert.That(metrics.ByProvider.Single(group => group.Key == "anthropic").FalsePositiveRate, Is.EqualTo(0.5));
        Assert.That(metrics.ByModel.Single(group => group.Key == "cheap-b").FalsePositiveRate, Is.EqualTo(1));
        Assert.That(metrics.ByPolicy.Any(group => group.Key == "unversioned"), Is.True);
        Assert.That(metrics.ByCategory.Single(group => group.Key == "ooc_in_ic").MeanSeverityCorrection, Is.EqualTo(-1));
    }

    [Test]
    public async Task ReviewStoreAtomicallyUpsertsOneReviewPerIncident()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moderation-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var incidentPath = Path.Combine(directory, "incidents.json");
        var reviewPath = Path.Combine(directory, "reviews.json");
        try
        {
            var incident = Incident("provider", "model", "v1", "category", 2, 0.75);
            await File.WriteAllTextAsync(incidentPath, JsonSerializer.Serialize(new EvaluationIncidentDocument
            {
                Incidents = new List<EvaluationIncident> { incident },
            }));
            var store = new ModerationEvaluationStore(incidentPath, reviewPath);

            await store.UpsertReviewAsync(Review(incident, HumanModerationVerdict.NeedsContext));
            await store.UpsertReviewAsync(Review(incident, HumanModerationVerdict.Confirmed));
            var loadedIncidents = await store.LoadIncidentsAsync();
            var loadedReviews = await store.LoadReviewsAsync();

            Assert.That(loadedIncidents, Has.Count.EqualTo(1));
            Assert.That(loadedReviews, Has.Count.EqualTo(1));
            Assert.That(loadedReviews[incident.IncidentId].Verdict, Is.EqualTo(HumanModerationVerdict.Confirmed));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void CsvExportNeutralizesSpreadsheetFormulaCellsAndExcludesEvidence()
    {
        var incident = Incident("=FORMULA", "model", "v1", "category", 2, 0.75);
        var rows = ModerationEvaluationService.JoinRows(new[] { incident }, new Dictionary<Guid, ModerationHumanReview>());

        var csv = ModerationEvaluationService.ToCsv(rows);

        Assert.That(csv, Does.Contain("\"'=FORMULA\""));
        Assert.That(csv, Does.Not.Contain("secret evidence"));
    }

    private static EvaluationIncident Incident(
        string provider,
        string model,
        string? policy,
        string category,
        int severity,
        double confidence)
    {
        return new EvaluationIncident
        {
            IncidentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            RoundId = 42,
            Provider = provider,
            Model = model,
            PolicyVersion = policy,
            Category = category,
            Severity = severity,
            Confidence = confidence,
            UserId = "user",
            SpeakerName = "speaker",
            Summary = "summary",
            RuleCitation = "rule",
            RecommendedAction = "admin_review",
            Evidence = JsonSerializer.SerializeToElement(new[] { new { Message = "secret evidence" } }),
        };
    }

    private static ModerationHumanReview Review(
        EvaluationIncident incident,
        HumanModerationVerdict verdict,
        int? correctedSeverity = null) =>
        new(incident.IncidentId, verdict, "reviewer", DateTimeOffset.UtcNow, null, correctedSeverity, null);
}
