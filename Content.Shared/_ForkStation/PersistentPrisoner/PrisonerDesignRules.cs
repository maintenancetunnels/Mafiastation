namespace Content.Shared._ForkStation.PersistentPrisoner;

/// <summary>
/// Pure design-doc rules for Persistent Prisoners. Systems call these so multi-round
/// behaviour is unit-testable without a full live shift.
/// </summary>
public static class PrisonerDesignRules
{
    public const int MaxPenaltyRounds = 20;
    public const int MaxPenaltiesPerRound = 5;
    public const int MaxExecutionPenalties = 2;
    public const int SolitaryThreshold = 15;
    public const int AntagZeroThreshold = 18;
    public const float BasePrisonerAntagChance = 0.05f;
    public const float SolitaryPrisonerAntagChance = 0.005f;
    public const float BaseFugitiveChance = 0.18f;
    public const int FugitiveDropoffThreshold = 5;
    public const int FugitiveZeroThreshold = 20;

    /// <summary>
    /// How many penalty rounds may actually be granted, applying the execution cap, the
    /// per-round cap, and the lifetime cap in that order.
    /// </summary>
    public static int ClampPenaltyRounds(
        int requested,
        int alreadyAppliedThisRound,
        int currentOutstandingTotal,
        bool isExecution,
        bool adminIssued)
    {
        if (requested <= 0)
            return 0;

        if (isExecution)
            requested = Math.Min(requested, MaxExecutionPenalties);

        // Admins are deliberately exempt from the per-round cap, but never from the lifetime cap.
        if (!adminIssued)
            requested = Math.Min(requested, MaxPenaltiesPerRound - alreadyAppliedThisRound);

        requested = Math.Min(requested, MaxPenaltyRounds - currentOutstandingTotal);

        return Math.Max(0, requested);
    }

    /// <summary>
    /// Chance for a prisoner holding <paramref name="penaltyRounds"/> to roll a station-bound
    /// stealth antag role. Hits exactly zero at <see cref="AntagZeroThreshold"/>.
    /// </summary>
    public static float GetPrisonerAntagChance(int penaltyRounds)
    {
        if (penaltyRounds <= 0)
            return 0f;

        if (penaltyRounds >= AntagZeroThreshold)
            return 0f;

        var last = AntagZeroThreshold - 1;
        if (last <= 1)
            return BasePrisonerAntagChance;

        var progress = (penaltyRounds - 1) / (float)(last - 1);
        return BasePrisonerAntagChance + (SolitaryPrisonerAntagChance - BasePrisonerAntagChance) * progress;
    }

    public static float GetFugitiveChance(int penaltyRounds)
    {
        if (penaltyRounds >= FugitiveZeroThreshold)
            return 0f;

        if (penaltyRounds <= FugitiveDropoffThreshold)
            return BaseFugitiveChance;

        var range = FugitiveZeroThreshold - FugitiveDropoffThreshold;
        var progress = penaltyRounds - FugitiveDropoffThreshold;
        return BaseFugitiveChance * (1f - (float)progress / range);
    }

    /// <summary>
    /// Security-issued penalties only count toward force-spawn balance after custody confirmation.
    /// Admin/system penalties count immediately. Served records never count.
    /// </summary>
    public static bool CountsTowardForceSpawnBalance(bool pendingCustody, bool isServed)
        => !pendingCustody && !isServed;

    /// <summary>
    /// At round end, a pending security sentence sticks only if the target is in design custody.
    /// </summary>
    public static bool ShouldConfirmPendingAtRoundEnd(bool pendingCustody, bool endedInDesignCustody)
        => pendingCustody && endedInDesignCustody;

    /// <summary>
    /// Design custody: permabrig (near prisoner/solitary spawns) or prisoner transport on the
    /// emergency shuttle (on shuttle while restrained).
    /// </summary>
    public static bool IsInDesignCustody(bool nearPrisonerOrSolitarySpawn, bool onEmergencyShuttleWhileCuffed)
        => nearPrisonerOrSolitarySpawn || onEmergencyShuttleWhileCuffed;

    /// <summary>
    /// Whether a death with active outstanding penalties should auto-apply an execution +1.
    /// Outside perma: always (prevents suicide-bombing out of a sentence). Inside perma:
    /// environmental/NPC/accident does not count. Antags never accumulate.
    /// </summary>
    public static bool ShouldApplyDeathExecutionPenalty(
        bool hasOutstandingPenalties,
        bool isAntagonist,
        bool alreadyCountedThisRound,
        bool diedInsidePermabrig)
    {
        if (!hasOutstandingPenalties || isAntagonist || alreadyCountedThisRound)
            return false;

        // Inside perma: no automatic execution penalty (env/NPC/accident).
        if (diedInsidePermabrig)
            return false;

        return true;
    }

    /// <summary>
    /// Serve credit requires alive + connected + at least one third of the round duration.
    /// </summary>
    public static bool EarnsServeCredit(bool isAlive, bool isConnected, TimeSpan timeServed, TimeSpan roundDuration)
    {
        if (!isAlive || !isConnected)
            return false;

        if (roundDuration <= TimeSpan.Zero)
            return false;

        return timeServed >= roundDuration / 3;
    }

    /// <summary>
    /// Security may undo only non-admin penalties issued in the current round.
    /// </summary>
    public static bool CanSecurityUndo(bool adminIssued, int issuedRoundId, int currentRoundId, out string denialReason)
    {
        if (adminIssued)
        {
            denialReason = "Penalty was issued by an administrator and only an administrator can remove it.";
            return false;
        }

        if (issuedRoundId != currentRoundId)
        {
            denialReason =
                "Penalty was issued in an earlier round. Only an administrator can adjust " +
                "a previously accumulated balance.";
            return false;
        }

        denialReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Fugitive end-round outcome once death has been handled separately (no double-count).
    /// </summary>
    public enum FugitiveRoundOutcome
    {
        DeadAlreadyHandled,
        CustodyPlusOne,
        FreeMinusOne,
    }

    public static FugitiveRoundOutcome EvaluateFugitiveOutcome(bool isDead, bool inCustodyCuffed)
    {
        if (isDead)
            return FugitiveRoundOutcome.DeadAlreadyHandled;

        if (inCustodyCuffed)
            return FugitiveRoundOutcome.CustodyPlusOne;

        return FugitiveRoundOutcome.FreeMinusOne;
    }

    public static bool WantsSolitaryPlacement(int outstandingPenaltyRounds)
        => outstandingPenaltyRounds > SolitaryThreshold;

    /// <summary>
    /// Antags must not accumulate penalties, but sec must not be told mid-round.
    /// When true, the issue path should report success without writing a record.
    /// </summary>
    public static bool ShouldSilentlySkipAntagPenalty(bool targetIsAntagonist)
        => targetIsAntagonist;

    /// <summary>
    /// Officer-facing success line for secpenalty / console feedback. Never names antag status.
    /// </summary>
    public static string FormatSecPenaltyAppliedMessage(int roundsApplied, string targetName, int pendingPlusOutstandingTotal)
        => $"Applied {roundsApplied} penalty round(s) to {targetName}. Total: {pendingPlusOutstandingTotal}.";
}
