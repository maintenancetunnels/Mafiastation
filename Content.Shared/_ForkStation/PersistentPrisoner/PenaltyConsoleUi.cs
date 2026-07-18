using Content.Shared._ForkStation.PersistentPrisoner;
using Robust.Shared.Serialization;

namespace Content.Shared._ForkStation.PersistentPrisoner;

[Serializable, NetSerializable]
public enum PenaltyConsoleKey : byte
{
    Key
}

/// <summary>
/// State sent from server to client for the penalty console.
/// </summary>
[Serializable, NetSerializable]
public sealed class PenaltyConsoleState : BoundUserInterfaceState
{
    /// <summary>
    /// All online players with their penalty info.
    /// Key = player name, Value = (userId, totalPenalties)
    /// </summary>
    public readonly Dictionary<string, PenaltyPlayerEntry> Players;

    /// <summary>
    /// Currently selected player name.
    /// </summary>
    public readonly string? SelectedPlayer;

    /// <summary>
    /// Penalty details for the selected player.
    /// </summary>
    public readonly List<PenaltyDisplayRecord>? SelectedPenalties;

    public PenaltyConsoleState(
        Dictionary<string, PenaltyPlayerEntry> players,
        string? selectedPlayer,
        List<PenaltyDisplayRecord>? selectedPenalties)
    {
        Players = players;
        SelectedPlayer = selectedPlayer;
        SelectedPenalties = selectedPenalties;
    }
}

[Serializable, NetSerializable]
public sealed class PenaltyPlayerEntry
{
    public readonly string UserId;
    public readonly int TotalPenalties;
    public readonly string JobTitle;

    public PenaltyPlayerEntry(string userId, int totalPenalties, string jobTitle)
    {
        UserId = userId;
        TotalPenalties = totalPenalties;
        JobTitle = jobTitle;
    }
}

[Serializable, NetSerializable]
public sealed class PenaltyDisplayRecord
{
    public readonly int Id;
    public readonly int RoundsAssigned;
    public readonly int RoundsServed;
    public readonly string Reason;
    public readonly string IssuedByName;
    public readonly bool AdminIssued;
    public readonly DateTime IssuedAt;

    public PenaltyDisplayRecord(int id, int roundsAssigned, int roundsServed,
        string reason, string issuedByName, bool adminIssued, DateTime issuedAt)
    {
        Id = id;
        RoundsAssigned = roundsAssigned;
        RoundsServed = roundsServed;
        Reason = reason;
        IssuedByName = issuedByName;
        AdminIssued = adminIssued;
        IssuedAt = issuedAt;
    }

    public int RoundsRemaining => Math.Max(0, RoundsAssigned - RoundsServed);
}

// === Messages (Client → Server) ===

/// <summary>
/// Select a player to view their penalties.
/// </summary>
[Serializable, NetSerializable]
public sealed class PenaltyConsoleSelectPlayer : BoundUserInterfaceMessage
{
    public readonly string PlayerName;

    public PenaltyConsoleSelectPlayer(string playerName)
    {
        PlayerName = playerName;
    }
}

/// <summary>
/// Apply penalty rounds to the selected player.
/// </summary>
[Serializable, NetSerializable]
public sealed class PenaltyConsoleAddPenalty : BoundUserInterfaceMessage
{
    public readonly string PlayerName;
    public readonly int Rounds;
    public readonly string Reason;

    public PenaltyConsoleAddPenalty(string playerName, int rounds, string reason)
    {
        PlayerName = playerName;
        Rounds = rounds;
        Reason = reason;
    }
}

/// <summary>
/// Undo a penalty applied this round (sec can only undo same-round penalties).
/// </summary>
[Serializable, NetSerializable]
public sealed class PenaltyConsoleUndoPenalty : BoundUserInterfaceMessage
{
    public readonly int PenaltyId;

    public PenaltyConsoleUndoPenalty(int penaltyId)
    {
        PenaltyId = penaltyId;
    }
}
