using Content.Server.GameTicking.Events;
using Content.Server.Mind;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Handles fugitive spawn logic. Persistent prisoners have a chance to spawn as a fugitive
/// instead of in the permabrig. Fugitives spawn at late-join points (arrivals) with nothing
/// but an orange jumpsuit and a mechanical toolbox: no backpack, no ID.
///
/// Probability: base 18%, drops above 5 penalty rounds, zero at 20.
/// Round-end outcomes (design doc): escape alive and free = -1 penalty; caught (cuffed) = +1.
/// Death is handled live by <see cref="PrisonerDeathTrackingSystem"/> (execution rule);
/// outcome evaluation lives in <see cref="PrisonerBreakoutSystem"/>.
/// </summary>
public sealed class FugitiveSpawnSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly StationSpawningSystem _spawning = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly ISawmill Log = Logger.GetSawmill("persistent.prisoner.fugitive");

    private const float BaseFugitiveChance = 0.18f;
    private const int FugitiveDropoffThreshold = 5;
    private const int FugitiveZeroThreshold = 20;

    private const string FugitiveJumpsuit = "ClothingUniformJumpsuitPrisoner";
    private const string FugitiveToolbox = "ToolboxMechanicalFilled";

    private readonly HashSet<string> _fugitivesThisRound = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBeforeSpawn);
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _fugitivesThisRound.Clear();
    }

    private void OnBeforeSpawn(PlayerBeforeSpawnEvent ev)
    {
        if (ev.Handled)
            return;

        var userId = ev.Player.UserId.ToString();
        var penaltyRounds = _penalties.GetPenaltyRounds(userId);

        if (penaltyRounds <= 0)
            return;

        var chance = GetFugitiveChance(penaltyRounds);
        if (chance <= 0f || !_random.Prob(chance))
            return;

        // Spawn at a late-join (arrivals) point instead of the prisoner spawn point.
        var lateJoinPoints = new List<EntityCoordinates>();
        var points = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();

        while (points.MoveNext(out var uid, out var sp, out var xform))
        {
            if (sp.SpawnType != SpawnPointType.LateJoin)
                continue;

            if (ev.Station != default && _station.GetOwningStation(uid, xform) != ev.Station)
                continue;

            lateJoinPoints.Add(xform.Coordinates);
        }

        if (lateJoinPoints.Count == 0)
        {
            Log.Warning("No late-join spawn points found for fugitive. Falling back to normal prisoner spawn.");
            return;
        }

        _fugitivesThisRound.Add(userId);
        Log.Info($"Player {ev.Player.Name} ({userId}) rolled fugitive! ({chance:P0} chance, {penaltyRounds} penalties)");

        var spawnLoc = _random.Pick(lateJoinPoints);

        // Spawn the mob with no job: no gear, no ID, no backpack. This is intentional.
        var mob = _spawning.SpawnPlayerMob(spawnLoc, null, ev.Profile, ev.Station);

        // "Nothing but an orange jumpsuit and a mechanical toolbox."
        var jumpsuit = Spawn(FugitiveJumpsuit, spawnLoc);
        if (!_inventory.TryEquip(mob, jumpsuit, "jumpsuit", silent: true, force: true))
            QueueDel(jumpsuit);

        var toolbox = Spawn(FugitiveToolbox, spawnLoc);
        if (!_hands.TryPickupAnyHand(mob, toolbox))
            _transform.SetCoordinates(toolbox, spawnLoc);

        var tracking = EnsureComp<PrisonerTrackingComponent>(mob);
        tracking.PlayerUserId = userId;
        tracking.IsFugitive = true;
        tracking.JoinTime = _timing.CurTime;
        tracking.PenaltyRoundsAtSpawn = penaltyRounds;

        var mind = _mind.CreateMind(ev.Player.UserId, ev.Profile.Name);
        _mind.TransferTo(mind, mob);

        ev.Handled = true;
        Log.Info($"Fugitive {ev.Player.Name} spawned at arrivals ({spawnLoc}).");
    }

    public bool IsFugitive(string userId) => _fugitivesThisRound.Contains(userId);

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
}
