using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Station event: the power dies station-wide and something starts hunting the crew in the dark.
/// Power is restored and the hunter vanishes when the event ends — as if it was never there.
/// </summary>
[RegisterComponent]
public sealed partial class BlackoutHuntRuleComponent : Component
{
    /// <summary>
    /// The entity spawned to hunt the crew.
    /// </summary>
    [DataField]
    public EntProtoId HunterPrototype = "MobStatueStalker";

    /// <summary>
    /// How many hunters to spawn.
    /// </summary>
    [DataField]
    public int HunterCount = 1;

    /// <summary>
    /// APCs this rule switched off, so it can restore exactly what it broke.
    /// </summary>
    public readonly List<EntityUid> Unpowered = new();

    /// <summary>
    /// Hunters spawned by this rule, removed when it ends.
    /// </summary>
    public readonly List<EntityUid> Hunters = new();

    /// <summary>
    /// Sound played globally when the power comes back.
    /// </summary>
    [DataField]
    public SoundSpecifier PowerOnSound = new SoundPathSpecifier("/Audio/Machines/machine_switch.ogg");

    public EntityUid AffectedStation;
}
