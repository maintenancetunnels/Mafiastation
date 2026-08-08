using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Robust.Shared.Log;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Notifies players of their penalty status when they connect to the server.
/// Players with active penalties see a message in their chat telling them
/// how many rounds they have left to serve.
/// </summary>
public sealed class PenaltyNotificationSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly IChatManager _chat = default!;


    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerJoined);
    }

    private void OnPlayerJoined(PlayerJoinedLobbyEvent ev)
    {
        var userId = ev.PlayerSession.UserId.ToString();
        var totalPenalties = _penalties.GetPenaltyRounds(userId);

        if (totalPenalties <= 0)
            return;

        var dangerLevel = "";
        if (totalPenalties >= PersistentPrisonerSystem.SolitaryThreshold)
            dangerLevel = " You are classified as EXTREMELY DANGEROUS and will spawn in solitary confinement.";
        else if (totalPenalties >= 10)
            dangerLevel = " You are classified as DANGEROUS.";

        var message = $"[CENTCOMM PERSONNEL NOTICE] You have {totalPenalties} outstanding penalty round(s). " +
                      $"You will be assigned to the station permabrig for the duration of this shift.{dangerLevel} " +
                      $"Serve your time peacefully. Good behavior may reduce your sentence. " +
                      $"Type 'mypenalties' in console to view details.";

        _chat.DispatchServerMessage(ev.PlayerSession, message);
        Log.Info($"Notified {ev.PlayerSession.Name} of {totalPenalties} penalty rounds.");
    }
}
