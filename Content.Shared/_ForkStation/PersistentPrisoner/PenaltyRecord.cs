using System.Text.Json.Serialization;

namespace Content.Shared._ForkStation.PersistentPrisoner;

/// <summary>
/// A single penalty entry for a player account. Tracks who issued it, why, and how many rounds remain.
/// </summary>
[Serializable]
public sealed class PenaltyRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// The user ID (GUID) of the penalized player.
    /// </summary>
    [JsonPropertyName("playerUserId")]
    public string PlayerUserId { get; set; } = string.Empty;

    /// <summary>
    /// The user ID of the security officer or admin who issued the penalty.
    /// </summary>
    [JsonPropertyName("issuedBy")]
    public string IssuedBy { get; set; } = string.Empty;

    /// <summary>
    /// The name of the person who issued it (for display).
    /// </summary>
    [JsonPropertyName("issuedByName")]
    public string IssuedByName { get; set; } = string.Empty;

    /// <summary>
    /// Number of penalty rounds originally assigned.
    /// </summary>
    [JsonPropertyName("roundsAssigned")]
    public int RoundsAssigned { get; set; }

    /// <summary>
    /// Number of penalty rounds already served.
    /// </summary>
    [JsonPropertyName("roundsServed")]
    public int RoundsServed { get; set; }

    /// <summary>
    /// Reason for the penalty.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// When the penalty was issued (UTC).
    /// </summary>
    [JsonPropertyName("issuedAt")]
    public DateTime IssuedAt { get; set; }

    /// <summary>
    /// The round ID during which the penalty was issued.
    /// </summary>
    [JsonPropertyName("issuedRoundId")]
    public int IssuedRoundId { get; set; }

    /// <summary>
    /// Whether this penalty was issued by an admin (true) or security (false).
    /// Admin-issued penalties can only be removed by admins.
    /// </summary>
    [JsonPropertyName("adminIssued")]
    public bool AdminIssued { get; set; }

    /// <summary>
    /// How many rounds remain to serve.
    /// </summary>
    [JsonIgnore]
    public int RoundsRemaining => Math.Max(0, RoundsAssigned - RoundsServed);

    /// <summary>
    /// Whether this penalty has been fully served.
    /// </summary>
    [JsonIgnore]
    public bool IsServed => RoundsServed >= RoundsAssigned;
}
