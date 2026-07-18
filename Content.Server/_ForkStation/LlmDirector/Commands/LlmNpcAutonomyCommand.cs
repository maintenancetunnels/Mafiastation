using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

/// <summary>
/// Attaches the low-frequency bounded director to an existing HTN NPC.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcAutonomyCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpcautonomy";
    public string Description =>
        "Configure contextual, allowlist-only LLM executive decisions for an HTN NPC.";
    public string Help =>
        "llmnpcautonomy <netEntityId> <seconds> <goalA,goalB,...> [persona/context]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3 ||
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

        var goals = args[2]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var persona = string.Join(" ", args.Skip(3));
        var autonomy = _systems.GetEntitySystem<LlmNpcAutonomySystem>();
        if (!autonomy.TryConfigureNpc(
                target.Value,
                goals,
                intervalSeconds,
                persona,
                out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine(
            "Bounded LLM NPC autonomy configured. The first decision is due in about one second.");
    }
}
