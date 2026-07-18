using Content.Server.GameTicking.Rules.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.StationEvents.Components;
using Content.Server.StationEvents.Events;
using Content.Shared.GameTicking.Components;
using Content.Shared.Station.Components;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// "The power went out and something is in here with us."
/// Cuts every APC on the station at once, spawns hunters at vent-critter locations,
/// then restores power and removes the hunters when the event ends.
/// </summary>
public sealed class BlackoutHuntRule : StationEventSystem<BlackoutHuntRuleComponent>
{
    [Dependency] private readonly ApcSystem _apc = default!;

    protected override void Started(EntityUid uid, BlackoutHuntRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var chosenStation))
            return;

        component.AffectedStation = chosenStation.Value;

        // Sudden, total darkness: every powered APC on the station trips at once.
        var apcs = AllEntityQuery<ApcComponent, TransformComponent>();
        while (apcs.MoveNext(out var apcUid, out var apc, out var xform))
        {
            if (!apc.MainBreakerEnabled)
                continue;

            if (CompOrNull<StationMemberComponent>(xform.GridUid)?.Station != chosenStation)
                continue;

            _apc.ApcToggleBreaker(apcUid, apc);
            component.Unpowered.Add(apcUid);
        }

        // Something wakes up in the dark.
        var spawnLocations = new List<EntityCoordinates>();
        var vents = AllEntityQuery<VentCritterSpawnLocationComponent, TransformComponent>();
        while (vents.MoveNext(out var ventUid, out _, out var xform))
        {
            if (CompOrNull<StationMemberComponent>(xform.GridUid)?.Station != chosenStation)
                continue;

            spawnLocations.Add(xform.Coordinates);
        }

        if (spawnLocations.Count == 0 && component.Unpowered.Count > 0)
        {
            // No vents? It comes out of the electrical room instead.
            foreach (var apcUid in component.Unpowered)
            {
                spawnLocations.Add(Transform(apcUid).Coordinates);
            }
        }

        if (spawnLocations.Count == 0)
            return;

        for (var i = 0; i < component.HunterCount; i++)
        {
            var coords = RobustRandom.Pick(spawnLocations);
            var hunter = Spawn(component.HunterPrototype, coords);
            component.Hunters.Add(hunter);
        }
    }

    protected override void Ended(EntityUid uid, BlackoutHuntRuleComponent component, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        foreach (var apcUid in component.Unpowered)
        {
            if (Deleted(apcUid))
                continue;

            if (TryComp<ApcComponent>(apcUid, out var apc) && !apc.MainBreakerEnabled)
                _apc.ApcToggleBreaker(apcUid, apc);
        }

        component.Unpowered.Clear();

        // It was never there.
        foreach (var hunter in component.Hunters)
        {
            if (!Deleted(hunter))
                QueueDel(hunter);
        }

        component.Hunters.Clear();

        Audio.PlayGlobal(component.PowerOnSound, Filter.Broadcast(), true);
    }
}
