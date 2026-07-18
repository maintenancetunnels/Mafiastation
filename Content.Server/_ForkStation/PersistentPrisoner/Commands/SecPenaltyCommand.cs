using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.PersistentPrisoner.Commands;

/// <summary>
/// In-game security command to apply penalty rounds to a player.
/// Only usable by players with Security Officer or higher sec roles.
/// No admin permission required — this is an IC action.
/// Usage: secpenalty <player> <rounds> <reason>
/// </summary>
public sealed class SecPenaltyCommand : IConsoleCommand
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "secpenalty";
    public string Description => "Apply penalty rounds to a player (Security Officer+).";
    public string Help => "secpenalty <player> <rounds 1-5> <reason>";

    /// <summary>
    /// Jobs that can apply penalties. Rookies have limited power (see design doc).
    /// </summary>
    private static readonly HashSet<string> AuthorizedJobs = new()
    {
        "SecurityOfficer",
        "SeniorOfficer",
        "Warden",
        "HeadOfSecurity",
        "PrisonGuard",
    };

    /// <summary>
    /// Rookie jobs can only apply penalties in lowpop (checked elsewhere).
    /// For now they can apply with a cap of 1 round.
    /// </summary>
    private static readonly HashSet<string> RookieJobs = new()
    {
        "SecurityCadet",
    };

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteError("This command can only be used by players in-game.");
            return;
        }

        // Verify the caller is playing a security role
        var callerSession = shell.Player;
        var jobSystem = _systems.GetEntitySystem<SharedJobSystem>();
        var mindSystem = _systems.GetEntitySystem<SharedMindSystem>();

        if (!mindSystem.TryGetMind(callerSession.UserId, out var mindId, out var mind))
        {
            shell.WriteError("You don't have a mind. Are you in-game?");
            return;
        }

        if (!jobSystem.MindTryGetJobId(mindId, out var jobId))
        {
            shell.WriteError("You don't have a job assigned.");
            return;
        }

        var isAuthorized = jobId != null && AuthorizedJobs.Contains(jobId);
        var isRookie = jobId != null && RookieJobs.Contains(jobId);

        if (!isAuthorized && !isRookie)
        {
            shell.WriteError("Only Security Officers and above can apply penalty rounds.");
            return;
        }

        if (args.Length < 3)
        {
            shell.WriteError("Usage: secpenalty <player> <rounds 1-5> <reason>");
            return;
        }

        var targetName = args[0];
        if (!int.TryParse(args[1], out var rounds) || rounds < 1 || rounds > 5)
        {
            shell.WriteError("Rounds must be between 1 and 5.");
            return;
        }

        // Rookies capped at 1 round
        if (isRookie && rounds > 1)
        {
            shell.WriteError("Security Cadets can only apply 1 penalty round at a time.");
            rounds = 1;
        }

        var reason = string.Join(" ", args.Skip(2));

        // Find target player
        ICommonSession? targetSession = null;
        foreach (var session in _playerManager.Sessions)
        {
            if (string.Equals(session.Name, targetName, StringComparison.OrdinalIgnoreCase))
            {
                targetSession = session;
                break;
            }
        }

        if (targetSession == null)
        {
            shell.WriteError($"Player '{targetName}' not found online.");
            return;
        }

        // Can't penalize yourself
        if (targetSession.UserId == callerSession.UserId)
        {
            shell.WriteError("You cannot apply penalties to yourself.");
            return;
        }

        var system = _systems.GetEntitySystem<PersistentPrisonerSystem>();
        var record = system.AddPenalty(
            targetSession.UserId.ToString(),
            callerSession.UserId.ToString(),
            callerSession.Name,
            rounds,
            reason,
            adminIssued: false);

        if (record != null)
        {
            var total = system.GetPenaltyRounds(targetSession.UserId.ToString());
            shell.WriteLine($"Applied {record.RoundsAssigned} penalty round(s) to {targetSession.Name}. Total: {total}.");
        }
        else
        {
            shell.WriteError("Failed to apply penalty (player may be at the cap).");
        }
    }
}
