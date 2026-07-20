using System.Linq;
using System.Net;
using System.Numerics;
using System.Threading;
using Content.Server.GameTicking;
using Content.Server.NPC.Pathfinding;
using Content.Server.Station.Systems;
using Content.Shared._ForkStation.AiPilot;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.AiPilot;

/// <summary>
/// Server authority boundary for the local AI player-pilot experiment. It never drives a player
/// entity. It authorizes one loopback/allowlisted session, validates lifecycle requests, and
/// returns bounded NPC-navmesh paths that the connected client follows using normal input.
/// </summary>
public sealed class AiPilotServerSystem : EntitySystem
{
    private const float MaximumObservationRadius = 30f;
    private const int MaximumObservedEntities = 128;
    private const float MaximumGoalDistance = 50f;
    private const int MaximumPathWaypoints = 256;
    private const float MaximumGoalTimeoutSeconds = 120f;
    private const int MaximumSpeechCharacters = 500;
    private const float MaximumSpeechCooldownSeconds = 60f;

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly StationSystem _stations = default!;
    [Dependency] private readonly StationJobsSystem _stationJobs = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;

    private readonly Dictionary<NetUserId, Queue<TimeSpan>> _requestTimes = new();
    private static readonly ISawmill Sawmill = Logger.GetSawmill("mafia.ai_pilot.server");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<AiPilotAuthorizationRequestEvent>(OnAuthorizationRequest);
        SubscribeNetworkEvent<AiPilotPathRequestEvent>(OnPathRequest);
        SubscribeNetworkEvent<AiPilotLifecycleRequestEvent>(OnLifecycleRequest);
    }

    private void OnAuthorizationRequest(
        AiPilotAuthorizationRequestEvent message,
        EntitySessionEventArgs args)
    {
        SendAuthorization(args.SenderSession, message.RequestId);
    }

    private async void OnPathRequest(
        AiPilotPathRequestEvent message,
        EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!TryAuthorize(session, out var authorizationError))
        {
            SendPathError(session, message, authorizationError);
            return;
        }

        if (!TryConsumeRequest(session, out var rateError))
        {
            SendPathError(session, message, rateError);
            return;
        }

        if (session.AttachedEntity is not { } actor ||
            !TryComp(actor, out TransformComponent? actorTransform))
        {
            SendPathError(session, message, "Pilot account is not attached to a controllable entity.");
            return;
        }

        EntityCoordinates destination;
        EntityUid? target = null;
        if (message.FinalAction is not (AiPilotGoalAction.Move or
            AiPilotGoalAction.Interact or AiPilotGoalAction.Pickup) ||
            (!message.HasTarget && message.FinalAction != AiPilotGoalAction.Move))
        {
            SendPathError(session, message, "Pilot goal contains an invalid final action.");
            return;
        }

        if (message.HasTarget)
        {
            target = GetEntity(message.Target);
            if (!target.Value.IsValid() ||
                !Exists(target.Value) ||
                !TryComp(target.Value, out TransformComponent? targetTransform))
            {
                SendPathError(session, message, "Pilot goal target no longer exists.");
                return;
            }

            destination = targetTransform.Coordinates;
        }
        else
        {
            destination = GetCoordinates(message.Destination);
        }

        var startMap = _transform.GetMapCoordinates(actor, actorTransform);
        var endMap = _transform.ToMapCoordinates(destination);
        if (startMap.MapId != endMap.MapId ||
            !float.IsFinite(endMap.Position.X) ||
            !float.IsFinite(endMap.Position.Y))
        {
            SendPathError(session, message, "Pilot goal destination is invalid or on another map.");
            return;
        }

        var maximumDistance = GetMaximumGoalDistance();
        if (Vector2.Distance(startMap.Position, endMap.Position) > maximumDistance)
        {
            SendPathError(
                session,
                message,
                $"Pilot goal exceeds the {maximumDistance:0.##}-tile server limit.");
            return;
        }

        if (!float.IsFinite(message.Range) || message.Range is < 0.25f or > 5f)
        {
            SendPathError(session, message, "Pilot goal range must be from 0.25 through 5 tiles.");
            return;
        }

        PathResultEvent result;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            result = await _pathfinding.GetPathSafe(
                actor,
                actorTransform.Coordinates,
                destination,
                message.Range,
                cancellation.Token,
                PathFlags.Interact);
        }
        catch (OperationCanceledException)
        {
            SendPathError(session, message, "Pilot path planning timed out.");
            return;
        }
        catch (Exception exception)
        {
            Sawmill.Error(
                $"Path request {message.RequestId} for {session.Name} failed: " +
                $"{exception.GetType().Name}.");
            SendPathError(session, message, "Pilot path planning failed safely.");
            return;
        }

        if (!TryAuthorize(session, out authorizationError) ||
            session.AttachedEntity != actor ||
            !Exists(actor))
        {
            SendPathError(
                session,
                message,
                string.IsNullOrWhiteSpace(authorizationError)
                    ? "Pilot attachment changed while planning."
                    : authorizationError);
            return;
        }

        if (result.Result != PathResult.Path)
        {
            SendPathError(session, message, "No bounded path was found for the pilot goal.");
            return;
        }

        var simplified = _pathfinding.Simplify(result.Path, 0.01f);
        // A* includes the polygon that already contains the actor. Normal NPC steering prunes
        // that start node; asking a player input loop to walk to its center can instead steer
        // into nearby furniture and falsely stall before the actual route begins.
        if (simplified.Count > 0)
            simplified.RemoveAt(0);

        var maximumWaypoints = GetMaximumPathWaypoints();
        if (simplified.Count > maximumWaypoints)
        {
            SendPathError(
                session,
                message,
                $"Pilot path exceeds the {maximumWaypoints}-waypoint server limit.");
            return;
        }

        var waypoints = simplified
            .Select(poly => GetNetCoordinates(poly.Coordinates))
            .ToList();
        if (waypoints.Count == 0)
            waypoints.Add(GetNetCoordinates(actorTransform.Coordinates));

        RaiseNetworkEvent(
            new AiPilotPathResultEvent(
                message.RequestId,
                true,
                string.Empty,
                waypoints,
                target != null,
                target != null ? GetNetEntity(target.Value) : NetEntity.Invalid,
                message.FinalAction,
                message.Range),
            session.Channel);
    }

    private void OnLifecycleRequest(
        AiPilotLifecycleRequestEvent message,
        EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!TryAuthorize(session, out var error))
        {
            SendLifecycleResult(session, message.RequestId, false, error);
            return;
        }

        if (!_cfg.GetCVar(CCVars.MafiaAiPilotAllowJoin))
        {
            SendLifecycleResult(
                session,
                message.RequestId,
                false,
                "Pilot lobby and joining actions are disabled.");
            return;
        }

        if (!TryConsumeRequest(session, out error))
        {
            SendLifecycleResult(session, message.RequestId, false, error);
            return;
        }

        switch (message.Action)
        {
            case AiPilotLifecycleAction.Ready:
                if (_ticker.RunLevel != GameRunLevel.PreRoundLobby)
                {
                    SendLifecycleResult(
                        session,
                        message.RequestId,
                        false,
                        "Ready state can only change in the pre-round lobby.");
                    return;
                }

                _ticker.ToggleReady(session, message.Ready);
                SendLifecycleResult(session, message.RequestId, true, string.Empty);
                return;

            case AiPilotLifecycleAction.Join:
                TryJoin(session, message.RequestId, message.RequestedJob);
                return;

            default:
                SendLifecycleResult(
                    session,
                    message.RequestId,
                    false,
                    "Unknown pilot lifecycle action.");
                return;
        }
    }

    private void TryJoin(
        ICommonSession session,
        int requestId,
        string requestedJob)
    {
        if (_ticker.RunLevel != GameRunLevel.InRound)
        {
            SendLifecycleResult(
                session,
                requestId,
                false,
                "Pilot late joining is only available during an active round.");
            return;
        }

        var allowedJobs = ParseCsv(_cfg.GetCVar(CCVars.MafiaAiPilotAllowedJobs));
        var jobId = requestedJob?.Trim() ?? string.Empty;
        if (jobId.Length == 0)
            jobId = _cfg.GetCVar(CCVars.MafiaAiPilotDefaultJob).Trim();

        if (jobId.Length is < 1 or > 64 ||
            jobId.Any(char.IsControl) ||
            !allowedJobs.Contains(jobId) ||
            !_prototypes.TryIndex<JobPrototype>(jobId, out var job))
        {
            SendLifecycleResult(
                session,
                requestId,
                false,
                "The requested pilot job is missing or not allowlisted.");
            return;
        }

        if (_ticker.PlayerGameStatuses.TryGetValue(session.UserId, out var status) &&
            status == PlayerGameStatus.JoinedGame)
        {
            if (!TryGetAssignedJob(session, out var assignedJob))
            {
                SendLifecycleResult(
                    session,
                    requestId,
                    false,
                    "Pilot is already attached, but its assigned job could not be verified.");
                return;
            }

            if (!string.Equals(assignedJob, jobId, StringComparison.OrdinalIgnoreCase))
            {
                SendLifecycleResult(
                    session,
                    requestId,
                    false,
                    $"Pilot is already joined as {assignedJob}, not requested job {jobId}.",
                    assignedJob);
                return;
            }

            // A lobby-disabled server may attach the client before its explicit join request is
            // processed. Treat the desired, verified job state as achieved so join is retryable.
            SendLifecycleResult(session, requestId, true, string.Empty, assignedJob);
            return;
        }

        EntityUid? selectedStation = null;
        foreach (var station in _stations.GetStations())
        {
            if (!_stationJobs.TryGetJobSlot(station, job, out var slots) ||
                slots == 0)
            {
                continue;
            }

            selectedStation = station;
            break;
        }

        if (selectedStation == null)
        {
            SendLifecycleResult(
                session,
                requestId,
                false,
                $"No station currently has an available {jobId} slot.");
            return;
        }

        _ticker.MakeJoinGame(session, selectedStation.Value, jobId);
        Sawmill.Info(
            $"Allowlisted local AI pilot account {session.Name} requested late join as {jobId}.");
        SendLifecycleResult(session, requestId, true, string.Empty, jobId);
    }

    private bool TryGetAssignedJob(ICommonSession session, out string jobId)
    {
        jobId = string.Empty;
        if (!_mind.TryGetMind(session.UserId, out var mindId, out _) ||
            !_jobs.MindTryGetJobId(mindId, out var assigned) ||
            assigned == null)
        {
            return false;
        }

        jobId = assigned.Value.Id;
        return true;
    }

    private void SendAuthorization(ICommonSession session, int requestId)
    {
        var authorized = TryAuthorize(session, out var reason);
        RaiseNetworkEvent(
            new AiPilotAuthorizationStateEvent(
                requestId,
                authorized,
                reason,
                authorized && _cfg.GetCVar(CCVars.MafiaAiPilotAllowJoin),
                authorized && _cfg.GetCVar(CCVars.MafiaAiPilotAllowSpeech),
                GetSpeechMaxCharacters(),
                GetSpeechCooldownSeconds(),
                GetObservationRadius(),
                GetMaximumObservedEntities(),
                GetMaximumGoalDistance(),
                GetMaximumPathWaypoints(),
                GetGoalTimeoutSeconds()),
            session.Channel);
    }

    private bool TryAuthorize(ICommonSession session, out string error)
    {
        if (!_cfg.GetCVar(CCVars.MafiaAiPilotServerEnabled))
        {
            error = "AI pilot server gate is disabled.";
            return false;
        }

        if (!IPAddress.IsLoopback(session.Channel.RemoteEndPoint.Address))
        {
            error = "AI pilot sessions must connect over loopback.";
            return false;
        }

        var allowlist = ParseCsv(_cfg.GetCVar(CCVars.MafiaAiPilotAllowedAccounts));
        if (!IsAllowlistedAccount(session, allowlist))
        {
            error = "AI pilot account is not allowlisted.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsAllowlistedAccount(
        ICommonSession session,
        IReadOnlySet<string> allowlist)
    {
        if (allowlist.Contains(session.Name) ||
            allowlist.Contains(session.UserId.ToString()))
        {
            return true;
        }

        // Robust's optional-auth mode prefixes unauthenticated loopback names with
        // "localhost@". The endpoint check above has already proven this is loopback, so permit
        // the configured local username without also accepting the broader "guest@" alias.
        const string localPrefix = "localhost@";
        return session.Name.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase) &&
               allowlist.Contains(session.Name[localPrefix.Length..]);
    }

    private bool TryConsumeRequest(ICommonSession session, out string error)
    {
        var maximum = Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaAiPilotServerRequestsPerSecond),
            1,
            20);
        var now = _timing.CurTime;
        if (!_requestTimes.TryGetValue(session.UserId, out var requests))
        {
            requests = new Queue<TimeSpan>();
            _requestTimes[session.UserId] = requests;
        }

        var oldest = now - TimeSpan.FromSeconds(1);
        while (requests.TryPeek(out var timestamp) && timestamp <= oldest)
            requests.Dequeue();

        if (requests.Count >= maximum)
        {
            error = "AI pilot server request rate exceeded.";
            return false;
        }

        requests.Enqueue(now);
        error = string.Empty;
        return true;
    }

    private void SendPathError(
        ICommonSession session,
        AiPilotPathRequestEvent request,
        string error)
    {
        RaiseNetworkEvent(
            new AiPilotPathResultEvent(
                request.RequestId,
                false,
                error,
                new List<NetCoordinates>(),
                false,
                NetEntity.Invalid,
                request.FinalAction,
                request.Range),
            session.Channel);
    }

    private void SendLifecycleResult(
        ICommonSession session,
        int requestId,
        bool accepted,
        string error,
        string assignedJob = "")
    {
        RaiseNetworkEvent(
            new AiPilotLifecycleResultEvent(requestId, accepted, error, assignedJob),
            session.Channel);
    }

    private HashSet<string> ParseCsv(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private float GetObservationRadius()
    {
        var value = _cfg.GetCVar(CCVars.MafiaAiPilotObservationRadius);
        return float.IsFinite(value) ? Math.Clamp(value, 1f, MaximumObservationRadius) : 1f;
    }

    private int GetSpeechMaxCharacters()
    {
        return Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaAiPilotSpeechMaxCharacters),
            1,
            MaximumSpeechCharacters);
    }

    private float GetSpeechCooldownSeconds()
    {
        var value = _cfg.GetCVar(CCVars.MafiaAiPilotSpeechCooldownSeconds);
        return float.IsFinite(value)
            ? Math.Clamp(value, 0.25f, MaximumSpeechCooldownSeconds)
            : MaximumSpeechCooldownSeconds;
    }

    private int GetMaximumObservedEntities()
    {
        return Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaAiPilotMaximumObservedEntities),
            1,
            MaximumObservedEntities);
    }

    private float GetMaximumGoalDistance()
    {
        var value = _cfg.GetCVar(CCVars.MafiaAiPilotMaximumGoalDistance);
        return float.IsFinite(value) ? Math.Clamp(value, 1f, MaximumGoalDistance) : 1f;
    }

    private int GetMaximumPathWaypoints()
    {
        return Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaAiPilotMaximumPathWaypoints),
            4,
            MaximumPathWaypoints);
    }

    private float GetGoalTimeoutSeconds()
    {
        var value = _cfg.GetCVar(CCVars.MafiaAiPilotGoalTimeoutSeconds);
        return float.IsFinite(value)
            ? Math.Clamp(value, 5f, MaximumGoalTimeoutSeconds)
            : 5f;
    }
}
