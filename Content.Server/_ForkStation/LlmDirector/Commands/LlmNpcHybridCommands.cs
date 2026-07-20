using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._ForkStation.LlmDirector.Commands;

[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcHybridCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpchybrid";
    public string Description =>
        "Configure routine HTN behavior with capability-filtered LLM escalation.";
    public string Help =>
        "llmnpchybrid <netEntityId> <routineRoot> <cooldownSeconds> <leaseSeconds> " +
        "<Move+Interact+Hands+...> " +
        "<choice=HTNRoot@Move+Interact,choice2=HTNRoot@Move> [persona]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 6 ||
            !int.TryParse(args[0], out var netId) ||
            !_entities.TryGetEntity(new NetEntity(netId), out var target) ||
            !float.TryParse(
                args[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var cooldown) ||
            !float.TryParse(
                args[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var lease) ||
            !TryParseCapabilities(args[4], out var capabilities) ||
            !TryParseGoals(args[5], out var goals))
        {
            shell.WriteError(Help);
            return;
        }

        var persona = string.Join(" ", args.Skip(6));
        var hybrid = _systems.GetEntitySystem<LlmNpcHybridSystem>();
        if (!hybrid.TryConfigureNpc(
                target.Value,
                args[1],
                capabilities,
                goals,
                persona,
                cooldown,
                lease,
                out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine(
            "Hybrid NPC configured: routine HTN is active and model calls occur only on escalation.");
    }

    private static bool TryParseCapabilities(
        string raw,
        out IReadOnlyCollection<HybridNpcCapability> capabilities)
    {
        var parsed = new HashSet<HybridNpcCapability>();
        foreach (var token in raw.Split(
                     '+',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<HybridNpcCapability>(
                    token,
                    ignoreCase: true,
                    out var capability))
            {
                capabilities = Array.Empty<HybridNpcCapability>();
                return false;
            }
            parsed.Add(capability);
        }

        capabilities = parsed;
        return parsed.Count > 0;
    }

    private static bool TryParseGoals(
        string raw,
        out IReadOnlyList<HybridNpcComplexGoal> goals)
    {
        var parsed = new List<HybridNpcComplexGoal>();
        foreach (var token in raw.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var mapping = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (mapping.Length != 2 || mapping.Any(part => part.Length == 0))
            {
                goals = Array.Empty<HybridNpcComplexGoal>();
                return false;
            }

            var taskAndCapabilities = mapping[1].Split(
                '@',
                2,
                StringSplitOptions.TrimEntries);
            if (taskAndCapabilities[0].Length == 0)
            {
                goals = Array.Empty<HybridNpcComplexGoal>();
                return false;
            }

            var required = new HashSet<HybridNpcCapability>();
            if (taskAndCapabilities.Length == 2)
            {
                if (!TryParseCapabilities(taskAndCapabilities[1], out var requiredParsed))
                {
                    goals = Array.Empty<HybridNpcComplexGoal>();
                    return false;
                }

                required.UnionWith(requiredParsed);
            }

            parsed.Add(new HybridNpcComplexGoal
            {
                Id = mapping[0],
                Task = taskAndCapabilities[0],
                Description =
                    $"Use server-authored HTN root '{taskAndCapabilities[0]}' when it best " +
                    "fits the escalation context.",
                RequiredCapabilities = required,
            });
        }

        goals = parsed;
        return parsed.Count >= 2;
    }
}

[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcHybridStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpchybridstatus";
    public string Description => "Show routine/complex state for one hybrid NPC.";
    public string Help => "llmnpchybridstatus <netEntityId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryTarget(args, _entities, out var target))
        {
            shell.WriteError(Help);
            return;
        }

        var status = _systems.GetEntitySystem<LlmNpcHybridSystem>().GetStatus(target);
        if (!status.Configured)
        {
            shell.WriteError("Target entity does not have hybrid NPC control configured.");
            return;
        }

        shell.WriteLine(
            $"routine={status.RoutineTask}; current={status.CurrentTask}; " +
            $"complexActive={status.ComplexGoalActive}; pending={status.PendingDecision}; " +
            $"outcome={status.LastOutcome}");
    }

    internal static bool TryTarget(
        string[] args,
        IEntityManager entities,
        out EntityUid target)
    {
        target = EntityUid.Invalid;
        if (args.Length != 1 ||
            !int.TryParse(args[0], out var netId) ||
            !entities.TryGetEntity(new NetEntity(netId), out var resolved))
        {
            return false;
        }

        target = resolved.Value;
        return true;
    }
}

[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcHybridEscalateCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpchybridescalate";
    public string Description => "Request one immediate bounded complex decision for a hybrid NPC.";
    public string Help => "llmnpchybridescalate <netEntityId> [reason]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 ||
            !int.TryParse(args[0], out var netId) ||
            !_entities.TryGetEntity(new NetEntity(netId), out var target))
        {
            shell.WriteError(Help);
            return;
        }

        var reason = string.Join(" ", args.Skip(1));
        if (!_systems.GetEntitySystem<LlmNpcHybridSystem>()
                .TryEscalateNow(target.Value, reason, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine("Hybrid NPC complex decision requested.");
    }
}

[AdminCommand(AdminFlags.Fun)]
public sealed class LlmNpcHybridOffCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Command => "llmnpchybridoff";
    public string Description =>
        "Disable hybrid supervision and leave the NPC on its routine HTN root.";
    public string Help => "llmnpchybridoff <netEntityId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LlmNpcHybridStatusCommand.TryTarget(args, _entities, out var target))
        {
            shell.WriteError(Help);
            return;
        }

        if (!_systems.GetEntitySystem<LlmNpcHybridSystem>()
                .TryDisableNpc(target, out var error))
        {
            shell.WriteError(error);
            return;
        }

        shell.WriteLine("Hybrid NPC disabled; routine HTN root retained.");
    }
}
