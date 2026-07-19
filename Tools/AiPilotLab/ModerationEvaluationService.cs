using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mafiastation.AiPilotLab;

public static class ModerationEvaluationService
{
    public static ModerationEvaluationMetrics ComputeMetrics(
        IReadOnlyList<EvaluationIncident> incidents,
        IReadOnlyDictionary<Guid, ModerationHumanReview> reviews)
    {
        var incidentIds = incidents.Select(incident => incident.IncidentId).ToHashSet();
        var applicable = reviews
            .Where(pair => incidentIds.Contains(pair.Key))
            .ToDictionary();
        var confirmed = applicable.Values.Count(review => review.Verdict == HumanModerationVerdict.Confirmed);
        var falsePositives = applicable.Values.Count(review => review.Verdict == HumanModerationVerdict.FalsePositive);
        var needsContext = applicable.Values.Count(review => review.Verdict == HumanModerationVerdict.NeedsContext);
        return new ModerationEvaluationMetrics(
            DateTimeOffset.UtcNow,
            incidents.Count,
            applicable.Count,
            incidents.Count - applicable.Count,
            confirmed,
            falsePositives,
            needsContext,
            Ratio(applicable.Count, incidents.Count),
            Ratio(falsePositives, confirmed + falsePositives),
            Group(incidents, applicable, incident => ValueOrUnknown(incident.Provider)),
            Group(incidents, applicable, incident => ValueOrUnknown(incident.Model)),
            Group(incidents, applicable, incident => incident.EffectivePolicyVersion),
            Group(incidents, applicable, incident => ValueOrUnknown(incident.Category)));
    }

    public static IReadOnlyList<ModerationEvaluationRow> JoinRows(
        IReadOnlyList<EvaluationIncident> incidents,
        IReadOnlyDictionary<Guid, ModerationHumanReview> reviews)
    {
        return incidents
            .OrderBy(incident => incident.CreatedAt)
            .Select(incident =>
            {
                reviews.TryGetValue(incident.IncidentId, out var review);
                return new ModerationEvaluationRow(
                    incident.IncidentId,
                    incident.CreatedAt,
                    incident.RoundId,
                    ValueOrUnknown(incident.Provider),
                    ValueOrUnknown(incident.Model),
                    incident.EffectivePolicyVersion,
                    ValueOrUnknown(incident.Category),
                    incident.Severity,
                    incident.Confidence,
                    review?.Verdict,
                    review?.CorrectedCategory,
                    review?.CorrectedSeverity,
                    review?.Reviewer,
                    review?.ReviewedAt,
                    review?.Notes);
            })
            .ToArray();
    }

    public static async Task WriteExportAsync(
        ModerationEvaluationExport export,
        string path,
        string format,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16_384,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await JsonSerializer.SerializeAsync(stream, export, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            else if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(temporary, ToCsv(export.Rows), new UTF8Encoding(false), cancellationToken);
            }
            else
            {
                throw new ArgumentException("Export format must be json or csv.");
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static string ToCsv(IReadOnlyList<ModerationEvaluationRow> rows)
    {
        var output = new StringBuilder();
        output.AppendLine("incident_id,created_at,round_id,provider,model,policy_version,category,severity,confidence,human_verdict,corrected_category,corrected_severity,reviewer,reviewed_at,notes");
        foreach (var row in rows)
        {
            var cells = new[]
            {
                row.IncidentId.ToString(),
                row.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                row.RoundId.ToString(CultureInfo.InvariantCulture),
                row.Provider,
                row.Model,
                row.PolicyVersion,
                row.Category,
                row.Severity.ToString(CultureInfo.InvariantCulture),
                row.Confidence.ToString("0.######", CultureInfo.InvariantCulture),
                row.HumanVerdict?.ToString() ?? string.Empty,
                row.CorrectedCategory ?? string.Empty,
                row.CorrectedSeverity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.Reviewer ?? string.Empty,
                row.ReviewedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.Notes ?? string.Empty,
            };
            output.AppendLine(string.Join(',', cells.Select(CsvCell)));
        }
        return output.ToString();
    }

    private static IReadOnlyList<ModerationMetricGroup> Group(
        IReadOnlyList<EvaluationIncident> incidents,
        IReadOnlyDictionary<Guid, ModerationHumanReview> reviews,
        Func<EvaluationIncident, string> keySelector)
    {
        return incidents
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupIncidents = group.ToArray();
                var groupReviews = groupIncidents
                    .Select(incident => reviews.GetValueOrDefault(incident.IncidentId))
                    .Where(review => review != null)
                    .Cast<ModerationHumanReview>()
                    .ToArray();
                var confirmed = groupReviews.Count(review => review.Verdict == HumanModerationVerdict.Confirmed);
                var falsePositives = groupReviews.Count(review => review.Verdict == HumanModerationVerdict.FalsePositive);
                var needsContext = groupReviews.Count(review => review.Verdict == HumanModerationVerdict.NeedsContext);
                var corrections = groupIncidents
                    .Select(incident => (Incident: incident, Review: reviews.GetValueOrDefault(incident.IncidentId)))
                    .Where(pair => pair.Review?.CorrectedSeverity != null)
                    .Select(pair => pair.Review!.CorrectedSeverity!.Value - pair.Incident.Severity)
                    .ToArray();
                return new ModerationMetricGroup(
                    group.Key,
                    groupIncidents.Length,
                    groupReviews.Length,
                    confirmed,
                    falsePositives,
                    needsContext,
                    Ratio(groupReviews.Length, groupIncidents.Length),
                    Ratio(falsePositives, confirmed + falsePositives),
                    groupIncidents.Average(incident => incident.Confidence),
                    corrections.Length == 0 ? null : corrections.Average());
            })
            .ToArray();
    }

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;

    private static string ValueOrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
