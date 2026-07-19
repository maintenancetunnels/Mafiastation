using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mafiastation.AiPilotLab;

public sealed class ModerationEvaluationStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private static readonly JsonSerializerOptions WriteOptions = new(ReadOptions)
    {
        WriteIndented = true,
    };

    public string IncidentPath { get; }
    public string ReviewPath { get; }

    public ModerationEvaluationStore(string incidentPath, string reviewPath)
    {
        IncidentPath = Path.GetFullPath(incidentPath);
        ReviewPath = Path.GetFullPath(reviewPath);
    }

    public async Task<IReadOnlyList<EvaluationIncident>> LoadIncidentsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(IncidentPath))
            throw new FileNotFoundException("Moderation incident audit was not found.", IncidentPath);
        await using var stream = File.OpenRead(IncidentPath);
        var document = await JsonSerializer.DeserializeAsync<EvaluationIncidentDocument>(stream, ReadOptions, cancellationToken)
            ?? throw new InvalidDataException("Moderation incident audit was empty.");
        if (document.Version != 1)
            throw new InvalidDataException($"Unsupported moderation incident document version {document.Version}.");
        if (document.Incidents.Count > 1_000_000)
            throw new InvalidDataException("Moderation incident audit exceeds the one-million incident safety limit.");
        var ids = new HashSet<Guid>();
        foreach (var incident in document.Incidents)
        {
            if (incident.IncidentId == Guid.Empty || !ids.Add(incident.IncidentId))
                throw new InvalidDataException("Moderation incident audit contains an empty or duplicate incident ID.");
        }
        return document.Incidents;
    }

    public async Task<IReadOnlyDictionary<Guid, ModerationHumanReview>> LoadReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ReviewPath))
            return new Dictionary<Guid, ModerationHumanReview>();
        await using var stream = File.OpenRead(ReviewPath);
        var document = await JsonSerializer.DeserializeAsync<ModerationReviewDocument>(stream, ReadOptions, cancellationToken)
            ?? throw new InvalidDataException("Moderation review document was empty.");
        if (document.Version != 1)
            throw new InvalidDataException($"Unsupported moderation review document version {document.Version}.");
        var result = new Dictionary<Guid, ModerationHumanReview>();
        foreach (var review in document.Reviews)
        {
            ValidateReview(review);
            if (!result.TryAdd(review.IncidentId, review))
                throw new InvalidDataException($"Moderation review document contains duplicate incident {review.IncidentId}.");
        }
        return result;
    }

    public async Task UpsertReviewAsync(ModerationHumanReview review, CancellationToken cancellationToken = default)
    {
        ValidateReview(review);
        var reviews = new Dictionary<Guid, ModerationHumanReview>(await LoadReviewsAsync(cancellationToken));
        reviews[review.IncidentId] = review;
        var document = new ModerationReviewDocument
        {
            Reviews = reviews.Values.OrderBy(value => value.ReviewedAt).ToList(),
        };
        var directory = Path.GetDirectoryName(ReviewPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = ReviewPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16_384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, WriteOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, ReviewPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static HumanModerationVerdict ParseVerdict(string value) => value.Trim().ToLowerInvariant() switch
    {
        "confirmed" => HumanModerationVerdict.Confirmed,
        "false-positive" or "false_positive" or "falsepositive" => HumanModerationVerdict.FalsePositive,
        "needs-context" or "needs_context" or "needscontext" => HumanModerationVerdict.NeedsContext,
        _ => throw new ArgumentException("Verdict must be confirmed, false-positive, or needs-context."),
    };

    public static string? NormalizeOptional(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = string.Join(" ", value.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
            throw new ArgumentException($"{field} must be at most {maximumLength} non-control characters.");
        return normalized;
    }

    private static void ValidateReview(ModerationHumanReview review)
    {
        if (review.IncidentId == Guid.Empty)
            throw new InvalidDataException("Review incident ID is required.");
        if (!Enum.IsDefined(review.Verdict))
            throw new InvalidDataException("Review verdict is invalid.");
        if (string.IsNullOrWhiteSpace(review.Reviewer) || review.Reviewer.Length > 128 || review.Reviewer.Any(char.IsControl))
            throw new InvalidDataException("Review reviewer is required and must be at most 128 non-control characters.");
        if (review.CorrectedSeverity is < 1 or > 4)
            throw new InvalidDataException("Corrected severity must be from 1 through 4.");
        if (review.CorrectedCategory is { Length: > 64 } || review.CorrectedCategory?.Any(char.IsControl) == true)
            throw new InvalidDataException("Corrected category must be at most 64 non-control characters.");
        if (review.Notes is { Length: > 1000 } || review.Notes?.Any(char.IsControl) == true)
            throw new InvalidDataException("Review notes must be at most 1000 non-control characters.");
    }
}
