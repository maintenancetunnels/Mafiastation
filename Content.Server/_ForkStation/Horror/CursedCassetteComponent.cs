namespace Content.Server._ForkStation.Horror;

/// <summary>
/// A cassette player that whispers to whoever is carrying it.
/// </summary>
[RegisterComponent]
public sealed partial class CursedCassetteComponent : Component
{
    /// <summary>
    /// Minimum seconds between whispers while carried.
    /// </summary>
    [DataField]
    public float MinInterval = 40f;

    /// <summary>
    /// Maximum seconds between whispers while carried.
    /// </summary>
    [DataField]
    public float MaxInterval = 90f;

    public TimeSpan NextWhisper = TimeSpan.Zero;
}
