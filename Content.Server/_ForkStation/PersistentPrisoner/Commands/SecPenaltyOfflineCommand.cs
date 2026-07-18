using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Mind;
using Content.Shared.Roles.Jobs;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// Security command to apply penalty rounds to a dead or disconnected player.
/// Per design doc: "Security can add penalty rounds to already-dead players.
/// This means they can discover a culprit who blew himself up. Capped at two."
/// Usage: secpenaltydead <player-name-or-id> <rounds 1-2> <reason>
/// </summary>
public sealed class SecPenaltyOfflineCommand : IConsoleCommand
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "secpenaltydead";
    public string Description => "Apply penalty rounds to a dead/disconnected player (Security Officer+). Capped at 2.";
    public string Help => "secpenaltydead <player> <rounds 1-2> <reason>";

    private static readonly HashSet<string> AuthorizedJobs = new()
    {
        "SecurityOfficer", "SeniorOfficer", "Warden", "HeadOfSecurity", "PrisonGuard",
    };

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteError("This command can only be used by players in-game.");
            return;
        }

        // Verify sec role
        var mindSystem = _systems.GetEntitySystem<SharedMindSystem>();
        var jobSystem = _systems.GetEntitySystem<SharedJobSystem>();

        if (!mindSystem.TryGetMind(shell.Player.UserId, out var mindId, out _))
        {
            shell.WriteError("You don't have a mind.");
            return;
        }

        if (!jobSystem.MindTryGetJobId(mindId, out var jobId) || jobId == null || !AuthorizedJobs.Contains(jobId))
        {
            shell.WriteError("Only Security Officers and above can apply penalty rounds.");
            return;
        }

        if (args.Length < 3)
        {
            shell.WriteError("Usage: secpenaltydead <player> <rounds 1-2> <reason>");
            return;
        }

        if (!int.TryParse(args[1], out var rounds) || rounds < 1 || rounds > 2)
        {
            shell.WriteError("Rounds for dead/offline players are capped at 2 (execution rules).");
            return;
        }

        var reason = string.Join(" ", args.Skip(2));

        // Look up by name or ID (works for offline players)
        var located = await _locator.LookupIdByNameOrIdAsync(args[0]);
        if (located == null)
        {
            shell.WriteError($"Player '{args[0]}' not found.");
            return;
        }

        var system = _systems.GetEntitySystem<PersistentPrisonerSystem>();
        var record = system.AddPenalty(
            located.UserId.ToString(),
            shell.Player.UserId.ToString(),
            shell.Player.Name,
            rounds,
            reason,
            adminIssued: false);

        if (record != null)
        {
            var total = system.GetPenaltyRounds(located.UserId.ToString());
            shell.WriteLine($"Applied {record.RoundsAssigned} penalty round(s) to {located.Username} (dead/offline). Total: {total}.");
        }
        else
        {
            shell.WriteError("Failed to apply penalty.");
        }
    }
}
