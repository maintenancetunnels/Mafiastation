using Content.IntegrationTests.Fixtures;
using Content.Server._ForkStation.PersistentPrisoner;
using Content.Server.Spawners.Components;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._ForkStation.PersistentPrisoner;

/// <summary>
/// Guards the contract the prisoner spawn override depends on. Persistent Prisoners was
/// completely inert for a long time because these pieces silently disagreed: the system looked
/// for a spawn point tagged with a job id that had to match the Prisoner job exactly, and
/// nothing failed loudly when it didn't.
/// </summary>
[TestFixture]
public sealed class PrisonerSpawnContractTest : GameTest
{
    private const string PrisonerJobId = "Prisoner";
    private static readonly EntProtoId PrisonerSpawnPoint = "SpawnPointPrisoner";

    [Test]
    public async Task PrisonerJobExists()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            Assert.That(prototypes.HasIndex<JobPrototype>(PrisonerJobId), Is.True,
                "the spawn override passes this job id straight to SpawnPlayerMob");
        });
    }

    [Test]
    public async Task PrisonerSpawnPointAdvertisesThePrisonerJob()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            Assert.That(prototypes.TryIndex<EntityPrototype>(PrisonerSpawnPoint, out var proto), Is.True,
                "prisoners need a marker to spawn at");

            Assert.That(proto!.TryGetComponent<SpawnPointComponent>(out var spawnPoint,
                    server.ResolveDependency<IComponentFactory>()), Is.True,
                $"{PrisonerSpawnPoint} must carry a SpawnPoint component");

            // This exact comparison is what TryFindPrisonerSpawn does. If the job id on the
            // marker ever drifts, prisoners silently spawn nowhere and serve no time.
            Assert.That(spawnPoint!.Job, Is.Not.Null);
            Assert.That(spawnPoint.Job!.Value.Id, Is.EqualTo(PrisonerJobId));
        });
    }

    [Test]
    public async Task SolitaryConfinementSpawnPointComponentIsRegistered()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var factory = server.ResolveDependency<IComponentFactory>();
            Assert.That(factory.TryGetRegistration(typeof(SolitaryConfinementSpawnPointComponent), out _), Is.True,
                "prisoners above the solitary threshold are placed at these markers");
        });
    }

    [Test]
    public async Task PenaltyCapsMatchTheDesignDocument()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(PersistentPrisonerSystem.MaxPenaltiesPerRound, Is.EqualTo(5));
                Assert.That(PersistentPrisonerSystem.MaxExecutionPenalties, Is.EqualTo(2));
                Assert.That(PersistentPrisonerSystem.MaxPenaltyRounds, Is.EqualTo(20));
                Assert.That(PersistentPrisonerSystem.SolitaryThreshold, Is.EqualTo(15));
                Assert.That(PersistentPrisonerSystem.AntagZeroThreshold, Is.EqualTo(18));
            });
        });
    }

    [Test]
    public async Task SolitaryAndPenaltyConsolePrototypesExistWithSecurityAccess()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var factory = server.ResolveDependency<IComponentFactory>();

            Assert.That(prototypes.TryIndex<EntityPrototype>("SpawnPointPrisonerSolitary", out var solitary), Is.True);
            Assert.That(solitary!.TryGetComponent<SolitaryConfinementSpawnPointComponent>(out _, factory), Is.True,
                "solitary markers must carry SolitaryConfinementSpawnPoint");

            Assert.That(prototypes.TryIndex<EntityPrototype>("ComputerPenaltyConsole", out var console), Is.True);
            Assert.That(console!.TryGetComponent<Content.Shared._ForkStation.PersistentPrisoner.PenaltyConsoleComponent>(out _, factory), Is.True);
            Assert.That(console.TryGetComponent<Content.Shared.Access.Components.AccessReaderComponent>(out var access, factory), Is.True,
                "penalty console must require access");
            Assert.That(access, Is.Not.Null);
        });
    }
}
