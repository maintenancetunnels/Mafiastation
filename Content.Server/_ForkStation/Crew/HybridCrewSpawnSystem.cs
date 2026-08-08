using System.Numerics;
using Content.Server._ForkStation.LlmDirector;
using Content.Server.Chat.Systems;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Station.Components;
using Content.Shared.Warps;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.Crew;

/// <summary>
/// World-population for the simulated station: spawns a configurable squad of Codex's
/// server-owned hybrid LLM NPCs (<c>MobMafiaHybridPrisoner</c>, carrying LlmNpcHybrid with its
/// intrinsic goal/capability profile) onto the station when the station is initialized. Routine
/// locomotion is ordinary HTN; the bounded LLM director (mafia.director.npc_hybrid_enabled)
/// drives goal and dialogue escalation. This system only places the bodies — it never touches
/// the AI wiring. Placement uses the station's largest grid tiles (the same proven technique as
/// BasementSystem), which is robust across maps where spawn-point tagging varies.
/// </summary>
public sealed class HybridCrewSpawnSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _protos = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly LlmNpcDialogueSystem _dialogue = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    // Warp-point entities are not available at station post-init, so the spawn is deferred.
    private const float SpawnDelaySeconds = 5f;
    private EntityUid? _pendingStation;
    private TimeSpan? _spawnAt;

    // Self-test probe (mafia.crew.radio_probe): fires once, this long after the crew spawn.
    private const float ProbeDelaySeconds = 25f;
    private const string ProbeMessage = ";Captain here. Who is on this channel? Identify yourself.";
    private TimeSpan? _probeAt;

    // Distinct name + persona per NPC. The name becomes the entity's real in-game name (shown on
    // radio/chat) AND feeds the model's self-context, so each crew member is a consistent
    // individual: no shared "experimental prisoner" tag, and no confusion over who the captain is
    // (each persona is explicitly ordinary crew, never a head of staff).
    private static readonly (string Name, string Persona)[] Crew =
    {
        ("Dell Marrow", "You are Dell Marrow, a tired station engineer. You are ordinary crew, NOT the captain or any head of staff. Dry, practical, a bit fatalistic; you answer plainly and don't spook easy."),
        ("Junie Vasquez", "You are Junie Vasquez, a jumpy junior medic. Ordinary crew, NOT the captain. Talkative and anxious; you crack nervous jokes when scared and notice every little thing that's wrong."),
        ("Sable Khole", "You are Sable Khole, an ex-security officer, gruff and suspicious. Ordinary crew, NOT the captain. Short, clipped answers; you trust no one on comms and you say so."),
        ("Pim Oduya", "You are Pim Oduya, a bored cargo tech glad anyone's finally talking. Ordinary crew, NOT the captain. Friendly and rambling, always floating some theory about what's going on."),
        ("Ansel Ruiz", "You are Ansel Ruiz, a calm, clinical doctor. Ordinary crew, NOT the captain. You observe more than you speak; when you do it is measured and a little unsettling."),
        ("Birch Colley", "You are Birch Colley, a superstitious maintenance worker sure the station is haunted. Ordinary crew, NOT the captain. You keep bringing up the cold, the noises, the flickering lights."),
    };


    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);
    }

    /// <summary>
    /// Self-test only (mafia.crew.radio_probe). Has one crew member key up Common once so the
    /// whole reply chain can be verified from the log with no client attached.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Deferred crew spawn: waits until the map's warp points exist so they can be placed
        // somewhere sensible (the bar) rather than a random, possibly airless, tile.
        if (_spawnAt is { } spawnDue && _timing.CurTime >= spawnDue)
        {
            _spawnAt = null;
            if (_pendingStation is { } station && !TerminatingOrDeleted(station))
                SpawnCrew(station);
            _pendingStation = null;
        }

        if (_probeAt is not { } due || _timing.CurTime < due)
            return;
        _probeAt = null; // one shot

        var query = EntityQueryEnumerator<LlmNpcHybridComponent>();
        if (!query.MoveNext(out var speaker, out _))
        {
            Log.Warning("[crewprobe] no hybrid crew to speak; probe skipped.");
            return;
        }

        Log.Info($"[crewprobe] {ToPrettyString(speaker)} keying Common: \"{ProbeMessage}\"");
        _chat.TrySendInGameICMessage(
            speaker,
            ProbeMessage,
            InGameICChatType.Speak,
            hideChat: false,
            hideLog: false,
            checkRadioPrefix: true);
    }

    /// <summary>
    /// Station post-init happens before the map's warp-point entities are available, so the
    /// actual spawn is deferred a few seconds. Spawning here instead placed the crew on random
    /// floor tiles — sometimes depressurized ones, where they suffocate.
    /// </summary>
    private void OnStationPostInit(ref StationPostInitEvent ev)
    {
        if (_cfg.GetCVar(CCVars.MafiaCrewHybridCount) <= 0)
            return;

        _pendingStation = ev.Station.Owner;
        _spawnAt = _timing.CurTime + TimeSpan.FromSeconds(SpawnDelaySeconds);
    }

    /// <summary>
    /// The station's bar, by warp point — a guaranteed-pressurized, central, social spot. Falls
    /// back to any named room on the station, since even that beats a random floor tile (which can
    /// be an airless hull edge where an occupant suffocates). Null if the station has no warp
    /// points at all, which is also the case before they are map-initialized.
    /// </summary>
    private EntityCoordinates? TryFindBar(EntityUid stationUid)
    {
        EntityCoordinates? anyWarp = null;
        var warps = EntityQueryEnumerator<WarpPointComponent, TransformComponent>();
        while (warps.MoveNext(out _, out var warp, out var warpXform))
        {
            // Only warp points on this station's grids — not centcomm or other maps.
            if (warpXform.GridUid is not { } warpGrid ||
                CompOrNull<StationMemberComponent>(warpGrid)?.Station != stationUid)
            {
                continue;
            }

            anyWarp ??= warpXform.Coordinates;
            if (warp.Location != null &&
                warp.Location.Contains("bar", System.StringComparison.OrdinalIgnoreCase))
            {
                return warpXform.Coordinates;
            }
        }

        return anyWarp;
    }

    /// <summary>
    /// Puts the joining player in the bar with the crew (mafia.crew.player_bar_spawn), so the
    /// scene starts with everyone in one room instead of the player hunting the station for them.
    /// </summary>
    private void OnPlayerSpawned(PlayerSpawnCompleteEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.MafiaCrewPlayerBarSpawn))
            return;

        if (TryFindBar(ev.Station) is not { } bar)
        {
            Log.Warning("[barscene] player bar spawn requested but no bar/warp point found.");
            return;
        }

        // Offset off the crew cluster so nobody spawns inside anyone.
        var spot = bar.Offset(new Vector2(_random.NextFloat(-1.5f, 1.5f), _random.NextFloat(0.8f, 2f)));
        _transform.SetCoordinates(ev.Mob, spot);
        Log.Info($"[barscene] placed {ToPrettyString(ev.Mob)} at the bar {spot}.");
    }

    private void SpawnCrew(EntityUid stationUid)
    {
        var count = _cfg.GetCVar(CCVars.MafiaCrewHybridCount);
        if (count <= 0)
            return;

        var prototype = _cfg.GetCVar(CCVars.MafiaCrewHybridPrototype);
        if (!_protos.HasIndex<EntityPrototype>(prototype))
        {
            Log.Error($"Hybrid crew prototype '{prototype}' does not exist; spawning nothing.");
            return;
        }

        Entity<StationDataComponent?> stationEnt = (stationUid, CompOrNull<StationDataComponent>(stationUid));
        if (_station.GetLargestGrid(stationEnt) is not { } grid
            || !TryComp<MapGridComponent>(grid, out var gridComp))
        {
            Log.Warning("Station has no usable grid; hybrid crew not spawned.");
            return;
        }

        // Ground tiles of the main station floor (skips space), same approach as the basement.
        var tiles = new List<TileRef>();
        foreach (var tile in _map.GetAllTiles(grid, gridComp))
            tiles.Add(tile);

        if (tiles.Count == 0)
        {
            Log.Warning("Station grid has no tiles; hybrid crew not spawned.");
            return;
        }

        var barCoords = TryFindBar(stationUid);
        if (barCoords == null)
            Log.Warning("No usable warp point found on the station; falling back to random tiles.");

        // Dialogue system enforces a hard 30s floor on the interval.
        var interval = Math.Max(30f, _cfg.GetCVar(CCVars.MafiaDirectorNpcDialogueMinimumDecisionSeconds));
        var configured = 0;
        var offset = _random.Next(Crew.Length); // vary which crew turn up round to round
        for (var i = 0; i < count; i++)
        {
            EntityCoordinates coords;
            if (barCoords is { } bar)
                coords = bar.Offset(new Vector2((i - count / 2f) * 0.7f, 0f)); // small spread near the bar
            else
                coords = _map.GridTileToLocal(grid, gridComp, _random.Pick(tiles).GridIndices);
            var npc = Spawn(prototype, coords);

            // Give them a real distinct name (shown on radio/chat and fed to the model's
            // self-context) plus their persona, so each is a consistent individual.
            var member = Crew[(offset + i) % Crew.Length];
            _metaData.SetEntityName(npc, member.Name);
            if (_dialogue.TryConfigureNpc(npc, interval, member.Persona, out var error))
                configured++;
            else
                Log.Warning($"Dialogue configure failed for crew NPC: {error}");
        }

        Log.Info($"Spawned {count} hybrid LLM crew ('{prototype}') at " +
                 (barCoords is { } placed ? $"the bar {placed}" : "random station-floor tiles (no bar warp point found)") +
                 $"; {configured} wired for dialogue. Routine = HTN; escalation = LLM director.");

        if (_cfg.GetCVar(CCVars.MafiaCrewRadioProbe))
            _probeAt = _timing.CurTime + TimeSpan.FromSeconds(ProbeDelaySeconds);
    }
}
