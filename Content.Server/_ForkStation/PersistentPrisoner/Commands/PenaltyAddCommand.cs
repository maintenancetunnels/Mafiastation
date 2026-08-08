using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// Admin command to add penalty rounds to a player.
/// Usage: penaltyadd <player> <rounds> <reason>
/// </summary>
[AdminCommand(AdminFlags.Ban)]
public sealed class PenaltyAddCommand : LocalizedCommands
{
    [Dependency] private readonly IPlayerLocator _locator = default!;

    public override string Command => "penaltyadd";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3)
        {
            shell.WriteError("Usage: penaltyadd <player> <rounds> <reason>");
            return;
        }

        var target = args[0];
        if (!int.TryParse(args[1], out var rounds) || rounds < 1)
        {
            shell.WriteError("Rounds must be a positive integer.");
            return;
        }

        var reason = string.Join(" ", args.Skip(2));

        var located = await _locator.LookupIdByNameOrIdAsync(target);
        if (located == null)
        {
            shell.WriteError($"Player '{target}' not found.");
            return;
        }

        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<PersistentPrisonerSystem>();
        var adminUserId = shell.Player?.UserId.ToString() ?? "CONSOLE";
        var adminName = shell.Player?.Name ?? "Console";

        var record = system.AddPenalty(
            located.UserId.ToString(),
            adminUserId,
            adminName,
            rounds,
            reason,
            adminIssued: true);

        if (record != null)
        {
            var total = system.GetPenaltyRounds(located.UserId.ToString());
            shell.WriteLine($"Added {record.RoundsAssigned} penalty round(s) to {located.Username}. Total outstanding: {total}.");
        }
        else
        {
            shell.WriteError("Failed to add penalty (player may be at the cap of 20 rounds).");
        }
    }
}
