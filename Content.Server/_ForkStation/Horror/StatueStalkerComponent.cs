using Content.Shared.Damage;
using Robust.Shared.Audio;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// A weeping-angel-style hunter: it only moves while no living player can see it.
/// When unobserved it closes in on the nearest living player and strikes.
/// </summary>
[RegisterComponent]
public sealed partial class StatueStalkerComponent : Component
{
    /// <summary>
    /// Movement speed (m/s) while unobserved.
    /// </summary>
    [DataField]
    public float MoveSpeed = 5.5f;

    /// <summary>
    /// Distance within which the stalker can strike.
    /// </summary>
    [DataField]
    public float AttackRange = 1.2f;

    /// <summary>
    /// Players within this range with an unoccluded line of sight freeze the stalker.
    /// </summary>
    [DataField]
    public float ObservationRange = 10f;

    /// <summary>
    /// Range within which it acquires prey.
    /// </summary>
    [DataField]
    public float HuntRange = 40f;

    /// <summary>
    /// Damage dealt per strike.
    /// </summary>
    [DataField]
    public DamageSpecifier Damage = new()
    {
        DamageDict = { ["Blunt"] = 30.0 }
    };

    /// <summary>
    /// Minimum time between strikes.
    /// </summary>
    [DataField]
    public TimeSpan AttackCooldown = TimeSpan.FromSeconds(1.5);

    public TimeSpan NextAttack = TimeSpan.Zero;

    /// <summary>
    /// Sound played on a successful strike.
    /// </summary>
    [DataField]
    public SoundSpecifier AttackSound = new SoundPathSpecifier("/Audio/Weapons/smash.ogg");

    /// <summary>
    /// Whether the stalker was observed last tick (used to gate movement).
    /// </summary>
    public bool Observed;
}
