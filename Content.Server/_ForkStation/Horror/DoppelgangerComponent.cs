using Robust.Shared.Audio;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// A motionless copy of a crew member, spawned by <see cref="DoppelgangerRule"/>.
/// When any living player gets close, it crumbles to ash — like it was never there.
/// </summary>
[RegisterComponent]
public sealed partial class DoppelgangerComponent : Component
{
    /// <summary>
    /// Distance at which a living player causes it to dissolve.
    /// </summary>
    [DataField]
    public float TriggerRange = 2.5f;

    /// <summary>
    /// What it leaves behind.
    /// </summary>
    [DataField]
    public string RemainsPrototype = "Ash";

    /// <summary>
    /// Sound played when it dissolves.
    /// </summary>
    [DataField]
    public SoundSpecifier DissolveSound = new SoundPathSpecifier("/Audio/_DV/Effects/creepyshriek.ogg");

    /// <summary>
    /// Lights within this range flicker when it dissolves.
    /// </summary>
    [DataField]
    public float FlickerRange = 6f;
}
