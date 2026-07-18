using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>Configures contextual dialogue proposals for an unpossessed HTN NPC.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcDialogueCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcdialogue";
    public string Description =>
        "Configure bounded contextual LLM dialogue proposals for an unpossessed HTN NPC.";
    public string Help =>
        "llmnpcdialogue <netEntityId> <seconds> [persona/voice guidance]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 ||
            !int.TryParse(args[0], out var netId) ||
            !_entities.TryGetEntity(new NetEntity(netId), out var target) ||
            !float.TryParse(
                args[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var intervalSeconds))
        {
            shell.WriteError(Help);
            return;
        }

        var persona = string.Join(" ", args.Skip(2));
        var dialogue = _systems.GetEntitySystem<LlmNpcDialogueSystem>();
        if (!dialogue.TryConfigureNpc(
                target.Value,
                intervalSeconds,
                persona,
                out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine(
            "Bounded NPC dialogue configured. The initial proposal is scheduled; generated speech " +
            "still requires mafia.director.npc_dialogue_allow_speech.");
    }
}
