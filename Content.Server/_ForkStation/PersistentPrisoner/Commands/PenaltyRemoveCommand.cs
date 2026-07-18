using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// Admin command to remove a specific penalty by ID.
/// Usage: penaltyremove <penalty-id>
/// </summary>
[AdminCommand(AdminFlags.Ban)]
public sealed class PenaltyRemoveCommand : LocalizedCommands
{
    public override string Command => "penaltyremove";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError("Usage: penaltyremove <penalty-id>");
            return;
        }

        if (!int.TryParse(args[0], out var penaltyId))
        {
            shell.WriteError("Penalty ID must be an integer.");
            return;
        }

        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<PersistentPrisonerSystem>();

        if (system.RemovePenalty(penaltyId))
        {
            shell.WriteLine($"Penalty #{penaltyId} removed.");
        }
        else
        {
            shell.WriteError($"Penalty #{penaltyId} not found.");
        }
    }
}
