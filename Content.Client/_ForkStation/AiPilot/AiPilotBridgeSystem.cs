using System.Collections.Concurrent;
using System.Threading.Tasks;
using Content.Shared._ForkStation.AiPilot;
using Content.Shared.CCVar;
using Robust.Client.Mafiastation.AiPilot;
using Robust.Client.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Client._ForkStation.AiPilot;

/// <summary>
/// Sandboxed content half of the local AI pilot. A narrow preloaded helper owns pipe/JSON access;
/// this system performs only simulation-thread observation, capacity checks, and ordinary input.
/// </summary>
public sealed partial class AiPilotBridgeSystem : EntitySystem
{
    private const int MaximumPipeRequestsPerSecond = 20;
    private static readonly TimeSpan AuthorizationRefresh = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AuthorizationFreshness = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LifecycleResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BridgeStartRetry = TimeSpan.FromSeconds(1);

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;

    private static readonly ISawmill Sawmill = Logger.GetSawmill("mafia.ai_pilot.client");
    private readonly AiPilotAuthorizationSnapshot _authorization = new();
    private readonly Queue<TimeSpan> _pipeRequestTimes = new();
    private readonly ConcurrentDictionary<int, PendingLifecycleRequest> _pendingLifecycle = new();

    private string _pipeName = string.Empty;
    private TimeSpan _nextAuthorizationRequest;
    private TimeSpan _nextBridgeStartAttempt;
    private TimeSpan _nextSpeechAt;
    private int _networkRequestId;

    public override void Initialize()
    {
        base.Initialize();
        InitializeActions();
        SubscribeNetworkEvent<AiPilotAuthorizationStateEvent>(OnAuthorizationState);
        SubscribeNetworkEvent<AiPilotPathResultEvent>(OnPathResult);
        SubscribeNetworkEvent<AiPilotLifecycleResultEvent>(OnLifecycleResult);
        Subs.CVar(_cfg, CCVars.MafiaAiPilotClientEnabled, OnClientEnabledChanged, true);
        Subs.CVar(_cfg, CCVars.MafiaAiPilotPipeName, OnPipeNameChanged, true);
    }

    public override void Shutdown()
    {
        StopBridge("AI pilot bridge shut down.");
        ShutdownActions();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Pipe/network/input side effects must run only on the first prediction. Dispatching a
        // new input while Robust is replaying its pending-input queue mutates that queue during
        // enumeration and is not how ordinary player controls enter the simulation.
        if (!_timing.IsFirstTimePredicted ||
            !_cfg.GetCVar(CCVars.MafiaAiPilotClientEnabled))
        {
            return;
        }

        var now = _timing.CurTime;
        if (!AiPilotPipeBridge.IsRunning && now >= _nextBridgeStartAttempt)
            StartBridge();

        if (_players.LocalSession != null && now >= _nextAuthorizationRequest)
        {
            _nextAuthorizationRequest = now + AuthorizationRefresh;
            RaiseNetworkEvent(new AiPilotAuthorizationRequestEvent(NextNetworkRequestId()));
        }

        ExpireLifecycleRequests(now);
        UpdateActions(frameTime);
    }

    private void OnClientEnabledChanged(bool enabled)
    {
        if (enabled)
        {
            _nextBridgeStartAttempt = TimeSpan.Zero;
            StartBridge();
            _nextAuthorizationRequest = TimeSpan.Zero;
            return;
        }

        StopBridge("AI pilot client gate was disabled.");
    }

    private void OnPipeNameChanged(string pipeName)
    {
        if (!_cfg.GetCVar(CCVars.MafiaAiPilotClientEnabled) ||
            string.Equals(pipeName.Trim(), _pipeName, StringComparison.Ordinal))
        {
            return;
        }

        StopBridge("AI pilot pipe name changed.");
        _nextBridgeStartAttempt = TimeSpan.Zero;
    }

    private void StartBridge()
    {
        _nextBridgeStartAttempt = _timing.CurTime + BridgeStartRetry;
        if (AiPilotPipeBridge.IsRunning)
            return;

        if (!AiPilotPipeBridge.IsProcessAuthorized)
        {
            Sawmill.Error(
                "AI pilot client CVar was enabled without the trusted headless-loopback " +
                "launcher authorization.");
            return;
        }

        var pipeName = _cfg.GetCVar(CCVars.MafiaAiPilotPipeName).Trim();
        if (!AiPilotPipeBridge.Start(pipeName, DispatchToMainThreadAsync, out var error))
        {
            if (!error.Contains("still stopping", StringComparison.Ordinal))
                Sawmill.Warning(error);
            return;
        }

        _pipeName = pipeName;
        Sawmill.Info($"AI pilot bridge listening on current-user pipe '{pipeName}'.");
    }

    private void StopBridge(string reason)
    {
        AiPilotPipeBridge.Stop();
        _pipeName = string.Empty;
        _authorization.Authorized = false;
        _authorization.Reason = reason;
        _authorization.AllowJoin = false;
        _authorization.AllowSpeech = false;
        _authorization.UpdatedAt = TimeSpan.Zero;

        CancelAllActions(reason);
        foreach (var (networkId, pending) in _pendingLifecycle.ToArray())
        {
            if (!_pendingLifecycle.TryRemove(networkId, out _))
                continue;
            pending.Completion.TrySetResult(
                AiPilotPipeResponse.Failure(pending.PipeRequestId, reason));
        }
    }

