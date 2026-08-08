using Content.Shared.Mind;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.Investigation.Commands;

/// <summary>
/// In-game command for investigators to pull everything the station recorded about someone:
/// camera sightings, door swipes, and any death they are implicated in or were the victim of,
/// merged into one timeline.
///
/// Restricted to investigative roles because it is a station capability, not omniscience — and
/// because the design doc puts the burden of proof on security rather than on admins.
///
/// Usage: casefile &lt;name&gt;
/// </summary>
public sealed class CaseFileCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "casefile";
    public string Description => "Collate station records about a person into a timeline (Security).";
    public string Help => "casefile <name>";

    /// <summary>
    /// Roles with a legitimate reason to comb station records. Detective first — this is their job.
    /// </summary>
    private static readonly HashSet<string> InvestigativeJobs = new()
    {
        "Detective", "SecurityOfficer", "SeniorOfficer", "Warden", "HeadOfSecurity",
        "PrisonGuard", "Captain", "Lawyer", "Prosecutor",
    };

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteError("This command can only be used by players in-game.");
            return;
        }

        if (args.Length < 1)
        {
            shell.WriteError(Help);
            return;
        }

        var minds = _systems.GetEntitySystem<SharedMindSystem>();
        var jobs = _systems.GetEntitySystem<SharedJobSystem>();

        if (!minds.TryGetMind(shell.Player.UserId, out var mindId, out _))
        {
            shell.WriteError("You don't have a mind.");
            return;
        }

        if (!jobs.MindTryGetJobId(mindId, out var jobId) || jobId == null || !InvestigativeJobs.Contains(jobId))
        {
            shell.WriteError("Only investigative personnel can pull station records.");
            return;
        }

        // Names have spaces; take everything after the command.
        var subject = string.Join(' ', args).Trim();

        var caseFile = _systems.GetEntitySystem<CaseFileSystem>();
        foreach (var line in caseFile.FormatTimeline(subject))
        {
            shell.WriteLine(line);
        }
    }
}
