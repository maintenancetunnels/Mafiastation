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

    /// <summary>
    /// Self-test only. When true, one hybrid crew member keys up Common once, shortly after
    /// round start, with a fixed question addressed to the crew. This exercises the whole
    /// reply chain (radio observation -> responder selection -> LLM request -> speech
    /// submission) so it can be verified from the server log without a connected client.
    /// Leave false for real play; it is not gameplay content.
    /// </summary>
    public static readonly CVarDef<bool> MafiaCrewRadioProbe =
        CVarDef.Create("mafia.crew.radio_probe", false, CVar.SERVERONLY);

    /// <summary>
    /// Place joining players in the bar alongside the hybrid crew, so a session opens with
    /// everyone in one room instead of the player searching the station for them.
    /// </summary>
    public static readonly CVarDef<bool> MafiaCrewPlayerBarSpawn =
        CVarDef.Create("mafia.crew.player_bar_spawn", false, CVar.SERVERONLY);
}
