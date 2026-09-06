using Content.Server.Chat.Systems;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.StationEvents.Components;
using Content.Server.StationEvents.Events;
using Content.Shared.GameTicking.Components;
using Content.Shared.Station.Components;
using Robust.Shared.Map;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Announces a Euclid breach and ensures MS-173 is loose in maintenance if it was not already.
/// Does not bump doors: crew must have opened something, or it waits in the tunnels.
/// </summary>
public sealed class ContainmentBreachRule : StationEventSystem<ContainmentBreachRuleComponent>
{
    [Dependency] private readonly ChatSystem _chat = default!;

    protected override void Started(
        EntityUid uid,
        ContainmentBreachRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var station))
            return;

        _chat.DispatchStationAnnouncement(
            station.Value,
            Loc.GetString("mafiastation-containment-breach"),
            sender: Loc.GetString("mafiastation-containment-sender"),
            playDefaultSound: true,
            colorOverride: Color.FromHex("#8B0000"));

        var existing = EntityQueryEnumerator<StatueStalkerComponent>();
        if (existing.MoveNext(out _, out _))
            return;

        var spots = new List<EntityCoordinates>();
        var vents = AllEntityQuery<VentCritterSpawnLocationComponent, TransformComponent>();
        while (vents.MoveNext(out _, out _, out var xform))
        {
            if (CompOrNull<StationMemberComponent>(xform.GridUid)?.Station != station)
                continue;
            spots.Add(xform.Coordinates);
        }

        if (spots.Count == 0)
            return;

        var spawned = Spawn(component.ObjectPrototype, spots[RobustRandom.Next(spots.Count)]);
        component.Spawned.Add(spawned);
    }

    protected override void Ended(
        EntityUid uid,
        ContainmentBreachRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        foreach (var spawned in component.Spawned)
        {
            if (!Deleted(spawned))
                QueueDel(spawned);
        }

        component.Spawned.Clear();
    }
}
