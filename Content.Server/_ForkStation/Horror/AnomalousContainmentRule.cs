using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server._ForkStation.PersistentPrisoner;
using Content.Shared.GameTicking.Components;
using Content.Shared.Station.Components;
using Robust.Shared.Map;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Places MS-173 in holding at shift start and briefs the site.
/// Prison Guards keep visual contact. D-class (prisoners) may be tasked. Science observes.
/// </summary>
public sealed class AnomalousContainmentRule : GameRuleSystem<AnomalousContainmentRuleComponent>
{
    [Dependency] private readonly ChatSystem _chat = default!;

    protected override void Started(
        EntityUid uid,
        AnomalousContainmentRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var station))
            return;

        component.Station = station.Value;
        if (FindContainmentSpawn(station.Value) is not { } coords)
            return;

        component.Contained = Spawn(component.ObjectPrototype, coords);
        component.Document = Spawn(component.DocumentPrototype, coords);

        _chat.DispatchStationAnnouncement(
            station.Value,
            Loc.GetString("mafiastation-containment-briefing"),
            sender: Loc.GetString("mafiastation-containment-sender"),
            playDefaultSound: true);
    }

    protected override void Ended(
        EntityUid uid,
        AnomalousContainmentRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        if (component.Contained is { } contained && !Deleted(contained))
            QueueDel(contained);
        if (component.Document is { } doc && !Deleted(doc))
            QueueDel(doc);

        component.Contained = null;
        component.Document = null;
    }

    protected override void AppendRoundEndText(
        EntityUid uid,
        AnomalousContainmentRuleComponent component,
        GameRuleComponent gameRule,
        ref RoundEndTextAppendEvent args)
    {
        args.AddLine(Loc.GetString("mafiastation-containment-roundend"));
    }

    private EntityCoordinates? FindContainmentSpawn(EntityUid station)
    {
        var markers = CollectOnStation<AnomalousContainmentSpawnPointComponent>(station);
        if (markers.Count > 0)
            return markers[RobustRandom.Next(markers.Count)];

        var solitary = CollectOnStation<SolitaryConfinementSpawnPointComponent>(station);
        if (solitary.Count > 0)
            return solitary[RobustRandom.Next(solitary.Count)];

        return null;
    }

    private List<EntityCoordinates> CollectOnStation<TComp>(EntityUid station)
        where TComp : IComponent
    {
        var list = new List<EntityCoordinates>();
        var query = EntityQueryEnumerator<TComp, TransformComponent>();
        while (query.MoveNext(out _, out _, out var xform))
        {
            if (CompOrNull<StationMemberComponent>(xform.GridUid)?.Station != station)
                continue;
            list.Add(xform.Coordinates);
        }

        return list;
    }
}
