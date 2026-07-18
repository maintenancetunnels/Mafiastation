using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>
/// Queues one immediate pass through the configured autonomous narrative allowlist.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNarrativeNowCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnarrativenow";
    public string Description =>
        "Run one bounded station-narrative decision using the configured game-rule allowlist.";
    public string Help => "llmnarrativenow";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var narrative = _systems.GetEntitySystem<LlmNarrativeDirectorSystem>();
        if (!narrative.TryRequestNow(out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine("Bounded autonomous station-narrative request queued.");
    }
}
