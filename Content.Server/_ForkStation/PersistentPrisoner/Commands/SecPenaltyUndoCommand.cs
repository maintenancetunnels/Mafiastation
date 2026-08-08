using Content.Shared.Mind;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// Security command to undo a penalty they applied this round.
/// Per design doc: "Security can undo penalty rounds applied on the same round
/// if they realize they messed up, but cannot undo penalty rounds accumulated
/// in other rounds."
/// Usage: secpenaltyundo <penalty-id>
/// </summary>
public sealed class SecPenaltyUndoCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "secpenaltyundo";
    public string Description => "Undo a penalty you applied this round (Security).";
    public string Help => "secpenaltyundo <penalty-id>";

    private static readonly HashSet<string> AuthorizedJobs = new()
    {
        "SecurityOfficer", "SeniorOfficer", "Warden", "HeadOfSecurity",
        "PrisonGuard", "SecurityCadet",
    };

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteError("This command can only be used by players in-game.");
            return;
        }

        var mindSystem = _systems.GetEntitySystem<SharedMindSystem>();
        var jobSystem = _systems.GetEntitySystem<SharedJobSystem>();

        if (!mindSystem.TryGetMind(shell.Player.UserId, out var mindId, out _))
        {
            shell.WriteError("You don't have a mind.");
            return;
        }

        if (!jobSystem.MindTryGetJobId(mindId, out var jobId) || jobId == null || !AuthorizedJobs.Contains(jobId))
        {
            shell.WriteError("Only Security personnel can undo penalties.");
            return;
        }

        if (args.Length < 1 || !int.TryParse(args[0], out var penaltyId))
        {
            shell.WriteError("Usage: secpenaltyundo <penalty-id>");
            return;
        }

        var system = _systems.GetEntitySystem<PersistentPrisonerSystem>();

        // Security may only correct a mistake they made in the round still being played. Anything
        // older, or anything an admin issued, is out of their hands.
        if (!system.CanSecurityUndo(penaltyId, out var denialReason))
        {
            shell.WriteError(denialReason);
            return;
        }

        if (system.RemovePenalty(penaltyId))
        {
            shell.WriteLine($"Penalty #{penaltyId} has been undone.");
        }
        else
        {
            shell.WriteError($"Penalty #{penaltyId} not found or cannot be undone.");
        }
    }
}
