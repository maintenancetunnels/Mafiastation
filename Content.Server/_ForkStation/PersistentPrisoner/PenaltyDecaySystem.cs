using System.Linq;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Shared._ForkStation.PersistentPrisoner;
using Robust.Shared.Log;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Automatically decays penalty rounds over time.
/// At each round start, checks all active penalties. For each penalty,
/// credits 1 served round per 48 hours elapsed since issuance (minus already served).
/// This means penalties passively expire even if the player doesn't log in.
/// </summary>
public sealed class PenaltyDecaySystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;

    private static readonly ISawmill Log = Logger.GetSawmill("persistent.prisoner.decay");

    /// <summary>
    /// Hours per auto-decayed penalty round.
    /// </summary>
    private const double HoursPerDecay = 48.0;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        var summary = _penalties.GetPenaltySummary();
        var now = DateTime.UtcNow;
        var totalDecayed = 0;

        foreach (var (userId, _) in summary)
        {
            var penalties = _penalties.GetActivePenalties(userId);

            foreach (var penalty in penalties)
            {
                var age = now - penalty.IssuedAt;
                // How many rounds should have auto-decayed by now
                var totalAutoDecay = (int)(age.TotalHours / HoursPerDecay);
                // How many more need to be applied (beyond what's already served)
                var additionalDecay = totalAutoDecay - penalty.RoundsServed;

                if (additionalDecay <= 0)
                    continue;

                // Apply the decay by serving rounds
                var toServe = Math.Min(additionalDecay, penalty.RoundsRemaining);
                for (var i = 0; i < toServe; i++)
                {
                    _penalties.ServeDecayRound(penalty.Id);
                    totalDecayed++;
                }
            }
        }

        if (totalDecayed > 0)
            Log.Info($"Auto-decayed {totalDecayed} penalty round(s) at round start.");
    }
}
