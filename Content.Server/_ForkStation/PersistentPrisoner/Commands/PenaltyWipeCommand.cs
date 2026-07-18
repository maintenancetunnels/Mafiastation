using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// Admin command to wipe all penalties for a player. One-click pardon.
/// Usage: penaltywipe <player>
/// </summary>
[AdminCommand(AdminFlags.Ban)]
public sealed class PenaltyWipeCommand : LocalizedCommands
{
    [Dependency] private readonly IPlayerLocator _locator = default!;

    public override string Command => "penaltywipe";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError("Usage: penaltywipe <player>");
            return;
        }

        var target = args[0];
        var located = await _locator.LookupIdByNameOrIdAsync(target);
        if (located == null)
        {
            shell.WriteError($"Player '{target}' not found.");
            return;
        }

        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<PersistentPrisonerSystem>();
        var count = system.WipePenalties(located.UserId.ToString());

        if (count > 0)
        {
            shell.WriteLine($"Wiped {count} penalty record(s) for {located.Username}.");
        }
        else
        {
            shell.WriteLine($"{located.Username} has no penalties to wipe.");
        }
    }
}
