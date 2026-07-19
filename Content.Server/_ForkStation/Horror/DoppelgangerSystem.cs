using Content.Server.Ghost;
using Content.Shared.Light.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Watches <see cref="DoppelgangerComponent"/> entities and dissolves them into ash the
/// moment a living player gets close. Nearby lights flicker as it goes.
/// </summary>
public sealed class DoppelgangerSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    private const float ScanInterval = 0.25f;
    private float _accumulator;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < ScanInterval)
            return;

        _accumulator = 0f;

        var query = EntityQueryEnumerator<DoppelgangerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var doppel, out var xform))
        {
            var ourPos = _transform.GetWorldPosition(xform);

            foreach (var session in _players.Sessions)
            {
                if (session.AttachedEntity is not { Valid: true } attached || attached == uid)
                    continue;

                if (!TryComp<MobStateComponent>(attached, out var mobState)
                    || mobState.CurrentState == MobState.Dead)
                    continue;

                var dist = (_transform.GetWorldPosition(attached) - ourPos).Length();
                if (dist > doppel.TriggerRange)
                    continue;

                Dissolve(uid, doppel, xform);
                break;
            }
        }
    }

    /// <summary>
    /// Crumble to ash, shriek quietly, flicker the nearby lights.
    /// </summary>
    public void Dissolve(EntityUid uid, DoppelgangerComponent doppel, TransformComponent xform)
    {
        Spawn(doppel.RemainsPrototype, xform.Coordinates);
        _audio.PlayPvs(doppel.DissolveSound, uid);

        var ourPos = _transform.GetWorldPosition(xform);
        var lights = EntityQueryEnumerator<PoweredLightComponent, TransformComponent>();
        while (lights.MoveNext(out var lightUid, out _, out var lightXform))
        {
            if ((_transform.GetWorldPosition(lightXform) - ourPos).Length() > doppel.FlickerRange)
                continue;

            var boo = new GhostBooEvent();
            RaiseLocalEvent(lightUid, boo);
        }

        EntityManager.System<ParanoiaTrackerSystem>().Sightings++;
        QueueDel(uid);
    }
}
