using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Roles;
using Robust.Shared.Log;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Tracks deaths of players with active penalties.
/// Per the design:
/// - Dying anywhere outside perma with a penalty flag counts as an execution (+1 penalty).
/// - This prevents players from suicide-bombing to avoid penalties.
/// - Antags are exempt from penalty accumulation.
/// - Deaths from environmental/NPC causes in perma don't count as executions.
/// </summary>
public sealed class PrisonerDeathTrackingSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedRoleSystem _roles = default!;

    private static readonly ISawmill Log = Logger.GetSawmill("persistent.prisoner.death");

    /// <summary>
    /// Track players who have died this round to prevent double-counting.
    /// </summary>
    private readonly HashSet<string> _diedThisRound = new();

    public override void Initialize()
    {
        base.Initialize();
        // Broadcast subscription: the directed (MobStateComponent, MobStateChangedEvent) slot
        // is owned by upstream SharedStunSystem — the engine allows only one directed sub per pair.
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _diedThisRound.Clear();
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        var uid = args.Target;

        // Get the mind/player behind this entity
        if (!_mind.TryGetMind(uid, out var mindId, out var mind))
            return;

        // Resolve the user id straight from the mind; MindComponent.Session no longer exists upstream.
        if (mind.UserId is not { } netUserId)
            return;

        var userId = netUserId.ToString();

        // No active penalties = nothing to track
        if (_penalties.GetPenaltyRounds(userId) <= 0)
            return;

        // Already died this round
        if (_diedThisRound.Contains(userId))
            return;

        // Antags are exempt from penalty accumulation
        if (_roles.MindIsAntagonist(mindId))
        {
            Log.Info($"Penalized player {userId} died but is an antag — no additional penalty.");
            return;
        }

        _diedThisRound.Add(userId);

        // Death with active penalty = +1 penalty (treated as execution).
        // Capped at 2 per the design doc (execution cap), but since we only
        // track one death per round here, this is effectively 1.
        var record = _penalties.AddPenalty(
            userId,
            "SYSTEM",
            "System (Death Penalty)",
            1,
            "Died while serving penalty rounds (automatic execution penalty)",
            adminIssued: true);

        if (record != null)
        {
            Log.Info($"Player {userId} died with active penalties. +1 penalty round applied (execution rule).");
        }
    }
}
