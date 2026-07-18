using Content.Server.Station.Systems;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.GameTicking;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Post-spawn setup for persistent prisoners:
/// - Attaches <see cref="PrisonerTrackingComponent"/> and <see cref="GoodBehaviorComponent"/>
///   to every penalized player's spawn.
/// - Prisoners at or above the solitary threshold (15+ penalty rounds) are relocated to a
///   <see cref="SolitaryConfinementSpawnPointComponent"/> marker if the map provides one.
/// Relocating after the normal spawn keeps the standard job pipeline (gear, mind roles) intact.
/// </summary>
public sealed class SolitaryConfinementSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly FugitiveSpawnSystem _fugitives = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly ISawmill Log = Logger.GetSawmill("persistent.prisoner.solitary");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawnComplete);
    }

    private void OnSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        var userId = ev.Player.UserId.ToString();
        var penaltyRounds = _penalties.GetPenaltyRounds(userId);

        if (penaltyRounds <= 0 || _fugitives.IsFugitive(userId))
            return;

        var tracking = EnsureComp<PrisonerTrackingComponent>(ev.Mob);
        tracking.PlayerUserId = userId;
        tracking.JoinTime = _timing.CurTime;
        tracking.PenaltyRoundsAtSpawn = penaltyRounds;
        EnsureComp<GoodBehaviorComponent>(ev.Mob);

        if (penaltyRounds < PersistentPrisonerSystem.SolitaryThreshold)
            return;

        // Extremely dangerous: relocate into solitary confinement if the map provides one.
        var candidates = new List<EntityCoordinates>();
        var query = EntityQueryEnumerator<SolitaryConfinementSpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (ev.Station != default && _station.GetOwningStation(uid, xform) != ev.Station)
                continue;

            candidates.Add(xform.Coordinates);
        }

        if (candidates.Count == 0)
        {
            Log.Warning($"Player {ev.Player.Name} has {penaltyRounds} penalties but the map has no solitary confinement spawn point.");
            return;
        }

        var target = _random.Pick(candidates);
        _transform.SetCoordinates(ev.Mob, target);
        Log.Info($"Player {ev.Player.Name} ({penaltyRounds} penalties) relocated to solitary confinement.");
    }
}
