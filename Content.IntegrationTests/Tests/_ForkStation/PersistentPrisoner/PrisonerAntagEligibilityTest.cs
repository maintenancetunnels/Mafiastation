using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._ForkStation.PersistentPrisoner;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._ForkStation.PersistentPrisoner;

/// <summary>
/// The prisoner antag allowlist is a set of raw prototype id strings, so a typo silently bars
/// prisoners from that antagonist forever with no error anywhere — exactly the class of quiet
/// failure that left Persistent Prisoners inert for months.
/// </summary>
[TestFixture]
public sealed class PrisonerAntagEligibilityTest : GameTest
{
    /// <summary>
    /// Rules a prisoner must never be able to roll: they arrive from off-station, or announce
    /// themselves loudly. "Persistent prisoners can only be station-bound stealth antagonists.
    /// No wizard, no nuke ops, no xenomorph."
    /// </summary>
    private static readonly string[] MustBeExcluded =
    {
        "Nukeops",
        "Wizard",
        "SubWizard",
        "Xenoborgs",
        "SubXenoborgs",
        "LoneOpsSpawn",
        "NinjaSpawn",
        "DragonSpawn",
        "RevenantSpawn",
        "Revolutionary",
    };

    [Test]
    public async Task EveryAllowedRuleActuallyExists()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var missing = PrisonerAntagEligibilitySystem.StationBoundStealthRules
                .Where(id => !prototypes.HasIndex<EntityPrototype>(id))
                .ToList();

            Assert.That(missing, Is.Empty,
                $"these allowlisted antag rules do not exist, so prisoners can never roll them: {string.Join(", ", missing)}");
        });
    }

    [Test]
    public async Task ExternalAndLoudAntagsAreNotAllowed()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            foreach (var forbidden in MustBeExcluded)
            {
                Assert.That(PrisonerAntagEligibilitySystem.StationBoundStealthRules, Does.Not.Contain(forbidden),
                    $"{forbidden} is not a station-bound stealth antagonist and must be closed to prisoners");
            }
        });
    }

    [Test]
    public async Task TraitorIsAllowedSoAnEscapedPrisonerIsNeverProvablyInnocent()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            Assert.That(PrisonerAntagEligibilitySystem.StationBoundStealthRules, Contains.Item("Traitor"),
                "if no stealth antag is reachable the crew can metagame-rule-out every prisoner, " +
                "which is the exact outcome the design doc is trying to prevent");
        });
    }
}
