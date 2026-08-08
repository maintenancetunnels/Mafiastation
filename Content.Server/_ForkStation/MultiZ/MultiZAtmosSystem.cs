using Content.Server.Atmos.EntitySystems;
using Content.Shared._ForkStation.MultiZ;
using Content.Shared.Atmos;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.MultiZ;

/// <summary>
/// Ports SS13's openturf z-atmos: while a <see cref="MultiZLinkComponent"/> shaft is open, the air
/// on the shaft tile and the air on its landing tile (a level below, a different grid) are pulled
/// toward each other every atmos step. Air rushes down into a low-pressure basement; a hull breach
/// on either level decompresses both through the shaft; gas and heat cross the seam. Sealing the
/// hatch (Open = false) closes the pressure boundary and the two levels isolate again.
/// </summary>
public sealed class MultiZAtmosSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    // Atmos is expensive; share on a coarse tick rather than every frame.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private TimeSpan _next;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _next)
            return;
        _next = _timing.CurTime + Interval;

        var query = EntityQueryEnumerator<MultiZLinkComponent>();
        while (query.MoveNext(out var uid, out var link))
        {
            if (!link.Open || link.Down is not { } down || Deleted(down))
                continue;

            var top = _atmos.GetTileMixture(uid, excite: true);
            var bottom = _atmos.GetTileMixture(down, excite: true);
            if (top == null || bottom == null)
                continue;

            Equalize(top, bottom, Math.Clamp(link.AtmosConductance, 0f, 1f));
        }
    }

    /// <summary>
    /// Move each tile's gas and temperature a <paramref name="rate"/> fraction toward their shared
    /// average — the same "adjacent tiles share air" model the tile solver uses, applied vertically.
    /// </summary>
    private static void Equalize(GasMixture a, GasMixture b, float rate)
    {
        if (a.Immutable || b.Immutable)
            return;

        for (var i = 0; i < Atmospherics.TotalNumberOfGases; i++)
        {
            var molesA = a.GetMoles(i);
            var molesB = b.GetMoles(i);
            var avg = (molesA + molesB) * 0.5f;
            a.SetMoles(i, molesA + rate * (avg - molesA));
            b.SetMoles(i, molesB + rate * (avg - molesB));
        }

        var avgTemp = (a.Temperature + b.Temperature) * 0.5f;
        a.Temperature += rate * (avgTemp - a.Temperature);
        b.Temperature += rate * (avgTemp - b.Temperature);
    }
}
