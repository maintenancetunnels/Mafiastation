using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Whether the Persistent Prisoner system is enabled.
    /// When enabled, security officers can assign penalty rounds
    /// and penalized players spawn as prisoners in future rounds.
    /// </summary>
    public static readonly CVarDef<bool> PersistentPrisonerEnabled =
        CVarDef.Create("game.persistent_prisoner", true, CVar.REPLICATED | CVar.SERVER);

    /// <summary>
    /// Whether penalty rounds auto-decay over time.
    /// When enabled, 1 penalty round decays per 48 hours of real time.
    /// </summary>
    public static readonly CVarDef<bool> PersistentPrisonerDecay =
        CVarDef.Create("game.persistent_prisoner_decay", true, CVar.SERVER);

    /// <summary>
    /// Hours per auto-decayed penalty round.
    /// </summary>
    public static readonly CVarDef<float> PersistentPrisonerDecayHours =
        CVarDef.Create("game.persistent_prisoner_decay_hours", 48f, CVar.SERVER);
}
