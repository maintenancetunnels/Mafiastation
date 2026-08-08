using Content.Server.GameTicking.Events;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Roles;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Tracks deaths of players with active outstanding penalties.
/// Per the design:
/// - Dying anywhere outside perma with a penalty flag counts as an execution (+1, once/round).
/// - Environmental/NPC/accident death inside permabrig does not auto-stack.
/// - Antags are exempt from penalty accumulation.
/// </summary>
public sealed class PrisonerDeathTrackingSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedRoleSystem _roles = default!;

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

        if (!_mind.TryGetMind(uid, out var mindId, out var mind))
            return;

        if (mind.UserId is not { } netUserId)
            return;

        var userId = netUserId.ToString();
        var hasOutstanding = _penalties.GetPenaltyRounds(userId) > 0;
        var isAntag = _roles.MindIsAntagonist(mindId);
        var already = _diedThisRound.Contains(userId);
        var inPerma = _penalties.IsEntityInPermabrig(uid);

        if (!PrisonerDesignRules.ShouldApplyDeathExecutionPenalty(
                hasOutstanding, isAntag, already, inPerma))
        {
            if (hasOutstanding && isAntag)
                Log.Info($"Penalized player {userId} died but is an antag — no additional penalty.");
            else if (hasOutstanding && inPerma)
                Log.Info($"Player {userId} died inside permabrig — no automatic execution penalty.");
            return;
        }

        _diedThisRound.Add(userId);

        // System-issued, execution-class, immediately confirmed (adminIssued path).
        var record = _penalties.AddPenalty(
            userId,
            "SYSTEM",
            "System (Death Penalty)",
            1,
            "Died while serving penalty rounds (automatic execution penalty)",
            adminIssued: true,
            isExecution: true);

        if (record != null)
        {
            Log.Info($"Player {userId} died outside perma with active penalties. +1 execution penalty.");
        }
    }
}
