using System.Collections.Generic;
using Content.IntegrationTests.Fixtures;
using Content.Server._ForkStation.LlmDirector;
using Content.Server.NPC.HTN;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._ForkStation.AiPilot;

[TestFixture]
public sealed class HybridNpcTests : GameTest
{
    private const string RoutinePrototype = "MafiaHybridPrisonerRoutine";
    private static readonly EntProtoId HybridNpcPrototype = "MobMafiaHybridPrisoner";

    [Test]
    public async Task LabPrototypeLoadsWithRoutineHybridController()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            Assert.That(
                prototypes.HasIndex<HTNCompoundPrototype>(RoutinePrototype),
                Is.True);
            Assert.That(
                prototypes.HasIndex<EntityPrototype>(HybridNpcPrototype),
                Is.True);
        });
    }

    [Test]
    public async Task ConfigurationRejectsCapacityMismatchAndKeepsRoutineRoot()
    {
        var server = Pair.Server;
        await server.WaitIdleAsync();

        EntityUid uid = default;
        await server.WaitPost(() =>
        {
            uid = server.EntMan.SpawnEntity(
                HybridNpcPrototype,
                MapCoordinates.Nullspace);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var hybrid = server.ResolveDependency<IEntitySystemManager>()
                .GetEntitySystem<LlmNpcHybridSystem>();
            var status = hybrid.GetStatus(uid);
            Assert.Multiple(() =>
            {
                Assert.That(status.Configured, Is.True);
                Assert.That(status.RoutineTask, Is.EqualTo("MafiaHybridPrisonerRoutine"));
                Assert.That(status.CurrentTask, Is.EqualTo("MafiaHybridPrisonerRoutine"));
            });

            var invalidGoals = new[]
            {
                new HybridNpcComplexGoal
                {
                    Id = "combat",
                    Task = "SimpleHostileCompound",
                    RequiredCapabilities = new HashSet<HybridNpcCapability>
                    {
                        HybridNpcCapability.Combat,
                    },
                },
                new HybridNpcComplexGoal
                {
                    Id = "idle",
                    Task = "IdleCompound",
                },
            };
            var configured = hybrid.TryConfigureNpc(
                uid,
                "MafiaHybridPrisonerRoutine",
                new[] { HybridNpcCapability.Move },
                invalidGoals,
                "test",
                60,
                20,
                out var error);

            Assert.That(configured, Is.False);
            Assert.That(error, Does.Contain("outside its profile"));
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(uid));
    }
}
