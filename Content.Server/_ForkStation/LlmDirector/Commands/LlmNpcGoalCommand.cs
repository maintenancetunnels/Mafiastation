using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>
/// Explicit admin experiment: the model can select only one of the supplied existing HTN roots.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcGoalCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcgoal";
    public string Description => "Ask the bounded LLM director to choose and apply an HTN root goal.";
    public string Help => "llmnpcgoal <netEntityId> <goalA,goalB,...> [context]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 ||
            !int.TryParse(args[0], out var netId) ||
            !_entities.TryGetEntity(new NetEntity(netId), out var target))
        {
            shell.WriteError(Help);
            return;
        }

        var options = args[1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => new DirectorChoiceOption(id, $"Existing HTN root task {id}."))
            .ToArray();
        var context = string.Join(" ", args.Skip(2));
        var director = _systems.GetEntitySystem<LlmGameplayDirectorSystem>();
        if (!director.TryRequestNpcGoal(
                shell.Player?.UserId,
                target.Value,
                options,
                context,
                out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine("Bounded LLM NPC-goal request queued.");
    }
}
