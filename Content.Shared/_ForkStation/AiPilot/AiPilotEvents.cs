using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._ForkStation.AiPilot;

/// <summary>
/// Final action for a deterministic pilot path. Movement is always performed by the connected
/// client through ordinary movement inputs; the optional final action is also emitted as a normal
/// interaction input after the path reaches its bounded range.
/// </summary>
[Serializable, NetSerializable]
public enum AiPilotGoalAction : byte
{
    Move,
    Interact,
    Pickup,
}

[Serializable, NetSerializable]
public enum AiPilotLifecycleAction : byte
{
    Ready,
    Join,
}

/// <summary>
/// Requests a fresh server authorization snapshot. Client-side enablement is never sufficient on
/// its own: the server independently requires its master gate, loopback transport, and account
/// allowlist.
/// </summary>
[Serializable, NetSerializable]
public sealed class AiPilotAuthorizationRequestEvent(int requestId) : EntityEventArgs
{
    public int RequestId { get; } = requestId;
}

[Serializable, NetSerializable]
public sealed class AiPilotAuthorizationStateEvent(
    int requestId,
    bool authorized,
    string reason,
    bool allowJoin,
    bool allowSpeech,
    int speechMaxCharacters,
    float speechCooldownSeconds,
    float observationRadius,
    int maximumObservedEntities,
    float maximumGoalDistance,
    int maximumPathWaypoints,
    float goalTimeoutSeconds) : EntityEventArgs
{
    public int RequestId { get; } = requestId;
    public bool Authorized { get; } = authorized;
    public string Reason { get; } = reason;
    public bool AllowJoin { get; } = allowJoin;
    public bool AllowSpeech { get; } = allowSpeech;
    public int SpeechMaxCharacters { get; } = speechMaxCharacters;
    public float SpeechCooldownSeconds { get; } = speechCooldownSeconds;
    public float ObservationRadius { get; } = observationRadius;
    public int MaximumObservedEntities { get; } = maximumObservedEntities;
    public float MaximumGoalDistance { get; } = maximumGoalDistance;
    public int MaximumPathWaypoints { get; } = maximumPathWaypoints;
    public float GoalTimeoutSeconds { get; } = goalTimeoutSeconds;
}

/// <summary>
/// Sends one bounded, server-authored damage perception to the affected pilot. This is not a
/// combat-log backchannel: the source is included only when it is a nearby world entity that the
/// injured character could ordinarily perceive.
/// </summary>
[Serializable, NetSerializable]
public sealed class AiPilotDamageEvent(
    float amount,
    bool hasSource,
    NetEntity source,
    string sourceName) : EntityEventArgs
{
    public float Amount { get; } = amount;
    public bool HasSource { get; } = hasSource;
    public NetEntity Source { get; } = source;
    public string SourceName { get; } = sourceName;
}

/// <summary>
/// Requests only a path, never server-side control of the player's entity. The server validates
/// the destination/target and returns a bounded list of waypoints. The connected client traverses
/// those waypoints using ordinary input commands.
/// </summary>
[Serializable, NetSerializable]
public sealed class AiPilotPathRequestEvent(
    int requestId,
    NetCoordinates destination,
    bool hasTarget,
    NetEntity target,
    AiPilotGoalAction finalAction,
    float range) : EntityEventArgs
{
    public int RequestId { get; } = requestId;
    public NetCoordinates Destination { get; } = destination;
    public bool HasTarget { get; } = hasTarget;
    public NetEntity Target { get; } = target;
    public AiPilotGoalAction FinalAction { get; } = finalAction;
    public float Range { get; } = range;
}

[Serializable, NetSerializable]
public sealed class AiPilotPathResultEvent(
    int requestId,
    bool accepted,
    string error,
    IReadOnlyList<NetCoordinates> waypoints,
    bool hasTarget,
    NetEntity target,
    AiPilotGoalAction finalAction,
    float range) : EntityEventArgs
{
    public int RequestId { get; } = requestId;
    public bool Accepted { get; } = accepted;
    public string Error { get; } = error;
    public IReadOnlyList<NetCoordinates> Waypoints { get; } = waypoints;
    public bool HasTarget { get; } = hasTarget;
    public NetEntity Target { get; } = target;
    public AiPilotGoalAction FinalAction { get; } = finalAction;
    public float Range { get; } = range;
}

[Serializable, NetSerializable]
public sealed class AiPilotLifecycleRequestEvent(
    int requestId,
    AiPilotLifecycleAction action,
    bool ready,
    string requestedJob) : EntityEventArgs
{
    public int RequestId { get; } = requestId;
    public AiPilotLifecycleAction Action { get; } = action;
    public bool Ready { get; } = ready;
    public string RequestedJob { get; } = requestedJob;
}

[Serializable, NetSerializable]
public sealed class AiPilotLifecycleResultEvent(
    int requestId,
    bool accepted,
    string error,
    string assignedJob) : EntityEventArgs
{
    public int RequestId { get; } = requestId;
    public bool Accepted { get; } = accepted;
    public string Error { get; } = error;
    public string AssignedJob { get; } = assignedJob;
}
