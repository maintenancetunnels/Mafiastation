using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcSceneStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcscenestatus";
    public string Description => "Show configured bounded LLM NPC scenes.";
    public string Help => "llmnpcscenestatus [sceneId]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }

        var statuses = _systems
            .GetEntitySystem<LlmNpcSceneSystem>()
            .GetStatuses(args.Length == 1 ? args[0] : null);
        if (statuses.Count == 0)
        {
            shell.WriteLine("No matching bounded LLM NPC scenes are configured.");
            return;
        }

        foreach (var status in statuses)
        {
            shell.WriteLine(
                $"{status.Id}: members={status.MemberCount}; beats={status.BeatCount}; " +
                $"pending={status.RequestPending}; " +
                $"nextDecisionIn={status.NextDecisionIn.TotalSeconds:0}s; " +
                $"lastBeat={status.LastBeat ?? "none"}");
            if (status.LastError.Length > 0)
                shell.WriteLine($"  lastError={status.LastError}");
        }
    }
}
