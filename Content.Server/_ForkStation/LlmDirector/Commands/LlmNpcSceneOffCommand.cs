using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcSceneOffCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcsceneoff";
    public string Description => "Disable and remove one bounded LLM NPC scene.";
    public string Help => "llmnpcsceneoff <sceneId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var scenes = _systems.GetEntitySystem<LlmNpcSceneSystem>();
        if (!scenes.TryDisableScene(args[0], shell.Player?.UserId, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine($"Bounded LLM NPC scene '{args[0]}' disabled.");
    }
}
