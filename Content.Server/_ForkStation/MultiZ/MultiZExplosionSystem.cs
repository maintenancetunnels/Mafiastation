using Content.Server.Explosion.EntitySystems;
using Content.Shared._ForkStation.MultiZ;
using Content.Shared.Explosion;

namespace Content.Server._ForkStation.MultiZ;

/// <summary>
/// Ports SS13's z-level explosion travel: when a blast reaches an open <see cref="MultiZLinkComponent"/>
/// shaft, a portion of it detonates on the level below at the landing tile. Drop a grenade down the
/// shaft, or blow a hole near it, and the deck beneath feels it. The shaft is indestructible but
/// Damageable, so it receives the explosion hit that drives this.
/// </summary>
public sealed class MultiZExplosionSystem : EntitySystem
{
    [Dependency] private readonly ExplosionSystem _explosion = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>Fraction of the blast that carries to the next level down.</summary>
    private const float Falloff = 0.5f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MultiZLinkComponent, BeforeExplodeEvent>(OnBeforeExplode);
    }

    private void OnBeforeExplode(Entity<MultiZLinkComponent> ent, ref BeforeExplodeEvent args)
    {
        if (!ent.Comp.Open || ent.Comp.Down is not { } down || Deleted(down))
            return;

        var xform = Transform(down);

        var intensity = (float)args.Damage.GetTotal() * Falloff;
        if (intensity < 1f)
            return;

        var landing = _transform.GetMapCoordinates(down, xform);
        _explosion.QueueExplosion(
            landing,
            args.Id,
            intensity,
            slope: 2f,
            maxTileIntensity: intensity,
            cause: ent,
            addLog: false);
    }
}
