using System;
using System.IO;
using Content.Server._ForkStation.PersistentPrisoner;
using Content.Shared._ForkStation.PersistentPrisoner;
using NUnit.Framework;

namespace Content.Tests.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Custody-gated confirmation, death classification, serve credit, undo policy, and
/// fugitive outcomes — all via shipped pure functions and the real PenaltyDataStore.
/// </summary>
[TestFixture]
[TestOf(typeof(PrisonerDesignRules))]
public sealed class CustodyAndDeathRulesTest
{
    [Test]
    public void PendingSecuritySentenceDoesNotCountTowardForceSpawnUntilConfirmed()
    {
        Assert.That(PrisonerDesignRules.CountsTowardForceSpawnBalance(pendingCustody: true, isServed: false), Is.False,
            "mid-round sec issue without custody must not force next-round prisoner spawn");
        Assert.That(PrisonerDesignRules.CountsTowardForceSpawnBalance(pendingCustody: false, isServed: false), Is.True,
            "confirmed outstanding balance drives force-spawn");
        Assert.That(PrisonerDesignRules.CountsTowardForceSpawnBalance(pendingCustody: false, isServed: true), Is.False);
    }

    [Test]
    public void CustodyAtRoundEndConfirmsPending_EscapeDoesNot()
    {
        Assert.That(PrisonerDesignRules.ShouldConfirmPendingAtRoundEnd(pendingCustody: true, endedInDesignCustody: true), Is.True);
        Assert.That(PrisonerDesignRules.ShouldConfirmPendingAtRoundEnd(pendingCustody: true, endedInDesignCustody: false), Is.False);
        Assert.That(PrisonerDesignRules.ShouldConfirmPendingAtRoundEnd(pendingCustody: false, endedInDesignCustody: true), Is.False);
    }

    [Test]
    public void DesignCustodyIsPermabrigOrCuffedShuttle()
    {
        Assert.That(PrisonerDesignRules.IsInDesignCustody(nearPrisonerOrSolitarySpawn: true, onEmergencyShuttleWhileCuffed: false), Is.True);
        Assert.That(PrisonerDesignRules.IsInDesignCustody(nearPrisonerOrSolitarySpawn: false, onEmergencyShuttleWhileCuffed: true), Is.True);
        Assert.That(PrisonerDesignRules.IsInDesignCustody(nearPrisonerOrSolitarySpawn: false, onEmergencyShuttleWhileCuffed: false), Is.False);
    }

