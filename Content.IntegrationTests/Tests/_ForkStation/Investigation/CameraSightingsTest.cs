using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._ForkStation.Investigation;
using Content.Shared._ForkStation.Investigation;
using Content.Shared.SurveillanceCamera.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._ForkStation.Investigation;

/// <summary>
/// Cameras must remember who they saw. Vanilla surveillance cameras record nothing — they hand a
/// live view to whoever is watching that instant — so a killing in full view of one leaves no
/// evidence at all.
/// </summary>
[TestFixture]
public sealed class CameraSightingsTest : GameTest
{
    private static readonly EntProtoId Camera = "SurveillanceCameraSecurity";

    [Test]
    public async Task StationCamerasCarryTheRecorder()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var factory = server.ResolveDependency<IComponentFactory>();

            Assert.That(prototypes.TryIndex<EntityPrototype>(Camera, out var proto), Is.True);
            Assert.That(proto!.TryGetComponent<CameraSightingsComponent>(out _, factory), Is.True,
                "a camera with no sighting record is a camera that produces no evidence");
            Assert.That(proto.TryGetComponent<SurveillanceCameraComponent>(out _, factory), Is.True);
        });
    }

    [Test]
    public async Task SightingsAreBoundedAndOrdered()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        EntityUid camera = default;

        await server.WaitPost(() =>
        {
            camera = server.EntMan.SpawnEntity(Camera, MapCoordinates.Nullspace);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var comp = server.EntMan.GetComponent<CameraSightingsComponent>(camera);
            var system = server.EntMan.System<CameraSightingsSystem>();

            // Overfill well past the limit; the buffer must evict the oldest, not grow forever,
            // and must not lose ordering — a timeline out of order is useless.
            for (var i = 0; i < comp.SightingLimit + 25; i++)
            {
                comp.Sightings.Enqueue(new CameraSighting(TimeSpan.FromSeconds(i), $"Suspect {i}"));
                while (comp.Sightings.Count > comp.SightingLimit)
                    comp.Sightings.Dequeue();
            }

            var recorded = system.GetSightings((camera, comp)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(recorded, Has.Count.EqualTo(comp.SightingLimit), "the buffer must stay bounded");
                Assert.That(recorded.First().SightingTime, Is.LessThan(recorded.Last().SightingTime),
                    "sightings must stay in chronological order");
            });
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(camera));
    }

    [Test]
    public async Task TrackingASubjectGathersSightingsAcrossCameras()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        EntityUid first = default;
        EntityUid second = default;

        await server.WaitPost(() =>
        {
            first = server.EntMan.SpawnEntity(Camera, MapCoordinates.Nullspace);
            second = server.EntMan.SpawnEntity(Camera, MapCoordinates.Nullspace);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var system = entMan.System<CameraSightingsSystem>();

            // Same suspect seen by two different cameras at different times, plus an unrelated
            // person who must not appear in the results.
            entMan.GetComponent<CameraSightingsComponent>(second).Sightings
                .Enqueue(new CameraSighting(TimeSpan.FromSeconds(90), "Dell Marrow"));
            entMan.GetComponent<CameraSightingsComponent>(first).Sightings
                .Enqueue(new CameraSighting(TimeSpan.FromSeconds(30), "Dell Marrow"));
            entMan.GetComponent<CameraSightingsComponent>(first).Sightings
                .Enqueue(new CameraSighting(TimeSpan.FromSeconds(45), "Someone Else"));

            var tracked = system.TrackSubject("Dell Marrow");

            Assert.Multiple(() =>
            {
                Assert.That(tracked, Has.Count.EqualTo(2), "both cameras that saw them must contribute");
                Assert.That(tracked[0].Sighting.SightingTime, Is.EqualTo(TimeSpan.FromSeconds(30)),
                    "movements must be returned in time order so a route can be reconstructed");
                Assert.That(tracked[1].Sighting.SightingTime, Is.EqualTo(TimeSpan.FromSeconds(90)));
                Assert.That(tracked.Select(t => t.Sighting.Subject), Has.All.EqualTo("Dell Marrow"));
            });

            // Case-insensitive, because names get typed by hand at a console.
            Assert.That(system.TrackSubject("dell marrow"), Has.Count.EqualTo(2));
        });

        await server.WaitPost(() =>
        {
            server.EntMan.DeleteEntity(first);
            server.EntMan.DeleteEntity(second);
        });
    }
}
