using Content.Server.Chat.Managers;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.StationEvents.Events;
using Content.Shared.Chat;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// While the event runs, random living crew members receive whispers in their chat
/// that nobody sent. Sometimes the whisper knows their name.
/// </summary>
public sealed class WhisperRule : StationEventSystem<WhisperRuleComponent>
{
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    protected override void ActiveTick(EntityUid uid, WhisperRuleComponent component, GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, component, gameRule, frameTime);

        component.Accumulator += frameTime;
        if (component.Accumulator < component.Interval)
            return;

        component.Accumulator = 0f;

        var candidates = new List<(ICommonSession Session, EntityUid Entity)>();
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } attached)
                continue;

            if (!TryComp<MobStateComponent>(attached, out var mobState)
                || mobState.CurrentState == MobState.Dead)
                continue;

            candidates.Add((session, attached));
        }

        if (candidates.Count == 0)
            return;

        var (victim, entity) = RobustRandom.Pick(candidates);

        string message;
        if (RobustRandom.Prob(component.PersonalizedChance))
        {
            var fullName = Name(entity);
            var firstName = fullName.Split(' ')[0];
            message = Loc.GetString("mafiastation-whisper-name", ("name", firstName));
        }
        else
        {
            message = Loc.GetString(RobustRandom.Pick(component.Lines));
        }

        var wrapped = $"[color=#8a7f9e][italic]{message}[/italic][/color]";
        _chatManager.ChatMessageToOne(ChatChannel.Whisper, message, wrapped,
            source: EntityUid.Invalid, hideChat: false, client: victim.Channel);

        EntityManager.System<ParanoiaTrackerSystem>().WhispersSent++;
    }
}
