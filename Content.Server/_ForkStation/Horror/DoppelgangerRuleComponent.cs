namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Station event: a motionless, naked copy of a random living crew member appears
/// somewhere in maintenance. It dissolves when approached — or when the event ends.
/// Nobody believes the person who saw it.
/// </summary>
[RegisterComponent]
public sealed partial class DoppelgangerRuleComponent : Component
{
    /// <summary>
    /// The doppelganger spawned by this rule, removed at event end if still standing.
    /// </summary>
    public EntityUid? Spawned;
}
