using Content.Server.Chat.Managers;
using Content.Shared.Chat;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Whoever carries a <see cref="CursedCassetteComponent"/> item occasionally hears
/// whispers in their chat. The tape is always mid-song. There is no song.
/// </summary>
public sealed class CursedCassetteSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly string[] Lines =
    {
        "mafiastation-whisper-1",
        "mafiastation-whisper-2",
        "mafiastation-whisper-3",
        "mafiastation-whisper-4",
        "mafiastation-whisper-5",
    };

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var tracker = EntityManager.System<ParanoiaTrackerSystem>();

        var query = EntityQueryEnumerator<CursedCassetteComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var cassette, out var xform))
        {
            if (_timing.CurTime < cassette.NextWhisper)
                continue;

            cassette.NextWhisper = _timing.CurTime
                + TimeSpan.FromSeconds(_random.NextFloat(cassette.MinInterval, cassette.MaxInterval));

            // Whisper only while someone is carrying it (held or in inventory, the item is
            // parented to the mob).
            if (xform.ParentUid is not { Valid: true } holder)
                continue;

            if (!TryComp<ActorComponent>(holder, out var actor))
                continue;

            var message = Loc.GetString(_random.Pick(Lines));
            var wrapped = $"[color=#8a7f9e][italic]{message}[/italic][/color]";
            _chatManager.ChatMessageToOne(ChatChannel.Whisper, message, wrapped,
                source: EntityUid.Invalid, hideChat: false, client: actor.PlayerSession.Channel);

            tracker.WhispersSent++;
        }
    }
}
