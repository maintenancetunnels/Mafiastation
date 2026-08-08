namespace Content.Server._ForkStation.MultiZ;

/// <summary>
/// Transient marker on an entity currently falling through a <see cref="Content.Shared._ForkStation.MultiZ.MultiZLinkComponent"/>
/// shaft to the level below. Removed once it lands.
/// </summary>
[RegisterComponent]
public sealed partial class MultiZFallingComponent : Component
{
    /// <summary>
    /// The link marker on the level below to land on.
    /// </summary>
    public EntityUid Target;

    /// <summary>
    /// When the fall completes and the entity is moved down.
    /// </summary>
    public TimeSpan LandTime;
}