    private Task<AiPilotPipeResponse> DispatchToMainThreadAsync(AiPilotPipeRequest request)
    {
        var completion = new TaskCompletionSource<AiPilotPipeResponse>();
        _taskManager.RunOnMainThread(() =>
        {
            try
            {
                _ = CompletePipeResponseAsync(
                    HandlePipeRequestAsync(request),
                    request.Id,
                    completion);
            }
            catch (Exception exception)
            {
                completion.TrySetResult(
                    AiPilotPipeResponse.Failure(
                        request.Id,
                        $"Pilot action failed safely ({exception.GetType().Name})."));
            }
        });
        return completion.Task;
    }

    private static async Task CompletePipeResponseAsync(
        Task<AiPilotPipeResponse> responseTask,
        string requestId,
        TaskCompletionSource<AiPilotPipeResponse> completion)
    {
        try
        {
            completion.TrySetResult(await responseTask);
        }
        catch (Exception exception)
        {
            completion.TrySetResult(
                AiPilotPipeResponse.Failure(
                    requestId,
                    $"Pilot action failed safely ({exception.GetType().Name})."));
        }
    }

    private Task<AiPilotPipeResponse> RequestLifecycleAsync(
        AiPilotPipeRequest request,
        AiPilotLifecycleAction action,
        bool ready,
        string requestedJob)
    {
        var networkId = NextNetworkRequestId();
        var completion = new TaskCompletionSource<AiPilotPipeResponse>();
        _pendingLifecycle[networkId] = new PendingLifecycleRequest(
            request.Id,
            _timing.CurTime + LifecycleResponseTimeout,
            completion);
        RaiseNetworkEvent(
            new AiPilotLifecycleRequestEvent(networkId, action, ready, requestedJob));
        return completion.Task;
    }

    private void ExpireLifecycleRequests(TimeSpan now)
    {
        foreach (var (networkId, pending) in _pendingLifecycle.ToArray())
        {
            if (pending.Deadline > now ||
                !_pendingLifecycle.TryRemove(networkId, out _))
            {
                continue;
            }

            pending.Completion.TrySetResult(
                AiPilotPipeResponse.Failure(
                    pending.PipeRequestId,
                    "Pilot lifecycle request timed out."));
        }
    }

    private void OnAuthorizationState(AiPilotAuthorizationStateEvent message)
    {
        _authorization.Authorized = message.Authorized;
        _authorization.Reason = message.Reason;
        _authorization.AllowJoin = message.AllowJoin;
        _authorization.AllowSpeech = message.AllowSpeech;
        _authorization.SpeechMaxCharacters = message.SpeechMaxCharacters;
        _authorization.SpeechCooldownSeconds = message.SpeechCooldownSeconds;
        _authorization.ObservationRadius = message.ObservationRadius;
        _authorization.MaximumObservedEntities = message.MaximumObservedEntities;
        _authorization.MaximumGoalDistance = message.MaximumGoalDistance;
        _authorization.MaximumPathWaypoints = message.MaximumPathWaypoints;
        _authorization.GoalTimeoutSeconds = message.GoalTimeoutSeconds;
        _authorization.UpdatedAt = _timing.CurTime;

        if (!message.Authorized)
            CancelAllActions(message.Reason);
    }

    private void OnLifecycleResult(AiPilotLifecycleResultEvent message)
    {
        if (!_pendingLifecycle.TryRemove(message.RequestId, out var pending))
            return;

        pending.Completion.TrySetResult(
            message.Accepted
                ? AiPilotPipeResponse.Success(
                    pending.PipeRequestId,
                    new
                    {
                        accepted = true,
                        assignedJob = message.AssignedJob.Length > 0
                            ? message.AssignedJob
                            : null,
                    })
                : AiPilotPipeResponse.Failure(pending.PipeRequestId, message.Error));
    }

    private bool IsAuthorizationFresh()
    {
        return _authorization.Authorized &&
               _authorization.UpdatedAt != TimeSpan.Zero &&
               _timing.CurTime - _authorization.UpdatedAt <= AuthorizationFreshness;
    }

    private bool TryConsumePipeRequest(out string error)
    {
        var now = _timing.CurTime;
        var oldest = now - TimeSpan.FromSeconds(1);
        while (_pipeRequestTimes.TryPeek(out var timestamp) && timestamp <= oldest)
            _pipeRequestTimes.Dequeue();

        if (_pipeRequestTimes.Count >= MaximumPipeRequestsPerSecond)
        {
            error = "Pilot pipe request rate exceeded.";
            return false;
        }

        _pipeRequestTimes.Enqueue(now);
        error = string.Empty;
        return true;
    }

    private int NextNetworkRequestId()
    {
        if (_networkRequestId == int.MaxValue)
            _networkRequestId = 0;
        return ++_networkRequestId;
    }

    partial void InitializeActions();
    partial void ShutdownActions();
    partial void UpdateActions(float frameTime);
    partial void CancelAllActions(string reason);
    partial void OnPathResult(AiPilotPathResultEvent message);
    private partial Task<AiPilotPipeResponse> HandlePipeRequestAsync(AiPilotPipeRequest request);

    private sealed record PendingLifecycleRequest(
        string PipeRequestId,
        TimeSpan Deadline,
        TaskCompletionSource<AiPilotPipeResponse> Completion);
}
