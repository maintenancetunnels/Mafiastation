namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Marks a tile as the Euclid holding cell. Roundstart MS-173 spawns here.
/// Prefer a windowed room: the object does not open doors. Visual contact is containment.
/// Prison Guards and D-class (prisoners) are the intended staff.
/// </summary>
[RegisterComponent]
public sealed partial class AnomalousContainmentSpawnPointComponent : Component;
