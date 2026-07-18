namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Station event: lights across the station flicker eerily for the duration.
/// No damage, no announcement — just wrongness.
/// </summary>
[RegisterComponent]
public sealed partial class LiminalFlickerRuleComponent : Component
{
    /// <summary>
    /// Average light flickers triggered per second across the station.
    /// </summary>
    [DataField]
    public float FlickersPerSecond = 8f;

    /// <summary>
    /// Lights eligible for flickering, collected when the event starts.
    /// </summary>
    public readonly List<EntityUid> Lights = new();

    public float Accumulator;

    public EntityUid AffectedStation;
}
