namespace Content.Shared._ForkStation.MultiZ;

/// <summary>
/// A vertical link between two stacked levels — a shaft, hole, or open floor that couples a point
/// on this level to the matching point on the level directly below (and/or above). RobustToolbox
/// has no native z-axis, so multi-Z is built as paired markers on separate aligned grids: this
/// component is the seam through which entities fall, air flows (openturf), and blasts propagate,
/// porting SS13's z-level behaviour.
/// </summary>
[RegisterComponent]
public sealed partial class MultiZLinkComponent : Component
{
    /// <summary>
    /// The paired link on the level directly below. Falling here drops to it. Null at the bottom.
    /// </summary>
    [DataField]
    public EntityUid? Down;

    /// <summary>
    /// The paired link on the level directly above. Climbing arrives here. Null at the top.
    /// </summary>
    [DataField]
    public EntityUid? Up;

    /// <summary>
    /// Whether the seam is physically open. A sealed hatch stops falling, air flow, and blasts;
    /// an open hole passes all three. This is the pressure/physics boundary.
    /// </summary>
    [DataField]
    public bool Open = true;

    /// <summary>
    /// Fraction of the gas difference equalized between the two linked tiles each atmos step while
    /// open (openturf conductance). Higher = air rushes between levels faster.
    /// </summary>
    [DataField]
    public float AtmosConductance = 0.35f;
}
