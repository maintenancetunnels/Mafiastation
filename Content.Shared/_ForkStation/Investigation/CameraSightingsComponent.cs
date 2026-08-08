using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._ForkStation.Investigation;

/// <summary>
/// A camera's memory of who it saw.
///
/// Surveillance cameras in Space Station 14 record nothing at all — they hand a live view to
/// whoever is watching the monitor at that instant, and if nobody is watching, nothing happened.
/// The Persistent Prisoners design doc calls this out directly and asks for "security cameras that
/// take a snapshot every few seconds", justified in-fiction by "an authoritarian megacorporation
/// paranoid about the safety of its assets".
///
/// Deliberately stores names rather than images: a list of who was where and when is what makes a
/// timeline reconstructable, and it lines up with the door access log so the two can be read
/// together.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class CameraSightingsComponent : Component
{
    /// <summary>
    /// Rolling log of sightings, oldest first. Bounded, like the door access log, so a two-hour
    /// shift cannot grow it without limit.
    /// </summary>
    [DataField]
    public Queue<CameraSighting> Sightings = new();

    /// <summary>
    /// How many sightings to keep. Door logs keep 20 per reader; cameras see far more traffic, so
    /// they keep more.
    /// </summary>
    [DataField]
    public int SightingLimit = 60;

    /// <summary>
    /// How far the camera notices people, in tiles.
    /// </summary>
    [DataField]
    public float Range = 6f;

    /// <summary>
    /// How often the camera takes a look.
    /// </summary>
    [DataField]
    public TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Don't log the same person repeatedly while they stand still — only re-log them after this
    /// long. Without it a person loitering in view floods the buffer and evicts everything useful.
    /// </summary>
    [DataField]
    public TimeSpan RepeatSuppression = TimeSpan.FromSeconds(45);

    /// <summary>
    /// When this camera next takes a look.
    /// </summary>
    [DataField]
    public TimeSpan NextScan;

    /// <summary>
    /// Last time each person was logged, for <see cref="RepeatSuppression"/>. Not saved.
    /// </summary>
    [ViewVariables]
    public Dictionary<string, TimeSpan> LastSeen = new();
}

/// <summary>
/// One person, seen by one camera, at one station time. Shaped to match
/// <c>AccessRecord</c> so investigation tooling can present both the same way.
/// </summary>
[DataDefinition, Serializable, NetSerializable]
public readonly partial record struct CameraSighting(
    [property: DataField, ViewVariables(VVAccess.ReadWrite)]
    TimeSpan SightingTime,
    [property: DataField, ViewVariables(VVAccess.ReadWrite)]
    string Subject)
{
    public CameraSighting() : this(TimeSpan.Zero, string.Empty)
    {
    }
}
