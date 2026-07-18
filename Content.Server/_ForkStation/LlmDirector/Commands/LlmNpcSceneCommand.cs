using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>
/// Configures a recurring, coordinated scene for a small cast of existing HTN NPCs.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcSceneCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcscene";
    public string Description =>
        "Configure bounded recurring scene beats for a cast of existing HTN NPCs.";
    public string Help =>
        "llmnpcscene <sceneId> <seconds> <netIdA,netIdB,...> " +
        "<beatA:goalA|goalB;beatB:goalC|goalD> [premise]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 4 ||
            !float.TryParse(
                args[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var intervalSeconds))
        {
            shell.WriteError(Help);
            return;
        }

        var members = new List<EntityUid>();
        foreach (var rawId in args[2].Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(rawId, out var netId) ||
                !_entities.TryGetEntity(new NetEntity(netId), out var entity))
            {
                shell.WriteError($"Unknown net entity ID '{rawId}'.");
                return;
            }

            members.Add(entity.Value);
        }

        var premise = string.Join(" ", args.Skip(4));
        var scenes = _systems.GetEntitySystem<LlmNpcSceneSystem>();
        if (!scenes.TryConfigureScene(
                args[0],
                intervalSeconds,
                members,
                args[3],
                premise,
                shell.Player?.UserId,
                out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine(
            "Bounded LLM NPC scene configured. It will begin when " +
            "mafia.director.npc_scenes_enabled is true.");
    }
}
