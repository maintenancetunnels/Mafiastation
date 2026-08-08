using System.Linq;
using Content.Server.GameTicking;
using Content.Shared._ForkStation.Investigation;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.Investigation;

/// <summary>
/// Records how a mob died so a murder can actually be investigated.
///
/// Damage events know who caused them (<see cref="DamageChangedEvent.Origin"/>) but nothing in the
/// game keeps that around, so by the time a body is found the killer is forgotten. This watches
/// damage as it lands, remembers the most recent responsible party, and freezes the whole picture
/// at the moment of death.
/// </summary>
public sealed class DeathCircumstancesSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    public override void Initialize()
    {
        base.Initialize();
        // Deliberately hung on MobStateComponent, not DamageableComponent: the engine allows only
        // one directed subscription per (component, event) pair and AiPilotServerSystem already
        // owns DamageableComponent + DamageChangedEvent. Keying on MobStateComponent avoids the
        // collision and is a better filter anyway, since only things that can die matter here.
        SubscribeLocalEvent<MobStateComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    private void OnDamageChanged(Entity<MobStateComponent> victim, ref DamageChangedEvent args)
    {
        // Only interested in harm.
        if (!args.DamageIncreased || args.DamageDelta == null)
            return;

        // Already dead: don't let post-mortem damage rewrite who the killer was.
        if (_mobState.IsDead(victim.Owner))
            return;

        var circumstances = EnsureComp<DeathCircumstancesComponent>(victim.Owner);

        var largest = LargestDamageType(args.DamageDelta);
        if (largest != null)
        {
            circumstances.KillingDamageType = largest.Value.Type;
            circumstances.FinalBlowAmount = largest.Value.Amount;
        }

        if (args.Origin is { } origin && origin != victim.Owner && !TerminatingOrDeleted(origin))
        {
            // Resolve through the identity system, so a killer wearing a mask and someone else's
            // ID is recorded as who they appeared to be. That keeps framing viable.
            circumstances.SuspectName = Identity.Name(origin, EntityManager);
            circumstances.HasSuspect = true;
            circumstances.SuspectWasCreature = HasComp<MobStateComponent>(origin);
        }
        else if (args.Origin == null)
        {
            // Environmental damage overwrites a stale suspect: someone who punched you an hour
            // ago should not be blamed for the airlock that later crushed you.
            circumstances.SuspectName = null;
            circumstances.HasSuspect = false;
            circumstances.SuspectWasCreature = false;
        }

        Dirty(victim.Owner, circumstances);
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        var circumstances = EnsureComp<DeathCircumstancesComponent>(args.Target);

        // Station time, matching the clock door access logs use, so the two line up.
        circumstances.TimeOfDeath = _timing.CurTime - _ticker.RoundStartTimeSpan;
        circumstances.LocationName ??= DescribeLocation(args.Target);

        Dirty(args.Target, circumstances);

        Log.Info(
            $"Death recorded: {ToPrettyString(args.Target)} at {circumstances.TimeOfDeath} " +
            $"by {(circumstances.HasSuspect ? circumstances.SuspectName : "no identified suspect")} " +
            $"({circumstances.KillingDamageType ?? "unknown"} {circumstances.FinalBlowAmount:0.#}).");
    }

    /// <summary>
    /// The damage type carrying the most of this blow. Ties are resolved arbitrarily but stably.
    /// </summary>
    private static (string Type, float Amount)? LargestDamageType(DamageSpecifier delta)
    {
        (string Type, float Amount)? best = null;

        foreach (var (type, amount) in delta.DamageDict)
        {
            var value = amount.Float();
            if (value <= 0f)
                continue;

            if (best == null || value > best.Value.Amount)
                best = (type, value);
        }

        return best;
    }

    private string DescribeLocation(EntityUid target)
    {
        if (!TryComp<TransformComponent>(target, out var xform))
            return "unknown";

        var grid = xform.GridUid;
        return grid != null ? Name(grid.Value) : "off-station";
    }

    /// <summary>
    /// A formatted autopsy line for investigation UI and printouts.
    /// </summary>
    public string Summarize(Entity<DeathCircumstancesComponent> body)
    {
        var c = body.Comp;
        var time = c.TimeOfDeath?.ToString(@"hh\:mm\:ss") ?? "unknown time";
        var cause = c.KillingDamageType ?? "unknown cause";
        var suspect = c.HasSuspect ? c.SuspectName : "no identified suspect";

        return $"Time of death {time}. Cause: {cause} ({c.FinalBlowAmount:0.#}). Attributed to: {suspect}.";
    }
}
