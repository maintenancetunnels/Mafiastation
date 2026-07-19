using Content.Server.Chat.Systems;
using Content.Server.CriminalRecords.Systems;
using Content.Server.GameTicking.Events;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Systems;
using Content.Server.Voting;
using Content.Server.Voting.Managers;
using Content.Shared.Security;
using Content.Shared.StationRecords;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.Accusation;

/// <summary>
/// Crew accusation votes: classic social-deduction emergency meetings. Any living crew
/// member can call one accusation per shift. If the crew votes Suspect, the target is
/// publicly announced and marked Wanted in the criminal records.
/// The crew can be wrong. That's the game.
/// </summary>
public sealed class AccusationSystem : EntitySystem
{
    [Dependency] private readonly IVoteManager _votes = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly CriminalRecordsSystem _criminal = default!;
    [Dependency] private readonly StationRecordsSystem _records = default!;
    [Dependency] private readonly StationSystem _station = default!;

    private readonly HashSet<NetUserId> _usedThisRound = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _usedThisRound.Clear();
    }

    public bool HasAccused(NetUserId user) => _usedThisRound.Contains(user);

    /// <summary>
    /// Start a crew accusation vote against the target. One per player per shift.
    /// </summary>
    public bool TryStartAccusation(ICommonSession initiator, EntityUid targetEntity, string targetName, out string error)
    {
        error = string.Empty;

        if (!_usedThisRound.Add(initiator.UserId))
        {
            error = Loc.GetString("mafiastation-accuse-once");
            return false;
        }

        var initiatorName = initiator.AttachedEntity is { Valid: true } initiatorEntity
            ? Name(initiatorEntity)
            : initiator.Name;

        var options = new VoteOptions
        {
            Title = Loc.GetString("mafiastation-accuse-title", ("target", targetName)),
            InitiatorText = initiatorName,
            InitiatorPlayer = initiator,
            Duration = TimeSpan.FromSeconds(45),
        };
        options.Options.Add((Loc.GetString("mafiastation-accuse-yes"), "yes"));
        options.Options.Add((Loc.GetString("mafiastation-accuse-no"), "no"));

        var station = _station.GetOwningStation(targetEntity);
        var vote = _votes.CreateVote(options);
        vote.OnFinished += (_, args) =>
        {
            if (args.Winner is not string winner || winner != "yes")
                return;

            OnAccusationPassed(station, targetName);
        };

        return true;
    }

    private void OnAccusationPassed(EntityUid? station, string targetName)
    {
        if (station == null)
            return;

        _chat.DispatchStationAnnouncement(station.Value,
            Loc.GetString("mafiastation-accuse-passed", ("target", targetName)),
            sender: Loc.GetString("mafiastation-accuse-sender"),
            playDefaultSound: true,
            colorOverride: Color.FromHex("#a83232"));

        // Best effort: mark them Wanted in the criminal records so sec consoles agree
        // with the mob outside.
        if (!TryComp<StationRecordsComponent>(station.Value, out var recordsComp))
            return;

        foreach (var (id, record) in _records.GetRecordsOfType<GeneralStationRecord>(station.Value, recordsComp))
        {
            if (!string.Equals(record.Name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = new StationRecordKey(id, station.Value);
            _criminal.TryChangeStatus(key, SecurityStatus.Wanted,
                Loc.GetString("mafiastation-accuse-reason"));
            break;
        }
    }
}
