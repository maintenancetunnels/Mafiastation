using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>
/// Removes the executive layer without changing the NPC's current HTN goal.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcAutonomyOffCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcautonomyoff";
    public string Description => "Disable contextual LLM executive decisions for an HTN NPC.";
    public string Help => "llmnpcautonomyoff <netEntityId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 ||
            !int.TryParse(args[0], out var netId) ||
            !_entities.TryGetEntity(new NetEntity(netId), out var target))
        {
            shell.WriteError(Help);
            return;
        }

        var autonomy = _systems.GetEntitySystem<LlmNpcAutonomySystem>();
        if (!autonomy.TryDisableNpc(target.Value, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine("Bounded LLM NPC autonomy disabled; the current HTN goal was retained.");
    }
}
