using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.Accusation;

/// <summary>
/// Player-callable crew accusation: accuse &lt;character name&gt;.
/// Living crew only, one accusation per player per shift.
/// </summary>
public sealed class AccuseCommand : IConsoleCommand
{
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "accuse";
    public string Description => "Call a crew accusation vote against a crew member.";
    public string Help => "accuse <character name>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError("Only players aboard the station can accuse.");
            return;
        }

        if (args.Length < 1)
        {
            shell.WriteError(Help);
            return;
        }

        // Caller must be alive and embodied.
        if (player.AttachedEntity is not { Valid: true } caller
            || !_entities.TryGetComponent<MobStateComponent>(caller, out var callerState)
            || callerState.CurrentState == MobState.Dead)
        {
            shell.WriteError(Loc.GetString("mafiastation-accuse-dead"));
            return;
        }

        var targetName = string.Join(" ", args).Trim();

        // Resolve by character name among living, embodied players.
        EntityUid? targetEntity = null;
        string? resolvedName = null;
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } attached)
                continue;

            var name = _entities.GetComponent<MetaDataComponent>(attached).EntityName;
            if (!string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!_entities.TryGetComponent<MobStateComponent>(attached, out var mobState)
                || mobState.CurrentState == MobState.Dead)
                continue;

            targetEntity = attached;
            resolvedName = name;
            break;
        }

        if (targetEntity == null || resolvedName == null)
        {
            shell.WriteError(Loc.GetString("mafiastation-accuse-no-target"));
            return;
        }

        if (targetEntity == caller)
        {
            shell.WriteError(Loc.GetString("mafiastation-accuse-self"));
            return;
        }

        var accusations = _entities.System<AccusationSystem>();
        if (!accusations.TryStartAccusation(player, targetEntity.Value, resolvedName, out var error))
            shell.WriteError(error);
    }
}
