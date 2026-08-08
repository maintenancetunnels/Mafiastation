using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.CCVar;
using Content.Shared.Cuffs.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Roles;
using Robust.Shared.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Core system for Persistent Prisoners. Manages penalty rounds, forces prisoner spawns,
/// and tracks round service. Security officers can issue penalty rounds; admins can wipe them.
/// Security-issued sentences only become outstanding force-spawn balance after end-round
/// design custody (permabrig or cuffed on the emergency shuttle).
/// </summary>
public sealed class PersistentPrisonerSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _res = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StationSpawningSystem _spawning = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly ISharedPlayerManager _players = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EmergencyShuttleSystem _emergency = default!;

    private bool _enabled;

    private PenaltyDataStore _store = default!;

    // Back-compat aliases used by older call sites / tests.
    public const int MaxPenaltyRounds = PrisonerDesignRules.MaxPenaltyRounds;
    public const int MaxPenaltiesPerRound = PrisonerDesignRules.MaxPenaltiesPerRound;
    public const int MaxExecutionPenalties = PrisonerDesignRules.MaxExecutionPenalties;
    public const int SolitaryThreshold = PrisonerDesignRules.SolitaryThreshold;
    public const int AntagZeroThreshold = PrisonerDesignRules.AntagZeroThreshold;
    public const float BasePrisonerAntagChance = PrisonerDesignRules.BasePrisonerAntagChance;
    public const float SolitaryPrisonerAntagChance = PrisonerDesignRules.SolitaryPrisonerAntagChance;

    private const string PrisonerJobId = "Prisoner";

    /// <summary>Tiles from a prisoner/solitary spawn that count as permabrig custody.</summary>
    public const float PermabrigCustodyRange = 8f;

    public static int ClampPenaltyRounds(
        int requested,
        int alreadyAppliedThisRound,
        int currentTotal,
        bool isExecution,
        bool adminIssued)
        => PrisonerDesignRules.ClampPenaltyRounds(
            requested, alreadyAppliedThisRound, currentTotal, isExecution, adminIssued);

    public static float GetPrisonerAntagChance(int penaltyRounds)
        => PrisonerDesignRules.GetPrisonerAntagChance(penaltyRounds);

    private readonly Dictionary<string, TimeSpan> _prisonersThisRound = new();
    private readonly Dictionary<string, int> _penaltiesAppliedThisRound = new();

    private int _currentRoundId;

    public override void Initialize()
    {
        base.Initialize();

        var dataDir = _res.UserData.RootDir ?? ".";
        _store = new PenaltyDataStore(dataDir);

        Subs.CVar(_cfg, CCVars.PersistentPrisonerEnabled, val => _enabled = val, true);

        // Ordered AFTER the fugitive roll: a fugitive handles its own spawn and marks the event
        // handled, and must not also be counted as a serving prisoner (that double-credited them).
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBeforeSpawn, after: [typeof(FugitiveSpawnSystem)]);
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
    /// Intercept player spawning. A player holding outstanding balance spawns as a prisoner in the
    /// permabrig — or in solitary confinement once past <see cref="SolitaryThreshold"/>.
    /// </summary>
    private void OnBeforeSpawn(PlayerBeforeSpawnEvent ev)
    {
        if (ev.Handled || !_enabled)
            return;

        var userId = ev.Player.UserId.ToString();
        var totalPenalties = _store.GetTotalPenaltyRounds(userId);

        if (totalPenalties <= 0)
            return;

        var wantSolitary = PrisonerDesignRules.WantsSolitaryPlacement(totalPenalties);

        if (!TryFindPrisonerSpawn(ev.Station, wantSolitary, out var spawnLoc))
        {
            // No cell to put them in. Let them spawn normally, but deliberately do NOT record
            // them as serving: crediting a round here is what previously let penalties expire
            // just by playing a normal shift.
            Log.Error(
                $"Player {ev.Player.Name} ({userId}) holds {totalPenalties} penalty rounds but this " +
                "station has no prisoner spawn point; spawning normally and NOT crediting a served round.");
            return;
        }

        var mob = _spawning.SpawnPlayerMob(spawnLoc, PrisonerJobId, ev.Profile, ev.Station);

        var tracking = EnsureComp<PrisonerTrackingComponent>(mob);
        tracking.PlayerUserId = userId;
        tracking.IsFugitive = false;
        tracking.JoinTime = _timing.CurTime;
        tracking.PenaltyRoundsAtSpawn = totalPenalties;

        // Job specials usually add GoodBehavior; ensure it for labor credit either way.
        EnsureComp<GoodBehaviorComponent>(mob);

        var mind = _mind.CreateMind(ev.Player.UserId, ev.Profile.Name);
        _mind.TransferTo(mind, mob);

        _prisonersThisRound[userId] = _timing.CurTime;
        ev.Handled = true;

        Log.Info(
            $"Player {ev.Player.Name} ({userId}) spawned as a prisoner with {totalPenalties} penalty " +
            $"rounds ({(wantSolitary ? "solitary confinement" : "permabrig")}) at {spawnLoc}.");
    }

    private bool TryFindPrisonerSpawn(EntityUid station, bool wantSolitary, out EntityCoordinates spawnLoc)
    {
        spawnLoc = default;

        if (wantSolitary && TryCollectSolitarySpawns(station, out var solitary))
        {
            spawnLoc = _random.Pick(solitary);
            return true;
        }

        var general = new List<EntityCoordinates>();
        var points = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (points.MoveNext(out var uid, out var point, out var xform))
        {
            if (point.Job != PrisonerJobId)
                continue;

            if (station != default && _station.GetOwningStation(uid, xform) != station)
                continue;

            general.Add(xform.Coordinates);
        }

        if (general.Count == 0)
            return false;

        spawnLoc = _random.Pick(general);
        return true;
    }

    private bool TryCollectSolitarySpawns(EntityUid station, out List<EntityCoordinates> found)
    {
        found = new List<EntityCoordinates>();
        var points = EntityQueryEnumerator<SolitaryConfinementSpawnPointComponent, TransformComponent>();
        while (points.MoveNext(out var uid, out _, out var xform))
        {
            if (station != default && _station.GetOwningStation(uid, xform) != station)
                continue;

            found.Add(xform.Coordinates);
        }

        return found.Count > 0;
    }

    /// <summary>
    /// True when <paramref name="uid"/> is inside design custody: within range of a prisoner or
    /// solitary spawn (permabrig), or cuffed on the emergency shuttle (prisoner transport).
    /// </summary>
    public bool IsEntityInDesignCustody(EntityUid uid)
    {
        if (!TryComp<TransformComponent>(uid, out var xform))
            return false;

        var nearBrig = IsNearPrisonerCustodyMarker(uid, xform);
        var onShuttleCuffed = IsCuffedOnEmergencyShuttle(uid, xform);
        return PrisonerDesignRules.IsInDesignCustody(nearBrig, onShuttleCuffed);
    }

    private bool IsNearPrisonerCustodyMarker(EntityUid uid, TransformComponent xform)
    {
        var origin = _transform.GetMapCoordinates(uid, xform);
        if (origin.MapId == MapId.Nullspace)
            return false;

        var rangeSq = PermabrigCustodyRange * PermabrigCustodyRange;

        var points = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (points.MoveNext(out var marker, out var point, out var markerXform))
        {
            if (point.Job != PrisonerJobId)
                continue;

            if (markerXform.MapID != origin.MapId)
                continue;

            var markerPos = _transform.GetMapCoordinates(marker, markerXform);
            if ((markerPos.Position - origin.Position).LengthSquared() <= rangeSq)
                return true;
        }

        var solitary = EntityQueryEnumerator<SolitaryConfinementSpawnPointComponent, TransformComponent>();
        while (solitary.MoveNext(out var marker, out _, out var markerXform))
        {
            if (markerXform.MapID != origin.MapId)
                continue;

            var markerPos = _transform.GetMapCoordinates(marker, markerXform);
            if ((markerPos.Position - origin.Position).LengthSquared() <= rangeSq)
                return true;
        }

        return false;
    }

    private bool IsCuffedOnEmergencyShuttle(EntityUid uid, TransformComponent xform)
    {
        if (xform.GridUid is not { } grid || !HasComp<EmergencyShuttleComponent>(grid))
            return false;

        // Prisoner section approximation: restrained while on the evacuating shuttle.
        if (!TryComp<CuffableComponent>(uid, out var cuffs) || cuffs.CuffedHandCount <= 0)
            return false;

        // Prefer the engine's escaping check when evac has arrived; fall back to grid membership.
        return _emergency.IsTargetEscaping(uid) || HasComp<EmergencyShuttleComponent>(grid);
    }

    private void OnRoundEnded(RoundEndTextAppendEvent ev)
    {
        ConfirmPendingSentencesFromCustody();
        DiscardUnconfirmedSentences();
        CreditServedRounds();
    }

    /// <summary>
    /// Security-issued pending sentences stick only if the target ends the round in design custody.
    /// </summary>
    private void ConfirmPendingSentencesFromCustody()
    {
        // Walk every attached session entity and every tracked prisoner body.
        var considered = new HashSet<string>();

        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } entity)
                continue;

            var userId = session.UserId.ToString();
            if (!considered.Add(userId))
                continue;

            TryConfirmPendingForPlayer(userId, entity);
        }

        var query = EntityQueryEnumerator<PrisonerTrackingComponent>();
        while (query.MoveNext(out var uid, out var tracking))
        {
            if (string.IsNullOrEmpty(tracking.PlayerUserId) || !considered.Add(tracking.PlayerUserId))
                continue;

            TryConfirmPendingForPlayer(tracking.PlayerUserId, uid);
        }
    }

    private void TryConfirmPendingForPlayer(string userId, EntityUid entity)
    {
        var pending = _store.GetPendingPenalties(userId);
        if (pending.Count == 0)
            return;

        var inCustody = IsEntityInDesignCustody(entity);
        if (!PrisonerDesignRules.ShouldConfirmPendingAtRoundEnd(true, inCustody))
        {
            Log.Info(
                $"Player {userId} has {pending.Count} pending security sentence(s) but was not in " +
                "design custody at round end — sentences will not stick.");
            return;
        }

        foreach (var record in pending)
        {
            if (_store.ConfirmPending(record.Id))
            {
                Log.Info(
                    $"Confirmed pending penalty #{record.Id} ({record.RoundsAssigned} rounds) for " +
                    $"{userId} — ended round in design custody.");
            }
        }
    }

    private void DiscardUnconfirmedSentences()
    {
        var discarded = _store.DiscardAllRemainingPending();
        if (discarded > 0)
            Log.Info($"Discarded {discarded} unconfirmed security sentence(s) (escape / no custody).");
    }

    private void CreditServedRounds()
    {
        var roundDuration = _gameTicker.RoundDuration();
        var credited = new HashSet<string>();

        var query = EntityQueryEnumerator<PrisonerTrackingComponent, MobStateComponent>();
        while (query.MoveNext(out var uid, out var tracking, out var mobState))
        {
            // Fugitive outcomes are settled by their own -1/+1 rules, not by serving a round.
            if (tracking.IsFugitive)
                continue;

            var userId = tracking.PlayerUserId;
            if (string.IsNullOrEmpty(userId) || !_prisonersThisRound.ContainsKey(userId))
                continue;

            if (!credited.Add(userId))
                continue;

            var connected = _players.TryGetSessionByEntity(uid, out _);
            var alive = mobState.CurrentState == MobState.Alive;
            var timeServed = _timing.CurTime - tracking.JoinTime;

            if (!PrisonerDesignRules.EarnsServeCredit(alive, connected, timeServed, roundDuration))
            {
                if (!connected)
                    Log.Info($"Prisoner {userId} was not connected at round end. Penalty round NOT served.");
                else if (!alive)
                    Log.Info($"Prisoner {userId} ended the round {mobState.CurrentState}. Penalty round NOT served.");
                else
                    Log.Info($"Prisoner {userId} served {timeServed} of required {roundDuration / 3}. Penalty round NOT served.");
                continue;
            }

            _store.ServeRound(userId);
            Log.Info($"Prisoner {userId} served a penalty round. {_store.GetTotalPenaltyRounds(userId)} remaining.");
        }
    }

    // === Public API for commands and UI ===

    /// <summary>
    /// Add penalty rounds to a player. Ordinary security issues are pending custody confirmation.
    /// Admin-issued, system, and execution-class grants become outstanding balance immediately.
    /// </summary>
    public PenaltyRecord? AddPenalty(string playerUserId, string issuedBy, string issuedByName,
        int rounds, string reason, bool adminIssued, bool isExecution = false, bool pendingCustody = false)
    {
        // Ordinary sec sentences stick only after design custody at round end.
        // Admin, system, and execution-class (including secpenaltydead) apply immediately.
        pendingCustody = !adminIssued && !isExecution;

        _penaltiesAppliedThisRound.TryGetValue(playerUserId, out var appliedThisRound);
        // Lifetime cap must count pending + confirmed so confirm-after-custody cannot push past 20.
        // Escape still frees the slot when pending is discarded. Per-round budget uses applied-this-round.
        var currentTotal = _store.GetPendingPlusOutstandingRounds(playerUserId);

        var effectiveRounds = ClampPenaltyRounds(
            rounds,
            appliedThisRound,
            currentTotal,
            isExecution,
            adminIssued);

        if (effectiveRounds <= 0)
        {
            Log.Warning(
                $"Refused penalty for {playerUserId}: requested {rounds}, already applied " +
                $"{appliedThisRound} this round, lifetime total {currentTotal}/{MaxPenaltyRounds}.");
            return null;
        }

        var record = _store.AddPenalty(playerUserId, issuedBy, issuedByName,
            effectiveRounds, reason, _currentRoundId, adminIssued, pendingCustody);

        _penaltiesAppliedThisRound[playerUserId] = appliedThisRound + effectiveRounds;

        Log.Info(
            $"Penalty added: {effectiveRounds} rounds to {playerUserId} by {issuedByName} " +
            $"(pendingCustody={pendingCustody}). Reason: {reason}");
        return record;
    }

    /// <summary>Confirmed outstanding force-spawn balance only.</summary>
    public int GetPenaltyRounds(string playerUserId)
        => _store.GetTotalPenaltyRounds(playerUserId);

    /// <summary>
    /// Confirmed outstanding plus still-pending security sentences. Shown to officers mid-round
    /// so an unconfirmed issue does not look like Total: 0.
    /// </summary>
    public int GetPendingPlusOutstandingRounds(string playerUserId)
        => _store.GetPendingPlusOutstandingRounds(playerUserId);

    public List<PenaltyRecord> GetActivePenalties(string playerUserId)
        => _store.GetActivePenalties(playerUserId);

    public List<PenaltyRecord> GetAllPenalties(string playerUserId)
        => _store.GetAllPenalties(playerUserId);

    public bool ServeDecayRound(int penaltyId)
        => _store.ServeRoundOnPenalty(penaltyId);

    public bool ServeGoodBehaviorRound(string playerUserId)
        => _store.ServeRound(playerUserId);

    public int CurrentRoundId => _currentRoundId;

    public PenaltyRecord? GetPenaltyById(int penaltyId) => _store.GetPenaltyById(penaltyId);

    public bool CanSecurityUndo(int penaltyId, out string denialReason)
    {
        denialReason = string.Empty;

        var record = _store.GetPenaltyById(penaltyId);
        if (record == null)
        {
            denialReason = $"Penalty #{penaltyId} does not exist.";
            return false;
        }

        if (!PrisonerDesignRules.CanSecurityUndo(record.AdminIssued, record.IssuedRoundId, _currentRoundId, out denialReason))
        {
            if (!string.IsNullOrEmpty(denialReason) && !denialReason.Contains('#'))
                denialReason = $"Penalty #{penaltyId}: {denialReason}";
            return false;
        }

        return true;
    }

    public bool RemovePenalty(int penaltyId)
        => _store.RemovePenalty(penaltyId);

    public int WipePenalties(string playerUserId)
        => _store.WipePenalties(playerUserId);

    public Dictionary<string, int> GetPenaltySummary()
        => _store.GetAllActivePenaltySummary();

    public bool ShouldSpawnAsPrisoner(string playerUserId)
        => _store.GetTotalPenaltyRounds(playerUserId) > 0;

    /// <summary>
    /// True when the entity is inside permabrig custody range (for death classification).
    /// </summary>
    public bool IsEntityInPermabrig(EntityUid uid)
    {
        if (!TryComp<TransformComponent>(uid, out var xform))
            return false;

        return IsNearPrisonerCustodyMarker(uid, xform);
    }
}
