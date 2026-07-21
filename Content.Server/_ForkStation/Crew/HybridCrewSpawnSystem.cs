using Content.Server._ForkStation.LlmDirector;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.Station.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

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

    private const string CrewPersona =
        "A wary station crew member. Speak in short, natural first-person lines. React to what you see and hear.";

    private static readonly ISawmill Log = Logger.GetSawmill("mafia.crew");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(ref StationPostInitEvent ev)
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

        Entity<StationDataComponent?> stationEnt = (ev.Station.Owner, ev.Station.Comp);
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

        // Dialogue system enforces a hard 30s floor on the interval.
        var interval = Math.Max(30f, _cfg.GetCVar(CCVars.MafiaDirectorNpcDialogueMinimumDecisionSeconds));
        var configured = 0;
        for (var i = 0; i < count; i++)
        {
            var tile = _random.Pick(tiles);
            var coords = _map.GridTileToLocal(grid, gridComp, tile.GridIndices);
            var npc = Spawn(prototype, coords);

            // Wire the goal-driven NPC to also SPEAK: the dialogue system only processes
            // entities that carry LlmNpcDialogueComponent, which the command normally adds
            // per-entity. Configure it here so auto-spawned crew can talk, not just move.
            if (_dialogue.TryConfigureNpc(npc, interval, CrewPersona, out var error))
                configured++;
            else
                Log.Warning($"Dialogue configure failed for crew NPC: {error}");
        }

        Log.Info($"Spawned {count} hybrid LLM crew ('{prototype}') on the station floor; " +
                 $"{configured} wired for dialogue. Routine = HTN; escalation = LLM director.");
    }
}
