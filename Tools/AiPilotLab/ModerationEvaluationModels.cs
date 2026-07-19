using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mafiastation.AiPilotLab;

public sealed class EvaluationIncidentDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("incidents")]
    public List<EvaluationIncident> Incidents { get; init; } = new();
}

public sealed class EvaluationIncident
{
    public Guid IncidentId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int RoundId { get; init; }
    public string Category { get; init; } = string.Empty;
    public int Severity { get; init; }
    public double Confidence { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string SpeakerName { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string RuleCitation { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? PolicyVersion { get; init; }
    public bool AutomatedActionTaken { get; init; }
    public JsonElement Evidence { get; init; }

    [JsonIgnore]
    public string EffectivePolicyVersion => string.IsNullOrWhiteSpace(PolicyVersion) ? "unversioned" : PolicyVersion;
}

public enum HumanModerationVerdict : byte
{
    Confirmed,
    FalsePositive,
    NeedsContext,
}

public sealed record ModerationHumanReview(
    Guid IncidentId,
    HumanModerationVerdict Verdict,
    string Reviewer,
    DateTimeOffset ReviewedAt,
    string? CorrectedCategory,
    int? CorrectedSeverity,
    string? Notes);

public sealed class ModerationReviewDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("reviews")]
    public List<ModerationHumanReview> Reviews { get; init; } = new();
}

public sealed record ModerationMetricGroup(
    string Key,
    int Incidents,
    int Reviewed,
    int Confirmed,
    int FalsePositives,
    int NeedsContext,
    double ReviewCoverage,
    double FalsePositiveRate,
    double MeanConfidence,
    double? MeanSeverityCorrection);

public sealed record ModerationEvaluationMetrics(
    DateTimeOffset GeneratedAt,
    int Incidents,
    int Reviewed,
    int Unreviewed,
    int Confirmed,
    int FalsePositives,
    int NeedsContext,
    double ReviewCoverage,
    double FalsePositiveRate,
    IReadOnlyList<ModerationMetricGroup> ByProvider,
    IReadOnlyList<ModerationMetricGroup> ByModel,
    IReadOnlyList<ModerationMetricGroup> ByPolicy,
    IReadOnlyList<ModerationMetricGroup> ByCategory);

public sealed record ModerationEvaluationRow(
    Guid IncidentId,
    DateTimeOffset CreatedAt,
    int RoundId,
    string Provider,
    string Model,
    string PolicyVersion,
    string Category,
    int Severity,
    double Confidence,
    HumanModerationVerdict? HumanVerdict,
    string? CorrectedCategory,
    int? CorrectedSeverity,
    string? Reviewer,
    DateTimeOffset? ReviewedAt,
    string? Notes);

public sealed record ModerationEvaluationExport(
    int Version,
    DateTimeOffset GeneratedAt,
    string IncidentSource,
    ModerationEvaluationMetrics Metrics,
    IReadOnlyList<ModerationEvaluationRow> Rows);
