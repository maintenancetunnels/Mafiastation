using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// Admin command to view penalty rounds for a player.
/// Usage: penaltylist <player>
/// Or: penaltylist (no args = show all players with active penalties)
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class PenaltyListCommand : LocalizedCommands
{
    [Dependency] private readonly IPlayerLocator _locator = default!;

    public override string Command => "penaltylist";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<PersistentPrisonerSystem>();

        if (args.Length == 0)
        {
            // Show summary of all players with active penalties
            var summary = system.GetPenaltySummary();
            if (summary.Count == 0)
            {
                shell.WriteLine("No active penalties.");
                return;
            }

            shell.WriteLine($"Players with active penalties ({summary.Count}):");
            foreach (var (userId, rounds) in summary.OrderByDescending(x => x.Value))
            {
                shell.WriteLine($"  {userId}: {rounds} rounds remaining");
            }
            return;
        }

        var target = args[0];
        var located = await _locator.LookupIdByNameOrIdAsync(target);
        if (located == null)
        {
            shell.WriteError($"Player '{target}' not found.");
            return;
        }

        var penalties = system.GetAllPenalties(located.UserId.ToString());
        if (penalties.Count == 0)
        {
            shell.WriteLine($"{located.Username} has no penalties.");
            return;
        }

        var totalRemaining = system.GetPenaltyRounds(located.UserId.ToString());
        shell.WriteLine($"Penalties for {located.Username} ({totalRemaining} rounds remaining):");

        foreach (var p in penalties.OrderByDescending(x => x.IssuedAt))
        {
            var status = p.IsServed ? "[SERVED]" : $"[{p.RoundsRemaining} remaining]";
            var source = p.AdminIssued ? "ADMIN" : "SEC";
            shell.WriteLine($"  #{p.Id} {status} {p.RoundsAssigned}r by {p.IssuedByName} ({source}) - {p.Reason} ({p.IssuedAt:yyyy-MM-dd HH:mm})");
        }
    }
}