    [Test]
    public void Store_PendingDoesNotAffectOutstanding_ConfirmDoes()
    {
        var path = Path.Combine(Path.GetTempPath(), "pp-test-" + Path.GetRandomFileName() + ".json");
        try
        {
            var store = new PenaltyDataStore(path, forTests: true);

            var pending = store.AddPenalty("user-a", "sec", "Sec", 3, "assault", roundId: 1,
                adminIssued: false, pendingCustody: true);

            Assert.That(store.GetTotalPenaltyRounds("user-a"), Is.EqualTo(0),
                "pending sec sentence must not create force-spawn balance");
            Assert.That(store.GetPendingPlusOutstandingRounds("user-a"), Is.EqualTo(3));

            Assert.That(store.ConfirmPending(pending.Id), Is.True);
            Assert.That(store.GetTotalPenaltyRounds("user-a"), Is.EqualTo(3),
                "after custody confirmation the balance is outstanding");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void LifetimeCapCountsPendingSoConfirmCannotExceedTwenty()
    {
        // Skeptic case: outstanding 18 + pending 2 + execution 2 must not land at 22 after confirm.
        // Clamp against pending+outstanding refuses the execution grant once headroom is gone.
        Assert.That(
            PrisonerDesignRules.ClampPenaltyRounds(
                requested: 2,
                alreadyAppliedThisRound: 2,
                currentOutstandingTotal: 20, // 18 confirmed + 2 pending already counted
                isExecution: true,
                adminIssued: false),
            Is.Zero,
            "execution-class grant must not push pending+outstanding past 20");

        Assert.That(
            PrisonerDesignRules.ClampPenaltyRounds(
                requested: 2,
                alreadyAppliedThisRound: 0,
                currentOutstandingTotal: 18,
                isExecution: false,
                adminIssued: false),
            Is.EqualTo(2),
            "pending grant still allowed while room remains under 20");
    }

    [Test]
    public void ConfirmPendingTruncatesIfLifetimeWouldExceedCap()
    {
        var path = Path.Combine(Path.GetTempPath(), "pp-test-" + Path.GetRandomFileName() + ".json");
        try
        {
            var store = new PenaltyDataStore(path, forTests: true);

            // Confirmed 18 + pending 5 (simulate race) → confirm must leave at most 20.
            store.AddPenalty("user-c", "admin", "Admin", 18, "prior", roundId: 0,
                adminIssued: true, pendingCustody: false);
            var pending = store.AddPenalty("user-c", "sec", "Sec", 5, "stack", roundId: 1,
                adminIssued: false, pendingCustody: true);

            Assert.That(store.ConfirmPending(pending.Id), Is.True);
            Assert.That(store.GetTotalPenaltyRounds("user-c"), Is.EqualTo(PrisonerDesignRules.MaxPenaltyRounds),
                "confirm must truncate so lifetime never exceeds 20");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SkepticScenario_Outstanding18Pending2ExecutionBlocked_ConfirmStaysAt20()
    {
        // Full skeptic path against the real store (shipped Add/Confirm/GetTotal).
        var path = Path.Combine(Path.GetTempPath(), "pp-test-" + Path.GetRandomFileName() + ".json");
        try
        {
            var store = new PenaltyDataStore(path, forTests: true);

            store.AddPenalty("user-sk", "admin", "Admin", 18, "prior", roundId: 0,
                adminIssued: true, pendingCustody: false);
            var pending = store.AddPenalty("user-sk", "sec", "Sec", 2, "assault", roundId: 1,
                adminIssued: false, pendingCustody: true);

            Assert.That(store.GetTotalPenaltyRounds("user-sk"), Is.EqualTo(18), "force-spawn still 18");
            Assert.That(store.GetPendingPlusOutstandingRounds("user-sk"), Is.EqualTo(20),
                "officers see pending+outstanding = 20 mid-round");

            // What Clamp does for secpenaltydead +2 when callers pass pending+outstanding (shipped path).
            var room = PrisonerDesignRules.ClampPenaltyRounds(
                2, alreadyAppliedThisRound: 2,
                currentOutstandingTotal: store.GetPendingPlusOutstandingRounds("user-sk"),
                isExecution: true, adminIssued: false);
            Assert.That(room, Is.Zero, "execution grant refused at lifetime full");

            Assert.That(store.ConfirmPending(pending.Id), Is.True);
            Assert.That(store.GetTotalPenaltyRounds("user-sk"), Is.EqualTo(20),
                "after custody confirm outstanding is exactly 20, never 22");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void MidRoundSecIssueVisibleViaPendingPlusOutstandingNotConfirmedOnly()
    {
        // Skeptic gap: after mid-round sec issue, GetPenaltyRounds alone is 0; officers need
        // GetPendingPlusOutstandingRounds so Total is not 0.
        var path = Path.Combine(Path.GetTempPath(), "pp-test-" + Path.GetRandomFileName() + ".json");
        try
        {
            var store = new PenaltyDataStore(path, forTests: true);
            store.AddPenalty("user-mid", "sec", "Sec", 3, "theft", roundId: 7,
                adminIssued: false, pendingCustody: true);

            Assert.That(store.GetTotalPenaltyRounds("user-mid"), Is.EqualTo(0),
                "confirmed-only must stay 0 until custody");
            Assert.That(store.GetPendingPlusOutstandingRounds("user-mid"), Is.EqualTo(3),
                "command/console Total must use this and show 3");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SecPenaltyFeedbackNeverRevealsAntagStatus()
    {
        // Shipped helpers used by SecPenaltyCommand / SecPenaltyOfflineCommand.
        Assert.That(PrisonerDesignRules.ShouldSilentlySkipAntagPenalty(true), Is.True);
        Assert.That(PrisonerDesignRules.ShouldSilentlySkipAntagPenalty(false), Is.False);

        var message = PrisonerDesignRules.FormatSecPenaltyAppliedMessage(3, "Bob", 3);
        Assert.That(message, Does.Contain("Applied 3"));
        Assert.That(message, Does.Contain("Total: 3"));
        Assert.That(message, Does.Not.Contain("Antagonist").IgnoreCase);
        Assert.That(message, Does.Not.Contain("antag").IgnoreCase);
    }

    [Test]
    public void Store_DiscardPendingOnEscapeClearsUnconfirmed()
    {
        var path = Path.Combine(Path.GetTempPath(), "pp-test-" + Path.GetRandomFileName() + ".json");
        try
        {
            var store = new PenaltyDataStore(path, forTests: true);

            store.AddPenalty("user-b", "sec", "Sec", 2, "theft", roundId: 1,
                adminIssued: false, pendingCustody: true);
            store.AddPenalty("user-b", "admin", "Admin", 1, "note", roundId: 1,
                adminIssued: true, pendingCustody: false);

            Assert.That(store.GetTotalPenaltyRounds("user-b"), Is.EqualTo(1), "admin sticks immediately");

            var discarded = store.DiscardAllRemainingPending();
            Assert.That(discarded, Is.EqualTo(1));
            Assert.That(store.GetTotalPenaltyRounds("user-b"), Is.EqualTo(1), "confirmed admin remains");
            Assert.That(store.GetPendingPlusOutstandingRounds("user-b"), Is.EqualTo(1));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void DeathOutsidePermaAppliesExecution_InsidePermaDoesNot()
    {
        Assert.That(
            PrisonerDesignRules.ShouldApplyDeathExecutionPenalty(
                hasOutstandingPenalties: true, isAntagonist: false, alreadyCountedThisRound: false,
                diedInsidePermabrig: false),
            Is.True,
            "outside perma + active penalties → execution +1");

        Assert.That(
            PrisonerDesignRules.ShouldApplyDeathExecutionPenalty(
                hasOutstandingPenalties: true, isAntagonist: false, alreadyCountedThisRound: false,
                diedInsidePermabrig: true),
            Is.False,
            "env/NPC death inside perma must not stack");

        Assert.That(
            PrisonerDesignRules.ShouldApplyDeathExecutionPenalty(
                hasOutstandingPenalties: true, isAntagonist: true, alreadyCountedThisRound: false,
                diedInsidePermabrig: false),
            Is.False,
            "antags do not accumulate");

        Assert.That(
            PrisonerDesignRules.ShouldApplyDeathExecutionPenalty(
                hasOutstandingPenalties: true, isAntagonist: false, alreadyCountedThisRound: true,
                diedInsidePermabrig: false),
            Is.False,
            "once per round");

        Assert.That(
            PrisonerDesignRules.ShouldApplyDeathExecutionPenalty(
                hasOutstandingPenalties: false, isAntagonist: false, alreadyCountedThisRound: false,
                diedInsidePermabrig: false),
            Is.False);
    }

    [Test]
    public void ServeRequiresAliveConnectedAndOneThird()
    {
        var round = TimeSpan.FromMinutes(60);
        Assert.That(PrisonerDesignRules.EarnsServeCredit(true, true, TimeSpan.FromMinutes(20), round), Is.True);
        Assert.That(PrisonerDesignRules.EarnsServeCredit(true, true, TimeSpan.FromMinutes(19), round), Is.False);
        Assert.That(PrisonerDesignRules.EarnsServeCredit(false, true, TimeSpan.FromMinutes(40), round), Is.False);
        Assert.That(PrisonerDesignRules.EarnsServeCredit(true, false, TimeSpan.FromMinutes(40), round), Is.False);
    }

    [Test]
    public void SecurityUndoRejectsAdminAndPriorRound()
    {
        Assert.That(PrisonerDesignRules.CanSecurityUndo(adminIssued: true, issuedRoundId: 5, currentRoundId: 5, out var r1), Is.False);
        Assert.That(r1, Does.Contain("administrator").IgnoreCase);

        Assert.That(PrisonerDesignRules.CanSecurityUndo(adminIssued: false, issuedRoundId: 4, currentRoundId: 5, out var r2), Is.False);
        Assert.That(r2, Does.Contain("earlier").IgnoreCase);

        Assert.That(PrisonerDesignRules.CanSecurityUndo(adminIssued: false, issuedRoundId: 5, currentRoundId: 5, out var r3), Is.True);
        Assert.That(r3, Is.Empty);
    }

    [Test]
    public void FugitiveOutcomesMatchDesignWithoutDoubleCountOnDeath()
    {
        Assert.That(
            PrisonerDesignRules.EvaluateFugitiveOutcome(isDead: true, inCustodyCuffed: false),
            Is.EqualTo(PrisonerDesignRules.FugitiveRoundOutcome.DeadAlreadyHandled));
        Assert.That(
            PrisonerDesignRules.EvaluateFugitiveOutcome(isDead: false, inCustodyCuffed: true),
            Is.EqualTo(PrisonerDesignRules.FugitiveRoundOutcome.CustodyPlusOne));
        Assert.That(
            PrisonerDesignRules.EvaluateFugitiveOutcome(isDead: false, inCustodyCuffed: false),
            Is.EqualTo(PrisonerDesignRules.FugitiveRoundOutcome.FreeMinusOne));
    }

    [Test]
    public void SolitaryPlacementThresholdIsAboveFifteen()
    {
        Assert.That(PrisonerDesignRules.WantsSolitaryPlacement(15), Is.False);
        Assert.That(PrisonerDesignRules.WantsSolitaryPlacement(16), Is.True);
    }

    [Test]
    public void RecordCountsTowardBalancePropertyUsesShippedRule()
    {
        var pending = new PenaltyRecord { RoundsAssigned = 2, RoundsServed = 0, PendingCustody = true };
        Assert.That(pending.CountsTowardBalance, Is.False);

        var confirmed = new PenaltyRecord { RoundsAssigned = 2, RoundsServed = 0, PendingCustody = false };
        Assert.That(confirmed.CountsTowardBalance, Is.True);
    }
}
