using Content.Server.GameTicking;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.Cuffs.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Log;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Evaluates fugitive outcomes at round end using <see cref="PrisonerTrackingComponent"/>.
/// Design doc:
/// - Fugitive ends the round alive and free (not cuffed): -1 penalty round.
/// - Fugitive ends the round in custody (cuffed): +1 penalty round.
/// - Fugitive death is handled live by <see cref="PrisonerDeathTrackingSystem"/> (execution
///   rule), not here, so death never double-counts.
/// - Regular prisoners are credited by <see cref="PersistentPrisonerSystem"/>'s round-end pass.
/// </summary>
public sealed class PrisonerBreakoutSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;

    private static readonly ISawmill Log = Logger.GetSawmill("persistent.prisoner.breakout");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEnded);
    }

    private void OnRoundEnded(RoundEndTextAppendEvent ev)
    {
        var query = EntityQueryEnumerator<PrisonerTrackingComponent>();
        while (query.MoveNext(out var uid, out var tracking))
        {
            if (!tracking.IsFugitive)
                continue;

            var userId = tracking.PlayerUserId;
            if (string.IsNullOrEmpty(userId))
                continue;

            var dead = !TryComp<MobStateComponent>(uid, out var mobState)
                       || mobState.CurrentState == MobState.Dead;
            if (dead)
            {
                // The execution rule already applied +1 on death via PrisonerDeathTrackingSystem.
                Log.Info($"Fugitive {userId}: ended the round dead. Death penalty already applied.");
                continue;
            }

            var inCustody = TryComp<CuffableComponent>(uid, out var cuffs) && cuffs.CuffedHandCount > 0;
            if (inCustody)
            {
                _penalties.AddPenalty(userId, "SYSTEM", "System (Fugitive Caught)", 1,
                    "Fugitive ended the round in custody", adminIssued: true);
                Log.Info($"Fugitive {userId}: ended the round in custody. +1 penalty round.");
                continue;
            }

            // Alive and free: escaped. -1 penalty round (serves the oldest unserved penalty).
            if (_penalties.ServeGoodBehaviorRound(userId))
                Log.Info($"Fugitive {userId}: survived the round free. -1 penalty round.");
        }
    }
}
