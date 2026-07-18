using Content.Server.Ghost;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.StationEvents.Events;
using Content.Shared.GameTicking.Components;
using Content.Shared.Light.Components;
using Content.Shared.Station.Components;
using Robust.Shared.Random;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Flickers random lights across the station for the event duration by raising the same
/// event a ghost's boo ability uses. Pure atmosphere: liminal, wrong, unexplained.
/// </summary>
public sealed class LiminalFlickerRule : StationEventSystem<LiminalFlickerRuleComponent>
{
    protected override void Started(EntityUid uid, LiminalFlickerRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var chosenStation))
            return;

        component.AffectedStation = chosenStation.Value;

        var query = AllEntityQuery<PoweredLightComponent, TransformComponent>();
        while (query.MoveNext(out var lightUid, out _, out var xform))
        {
            if (CompOrNull<StationMemberComponent>(xform.GridUid)?.Station != chosenStation)
                continue;

            component.Lights.Add(lightUid);
        }
    }

    protected override void ActiveTick(EntityUid uid, LiminalFlickerRuleComponent component, GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, component, gameRule, frameTime);

        if (component.Lights.Count == 0)
            return;

        component.Accumulator += frameTime * component.FlickersPerSecond;
        while (component.Accumulator >= 1f)
        {
            component.Accumulator -= 1f;

            var light = RobustRandom.Pick(component.Lights);
            if (Deleted(light))
                continue;

            var boo = new GhostBooEvent();
            RaiseLocalEvent(light, boo);
        }
    }

    protected override void Ended(EntityUid uid, LiminalFlickerRuleComponent component, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);
        component.Lights.Clear();
    }
}
