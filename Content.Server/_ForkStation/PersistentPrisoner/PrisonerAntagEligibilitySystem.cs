using Content.Server.Antag;
using Content.Server.GameTicking.Events;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Decides whether a player serving penalty rounds may roll an antagonist role.
///
/// This exists to protect paranoia, not to punish. From the design doc: "persistent prisoners can
/// roll for some antag roles with a greatly reduced probability. This serves the purpose of not
/// allowing players to metagame rule-out an escaped prisoner as an antag, protecting the paranoid
/// atmosphere." If prisoners were simply barred, the crew could treat every prisoner as
/// definitively harmless, and an escaped prisoner would be a walking alibi.
///
/// Two rules apply: the chance tapers to exactly zero at
/// <see cref="PersistentPrisonerSystem.AntagZeroThreshold"/> penalties, and prisoners are limited
/// to station-bound stealth antagonists — "no wizard, no nuke ops, no xenomorph."
/// </summary>
public sealed class PrisonerAntagEligibilitySystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly PersistentPrisonerSystem _prisoners = default!;

    /// <summary>
    /// Antag game rules a prisoner may roll: station-bound and stealthy. Anything that spawns off
    /// station, arrives by shuttle, or announces itself loudly is excluded, both because the doc
    /// says so and because a prisoner cannot plausibly be a wizard.
    /// </summary>
    public static readonly HashSet<string> StationBoundStealthRules = new()
    {
        "Traitor",
        "TraitorReinforcement",
        "Changeling",
        "Thief",
        "Hitman",
        "Devil",
        "Zombie",
        "ZombieOutbreak",
        "SleeperAgents",
        "NTAgentSleeper",
        "Conspirators",
        "CosmicCult",
    };

    /// <summary>
    /// The roll is made once per player per round and remembered. Eligibility is consulted many
    /// times as different rules build their pools, and re-rolling each time would quietly give a
    /// prisoner far better odds than the curve specifies.
    /// </summary>
    private readonly Dictionary<NetUserId, bool> _rolledThisRound = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AntagSessionEligibilityEvent>(OnAntagEligibility);
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _rolledThisRound.Clear();
    }

    private void OnAntagEligibility(ref AntagSessionEligibilityEvent ev)
    {
        if (ev.Cancelled)
            return;

        var userId = ev.Session.UserId.ToString();
        var penalties = _prisoners.GetPenaltyRounds(userId);

        // Not serving time: this system has no opinion.
        if (penalties <= 0)
            return;

        // Prisoners are station-bound stealth only.
        if (!IsStationBoundStealth(ev.GameRule))
        {
            ev.Cancelled = true;
            return;
        }

        if (!MayRollAntag(ev.Session.UserId, penalties))
            ev.Cancelled = true;
    }

    /// <summary>
    /// Whether this player won their single antag roll for the round.
    /// </summary>
    private bool MayRollAntag(NetUserId userId, int penalties)
    {
        if (_rolledThisRound.TryGetValue(userId, out var alreadyRolled))
            return alreadyRolled;

        var chance = PersistentPrisonerSystem.GetPrisonerAntagChance(penalties);
        var succeeded = chance > 0f && _random.Prob(chance);
        _rolledThisRound[userId] = succeeded;

        if (succeeded)
        {
            Log.Info(
                $"Prisoner {userId} won their antag roll at {penalties} penalty rounds " +
                $"({chance:P2} chance). An escaped prisoner is not automatically innocent.");
        }

        return succeeded;
    }

    private bool IsStationBoundStealth(EntityUid rule)
    {
        var proto = MetaData(rule).EntityPrototype?.ID;
        return proto != null && StationBoundStealthRules.Contains(proto);
    }
}
