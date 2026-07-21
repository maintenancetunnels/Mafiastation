using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// How many server-owned LLM hybrid NPC crew to spawn at arrivals each round.
    /// These use ordinary HTN for routine behavior and Codex's bounded LLM director
    /// (mafia.director.npc_hybrid_enabled) for goal/dialogue escalation. 0 = none.
    /// </summary>
    public static readonly CVarDef<int> MafiaCrewHybridCount =
        CVarDef.Create("mafia.crew.hybrid_count", 0, CVar.SERVERONLY);

    /// <summary>
    /// The hybrid NPC prototype spawned as crew. Defaults to the AI pilot lab's
    /// goal/capacity humanoid.
    /// </summary>
    public static readonly CVarDef<string> MafiaCrewHybridPrototype =
        CVarDef.Create("mafia.crew.hybrid_prototype", "MobMafiaHybridPrisoner", CVar.SERVERONLY);

    /// <summary>
    /// Sandbox convenience: attach an infinite generator to the station's power net at round
    /// start so an unmanned station stays lit. The rare BlackoutHunt event still cuts power.
    /// </summary>
    public static readonly CVarDef<bool> MafiaInfinitePower =
        CVarDef.Create("mafia.infinite_power", true, CVar.SERVERONLY);
}
