using System.Linq;
using Content.Shared._ForkStation.PersistentPrisoner;
using Content.Shared.Access.Systems;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
// PrisonerDesignRules
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Player;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Server system for the Penalty Management Console.
/// Handles BUI events and sends state to clients.
/// </summary>
public sealed class PenaltyConsoleSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedRoleSystem _roles = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<PenaltyConsoleComponent>(PenaltyConsoleKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<PenaltyConsoleSelectPlayer>(OnSelectPlayer);
            subs.Event<PenaltyConsoleAddPenalty>(OnAddPenalty);
            subs.Event<PenaltyConsoleUndoPenalty>(OnUndoPenalty);
        });
    }

    private void OnOpened(Entity<PenaltyConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnSelectPlayer(Entity<PenaltyConsoleComponent> ent, ref PenaltyConsoleSelectPlayer msg)
    {
        if (!_access.IsAllowed(msg.Actor, ent))
            return;

        ent.Comp.SelectedPlayer = msg.PlayerName;
        UpdateUi(ent);
    }

    private void OnAddPenalty(Entity<PenaltyConsoleComponent> ent, ref PenaltyConsoleAddPenalty msg)
    {
        if (!_access.IsAllowed(msg.Actor, ent))
            return;

        // Validate
        if (msg.Rounds < 1 || msg.Rounds > 5)
            return;

        var reason = msg.Reason.Trim();
        if (reason.Length < 1 || reason.Length > ent.Comp.MaxReasonLength)
            return;

        // Find the target player
        ICommonSession? targetSession = null;
        foreach (var session in _playerManager.Sessions)
        {
            if (string.Equals(session.Name, msg.PlayerName, StringComparison.OrdinalIgnoreCase))
            {
                targetSession = session;
                break;
            }
        }

        if (targetSession == null)
            return;

        // Get the issuer's name
        var issuerName = "Unknown";
        if (Exists(msg.Actor))
        {
            var meta = MetaData(msg.Actor);
            issuerName = meta.EntityName;
        }

        // Antags cannot accumulate; refresh UI without naming the reason (looks like a no-op refresh).
        var isAntag = _mind.TryGetMind(targetSession.UserId, out var targetMindId, out _) &&
                       _roles.MindIsAntagonist(targetMindId);
        if (PrisonerDesignRules.ShouldSilentlySkipAntagPenalty(isAntag))
        {
            UpdateUi(ent);
            return;
        }

        _penalties.AddPenalty(
            targetSession.UserId.ToString(),
            msg.Actor.ToString(),
            issuerName,
            msg.Rounds,
            reason,
            adminIssued: false);

        UpdateUi(ent);
    }

    private void OnUndoPenalty(Entity<PenaltyConsoleComponent> ent, ref PenaltyConsoleUndoPenalty msg)
    {
        if (!_access.IsAllowed(msg.Actor, ent))
            return;

        // Same policy as secpenaltyundo: only non-admin, same-round penalties.
        if (!_penalties.CanSecurityUndo(msg.PenaltyId, out _))
            return;

        _penalties.RemovePenalty(msg.PenaltyId);
        UpdateUi(ent);
    }

    private void UpdateUi(Entity<PenaltyConsoleComponent> ent)
    {
        // Build player list: online players + offline players with active penalties
        var players = new Dictionary<string, PenaltyPlayerEntry>();

        // Add all online players — show pending+outstanding so mid-round issues are visible.
        foreach (var session in _playerManager.Sessions)
        {
            var userId = session.UserId.ToString();
            var totalPenalties = _penalties.GetPendingPlusOutstandingRounds(userId);
            var jobTitle = "Unknown";

            if (session.AttachedEntity is { Valid: true } attached
                && _mind.TryGetMind(attached, out var mindId, out _)
                && _jobs.MindTryGetJobName(mindId, out var name))
            {
                jobTitle = name;
            }

            players[session.Name] = new PenaltyPlayerEntry(userId, totalPenalties, jobTitle);
        }

        // Add offline players who have confirmed outstanding (force-spawn balance).
        var allPenalized = _penalties.GetPenaltySummary();
        var onlineUserIds = new HashSet<string>();
        foreach (var entry in players.Values)
            onlineUserIds.Add(entry.UserId);

        foreach (var (userId, rounds) in allPenalized)
        {
            if (onlineUserIds.Contains(userId))
                continue;

            // Show offline penalized players with a marker
            var displayName = $"[OFFLINE] {userId[..8]}...";
            players[displayName] = new PenaltyPlayerEntry(userId, rounds, "Offline");
        }

        // Get penalties for selected player (includes pending records).
        List<PenaltyDisplayRecord>? selectedPenalties = null;
        if (ent.Comp.SelectedPlayer != null && players.TryGetValue(ent.Comp.SelectedPlayer, out var selectedEntry))
        {
            var records = _penalties.GetAllPenalties(selectedEntry.UserId);
            selectedPenalties = records.Select(r => new PenaltyDisplayRecord(
                r.Id, r.RoundsAssigned, r.RoundsServed,
                r.Reason, r.IssuedByName, r.AdminIssued, r.IssuedAt
            )).ToList();
        }

        var state = new PenaltyConsoleState(players, ent.Comp.SelectedPlayer, selectedPenalties);
        _ui.SetUiState(ent.Owner, PenaltyConsoleKey.Key, state);
    }
}
