using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>Schedules an immediate proposal without bypassing any generation or speech gate.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcDialogueNowCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcdialoguenow";
    public string Description => "Schedule an immediate bounded dialogue proposal for one NPC.";
    public string Help => "llmnpcdialoguenow <netEntityId>";

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
        if (!dialogue.TryRequestNow(target.Value, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine(
            "Immediate dialogue proposal scheduled. This does not bypass the IC speech gate.");
    }
}
