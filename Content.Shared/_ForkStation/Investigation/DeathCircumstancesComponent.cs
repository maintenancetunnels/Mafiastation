using Robust.Shared.GameStates;

namespace Content.Shared._ForkStation.Investigation;

/// <summary>
/// What happened to a body. Space Station 14 records none of this: a corpse knows its accumulated
/// damage but not what killed it, when, where, or who was holding the weapon. Without it a
/// murder is unsolvable except by eyewitness, which makes security's penalties guesswork — and
/// the Persistent Prisoners design only works if security can actually build a case.
///
/// Written continuously while the mob takes damage, then frozen at the moment of death.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DeathCircumstancesComponent : Component
{
    /// <summary>
    /// Station time the mob died. Matches the clock used by door access logs so the two can be
    /// lined up on one timeline.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan? TimeOfDeath;

    /// <summary>
    /// The damage type that contributed most of the final blow, e.g. Slash, Asphyxiation, Heat.
    /// This is the closest thing to a murder-weapon signature.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? KillingDamageType;

    /// <summary>
    /// How much damage the final blow did.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float FinalBlowAmount;

    /// <summary>
    /// The name the killer was presenting as, resolved through the identity system rather than
    /// the true name — a murderer in a mask and a stolen ID should show up as whoever they were
    /// pretending to be. Framing has to remain possible.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? SuspectName;

    /// <summary>
    /// Whether a responsible entity was identified at all. Environmental deaths (vacuum, fire,
    /// falling) legitimately have no suspect, and the design doc treats those differently from
    /// executions when deciding whether a prisoner's death counts against them.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool HasSuspect;

    /// <summary>
    /// Whether the killing blow came from another creature rather than the environment.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool SuspectWasCreature;

    /// <summary>
    /// Human-readable location, taken from the nearest station beacon where possible.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? LocationName;
}
