using Content.Shared._ForkStation.PersistentPrisoner;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Awards good behavior points to prisoners over time for being alive, connected,
/// and not causing trouble. This represents peaceful time served and labor.
///
/// Prisoners earn 1 point per minute of peaceful play.
/// At 100 points, they earn 1 bonus penalty round served.
/// This means ~1h40m of peaceful play per bonus credit (capped at 1 per round).
///
/// Future: hook into specific game systems (botany, mining, cooking) to award
/// bonus points for productive labor.
/// </summary>
public sealed class PrisonerLaborSystem : EntitySystem
{
    [Dependency] private readonly GoodBehaviorSystem _behavior = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>
    /// How often to award passive labor points (in seconds).
    /// </summary>
    private const float TickIntervalSeconds = 60f;

    /// <summary>
    /// Points awarded per tick interval.
    /// </summary>
    private const float PointsPerTick = 1f;

    private TimeSpan _nextTick;

    public override void Initialize()
    {
        base.Initialize();
        _nextTick = TimeSpan.Zero;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextTick)
            return;

        _nextTick = _timing.CurTime + TimeSpan.FromSeconds(TickIntervalSeconds);

        // Award points to all entities with GoodBehaviorComponent
        var query = EntityQueryEnumerator<GoodBehaviorComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            _behavior.AwardPoints(uid, PointsPerTick, "peaceful time served");
        }
    }
}
