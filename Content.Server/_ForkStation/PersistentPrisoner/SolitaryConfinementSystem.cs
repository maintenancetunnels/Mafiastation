using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.GameTicking;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Edge-case post-spawn setup only. Force-spawn of penalized players is owned entirely by
/// <see cref="PersistentPrisonerSystem.OnBeforeSpawn"/> (including solitary placement), which
/// marks <see cref="PlayerBeforeSpawnEvent"/> handled so <see cref="PlayerSpawnCompleteEvent"/>
/// never fires for them. This system only attaches tracking/good-behavior if a penalized player
/// somehow reaches normal spawn completion (e.g. missing prisoner markers).
/// </summary>
public sealed class SolitaryConfinementSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly FugitiveSpawnSystem _fugitives = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawnComplete);
    }

    private void OnSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        var userId = ev.Player.UserId.ToString();
        var penaltyRounds = _penalties.GetPenaltyRounds(userId);

        if (penaltyRounds <= 0 || _fugitives.IsFugitive(userId))
            return;

        // Force-spawn path should have handled these players. If we still see them here, the
        // station had no prisoner marker and they spawned normally — attach tracking so death
        // and serve rules can still reason about them, but do NOT relocate (no dual solitary path).
        var tracking = EnsureComp<PrisonerTrackingComponent>(ev.Mob);
        tracking.PlayerUserId = userId;
        tracking.JoinTime = _timing.CurTime;
        tracking.PenaltyRoundsAtSpawn = penaltyRounds;
        EnsureComp<GoodBehaviorComponent>(ev.Mob);

        Log.Warning(
            $"Player {ev.Player.Name} has {penaltyRounds} outstanding penalties but completed a " +
            "normal spawn (no force-spawn). Tracking attached; solitary placement is BeforeSpawn-only.");
    }
}
