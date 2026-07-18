using Robust.Shared.Console;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// Player command to view their own penalty status.
/// No permissions required — any player can check their own record.
/// Usage: mypenalties
/// </summary>
public sealed class MyPenaltiesCommand : IConsoleCommand
{
    public string Command => "mypenalties";
    public string Description => "View your own penalty round status.";
    public string Help => "mypenalties";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteError("This command can only be used by players.");
            return;
        }

        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<PersistentPrisonerSystem>();
        var userId = shell.Player.UserId.ToString();
        var total = system.GetPenaltyRounds(userId);

        if (total == 0)
        {
            shell.WriteLine("You have no active penalties. Your record is clean.");
            return;
        }

        var dangerLevel = "";
        if (total >= PersistentPrisonerSystem.SolitaryThreshold)
            dangerLevel = " [EXTREMELY DANGEROUS - SOLITARY CONFINEMENT]";
        else if (total >= 10)
            dangerLevel = " [DANGEROUS]";

        shell.WriteLine($"=== YOUR PENALTY RECORD ==={dangerLevel}");
        shell.WriteLine($"Total penalty rounds remaining: {total}");
        shell.WriteLine("");

        var penalties = system.GetActivePenalties(userId);
        foreach (var p in penalties)
        {
            var source = p.AdminIssued ? "Administration" : p.IssuedByName;
            shell.WriteLine($"  Penalty #{p.Id}: {p.RoundsRemaining} round(s) remaining (of {p.RoundsAssigned})");
            shell.WriteLine($"    Reason: {p.Reason}");
            shell.WriteLine($"    Issued by: {source}");
            shell.WriteLine($"    Date: {p.IssuedAt:yyyy-MM-dd HH:mm} UTC");
            shell.WriteLine("");
        }

        shell.WriteLine("Serve your time peacefully. Good behavior earns early credit.");
        shell.WriteLine("Penalties auto-decay at a rate of 1 round per 48 hours.");
    }
}
