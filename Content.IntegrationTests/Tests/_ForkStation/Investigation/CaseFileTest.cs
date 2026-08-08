using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._ForkStation.Investigation;
using Content.Shared._ForkStation.Investigation;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._ForkStation.Investigation;

/// <summary>
/// The case file is what turns scattered logs into a case. Evidence exists in half a dozen
/// unrelated places that nobody could collate by hand mid-shift, and the design doc's argument is
/// that persistent penalties are only legitimate if security can actually prove something.
/// </summary>
[TestFixture]
public sealed class CaseFileTest : GameTest
{
    private static readonly EntProtoId Camera = "SurveillanceCameraSecurity";

    [Test]
    public async Task TimelineMergesSourcesInTimeOrder()
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
            var entMan = server.EntMan;
            var sightings = entMan.GetComponent<CameraSightingsComponent>(camera);

            // Two camera sightings, deliberately inserted out of order.
            sightings.Sightings.Enqueue(new CameraSighting(TimeSpan.FromSeconds(200), "Sable Khole"));
            sightings.Sightings.Enqueue(new CameraSighting(TimeSpan.FromSeconds(60), "Sable Khole"));

            var caseFile = entMan.System<CaseFileSystem>();
            var timeline = caseFile.BuildTimeline("Sable Khole");

            Assert.That(timeline, Has.Count.GreaterThanOrEqualTo(2));

            // The whole point is chronology; unordered evidence cannot be reasoned about.
            var times = timeline.Select(e => e.Time).ToList();
            Assert.That(times, Is.Ordered, "the timeline must be in chronological order");
            Assert.That(timeline.First().Time, Is.EqualTo(TimeSpan.FromSeconds(60)));
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(camera));
    }

    [Test]
    public async Task UnknownSubjectReportsNothingRatherThanGuessing()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var caseFile = server.EntMan.System<CaseFileSystem>();

            Assert.That(caseFile.BuildTimeline("Nobody At All"), Is.Empty);

            var formatted = caseFile.FormatTimeline("Nobody At All");
            Assert.That(formatted, Has.Count.EqualTo(1));
            Assert.That(formatted[0], Does.Contain("No station records"));
        });
    }

    [Test]
    public async Task EmptySubjectIsRejected()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var caseFile = server.EntMan.System<CaseFileSystem>();
            Assert.That(caseFile.BuildTimeline(string.Empty), Is.Empty);
            Assert.That(caseFile.BuildTimeline("   "), Is.Empty);
        });
    }

    [Test]
    public async Task DeathAppearsForBothVictimAndAccused()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        EntityUid body = default;

        await server.WaitPost(() =>
        {
            body = server.EntMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            server.EntMan.EnsureComponent<DeathCircumstancesComponent>(body);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var death = entMan.GetComponent<DeathCircumstancesComponent>(body);
            death.TimeOfDeath = TimeSpan.FromSeconds(500);
            death.KillingDamageType = "Slash";
            death.SuspectName = "Birch Colley";
            death.HasSuspect = true;

            var caseFile = entMan.System<CaseFileSystem>();

            // Searching the accused finds the killing.
            var accused = caseFile.BuildTimeline("Birch Colley");
            Assert.That(accused.Any(e => e.Source == "body" && e.Detail.Contains("implicated")), Is.True,
                "the accused's timeline must show the death they are implicated in");

            // Searching the victim finds their own death, with the attribution.
            var victimName = entMan.GetComponent<MetaDataComponent>(body).EntityName;
            var victim = caseFile.BuildTimeline(victimName);
            Assert.That(victim.Any(e => e.Source == "body" && e.Detail.Contains("Birch Colley")), Is.True,
                "the victim's timeline must name who it was attributed to");
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(body));
    }
}
