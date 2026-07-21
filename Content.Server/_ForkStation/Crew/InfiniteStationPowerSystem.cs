using Content.Server.Power.EntitySystems;
using Content.Shared.CCVar;
using Content.Shared.Power.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.Crew;

/// <summary>
/// Sandbox convenience: keeps an unmanned station permanently lit by topping every battery
/// (APCs, SMES, substations) back to full on a slow timer. This is charge-only, so it never
/// fights the rare BlackoutHunt event — that trips APC breakers, a separate mechanism — meaning
/// a deliberate blackout still plunges the station into darkness, but idle drain never does.
/// </summary>
public sealed class InfiniteStationPowerSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private TimeSpan _next;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _next)
            return;
        _next = _timing.CurTime + Interval;

        if (!_cfg.GetCVar(CCVars.MafiaInfinitePower))
            return;

        var query = EntityQueryEnumerator<BatteryComponent>();
        while (query.MoveNext(out var uid, out var battery))
        {
            if (battery.MaxCharge > 0f)
                _battery.SetCharge((uid, battery), battery.MaxCharge);
        }
    }
}
