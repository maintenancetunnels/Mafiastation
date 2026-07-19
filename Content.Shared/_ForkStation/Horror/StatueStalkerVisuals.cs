using Robust.Shared.Serialization;

namespace Content.Shared._ForkStation.Horror;

/// <summary>
/// Appearance keys for the statue stalker. Observers (ghosts, mostly) get to watch it hunt.
/// </summary>
[Serializable, NetSerializable]
public enum StatueStalkerVisuals : byte
{
    Moving,
}
