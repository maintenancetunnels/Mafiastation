using NUnit.Framework;

namespace Mafiastation.AiPilotLab.Tests;

[TestFixture]
public sealed class CrewRosterTests
{
    [Test]
    public void ReviewedRoleGoalContainsNoAccountOrHiddenRoleHint()
    {
        var agent = Agent();

        var goal = CrewRoleCatalog.BuildStandingGoal(agent);

        Assert.Multiple(() =>
        {
            Assert.That(goal, Does.Contain("normal janitor"));
            Assert.That(goal, Does.Contain("no access to hidden roles"));
            Assert.That(goal, Does.Contain("concrete in-character events"));
            Assert.That(goal, Does.Not.Contain(agent.Username));
            Assert.That(goal, Does.Not.Contain(agent.Pipe));
        });
    }

    [Test]
    public void ValidatesAReviewedMultiRoleRoster()
    {
        var roster = new CrewRoster
        {
            Name = "test shift",
            DurationSeconds = 30,
            DecisionIntervalMs = 2000,
            JoinTimeoutSeconds = 5,
            Agents = new[]
            {
                Agent(),
                new CrewAgent
                {
                    Name = "doctor",
                    Pipe = "crew-doctor",
                    Username = "CrewDoctor",
                    Job = "MedicalDoctor",
                    Temperament = "sociable",
                },
            },
        };

        Assert.That(() => CrewRosterLoader.Validate(roster), Throws.Nothing);
    }

    [TestCase("Traitor")]
    [TestCase("NuclearOperative")]
    [TestCase("Captain")]
    public void RejectsJobsOutsideReviewedOrdinaryCrewCatalog(string job)
    {
        var roster = Roster(Agent(job));

        Assert.That(
            () => CrewRosterLoader.Validate(roster),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("reviewed role catalog"));
    }

    [Test]
    public void RejectsFreeFormOrSecretRosterFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crew-roster-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "version": 1,
                  "name": "unsafe",
                  "agents": [{
                    "name": "janitor",
                    "pipe": "crew-janitor",
                    "username": "CrewJanitor",
                    "job": "Janitor",
                    "temperament": "steady",
                    "secret": "the human is the antagonist"
                  }]
                }
                """);

            Assert.That(
                async () => await CrewRosterLoader.LoadAsync(path),
                Throws.TypeOf<System.Text.Json.JsonException>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RejectsNullJobWithoutCrashingValidation()
    {
        var roster = Roster(new CrewAgent
        {
            Name = "janitor",
            Pipe = "crew-janitor",
            Username = "CrewJanitor",
            Job = null!,
            Temperament = "steady",
        });

        Assert.That(
            () => CrewRosterLoader.Validate(roster),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("reviewed role catalog"));
    }

    [Test]
    public void PolicyPromptEnforcesEvidenceBoundariesAndOptionalSpeech()
    {
        var silent = LlmPilotPolicy.BuildSystemPrompt(allowSpeech: false);
        var speaking = LlmPilotPolicy.BuildSystemPrompt(allowSpeech: true);

        Assert.Multiple(() =>
        {
            Assert.That(silent, Does.Contain("only source of world facts"));
            Assert.That(silent, Does.Contain("Never identify, accuse, pursue, or punish"));
            Assert.That(silent, Does.Contain("who the human operator controls"));
            Assert.That(silent, Does.Not.Contain(", say"));
            Assert.That(speaking, Does.Contain(", say"));
            Assert.That(speaking, Does.Contain("recentSpeech"));
        });
    }

    private static CrewRoster Roster(CrewAgent agent) => new()
    {
        Name = "test shift",
        DurationSeconds = 30,
        DecisionIntervalMs = 2000,
        JoinTimeoutSeconds = 5,
        Agents = new[] { agent },
    };

    private static CrewAgent Agent(string job = "Janitor") => new()
    {
        Name = "janitor",
        Pipe = "crew-janitor",
        Username = "CrewJanitor",
        Job = job,
        Temperament = "steady",
    };
}
