using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>
/// Reports gates and scheduling state without exposing endpoint credentials or prompt contents.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNarrativeStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnarrativestatus";
    public string Description => "Show bounded autonomous station-narrative scheduling state.";
    public string Help => "llmnarrativestatus";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var status = _systems
            .GetEntitySystem<LlmNarrativeDirectorSystem>()
            .GetStatus();
        shell.WriteLine(
            $"enabled={status.Enabled}; pending={status.RequestPending}; " +
            $"automaticStartGate={status.AutomaticEventStartEnabled}; " +
            $"configuredRules={status.ConfiguredRuleCount}; " +
            $"rememberedChoices={status.RememberedChoiceCount}; " +
            $"nextDecisionIn={status.NextDecisionIn.TotalSeconds:0}s");
        if (status.LastError.Length > 0)
            shell.WriteLine($"lastError={status.LastError}");
    }
}
