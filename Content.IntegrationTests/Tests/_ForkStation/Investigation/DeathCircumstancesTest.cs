using Content.IntegrationTests.Fixtures;
using Content.Shared._ForkStation.Investigation;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._ForkStation.Investigation;

/// <summary>
/// A murder has to leave a record. Before this existed a corpse knew how much damage it had
/// taken but not what killed it, when, or who was responsible — which made security's penalties
/// pure guesswork.
/// </summary>
[TestFixture]
public sealed class DeathCircumstancesTest : GameTest
{
    private static readonly EntProtoId Victim = "MobHuman";
    private static readonly EntProtoId Killer = "MobHuman";

    [Test]
    public async Task LethalDamageRecordsCauseAndSuspect()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        var entMan = server.EntMan;
        EntityUid victim = default;
        EntityUid killer = default;

        await server.WaitPost(() =>
        {
            victim = entMan.SpawnEntity(Victim, MapCoordinates.Nullspace);
            killer = entMan.SpawnEntity(Killer, MapCoordinates.Nullspace);
        });
        await server.WaitRunTicks(2);

        await server.WaitPost(() =>
        {
            var damageable = entMan.System<DamageableSystem>();

            // Enough Slash to be unambiguously lethal, attributed to the killer.
            var wound = new DamageSpecifier();
            wound.DamageDict["Slash"] = FixedPoint2.New(500);

            damageable.TryChangeDamage(victim, wound, ignoreResistances: true, origin: killer);
        });
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<DeathCircumstancesComponent>(victim, out var circumstances),
                Is.True, "a killed mob must carry death circumstances");

            Assert.Multiple(() =>
            {
                Assert.That(circumstances!.KillingDamageType, Is.EqualTo("Slash"),
                    "the dominant damage type is the murder-weapon signature");
                Assert.That(circumstances.HasSuspect, Is.True, "damage with an origin has a suspect");
                Assert.That(circumstances.SuspectName, Is.Not.Null.And.Not.Empty);
                Assert.That(circumstances.TimeOfDeath, Is.Not.Null, "time of death anchors the timeline");
            });
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(victim);
            entMan.DeleteEntity(killer);
        });
    }

    [Test]
    public async Task EnvironmentalDamageLeavesNoSuspect()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        var entMan = server.EntMan;
        EntityUid victim = default;

        await server.WaitPost(() =>
        {
            victim = entMan.SpawnEntity(Victim, MapCoordinates.Nullspace);
        });
        await server.WaitRunTicks(2);

        await server.WaitPost(() =>
        {
            var damageable = entMan.System<DamageableSystem>();
            var wound = new DamageSpecifier();
            wound.DamageDict["Asphyxiation"] = FixedPoint2.New(500);

            // No origin: vacuum, fire, nobody to blame.
            damageable.TryChangeDamage(victim, wound, ignoreResistances: true);
        });
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<DeathCircumstancesComponent>(victim, out var circumstances),
                Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(circumstances!.HasSuspect, Is.False,
                    "an environmental death must not implicate anyone — the doc treats these differently from executions");
                Assert.That(circumstances.SuspectName, Is.Null);
                Assert.That(circumstances.KillingDamageType, Is.EqualTo("Asphyxiation"));
            });
        });

        await server.WaitPost(() => entMan.DeleteEntity(victim));
    }
}
