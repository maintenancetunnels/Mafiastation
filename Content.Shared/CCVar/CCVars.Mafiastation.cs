using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Whether the station spawns a basement sublevel at round start: a derelict deck
    /// joined to the station, reachable only by stairwell portals.
    /// </summary>
    public static readonly CVarDef<bool> BasementEnabled =
        CVarDef.Create("mafiastation.basement_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Grid file loaded as the basement.
    /// </summary>
    public static readonly CVarDef<string> BasementGridPath =
        CVarDef.Create("mafiastation.basement_grid", "/Maps/Ruins/ruined_prison_ship.yml", CVar.SERVERONLY);
}
