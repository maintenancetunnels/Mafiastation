using Robust.Shared.GameStates;

namespace Content.Shared._ForkStation.PersistentPrisoner;

/// <summary>
/// Component for the Penalty Management Console.
/// Allows authorized security officers to view and assign penalty rounds.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class PenaltyConsoleComponent : Component
{
    /// <summary>
    /// Maximum length for penalty reason strings.
    /// </summary>
    [DataField]
    public int MaxReasonLength = 256;

    /// <summary>
    /// Currently selected player name.
    /// </summary>
    [DataField]
    public string? SelectedPlayer;
}
