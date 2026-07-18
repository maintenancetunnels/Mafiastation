using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>Removes the dialogue layer without changing the NPC's HTN goal.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcDialogueOffCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcdialogueoff";
    public string Description => "Disable contextual LLM dialogue for an HTN NPC.";
    public string Help => "llmnpcdialogueoff <netEntityId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 ||
            !int.TryParse(args[0], out var netId) ||
            !_entities.TryGetEntity(new NetEntity(netId), out var target))
        {
            shell.WriteError(Help);
            return;
        }

        var dialogue = _systems.GetEntitySystem<LlmNpcDialogueSystem>();
        if (!dialogue.TryDisableNpc(target.Value, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine("Bounded LLM NPC dialogue disabled.");
    }
}
