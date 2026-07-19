using Content.Server.Chat.Systems;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.StationEvents.Events;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// The announcer lies. Plays one official-sounding station announcement that is
/// subtly wrong — a passenger manifest of one, a crew census that counts one extra,
/// an apology for an announcement that never happened.
/// </summary>
public sealed class PhantomAnnouncementRule : StationEventSystem<PhantomAnnouncementRuleComponent>
{
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    protected override void Started(EntityUid uid, PhantomAnnouncementRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var chosenStation))
            return;

        string message;
        // Roughly one in three phantom announcements is the crew census that counts one extra.
        if (component.Lines.Count == 0 || RobustRandom.Prob(0.35f))
        {
            var alive = 0;
            foreach (var session in _players.Sessions)
            {
                if (session.AttachedEntity is { Valid: true } attached
                    && TryComp<MobStateComponent>(attached, out var mobState)
                    && mobState.CurrentState != MobState.Dead)
                {
                    alive++;
                }
            }

            message = Loc.GetString("mafiastation-phantom-crewcount", ("count", alive + 1));
        }
        else
        {
            message = Loc.GetString(RobustRandom.Pick(component.Lines));
        }

        _chat.DispatchStationAnnouncement(chosenStation.Value, message,
            sender: Loc.GetString("mafiastation-phantom-sender"), playDefaultSound: true);
    }
}
