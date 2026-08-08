using System.Numerics;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared._ForkStation.MultiZ;
using Content.Shared.Station.Components;
using Content.Server.StationEvents.Components;
using Content.Shared.CCVar;
using Content.Shared.Teleportation.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._ForkStation.Basement;

/// <summary>
/// The station has a basement. Nobody built it. It was always there.
///
/// At station post-init, loads a derelict grid onto the station's map far below anything,
/// adopts it into the station, and connects it via a pair of linked stairwell portals — one
/// dropped at a random air vent in the station, one in the sublevel. Portals teleport
/// whatever collides with them, which means things that hunt you can follow you down.
/// </summary>
public sealed class BasementSystem : EntitySystem
{
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly LinkedEntitySystem _link = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;


    private const string StairwellDownProto = "StairwellDown";
    private const string StairwellUpProto = "StairwellUp";

    /// <summary>
    /// Where the sublevel sits relative to the station origin. Far enough that no window,
    /// sensor, or shuttle path ever shows it.
    /// </summary>
    private static readonly Vector2 BasementOffset = new(0f, -650f);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(ref StationPostInitEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.BasementEnabled))
            return;

        var stationUid = ev.Station.Owner;
        var stationData = ev.Station.Comp;

        // Find the map the station lives on via its largest grid.
        Entity<StationDataComponent?> stationEnt = (stationUid, stationData);
        if (_station.GetLargestGrid(stationEnt) is not { } anyGrid)
            return;

        var mapId = Transform(anyGrid).MapID;

        var path = new ResPath(_cfg.GetCVar(CCVars.BasementGridPath));
        var opts = DeserializationOptions.Default with { InitializeMaps = true };
        if (!_mapLoader.TryLoadGrid(mapId, path, out var basement, opts, offset: BasementOffset))
        {
            Log.Error($"Failed to load basement grid from {path}.");
            return;
        }

        var basementUid = basement.Value.Owner;
        var basementComp = basement.Value.Comp;

        _station.AddGridToStation(stationUid, basementUid, basementComp, stationData, name: "Sublevel");

        // Station-side stairwell: at a random air vent (maintenance-adjacent by nature).
        var ventSpots = new List<EntityCoordinates>();
        var vents = AllEntityQuery<VentCritterSpawnLocationComponent, TransformComponent>();
        while (vents.MoveNext(out var ventUid, out _, out var xform))
        {
            if (xform.GridUid is not { } ventGrid || ventGrid == basementUid)
                continue;

            if (CompOrNull<StationMemberComponent>(ventGrid)?.Station != stationUid)
                continue;

            ventSpots.Add(xform.Coordinates);
        }

        if (ventSpots.Count == 0)
        {
            Log.Warning("No vent locations found for the stairwell; basement loaded but unconnected.");
            return;
        }

        var downCoords = _random.Pick(ventSpots);

        // Basement-side stairwell: a random real floor tile of the sublevel.
        var tiles = new List<Robust.Shared.Map.TileRef>();
        foreach (var tile in _map.GetAllTiles(basementUid, basementComp))
        {
            tiles.Add(tile);
        }

        if (tiles.Count == 0)
        {
            Log.Warning("Basement grid has no tiles; skipping stairwell.");
            return;
        }

        var upTile = _random.Pick(tiles);
        var upCoords = _map.GridTileToLocal(basementUid, basementComp, upTile.GridIndices);

        var down = Spawn(StairwellDownProto, downCoords);
        var up = Spawn(StairwellUpProto, upCoords);
        _link.TryLink(down, up);

        // Multi-Z seam: a physical shaft next to the stairs. Unlike the teleport stairwell, this
        // is the seam that couples the two levels — you fall through it, and air/blasts propagate
        // across it (MultiZ atmos + explosion systems). Landing sits beside the basement stairwell.
        var shaftCoords = downCoords.Offset(new Vector2(1f, 0f));
        var landingCoords = upCoords.Offset(new Vector2(1f, 0f));
        var shaft = Spawn("MultiZShaft", shaftCoords);
        var landing = Spawn("MultiZLanding", landingCoords);
        var shaftLink = EnsureComp<MultiZLinkComponent>(shaft);
        var landingLink = EnsureComp<MultiZLinkComponent>(landing);
        shaftLink.Down = landing;
        landingLink.Up = shaft;

        // Populate the sublevel: solitary confinement lives down here now, contraband
        // makes it worth robbing, and something stands very still in the dark.
        SpawnAtRandomTile(basementUid, basementComp, tiles, "SpawnPointPrisonerSolitary");

        for (var i = 0; i < 3; i++)
        {
            SpawnAtRandomTile(basementUid, basementComp, tiles, "PrisonContrabandSpawner");
        }

        if (_cfg.GetCVar(CCVars.BasementStatue))
            SpawnAtRandomTile(basementUid, basementComp, tiles, "MobStatueStalker");

        Log.Info($"Basement loaded ({path}), joined to station {ToPrettyString(stationUid)}, stairwell linked at {downCoords} <-> {upCoords}.");
    }

    private void SpawnAtRandomTile(EntityUid gridUid, MapGridComponent grid, List<Robust.Shared.Map.TileRef> tiles, string prototype)
    {
        if (tiles.Count == 0)
            return;

        var tile = _random.Pick(tiles);
        var coords = _map.GridTileToLocal(gridUid, grid, tile.GridIndices);
        Spawn(prototype, coords);
    }
}
