using Content.Server._ForkStation.PersistentPrisoner;
using Content.Shared._ForkStation.PersistentPrisoner;
using NUnit.Framework;

namespace Content.Tests.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Covers the penalty arithmetic and design-rule functions that the Persistent Prisoners
/// design doc specifies numerically. All assertions call shipped pure functions.
/// </summary>
[TestFixture]
[TestOf(typeof(PrisonerDesignRules))]
public sealed class PenaltyMathTest
{
    private static int Clamp(int requested, int applied = 0, int total = 0, bool execution = false, bool admin = false)
        => PrisonerDesignRules.ClampPenaltyRounds(requested, applied, total, execution, admin);

    [Test]
    public void NonPositiveRequestGrantsNothing()
    {
        Assert.That(Clamp(0), Is.Zero);
        Assert.That(Clamp(-3), Is.Zero);
    }

    [Test]
    public void OrdinaryPenaltyIsGrantedInFull()
    {
        Assert.That(Clamp(3), Is.EqualTo(3));
    }

    [Test]
    public void PerRoundCapIsFivePenaltyRoundsNotFiveRecords()
    {
        Assert.That(Clamp(5), Is.EqualTo(5), "a single five-round penalty should fit the shift budget");
        Assert.That(Clamp(5, applied: 5), Is.Zero, "budget already spent");
        Assert.That(Clamp(5, applied: 3), Is.EqualTo(2), "only the remaining budget is granted");
    }

    [Test]
    public void RequestOverPerRoundCapIsTrimmedNotRejected()
    {
        Assert.That(Clamp(9), Is.EqualTo(PrisonerDesignRules.MaxPenaltiesPerRound));
    }

    [Test]
    public void ExecutionIsCappedAtTwo()
    {
        Assert.That(Clamp(5, execution: true), Is.EqualTo(PrisonerDesignRules.MaxExecutionPenalties));
        Assert.That(Clamp(1, execution: true), Is.EqualTo(1), "a one-round execution stays one");
    }

    [Test]
    public void LifetimeCapIsTwentyAndAppliesToAdminsToo()
    {
        Assert.That(Clamp(5, total: 18), Is.EqualTo(2));
        Assert.That(Clamp(5, total: PrisonerDesignRules.MaxPenaltyRounds), Is.Zero);
        Assert.That(Clamp(5, total: 19, admin: true), Is.EqualTo(1),
            "admins bypass the per-round cap but never the lifetime cap");
    }

    [Test]
    public void AdminBypassesPerRoundCapOnly()
    {
        Assert.That(Clamp(4, applied: 5, admin: true), Is.EqualTo(4));
        Assert.That(Clamp(4, applied: 5, admin: false), Is.Zero);
    }

    [Test]
    public void ExecutionCapAppliesBeforeTheOtherCaps()
    {
        Assert.That(Clamp(10, total: 19, execution: true), Is.EqualTo(1));
    }

    [Test]
    public void AntagChanceIsZeroForNonPrisoners()
    {
        Assert.That(PrisonerDesignRules.GetPrisonerAntagChance(0), Is.Zero);
        Assert.That(PrisonerDesignRules.GetPrisonerAntagChance(-1), Is.Zero);
    }

    [Test]
    public void AntagChanceHitsExactlyZeroAtEighteen()
    {
        Assert.That(PrisonerDesignRules.GetPrisonerAntagChance(17), Is.GreaterThan(0f));
        Assert.That(PrisonerDesignRules.GetPrisonerAntagChance(PrisonerDesignRules.AntagZeroThreshold), Is.Zero);
        Assert.That(PrisonerDesignRules.GetPrisonerAntagChance(20), Is.Zero);
    }

    [Test]
    public void AntagChanceDecreasesMonotonicallyWithPenalties()
    {
        var previous = float.MaxValue;
        for (var penalties = 1; penalties < PrisonerDesignRules.AntagZeroThreshold; penalties++)
        {
            var chance = PrisonerDesignRules.GetPrisonerAntagChance(penalties);
            Assert.That(chance, Is.LessThan(previous), $"chance should fall at {penalties} penalties");
            previous = chance;
        }
    }

    [Test]
    public void SolitaryPrisonerKeepsTheOneInTwoHundredChance()
    {
        var atTopOfBand = PrisonerDesignRules.GetPrisonerAntagChance(17);
        Assert.That(atTopOfBand, Is.EqualTo(PrisonerDesignRules.SolitaryPrisonerAntagChance).Within(0.0001f));

        var solitaryFloor = PrisonerDesignRules.GetPrisonerAntagChance(PrisonerDesignRules.SolitaryThreshold);
        Assert.That(solitaryFloor, Is.GreaterThan(0f), "a solitary prisoner must still be able to roll antag");
        Assert.That(solitaryFloor, Is.LessThan(0.02f), "but only barely");
    }

    [Test]
    public void FugitiveChanceMatchesTheDocumentedShape()
    {
        Assert.That(PrisonerDesignRules.GetFugitiveChance(1), Is.LessThan(0.20f));
        Assert.That(PrisonerDesignRules.GetFugitiveChance(1),
            Is.EqualTo(PrisonerDesignRules.GetFugitiveChance(5)).Within(0.0001f),
            "chance is flat up to the drop-off threshold");
        Assert.That(PrisonerDesignRules.GetFugitiveChance(10),
            Is.LessThan(PrisonerDesignRules.GetFugitiveChance(5)),
            "chance drops above five penalties");
        Assert.That(PrisonerDesignRules.GetFugitiveChance(20), Is.Zero, "zero at twenty");
        Assert.That(PrisonerDesignRules.GetFugitiveChance(25), Is.Zero);
    }

    [Test]
    public void SystemAliasesMatchDesignRules()
    {
        // Call sites still use PersistentPrisonerSystem constants / wrappers.
        Assert.That(PersistentPrisonerSystem.MaxPenaltiesPerRound, Is.EqualTo(5));
        Assert.That(PersistentPrisonerSystem.ClampPenaltyRounds(5, 0, 0, false, false), Is.EqualTo(5));
        Assert.That(FugitiveSpawnSystem.GetFugitiveChance(20), Is.Zero);
    }
}
