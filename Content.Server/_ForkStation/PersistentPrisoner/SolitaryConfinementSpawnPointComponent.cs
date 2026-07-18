namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Marks a spawn location for solitary confinement. Persistent prisoners at or above the
/// solitary threshold (15+ penalty rounds) are relocated here after spawning.
/// </summary>
[RegisterComponent]
public sealed partial class SolitaryConfinementSpawnPointComponent : Component
{
}
