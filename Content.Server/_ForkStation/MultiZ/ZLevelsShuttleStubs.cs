using Content.Server.Shuttles.Systems;

namespace Content.Server.Shuttles.Systems;

// _CE ZLevels port: the shuttle-z-transit code (CEZLevelsSystem.Transit / PilotControl) calls a
// couple of Monolith-added shuttle/docking helpers our Delta-V base lacks. These stubs satisfy the
// core multi-Z build. Character multi-Z (falling, rendering, gravity, climbing) is fully live;
// flying whole shuttles between z-levels is the peripheral feature these stub out — a docked
// shuttle simply isn't dragged along, and re-docking is a no-op.
public sealed partial class ShuttleSystem
{
    /// <summary>Stub: return only the grid itself, not its docked network.</summary>
    public void GetAllDockedShuttles(EntityUid shuttleUid, HashSet<EntityUid> dockedShuttles)
    {
        dockedShuttles.Add(shuttleUid);
    }
}

public sealed partial class DockingSystem
{
    /// <summary>Stub: re-docking after a z-move is a no-op in the core port.</summary>
    public void RedockDocks(EntityUid gridUid)
    {
    }
}
