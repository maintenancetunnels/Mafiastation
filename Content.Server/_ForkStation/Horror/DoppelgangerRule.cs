using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Station.Systems;
using Content.Server.StationEvents.Components;
using Content.Server.StationEvents.Events;
using Content.Shared.GameTicking.Components;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Station.Components;
using Robust.Shared.Map;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Spawns a motionless copy of a random living crew member at a vent-critter location.
/// The copy is handled (and dissolved) by <see cref="DoppelgangerSystem"/>.
/// </summary>
public sealed class DoppelgangerRule : StationEventSystem<DoppelgangerRuleComponent>
{
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly StationSpawningSystem _spawning = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    protected override void Started(EntityUid uid, DoppelgangerRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var chosenStation))
            return;

        // Pick a living crew member with a humanoid body to copy.
        var candidates = new List<ICommonSession>();
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } attached)
                continue;

            if (!HasComp<HumanoidProfileComponent>(attached))
                continue;

            if (!TryComp<MobStateComponent>(attached, out var mobState)
                || mobState.CurrentState == MobState.Dead)
                continue;

            candidates.Add(session);
        }

        if (candidates.Count == 0)
            return;

        var victim = RobustRandom.Pick(candidates);
        var profile = _gameTicker.GetPlayerProfile(victim);

        // Somewhere out of the way: vent-critter markers, like the blackout hunter.
        var spawnLocations = new List<EntityCoordinates>();
        var vents = AllEntityQuery<VentCritterSpawnLocationComponent, TransformComponent>();
        while (vents.MoveNext(out var ventUid, out _, out var xform))
        {
            if (CompOrNull<StationMemberComponent>(xform.GridUid)?.Station != chosenStation)
                continue;

            spawnLocations.Add(xform.Coordinates);
        }

        if (spawnLocations.Count == 0)
            return;

        var coords = RobustRandom.Pick(spawnLocations);

        // No job, no gear: just them, standing there, wrong.
        var copy = _spawning.SpawnPlayerMob(coords, null, profile, chosenStation.Value);
        EnsureComp<DoppelgangerComponent>(copy);
        component.Spawned = copy;

        Log.Info($"Doppelganger of {profile.Name} spawned at {coords}.");
    }

    protected override void Ended(EntityUid uid, DoppelgangerRuleComponent component, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        // Still standing at event end: it quietly stops having ever been there.
        if (component.Spawned is { } copy && !Deleted(copy)
            && TryComp<DoppelgangerComponent>(copy, out var doppel)
            && TryComp<TransformComponent>(copy, out var xform))
        {
            EntityManager.System<DoppelgangerSystem>().Dissolve(copy, doppel, xform);
        }

        component.Spawned = null;
    }
}
