using Robust.Shared.Prototypes;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Roundstart SCP-style holding: one Euclid object in the brig/containment cell.
/// It does not hunt until unobserved. Closed doors hold it; it does not bump them open.
/// </summary>
[RegisterComponent]
public sealed partial class AnomalousContainmentRuleComponent : Component
{
    [DataField]
    public EntProtoId ObjectPrototype = "MobMS173";

    [DataField]
    public EntProtoId DocumentPrototype = "PaperMS173Procedures";

    public EntityUid? Contained;
    public EntityUid? Document;
    public EntityUid Station;
}
