using Robust.Shared.Prototypes;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Mid-shift: holding fails. MS-173 is already in the halls, or it is now.
/// </summary>
[RegisterComponent]
public sealed partial class ContainmentBreachRuleComponent : Component
{
    [DataField]
    public EntProtoId ObjectPrototype = "MobMS173";

    public readonly List<EntityUid> Spawned = new();
}
