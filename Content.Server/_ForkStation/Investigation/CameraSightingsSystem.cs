using System.Linq;
using Content.Server.GameTicking;
using Content.Shared._ForkStation.Investigation;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Components;
using Content.Shared.SurveillanceCamera.Components;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.Investigation;

/// <summary>
/// Makes surveillance cameras remember who walked past them.
///
/// Without this, camera footage only exists while somebody happens to be staring at the monitor,
/// so a murder committed in full view of a camera leaves no evidence whatsoever. Security then has
/// nothing to build a case on, and the persistent penalties they hand out are guesswork — which is
/// the failure mode the design doc is trying to eliminate.
/// </summary>
public sealed class CameraSightingsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<CameraSightingsComponent, SurveillanceCameraComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var sightings, out var camera, out var xform))
        {
            if (now < sightings.NextScan)
                continue;

            sightings.NextScan = now + sightings.Interval;

            // A camera that has been switched off or cut sees nothing. That's deliberate: it
            // leaves sabotaging the cameras as a real way to commit an unwitnessed crime.
            if (!camera.Active)
                continue;

            Scan((uid, sightings), xform, now);
        }
    }

    private void Scan(Entity<CameraSightingsComponent> camera, TransformComponent xform, TimeSpan now)
    {
        var stationTime = now - _ticker.RoundStartTimeSpan;
        var origin = _transform.GetMapCoordinates(camera.Owner, xform);

        foreach (var seen in _lookup.GetEntitiesInRange<MobStateComponent>(origin, camera.Comp.Range))
        {
            var subject = Identity.Name(seen.Owner, EntityManager);
            if (string.IsNullOrWhiteSpace(subject))
                continue;

            // Someone loitering in view shouldn't flood the buffer and evict older, more useful
            // sightings.
            if (camera.Comp.LastSeen.TryGetValue(subject, out var last) &&
                now - last < camera.Comp.RepeatSuppression)
            {
                continue;
            }

            camera.Comp.LastSeen[subject] = now;
            Record(camera, new CameraSighting(stationTime, subject));
        }
    }

    private void Record(Entity<CameraSightingsComponent> camera, CameraSighting sighting)
    {
        camera.Comp.Sightings.Enqueue(sighting);

        while (camera.Comp.Sightings.Count > camera.Comp.SightingLimit)
            camera.Comp.Sightings.Dequeue();
    }

    /// <summary>
    /// Everyone this camera remembers seeing, oldest first.
    /// </summary>
    public IReadOnlyCollection<CameraSighting> GetSightings(Entity<CameraSightingsComponent> camera)
        => camera.Comp.Sightings;

    /// <summary>
    /// Every sighting of <paramref name="subject"/> across all cameras, ordered by time. This is
    /// the query that turns scattered footage into a suspect's movements through the station.
    /// </summary>
    public List<(string Camera, CameraSighting Sighting)> TrackSubject(string subject)
    {
        var found = new List<(string, CameraSighting)>();
        var query = EntityQueryEnumerator<CameraSightingsComponent>();

        while (query.MoveNext(out var uid, out var sightings))
        {
            foreach (var sighting in sightings.Sightings)
            {
                if (!sighting.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase))
                    continue;

                found.Add((Name(uid), sighting));
            }
        }

        return found.OrderBy(entry => entry.Item2.SightingTime).ToList();
    }
}
