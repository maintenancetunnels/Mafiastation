using Robust.Shared.GameStates;

namespace Content.Shared._ForkStation.PersistentPrisoner;

/// <summary>
/// Tracks good behavior credit for persistent prisoners.
/// Prisoners can earn credit toward serving penalty rounds faster
/// by performing useful tasks like farming hydroponic plants.
/// When enough credit is accumulated, an additional penalty round is served.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class GoodBehaviorComponent : Component
{
    /// <summary>
    /// Accumulated good behavior points this round.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Points;

    /// <summary>
    /// Points required to earn one additional served penalty round.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float PointsPerCredit = 100f;

    /// <summary>
    /// Credits earned this round (capped at 1 — you can only earn 1 bonus served round per round).
    /// </summary>
    [DataField, AutoNetworkedField]
    public int CreditsEarned;

    /// <summary>
    /// Max credits earnable per round.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int MaxCreditsPerRound = 1;
}
