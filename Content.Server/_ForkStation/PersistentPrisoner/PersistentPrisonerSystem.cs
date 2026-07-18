using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Core system for Persistent Prisoners. Manages penalty rounds, forces prisoner spawns,
/// and tracks round service. Security officers can issue penalty rounds; admins can wipe them.
/// </summary>
public sealed class PersistentPrisonerSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _res = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;

    private bool _enabled;

    private static readonly ISawmill Log = Logger.GetSawmill("persistent.prisoner");

    private PenaltyDataStore _store = default!;

    /// <summary>
    /// Max penalty rounds a player can accumulate (cap at 20).
    /// </summary>
    public const int MaxPenaltyRounds = 20;

    /// <summary>
    /// Max penalties that can be applied in a single round.
    /// </summary>
    public const int MaxPenaltiesPerRound = 5;

    /// <summary>
    /// Penalty count threshold above which the prisoner is marked "extremely dangerous"
    /// and spawns in solitary confinement.
    /// </summary>
    public const int SolitaryThreshold = 15;

    /// <summary>
    /// The Prisoner job prototype ID.
    /// </summary>
    private const string PrisonerJobId = "Prisoner";

    /// <summary>
    /// Track players who spawned as prisoners this round, for round-end penalty serving.
    /// Maps UserId string -> join time.
    /// </summary>
    private readonly Dictionary<string, TimeSpan> _prisonersThisRound = new();

    /// <summary>
    /// Track how many penalties have been applied per player this round.
    /// </summary>
    private readonly Dictionary<string, int> _penaltiesAppliedThisRound = new();

    private int _currentRoundId;

    public override void Initialize()
    {
        base.Initialize();

        var dataDir = _res.UserData.RootDir ?? ".";
        _store = new PenaltyDataStore(dataDir);

        Subs.CVar(_cfg, CCVars.PersistentPrisonerEnabled, val => _enabled = val, true);

        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBeforeSpawn);
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEnded);

        Log.Info("Persistent Prisoner system initialized.");
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _currentRoundId = ev.Id;
        _prisonersThisRound.Clear();
        _penaltiesAppliedThisRound.Clear();
    }

    /// <summary>
    /// Intercept player spawning. If they have active penalties, force them to spawn as a prisoner.
    /// </summary>
    private void OnBeforeSpawn(PlayerBeforeSpawnEvent ev)
    {
        if (ev.Handled || !_enabled)
            return;

        var userId = ev.Player.UserId.ToString();
        var totalPenalties = _store.GetTotalPenaltyRounds(userId);

        if (totalPenalties <= 0)
            return;

        // Player has active penalties — they must spawn as a prisoner.
        // We don't handle the spawn ourselves; we just override the job.
        // The GameTicker will use the Prisoner job's spawn point.
        // We track them for round-end penalty serving.
        _prisonersThisRound[userId] = _timing.CurTime;

        Log.Info($"Player {ev.Player.Name} ({userId}) has {totalPenalties} penalty rounds. Forcing prisoner spawn.");

        // We don't set ev.Handled = true because we want the normal spawn flow,
        // just with a different job. Instead, we hook GetDisallowedJobsEvent.
    }

    /// <summary>
    /// At round end, credit served rounds to prisoners who were alive long enough.
    /// A penalty round is considered served if the player was connected for at least 1/3 of the round.
    /// </summary>
    private void OnRoundEnded(RoundEndTextAppendEvent ev)
    {
        var roundDuration = _gameTicker.RoundDuration();
        var minimumTime = roundDuration / 3;

        foreach (var (userId, joinTime) in _prisonersThisRound)
        {
            var timeServed = _timing.CurTime - joinTime;

            if (timeServed >= minimumTime)
            {
                _store.ServeRound(userId);
                var remaining = _store.GetTotalPenaltyRounds(userId);
                Log.Info($"Player {userId} served a penalty round. {remaining} remaining.");
            }
            else
            {
                Log.Info($"Player {userId} didn't serve enough time ({timeServed} < {minimumTime}). Penalty not counted.");
            }
        }
    }

    // === Public API for commands and UI ===

    /// <summary>
    /// Add penalty rounds to a player.
    /// </summary>
    public PenaltyRecord? AddPenalty(string playerUserId, string issuedBy, string issuedByName,
        int rounds, string reason, bool adminIssued)
    {
        // Check per-round cap (unless admin)
        if (!adminIssued)
        {
            _penaltiesAppliedThisRound.TryGetValue(playerUserId, out var appliedThisRound);
            if (appliedThisRound >= MaxPenaltiesPerRound)
            {
                Log.Warning($"Cannot add more penalties to {playerUserId} this round (cap: {MaxPenaltiesPerRound}).");
                return null;
            }
        }

        // Check total cap
        var currentTotal = _store.GetTotalPenaltyRounds(playerUserId);
        var effectiveRounds = Math.Min(rounds, MaxPenaltyRounds - currentTotal);

        if (effectiveRounds <= 0)
        {
            Log.Warning($"Player {playerUserId} already at max penalties ({MaxPenaltyRounds}).");
            return null;
        }

        var record = _store.AddPenalty(playerUserId, issuedBy, issuedByName,
            effectiveRounds, reason, _currentRoundId, adminIssued);

        // Track per-round count
        _penaltiesAppliedThisRound.TryGetValue(playerUserId, out var current);
        _penaltiesAppliedThisRound[playerUserId] = current + 1;

        Log.Info($"Penalty added: {effectiveRounds} rounds to {playerUserId} by {issuedByName}. Reason: {reason}");
        return record;
    }

    /// <summary>
    /// Get total outstanding penalty rounds for a player.
    /// </summary>
    public int GetPenaltyRounds(string playerUserId)
    {
        return _store.GetTotalPenaltyRounds(playerUserId);
    }

    /// <summary>
    /// Get all active penalties for a player.
    /// </summary>
    public List<PenaltyRecord> GetActivePenalties(string playerUserId)
    {
        return _store.GetActivePenalties(playerUserId);
    }

    /// <summary>
    /// Get all penalties (including served) for a player.
    /// </summary>
    public List<PenaltyRecord> GetAllPenalties(string playerUserId)
    {
        return _store.GetAllPenalties(playerUserId);
    }

    /// <summary>
    /// Serve a decay round on a specific penalty by ID.
    /// Used by the auto-decay system.
    /// </summary>
    public bool ServeDecayRound(int penaltyId)
    {
        return _store.ServeRoundOnPenalty(penaltyId);
    }

    /// <summary>
    /// Serve a round via good behavior credit. Applies to oldest unserved penalty.
    /// </summary>
    public bool ServeGoodBehaviorRound(string playerUserId)
    {
        return _store.ServeRound(playerUserId);
    }

    /// <summary>
    /// Remove a specific penalty by ID. Returns true if successful.
    /// </summary>
    public bool RemovePenalty(int penaltyId)
    {
        return _store.RemovePenalty(penaltyId);
    }

    /// <summary>
    /// Wipe all penalties for a player. Returns the number removed.
    /// </summary>
    public int WipePenalties(string playerUserId)
    {
        return _store.WipePenalties(playerUserId);
    }

    /// <summary>
    /// Get summary of all players with active penalties.
    /// </summary>
    public Dictionary<string, int> GetPenaltySummary()
    {
        return _store.GetAllActivePenaltySummary();
    }

    /// <summary>
    /// Check if a player should spawn as a prisoner.
    /// </summary>
    public bool ShouldSpawnAsPrisoner(string playerUserId)
    {
        return _store.GetTotalPenaltyRounds(playerUserId) > 0;
    }
}
