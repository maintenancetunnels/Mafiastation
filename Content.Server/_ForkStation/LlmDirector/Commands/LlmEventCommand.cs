using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>
/// Explicit admin experiment. Preview is the default; starting requires --start plus a server CVar.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmEventCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmevent";
    public string Description => "Ask the bounded LLM director to select an allowlisted game rule.";
    public string Help => "llmevent <ruleA,ruleB,...> [--start] [context]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError(Help);
            return;
        }

        var options = args[0]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => new DirectorChoiceOption(id, $"Existing game-rule prototype {id}."))
            .ToArray();
        var start = args.Skip(1).Any(arg => arg == "--start");
        var context = string.Join(" ", args.Skip(1).Where(arg => arg != "--start"));
        var director = _systems.GetEntitySystem<LlmGameplayDirectorSystem>();
        if (!director.TryRequestGameRule(
                shell.Player?.UserId,
                options,
                context,
                start,
                out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine(start
            ? "Bounded LLM event-selection-and-start request queued."
            : "Bounded LLM event preview request queued.");
    }
}
