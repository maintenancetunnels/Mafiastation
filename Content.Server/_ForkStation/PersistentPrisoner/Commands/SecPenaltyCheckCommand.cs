using Content.Shared.Mind;
using Content.Shared.Roles.Jobs;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// In-game command for security to check a player's penalty status.
/// Any security role can use this.
/// Usage: secpenaltycheck <player>
/// </summary>
public sealed class SecPenaltyCheckCommand : IConsoleCommand
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "secpenaltycheck";
    public string Description => "Check a player's penalty round status (Security).";
    public string Help => "secpenaltycheck <player>";

    private static readonly HashSet<string> AllSecJobs = new()
    {
        "SecurityOfficer", "SeniorOfficer", "Warden", "HeadOfSecurity",
        "PrisonGuard", "SecurityCadet", "Detective",
    };

    public void Execute(IConsoleShell shell, string argStr, string[] args)
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

        if (!jobSystem.MindTryGetJobId(mindId, out var jobId) || jobId == null || !AllSecJobs.Contains(jobId))
        {
            shell.WriteError("Only Security personnel can check penalty records.");
            return;
        }

        if (args.Length < 1)
        {
            shell.WriteError("Usage: secpenaltycheck <player>");
            return;
        }

        // Find target
        ICommonSession? targetSession = null;
        foreach (var session in _playerManager.Sessions)
        {
            if (string.Equals(session.Name, args[0], StringComparison.OrdinalIgnoreCase))
            {
                targetSession = session;
                break;
            }
        }

        if (targetSession == null)
        {
            shell.WriteError($"Player '{args[0]}' not found online.");
            return;
        }

        var system = _systems.GetEntitySystem<PersistentPrisonerSystem>();
        var userId = targetSession.UserId.ToString();
        var total = system.GetPenaltyRounds(userId);
        var active = system.GetActivePenalties(userId);

        if (total == 0)
        {
            shell.WriteLine($"{targetSession.Name}: No active penalties. Clean record.");
            return;
        }

        var dangerLevel = total >= PersistentPrisonerSystem.SolitaryThreshold
            ? " [EXTREMELY DANGEROUS]"
            : total >= 10 ? " [DANGEROUS]" : "";

        shell.WriteLine($"{targetSession.Name}: {total} penalty round(s) remaining{dangerLevel}");

        foreach (var p in active)
        {
            shell.WriteLine($"  #{p.Id}: {p.RoundsRemaining}r remaining — {p.Reason} (by {p.IssuedByName})");
        }
    }
}
