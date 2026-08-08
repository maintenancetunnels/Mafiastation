using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Shared.Log;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Tracks good behavior for persistent prisoners. Prisoners earn points
/// for constructive activities, and earning enough points credits an
/// additional penalty round as "served" (like time off for good behavior).
///
/// Points are awarded by other systems calling AwardPoints().
/// Examples: harvesting plants, mining ore, cooking food, cleaning.
///
/// Design doc: "Prisoners can have objectives that function like 'good behavior'
/// and award '1 additional penalty round served' credits."
/// </summary>
public sealed class GoodBehaviorSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;


    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEnded);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        // Reset all good behavior tracking at round start
        var query = EntityQueryEnumerator<GoodBehaviorComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            comp.Points = 0;
            comp.CreditsEarned = 0;
        }
    }

    private void OnRoundEnded(RoundEndTextAppendEvent ev)
    {
        // At round end, apply any earned credits
        var query = EntityQueryEnumerator<GoodBehaviorComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.CreditsEarned <= 0)
                continue;

            if (!TryComp<MindContainerComponent>(uid, out var container) || container.Mind == null)
                continue;

            if (!_mind.TryGetMind(uid, out _, out var mind))
                continue;

            // Get the player session from the ActorComponent on the entity
            if (!TryComp<ActorComponent>(uid, out var actor))
                continue;

            var userId = actor.PlayerSession.UserId.ToString();

            for (var i = 0; i < comp.CreditsEarned; i++)
            {
                _penalties.ServeGoodBehaviorRound(userId);
            }

            Log.Info($"Player {userId} earned {comp.CreditsEarned} good behavior credit(s).");
        }
    }

    /// <summary>
    /// Award good behavior points to a prisoner entity.
    /// Call this from other systems when prisoners do constructive things.
    /// </summary>
    /// <param name="uid">The prisoner entity.</param>
    /// <param name="points">Points to award.</param>
    /// <param name="reason">What they did (for logging).</param>
    public void AwardPoints(EntityUid uid, float points, string reason)
    {
        if (!TryComp<GoodBehaviorComponent>(uid, out var comp))
            return;

        if (comp.CreditsEarned >= comp.MaxCreditsPerRound)
            return; // Already maxed out this round

        comp.Points += points;

        // Check if they've earned a credit
        while (comp.Points >= comp.PointsPerCredit && comp.CreditsEarned < comp.MaxCreditsPerRound)
        {
            comp.Points -= comp.PointsPerCredit;
            comp.CreditsEarned++;
            Log.Info($"Entity {uid} earned a good behavior credit! ({reason}) Total credits: {comp.CreditsEarned}");
        }
    }
}
