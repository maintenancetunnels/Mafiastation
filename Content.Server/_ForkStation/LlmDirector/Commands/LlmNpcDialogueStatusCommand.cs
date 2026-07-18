using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>Reports dialogue gates and bounded per-NPC scheduling state.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcDialogueStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcdialoguestatus";
    public string Description => "Show bounded LLM dialogue state for one NPC.";
    public string Help => "llmnpcdialoguestatus <netEntityId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 ||
            !int.TryParse(args[0], out var netId) ||
            !_entities.TryGetEntity(new NetEntity(netId), out var target))
        {
            shell.WriteError(Help);
            return;
        }

        var status = _systems
            .GetEntitySystem<LlmNpcDialogueSystem>()
            .GetStatus(target.Value);
        shell.WriteLine(
            $"generationEnabled={status.GenerationEnabled}; " +
            $"speechEnabled={status.SpeechEnabled}; configured={status.Configured}; " +
            $"pending={status.RequestPending}; nextDecisionIn={status.NextDecisionIn.TotalSeconds:0}s; " +
            $"speechMemories={status.SpeechMemoryCount}; " +
            $"utteranceMemories={status.UtteranceMemoryCount}");
        if (status.LastOutcome.Length > 0)
            shell.WriteLine($"lastOutcome={status.LastOutcome}");
    }
}
