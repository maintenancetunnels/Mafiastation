using Content.Server.GameTicking.Events;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Automatically decays confirmed outstanding penalty rounds over real time.
/// Honors <see cref="CCVars.PersistentPrisonerDecay"/> and
/// <see cref="CCVars.PersistentPrisonerDecayHours"/>.
/// </summary>
public sealed class PenaltyDecaySystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    private bool _decayEnabled = true;
    private float _hoursPerDecay = 48f;

    public override void Initialize()
    {
        base.Initialize();
        Subs.CVar(_cfg, CCVars.PersistentPrisonerDecay, v => _decayEnabled = v, true);
        Subs.CVar(_cfg, CCVars.PersistentPrisonerDecayHours, v => _hoursPerDecay = v, true);
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        if (!_decayEnabled || _hoursPerDecay <= 0f)
            return;

        var summary = _penalties.GetPenaltySummary();
        var now = DateTime.UtcNow;
        var totalDecayed = 0;

        foreach (var (userId, _) in summary)
        {
            var penalties = _penalties.GetActivePenalties(userId);

            foreach (var penalty in penalties)
            {
                var age = now - penalty.IssuedAt;
                var totalAutoDecay = (int)(age.TotalHours / _hoursPerDecay);
                var additionalDecay = totalAutoDecay - penalty.RoundsServed;

                if (additionalDecay <= 0)
                    continue;

                var toServe = Math.Min(additionalDecay, penalty.RoundsRemaining);
                for (var i = 0; i < toServe; i++)
                {
                    _penalties.ServeDecayRound(penalty.Id);
                    totalDecayed++;
                }
            }
        }

        if (totalDecayed > 0)
            Log.Info($"Auto-decayed {totalDecayed} penalty round(s) at round start ({_hoursPerDecay}h each).");
    }
}
