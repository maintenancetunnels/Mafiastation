using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;

namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Counts the round's unexplained phenomena and confesses them — vaguely — on the
/// round-end summary. The station never says what it was. Only how many.
/// </summary>
public sealed class ParanoiaTrackerSystem : EntitySystem
{
    public int WhispersSent;
    public int Sightings;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        WhispersSent = 0;
        Sightings = 0;
    }

    private void OnRoundEndText(RoundEndTextAppendEvent ev)
    {
        if (WhispersSent <= 0 && Sightings <= 0)
            return;

        ev.AddLine(string.Empty);
        ev.AddLine(Loc.GetString("mafiastation-roundend-header"));

        if (WhispersSent > 0)
            ev.AddLine(Loc.GetString("mafiastation-roundend-whispers", ("count", WhispersSent)));

        if (Sightings > 0)
            ev.AddLine(Loc.GetString("mafiastation-roundend-sightings", ("count", Sightings)));
    }
}
