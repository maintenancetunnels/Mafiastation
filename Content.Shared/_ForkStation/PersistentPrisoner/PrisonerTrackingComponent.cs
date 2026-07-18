using Robust.Shared.GameStates;

namespace Content.Shared._ForkStation.PersistentPrisoner;

/// <summary>
/// Added to entities that are serving as persistent prisoners.
/// Tracks their status for breakout detection and round-end processing.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PrisonerTrackingComponent : Component
{
    /// <summary>
    /// The player's user ID for penalty lookups.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string PlayerUserId = string.Empty;

    /// <summary>
    /// Whether this prisoner has broken out of the permabrig.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool HasEscaped;

    /// <summary>
    /// Whether this prisoner is a fugitive (spawned outside perma).
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool IsFugitive;

    /// <summary>
    /// Time the player joined the round (for 1/3 round service check).
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan JoinTime;

    /// <summary>
    /// Total penalty rounds at time of spawn.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int PenaltyRoundsAtSpawn;
}
