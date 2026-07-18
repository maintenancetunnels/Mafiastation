using System.Numerics;
using Content.Server.Examine;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Drives <see cref="StatueStalkerComponent"/> entities: frozen while any living player has an
/// unoccluded line of sight to them, hunting the nearest living player when unobserved.
/// Classic weeping-angel rules — light does not matter, eyes do.
/// </summary>
public sealed class StatueStalkerSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystem _examine = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>
    /// How often (seconds) the observation/hunt logic reevaluates. Movement continues between
    /// scans because velocity persists on the physics body.
    /// </summary>
    private const float ScanInterval = 0.1f;

    private float _accumulator;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < ScanInterval)
            return;

        _accumulator = 0f;

        // Collect living player bodies once per scan.
        var observers = new List<(EntityUid Entity, Vector2 Pos)>();
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } attached)
                continue;

            if (!TryComp<MobStateComponent>(attached, out var mobState)
                || mobState.CurrentState == MobState.Dead)
                continue;

            observers.Add((attached, _transform.GetWorldPosition(attached)));
        }

        var query = EntityQueryEnumerator<StatueStalkerComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var stalker, out var physics, out var xform))
        {
            var ourPos = _transform.GetWorldPosition(xform);
            var ourMap = _transform.GetMapCoordinates(uid, xform);

            // Frozen if anyone alive can see us.
            var observed = false;
            foreach (var (observer, observerPos) in observers)
            {
                if ((observerPos - ourPos).Length() > stalker.ObservationRange)
                    continue;

                var observerMap = _transform.GetMapCoordinates(observer);
                if (_examine.InRangeUnOccluded(observerMap, ourMap, stalker.ObservationRange, null))
                {
                    observed = true;
                    break;
                }
            }

            stalker.Observed = observed;

            if (observed)
            {
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
                continue;
            }

            // Unobserved: hunt the nearest living player.
            EntityUid? prey = null;
            var bestDist = float.MaxValue;
            Vector2 bestPos = default;
            foreach (var (candidate, candidatePos) in observers)
            {
                var dist = (candidatePos - ourPos).Length();
                if (dist < bestDist && dist <= stalker.HuntRange)
                {
                    bestDist = dist;
                    prey = candidate;
                    bestPos = candidatePos;
                }
            }

            if (prey == null)
            {
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
                continue;
            }

            if (bestDist <= stalker.AttackRange)
            {
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);

                if (_timing.CurTime >= stalker.NextAttack)
                {
                    _damageable.TryChangeDamage(prey.Value, stalker.Damage, origin: uid);
                    _audio.PlayPvs(stalker.AttackSound, uid);
                    stalker.NextAttack = _timing.CurTime + stalker.AttackCooldown;
                }

                continue;
            }

            var direction = bestPos - ourPos;
            var length = direction.Length();
            if (length < 0.01f)
                continue;

            _physics.SetLinearVelocity(uid, direction / length * stalker.MoveSpeed, body: physics);
        }
    }
}
