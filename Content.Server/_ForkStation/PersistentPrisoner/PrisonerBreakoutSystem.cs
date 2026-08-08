using Content.Server.GameTicking;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.Cuffs.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Evaluates fugitive outcomes at round end using <see cref="PrisonerTrackingComponent"/>.
/// Death is handled live by <see cref="PrisonerDeathTrackingSystem"/> (no double-count here).
/// </summary>
public sealed class PrisonerBreakoutSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;

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
            var inCustody = TryComp<CuffableComponent>(uid, out var cuffs) && cuffs.CuffedHandCount > 0;

            switch (PrisonerDesignRules.EvaluateFugitiveOutcome(dead, inCustody))
            {
                case PrisonerDesignRules.FugitiveRoundOutcome.DeadAlreadyHandled:
                    Log.Info($"Fugitive {userId}: ended the round dead. Death penalty already applied.");
                    break;

                case PrisonerDesignRules.FugitiveRoundOutcome.CustodyPlusOne:
                    _penalties.AddPenalty(userId, "SYSTEM", "System (Fugitive Caught)", 1,
                        "Fugitive ended the round in custody", adminIssued: true);
                    Log.Info($"Fugitive {userId}: ended the round in custody. +1 penalty round.");
                    break;

                case PrisonerDesignRules.FugitiveRoundOutcome.FreeMinusOne:
                    if (_penalties.ServeGoodBehaviorRound(userId))
                        Log.Info($"Fugitive {userId}: survived the round free. -1 penalty round.");
                    break;
            }
        }
    }
}
