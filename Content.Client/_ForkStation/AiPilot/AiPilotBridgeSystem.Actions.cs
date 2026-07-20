using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Client.Chat.Managers;
using Content.Client.GameTicking.Managers;
using Content.Client.UserInterface.Systems.Chat;
using Content.Shared._ForkStation.AiPilot;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Input;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.Mafiastation.AiPilot;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._ForkStation.AiPilot;

public sealed partial class AiPilotBridgeSystem
{
    private const float TargetLeaseSeconds = 6f;
    private const float GoalWaypointTolerance = 0.35f;
    private const float GoalProgressEpsilon = 0.03f;
    private const float GoalStallSeconds = 4f;
    private const int MaximumRecentSpeech = 16;
    private const int MaximumObservedSpeechCharacters = 300;
    private static readonly TimeSpan ObservedSpeechMemory = TimeSpan.FromMinutes(3);

    [Dependency] private readonly IInputManager _inputManager = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly ClientGameTicker _ticker = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    private InputSystem _input = default!;
    private readonly Dictionary<int, ObservedTargetLease> _observedTargets = new();
    private readonly Dictionary<EntityUid, int> _observedEntityIds = new();
    private readonly HashSet<BoundKeyFunction> _heldMovement = new();
    private readonly Queue<PilotObservedSpeech> _recentSpeech = new();
    private ChatUIController? _chatController;
    private EntityUid? _speechOwner;
    private ActivePilotGoal? _goal;
    private TimeSpan _manualMoveDeadline;
    private int _nextObservedId;

    partial void InitializeActions()
    {
        _input = EntityManager.System<InputSystem>();
        _chatController = _ui.GetUIController<ChatUIController>();
        _chatController.MessageAdded += OnChatMessage;
    }

    partial void ShutdownActions()
    {
        if (_chatController != null)
            _chatController.MessageAdded -= OnChatMessage;
        _chatController = null;
        CancelAllActions("AI pilot bridge shut down.");
        _observedTargets.Clear();
        _observedEntityIds.Clear();
    }

    partial void UpdateActions(float frameTime)
    {
        var now = _timing.CurTime;
        if (_manualMoveDeadline != TimeSpan.Zero && now >= _manualMoveDeadline)
        {
            _manualMoveDeadline = TimeSpan.Zero;
            SetHeldMovement(Array.Empty<BoundKeyFunction>());
        }

        if (_goal?.State != AiPilotGoalState.Moving)
            return;

        UpdateGoalMovement(_goal, now);
    }

    partial void CancelAllActions(string reason)
    {
        _manualMoveDeadline = TimeSpan.Zero;
        SetHeldMovement(Array.Empty<BoundKeyFunction>());
        if (_goal?.State is AiPilotGoalState.Planning or AiPilotGoalState.Moving)
        {
            _goal.State = AiPilotGoalState.Cancelled;
            _goal.Error = reason;
        }
        ClearSpeechMemory();
    }

    private void OnChatMessage(ChatMessage message)
    {
        if (!_cfg.GetCVar(CCVars.MafiaAiPilotClientEnabled) ||
            message.HideChat ||
            message.Channel is not (ChatChannel.Local or ChatChannel.Whisper or ChatChannel.Radio) ||
            _players.LocalEntity is not { } controlled)
        {
            return;
        }

        EnsureSpeechOwner(controlled);
        var sender = GetEntity(message.SenderEntity);
        if (sender == controlled)
            return;

        var text = NormalizeObservedSpeech(message.Message);
        if (text.Length == 0)
            return;

        var speaker = sender.IsValid() && Exists(sender)
            ? Name(sender)
            : message.Channel == ChatChannel.Radio
                ? "radio speaker"
                : "unknown speaker";
        while (_recentSpeech.Count >= MaximumRecentSpeech)
            _recentSpeech.Dequeue();
        _recentSpeech.Enqueue(new PilotObservedSpeech(
            _timing.CurTime,
            speaker,
            text,
            message.Channel.ToString().ToLowerInvariant()));
    }

    partial void OnPathResult(AiPilotPathResultEvent message)
    {
        if (_goal == null ||
            _goal.RequestId != message.RequestId ||
            _goal.State != AiPilotGoalState.Planning)
        {
            return;
        }

        if (!message.Accepted)
        {
            FailGoal(_goal, message.Error);
            return;
        }

        if (message.Waypoints.Count == 0 ||
            message.Waypoints.Count > _authorization.MaximumPathWaypoints)
        {
            FailGoal(_goal, "Server returned an invalid pilot waypoint count.");
            return;
        }

        var waypoints = new List<EntityCoordinates>(message.Waypoints.Count);
        foreach (var waypoint in message.Waypoints)
        {
            var coordinates = GetCoordinates(waypoint);
            if (!coordinates.IsValid(EntityManager))
            {
                FailGoal(_goal, "Server returned an invalid pilot waypoint.");
                return;
            }
            waypoints.Add(coordinates);
        }

        _goal.Waypoints = waypoints;
        _goal.WaypointIndex = 0;
        _goal.Target = message.HasTarget ? GetEntity(message.Target) : null;
        _goal.FinalAction = message.FinalAction;
        _goal.Range = message.Range;
        _goal.State = AiPilotGoalState.Moving;
        _goal.LastProgressAt = _timing.CurTime;
        _goal.BestWaypointDistance = float.PositiveInfinity;
    }

    private partial Task<AiPilotPipeResponse> HandlePipeRequestAsync(AiPilotPipeRequest request)
    {
        if (!_cfg.GetCVar(CCVars.MafiaAiPilotClientEnabled))
        {
            return Task.FromResult(
                AiPilotPipeResponse.Failure(request.Id, "AI pilot client gate is disabled."));
        }

        if (request.Action != "stop" && !TryConsumePipeRequest(out var rateError))
        {
            return Task.FromResult(AiPilotPipeResponse.Failure(request.Id, rateError));
        }

        switch (request.Action)
        {
            case "status":
                return Task.FromResult(AiPilotPipeResponse.Success(request.Id, BuildStatus()));
            case "observe":
                return Task.FromResult(Observe(request));
            case "goal_status":
                return Task.FromResult(
                    AiPilotPipeResponse.Success(request.Id, BuildGoalStatus()));
            case "stop":
                CancelAllActions("Pilot stop requested.");
                return Task.FromResult(
                    AiPilotPipeResponse.Success(
                        request.Id,
                        new { accepted = true, state = GoalStateName(_goal?.State ?? AiPilotGoalState.None) }));
        }

        if (!IsAuthorizationFresh())
        {
            return Task.FromResult(
                AiPilotPipeResponse.Failure(
                    request.Id,
                    string.IsNullOrWhiteSpace(_authorization.Reason)
                        ? "AI pilot server authorization is pending or stale."
                        : _authorization.Reason));
        }

        return request.Action switch
        {
            "move" => Task.FromResult(StartManualMove(request)),
            "interact" => Task.FromResult(PerformTargetAction(request, pickup: false)),
            "pickup" => Task.FromResult(PerformTargetAction(request, pickup: true)),
            "drop" => Task.FromResult(PerformDrop(request)),
            "swap_hands" => Task.FromResult(PerformSwapHands(request)),
            "goal" => Task.FromResult(StartGoal(request)),
            "ready" => RequestReadyAsync(request),
            "join" => RequestJoinAsync(request),
            "say" => Task.FromResult(PerformSpeech(request)),
            _ => Task.FromResult(
                AiPilotPipeResponse.Failure(
                    request.Id,
                    $"Pilot action '{request.Action}' is not supported.")),
        };
    }

    private object BuildStatus()
    {
        var capabilities = BuildCapabilities();
        return new
        {
            enabled = _cfg.GetCVar(CCVars.MafiaAiPilotClientEnabled),
            pipeRunning = AiPilotPipeBridge.IsRunning,
            pipe = _pipeName,
            connected = _players.LocalSession != null,
            attached = _players.LocalEntity != null,
            authorized = IsAuthorizationFresh(),
            authorizationReason = _authorization.Reason,
            allowJoin = _authorization.AllowJoin,
            allowSpeech =
                _authorization.AllowSpeech &&
                _cfg.GetCVar(CCVars.MafiaAiPilotClientAllowSpeech),
            gameStarted = _ticker.IsGameStarted,
            capabilities,
            goal = BuildGoalStatus(),
        };
    }

    private AiPilotPipeResponse Observe(AiPilotPipeRequest request)
    {
        if (_players.LocalEntity is not { } controlled ||
            !TryComp(controlled, out TransformComponent? controlledTransform))
        {
            ClearSpeechMemory();
            return AiPilotPipeResponse.Success(
                request.Id,
                new
                {
                    attached = false,
                    authorized = IsAuthorizationFresh(),
                    capabilities = BuildCapabilities(),
                    goal = BuildGoalStatus(),
                    entities = Array.Empty<object>(),
                    recentSpeech = Array.Empty<object>(),
                });
        }

        EnsureSpeechOwner(controlled);
        PruneObservedSpeech();
        var selfMap = _transform.GetMapCoordinates(controlled, controlledTransform);
        var radius = Math.Clamp(_authorization.ObservationRadius, 1f, 30f);
        var maximum = Math.Clamp(_authorization.MaximumObservedEntities, 1, 128);
        var candidates = new List<ObservedCandidate>();
        var query = AllEntityQuery<TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var transform, out _))
        {
            // Offer top-level world entities on the controlled character's grid. Inventory,
            // organs, actions, and other transform descendants share the actor's map position
            // but are not independently visible world targets and would crowd out useful input.
            if (uid == controlled ||
                transform.MapID != selfMap.MapId ||
                transform.ParentUid != controlledTransform.ParentUid)
            {
                continue;
            }

            var map = _transform.GetMapCoordinates(uid, transform);
            var distance = Vector2.Distance(selfMap.Position, map.Position);
            if (!float.IsFinite(distance) || distance > radius)
                continue;

            candidates.Add(new ObservedCandidate(uid, map.Position, distance));
        }

        candidates.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        PruneObservedTargets();
        var entities = candidates
            .Take(maximum)
            .Select(candidate =>
            {
                var id = LeaseObservedTarget(candidate.Uid);
                return new
                {
                    id,
                    name = Name(candidate.Uid),
                    kind = HasComp<MobStateComponent>(candidate.Uid)
                        ? "character"
                        : HasComp<ItemComponent>(candidate.Uid)
                            ? "item"
                            : "object",
                    condition = GetMobCondition(candidate.Uid),
                    position = new
                    {
                        x = candidate.Position.X,
                        y = candidate.Position.Y,
                    },
                    relative = new
                    {
                        x = candidate.Position.X - selfMap.Position.X,
                        y = candidate.Position.Y - selfMap.Position.Y,
                    },
                    distance = candidate.Distance,
                    hints = new
                    {
                        interact = true,
                        pickup = HasComp<ItemComponent>(candidate.Uid),
                    },
                };
            })
            .ToArray();

        string? activeHand = null;
        string? activeItem = null;
        if (TryComp(controlled, out HandsComponent? hands))
        {
            activeHand = hands.ActiveHandId;
            if (_hands.GetActiveItem((controlled, hands)) is { } item)
                activeItem = Name(item);
        }

        return AiPilotPipeResponse.Success(
            request.Id,
            new
            {
                attached = true,
                authorized = IsAuthorizationFresh(),
                self = new
                {
                    position = new
                    {
                        x = selfMap.Position.X,
                        y = selfMap.Position.Y,
                    },
                    map = (int) selfMap.MapId,
                    name = Name(controlled),
                    activeHand,
                    activeItem,
                    condition = GetMobCondition(controlled),
                },
                capabilities = BuildCapabilities(),
                goal = BuildGoalStatus(),
                entities,
                recentSpeech = _recentSpeech.Select(memory => new
                {
                    ageSeconds = (int) Math.Clamp(
                        (_timing.CurTime - memory.ObservedAt).TotalSeconds,
                        0,
                        int.MaxValue),
                    memory.Speaker,
                    memory.Message,
                    memory.Channel,
                }).ToArray(),
            });
    }

    private void EnsureSpeechOwner(EntityUid controlled)
    {
        if (_speechOwner == controlled)
            return;

        _recentSpeech.Clear();
        _speechOwner = controlled;
    }

    private string? GetMobCondition(EntityUid uid)
    {
        if (!TryComp<MobStateComponent>(uid, out var state))
            return null;
        if (_mobState.IsAlive(uid, state))
            return "alive";
        if (_mobState.IsCritical(uid, state))
            return "critical";
        if (_mobState.IsDead(uid, state))
            return "dead";
        return "unknown";
    }

    private void ClearSpeechMemory()
    {
        _recentSpeech.Clear();
        _speechOwner = null;
    }

    private void PruneObservedSpeech()
    {
        var oldest = _timing.CurTime - ObservedSpeechMemory;
        while (_recentSpeech.TryPeek(out var speech) && speech.ObservedAt < oldest)
            _recentSpeech.Dequeue();
    }

    private static string NormalizeObservedSpeech(string message)
    {
        var normalized = string.Join(
            " ",
            message.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumObservedSpeechCharacters
            ? normalized
            : normalized[..MaximumObservedSpeechCharacters];
    }

    private object BuildCapabilities()
    {
        var authorized = IsAuthorizationFresh();
        var controlled = _players.LocalEntity;
        var attached = controlled != null && Exists(controlled.Value);
        var canMove =
            authorized &&
            attached &&
            _actionBlocker.CanMove(controlled!.Value);
        var canInteract =
            authorized &&
            attached &&
            _actionBlocker.CanInteract(controlled!.Value, null);

        var canPickup = false;
        var canDrop = false;
        var canSwapHands = false;
        if (attached && TryComp(controlled!.Value, out HandsComponent? hands))
        {
            canPickup = canInteract && _hands.GetActiveItem((controlled.Value, hands)) == null;
            canDrop = canInteract && !canPickup;
            canSwapHands = canInteract && hands.Hands.Count > 1;
        }

        return new
        {
            canMove,
            canInteract,
            canPickup,
            canDrop,
            canSwapHands,
            canUseGoals = canMove,
            canObserve = attached,
            canJoin = authorized && _authorization.AllowJoin,
            canSpeak =
                authorized &&
                attached &&
                _actionBlocker.CanSpeak(controlled!.Value) &&
                _authorization.AllowSpeech &&
                _cfg.GetCVar(CCVars.MafiaAiPilotClientAllowSpeech),
        };
    }

    private AiPilotPipeResponse StartManualMove(AiPilotPipeRequest request)
    {
        if (_players.LocalEntity == null || !_actionBlocker.CanMove(_players.LocalEntity.Value))
            return AiPilotPipeResponse.Failure(request.Id, "Pilot character cannot currently move.");

        if (!TryReadString(request.Arguments, "direction", out var direction) ||
            !TryGetDirectionFunctions(direction, out var functions))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Move direction must be north, south, east, west, or a diagonal.");
        }

        var duration = 250;
        if (request.Arguments.Contains("durationMs") &&
            !request.Arguments.TryGetInt32("durationMs", out duration))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Move durationMs must be an integer.");
        }
        duration = Math.Clamp(duration, 50, 1000);

        CancelActiveGoal("Manual pilot movement replaced the deterministic goal.");
        SetHeldMovement(functions);
        _manualMoveDeadline = _timing.CurTime + TimeSpan.FromMilliseconds(duration);
        return AiPilotPipeResponse.Success(
            request.Id,
            new { accepted = true, direction = direction.ToLowerInvariant(), durationMs = duration });
    }

    private AiPilotPipeResponse PerformTargetAction(
        AiPilotPipeRequest request,
        bool pickup)
    {
        if (!TryResolveObservedTarget(request.Arguments, out var target, out var error))
            return AiPilotPipeResponse.Failure(request.Id, error);

        if (!TryPerformTargetAction(target, pickup, out error))
            return AiPilotPipeResponse.Failure(request.Id, error);

        request.Arguments.TryGetInt32("targetId", out var targetId);
        return AiPilotPipeResponse.Success(
            request.Id,
            new { accepted = true, targetId });
    }

    private bool TryPerformTargetAction(EntityUid target, bool pickup, out string error)
    {
        if (_players.LocalEntity is not { } controlled ||
            !TryComp(controlled, out TransformComponent? controlledTransform) ||
            !TryComp(target, out TransformComponent? targetTransform))
        {
            error = "Pilot character or target is unavailable.";
            return false;
        }

        var controlledMap = _transform.GetMapCoordinates(controlled, controlledTransform);
        var targetMap = _transform.GetMapCoordinates(target, targetTransform);
        if (controlledMap.MapId != targetMap.MapId ||
            Vector2.Distance(controlledMap.Position, targetMap.Position) >
            SharedInteractionSystem.InteractionRange + 0.25f)
        {
            error = "Pilot target is outside normal interaction range.";
            return false;
        }

        if (!_actionBlocker.CanInteract(controlled, target))
        {
            error = "Pilot character cannot currently interact with that target.";
            return false;
        }

        if (pickup)
        {
            if (!HasComp<ItemComponent>(target))
            {
                error = "Pilot pickup target is not an item.";
                return false;
            }

            if (!TryComp(controlled, out HandsComponent? hands) ||
                _hands.GetActiveItem((controlled, hands)) != null)
            {
                error = "Pilot pickup requires an empty active hand.";
                return false;
            }
        }

        SendOneShotInput(EngineKeyFunctions.Use, targetTransform.Coordinates, target);
        error = string.Empty;
        return true;
    }

    private AiPilotPipeResponse PerformDrop(AiPilotPipeRequest request)
    {
        if (_players.LocalEntity is not { } controlled ||
            !TryComp(controlled, out TransformComponent? transform) ||
            !TryComp(controlled, out HandsComponent? hands) ||
            !_actionBlocker.CanInteract(controlled, null) ||
            _hands.GetActiveItem((controlled, hands)) == null)
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot active hand has no item to drop.");
        }

        SendOneShotInput(ContentKeyFunctions.Drop, transform.Coordinates, EntityUid.Invalid);
        return AiPilotPipeResponse.Success(request.Id, new { accepted = true });
    }

    private AiPilotPipeResponse PerformSwapHands(AiPilotPipeRequest request)
    {
        if (_players.LocalEntity is not { } controlled ||
            !TryComp(controlled, out TransformComponent? transform) ||
            !TryComp(controlled, out HandsComponent? hands) ||
            !_actionBlocker.CanInteract(controlled, null) ||
            hands.Hands.Count < 2)
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot character does not have multiple usable hands.");
        }

        SendOneShotInput(
            ContentKeyFunctions.SwapHands,
            transform.Coordinates,
            EntityUid.Invalid);
        return AiPilotPipeResponse.Success(request.Id, new { accepted = true });
    }

    private AiPilotPipeResponse StartGoal(AiPilotPipeRequest request)
    {
        if (_players.LocalEntity is not { } controlled ||
            !TryComp(controlled, out TransformComponent? controlledTransform) ||
            !_actionBlocker.CanMove(controlled))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot character cannot currently start a movement goal.");
        }

        if (!TryReadString(request.Arguments, "kind", out var kind))
            return AiPilotPipeResponse.Failure(request.Id, "Pilot goal kind is required.");
        kind = kind.ToLowerInvariant();

        if (kind == "interact" &&
            !_actionBlocker.CanInteract(controlled, null))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot character cannot currently start an interaction goal.");
        }

        if (kind == "pickup" &&
            (!_actionBlocker.CanInteract(controlled, null) ||
             !TryComp(controlled, out HandsComponent? pickupHands) ||
             _hands.GetActiveItem((controlled, pickupHands)) != null))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot pickup goal requires current interaction capacity and an empty active hand.");
        }

        var range = 1.25f;
        if (request.Arguments.Contains("range") &&
            (!request.Arguments.TryGetSingle("range", out range) || !float.IsFinite(range)))
        {
            return AiPilotPipeResponse.Failure(request.Id, "Pilot goal range must be finite.");
        }
        if (range is < 0.25f or > 5f)
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot goal range must be from 0.25 through 5 tiles.");

        var selfMap = _transform.GetMapCoordinates(controlled, controlledTransform);
        if (controlledTransform.GridUid is not { } controlledGrid ||
            !TryComp(controlledGrid, out TransformComponent? controlledGridTransform))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot character is not on a navigable grid.");
        }

        EntityCoordinates destination;
        EntityUid? target = null;
        var finalAction = AiPilotGoalAction.Move;
        switch (kind)
        {
            case "move_relative":
                if (!TryReadFinite(request.Arguments, "x", out var deltaX) ||
                    !TryReadFinite(request.Arguments, "y", out var deltaY))
                {
                    return AiPilotPipeResponse.Failure(
                        request.Id,
                        "Relative pilot goal requires finite x and y offsets.");
                }
                destination = _transform.ToCoordinates(
                    (controlledGrid, controlledGridTransform),
                    new MapCoordinates(selfMap.Position + new Vector2(deltaX, deltaY), selfMap.MapId));
                break;

            case "move_to":
                if (!TryReadFinite(request.Arguments, "x", out var x) ||
                    !TryReadFinite(request.Arguments, "y", out var y))
                {
                    return AiPilotPipeResponse.Failure(
                        request.Id,
                        "Coordinate pilot goal requires finite x and y values.");
                }
                destination = _transform.ToCoordinates(
                    (controlledGrid, controlledGridTransform),
                    new MapCoordinates(new Vector2(x, y), selfMap.MapId));
                break;

            case "move_to_entity":
            case "interact":
            case "pickup":
                if (!TryResolveObservedTarget(request.Arguments, out var resolved, out var error) ||
                    !TryComp(resolved, out TransformComponent? targetTransform))
                {
                    return AiPilotPipeResponse.Failure(
                        request.Id,
                        string.IsNullOrWhiteSpace(error)
                            ? "Pilot goal target is unavailable."
                            : error);
                }
                target = resolved;
                destination = targetTransform.Coordinates;
                finalAction = kind switch
                {
                    "interact" => AiPilotGoalAction.Interact,
                    "pickup" => AiPilotGoalAction.Pickup,
                    _ => AiPilotGoalAction.Move,
                };
                break;

            default:
                return AiPilotPipeResponse.Failure(
                    request.Id,
                    "Pilot goal kind must be move_relative, move_to, move_to_entity, interact, or pickup.");
        }

        var destinationMap = _transform.ToMapCoordinates(destination);
        var distance = Vector2.Distance(selfMap.Position, destinationMap.Position);
        if (!float.IsFinite(distance) || distance > _authorization.MaximumGoalDistance)
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                $"Pilot goal exceeds the {_authorization.MaximumGoalDistance:0.##}-tile limit.");
        }

        CancelActiveGoal("A newer pilot goal replaced this goal.");
        _manualMoveDeadline = TimeSpan.Zero;
        SetHeldMovement(Array.Empty<BoundKeyFunction>());
        var networkId = NextNetworkRequestId();
        _goal = new ActivePilotGoal
        {
            RequestId = networkId,
            Kind = kind,
            State = AiPilotGoalState.Planning,
            Destination = destination,
            Target = target,
            FinalAction = finalAction,
            Range = range,
            StartedAt = _timing.CurTime,
            Deadline =
                _timing.CurTime +
                TimeSpan.FromSeconds(Math.Clamp(_authorization.GoalTimeoutSeconds, 5f, 120f)),
            LastProgressAt = _timing.CurTime,
        };

        RaiseNetworkEvent(
            new AiPilotPathRequestEvent(
                networkId,
                GetNetCoordinates(destination),
                target != null,
                target != null ? GetNetEntity(target.Value) : NetEntity.Invalid,
                finalAction,
                range));
        return AiPilotPipeResponse.Success(
            request.Id,
            new { accepted = true, state = "planning", kind });
    }

    private Task<AiPilotPipeResponse> RequestReadyAsync(AiPilotPipeRequest request)
    {
        if (!_authorization.AllowJoin)
            return Task.FromResult(
                AiPilotPipeResponse.Failure(
                    request.Id,
                    "Pilot ready/join gate is disabled."));

        var ready = true;
        if (request.Arguments.Contains("ready"))
        {
            if (!request.Arguments.TryGetBoolean("ready", out ready))
            {
                return Task.FromResult(
                    AiPilotPipeResponse.Failure(request.Id, "Pilot ready value must be boolean."));
            }
        }
        return RequestLifecycleAsync(
            request,
            AiPilotLifecycleAction.Ready,
            ready,
            requestedJob: string.Empty);
    }

    private Task<AiPilotPipeResponse> RequestJoinAsync(AiPilotPipeRequest request)
    {
        if (!_authorization.AllowJoin)
            return Task.FromResult(
                AiPilotPipeResponse.Failure(
                    request.Id,
                    "Pilot ready/join gate is disabled."));

        var requestedJob = string.Empty;
        if (request.Arguments.Contains("job") &&
            (!TryReadString(request.Arguments, "job", out requestedJob) ||
             requestedJob.Length > 64 ||
             requestedJob.Any(char.IsControl)))
        {
            return Task.FromResult(
                AiPilotPipeResponse.Failure(
                    request.Id,
                    "Pilot job must be a non-empty ID of at most 64 characters."));
        }

        return RequestLifecycleAsync(
            request,
            AiPilotLifecycleAction.Join,
            ready: false,
            requestedJob: requestedJob);
    }

    private AiPilotPipeResponse PerformSpeech(AiPilotPipeRequest request)
    {
        if (!_authorization.AllowSpeech ||
            !_cfg.GetCVar(CCVars.MafiaAiPilotClientAllowSpeech))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot speech requires both client and server speech gates.");
        }

        if (_players.LocalEntity is not { } controlled ||
            !_actionBlocker.CanSpeak(controlled))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot character cannot currently speak.");
        }

        if (_timing.CurTime < _nextSpeechAt)
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                "Pilot speech is still on cooldown.");
        }

        if (!TryReadString(request.Arguments, "text", out var text))
            return AiPilotPipeResponse.Failure(request.Id, "Pilot say requires text.");
        text = text.Trim();
        var maximum = Math.Clamp(_authorization.SpeechMaxCharacters, 1, 500);
        if (text.Length is < 1 || text.Length > maximum ||
            text.StartsWith('/') ||
            text.Any(char.IsControl))
        {
            return AiPilotPipeResponse.Failure(
                request.Id,
                $"Pilot speech must contain 1-{maximum} non-control characters and cannot begin with '/'.");
        }

        _chat.SendMessage(text, ChatSelectChannel.Local);
        _nextSpeechAt =
            _timing.CurTime +
            TimeSpan.FromSeconds(
                Math.Clamp(_authorization.SpeechCooldownSeconds, 0.25f, 60f));
        return AiPilotPipeResponse.Success(request.Id, new { accepted = true });
    }

    private void UpdateGoalMovement(ActivePilotGoal goal, TimeSpan now)
    {
        if (now >= goal.Deadline)
        {
            FailGoal(goal, "Pilot deterministic goal timed out.");
            return;
        }

        if (_players.LocalEntity is not { } controlled ||
            !TryComp(controlled, out TransformComponent? controlledTransform) ||
            !_actionBlocker.CanMove(controlled))
        {
            FailGoal(goal, "Pilot character lost movement capacity.");
            return;
        }

        var controlledMap = _transform.GetMapCoordinates(controlled, controlledTransform);
        while (goal.WaypointIndex < goal.Waypoints.Count)
        {
            var waypointMap = _transform.ToMapCoordinates(goal.Waypoints[goal.WaypointIndex]);
            if (waypointMap.MapId != controlledMap.MapId)
            {
                FailGoal(goal, "Pilot path changed maps unexpectedly.");
                return;
            }

            var distance = Vector2.Distance(controlledMap.Position, waypointMap.Position);
            if (distance <= GoalWaypointTolerance)
            {
                goal.WaypointIndex++;
                goal.BestWaypointDistance = float.PositiveInfinity;
                goal.LastProgressAt = now;
                continue;
            }

            if (distance + GoalProgressEpsilon < goal.BestWaypointDistance)
            {
                goal.BestWaypointDistance = distance;
                goal.LastProgressAt = now;
            }
            else if (now - goal.LastProgressAt >= TimeSpan.FromSeconds(GoalStallSeconds))
            {
                FailGoal(goal, "Pilot deterministic goal stalled.");
                return;
            }

            var delta = waypointMap.Position - controlledMap.Position;
            if (_cfg.GetCVar(CCVars.RelativeMovement) &&
                TryComp(controlled, out InputMoverComponent? mover))
            {
                delta = (-_mover.GetParentGridAngle(mover)).RotateVec(delta);
            }

            SetHeldMovement(DirectionFunctions(delta));
            return;
        }

        SetHeldMovement(Array.Empty<BoundKeyFunction>());
        if (goal.FinalAction is AiPilotGoalAction.Interact or AiPilotGoalAction.Pickup)
        {
            var finalActionError = string.Empty;
            if (goal.Target == null ||
                !Exists(goal.Target.Value) ||
                !TryPerformTargetAction(
                    goal.Target.Value,
                    goal.FinalAction == AiPilotGoalAction.Pickup,
                    out finalActionError))
            {
                FailGoal(
                    goal,
                    string.IsNullOrWhiteSpace(finalActionError)
                        ? "Pilot goal target disappeared before its final action."
                        : finalActionError);
                return;
            }
        }

        goal.State = AiPilotGoalState.Completed;
        goal.Error = string.Empty;
    }

    private object BuildGoalStatus()
    {
        if (_goal == null)
        {
            return new
            {
                state = "none",
                goalComplete = false,
            };
        }

        float? positionX = null;
        float? positionY = null;
        float? waypointX = null;
        float? waypointY = null;
        float? waypointDistance = null;
        if (_players.LocalEntity is { } controlled &&
            TryComp(controlled, out TransformComponent? controlledTransform))
        {
            var controlledMap = _transform.GetMapCoordinates(controlled, controlledTransform);
            positionX = controlledMap.Position.X;
            positionY = controlledMap.Position.Y;

            if (_goal.WaypointIndex < _goal.Waypoints.Count)
            {
                var waypointMap = _transform.ToMapCoordinates(
                    _goal.Waypoints[_goal.WaypointIndex]);
                if (waypointMap.MapId == controlledMap.MapId)
                {
                    waypointX = waypointMap.Position.X;
                    waypointY = waypointMap.Position.Y;
                    waypointDistance = Vector2.Distance(
                        controlledMap.Position,
                        waypointMap.Position);
                }
            }
        }

        return new
        {
            state = GoalStateName(_goal.State),
            kind = _goal.Kind,
            goalComplete = _goal.State == AiPilotGoalState.Completed,
            error = string.IsNullOrWhiteSpace(_goal.Error) ? null : _goal.Error,
            waypoint = _goal.WaypointIndex,
            waypoints = _goal.Waypoints.Count,
            elapsedMs = Math.Max(0, (_timing.CurTime - _goal.StartedAt).TotalMilliseconds),
            position = positionX != null
                ? new { x = positionX.Value, y = positionY!.Value }
                : null,
            waypointPosition = waypointX != null
                ? new { x = waypointX.Value, y = waypointY!.Value }
                : null,
            waypointDistance,
        };
    }

    private void FailGoal(ActivePilotGoal goal, string error)
    {
        SetHeldMovement(Array.Empty<BoundKeyFunction>());
        goal.State = AiPilotGoalState.Failed;
        goal.Error = error;
    }

    private void CancelActiveGoal(string reason)
    {
        if (_goal?.State is not (AiPilotGoalState.Planning or AiPilotGoalState.Moving))
            return;
        _goal.State = AiPilotGoalState.Cancelled;
        _goal.Error = reason;
    }

    private void SendOneShotInput(
        BoundKeyFunction function,
        EntityCoordinates coordinates,
        EntityUid target)
    {
        SendInput(function, BoundKeyState.Down, coordinates, target);
        SendInput(function, BoundKeyState.Up, coordinates, target);
    }

    private void SendInput(
        BoundKeyFunction function,
        BoundKeyState state,
        EntityCoordinates coordinates,
        EntityUid target)
    {
        if (_players.LocalSession == null)
            return;

        var functionId = _inputManager.NetworkBindMap.KeyFunctionID(function);
        var message = new ClientFullInputCmdMessage(
            _timing.CurTick,
            _timing.TickFraction,
            functionId,
            coordinates,
            new ScreenCoordinates(0, 0, default),
            state,
            target);
        _input.HandleInputCommand(_players.LocalSession, function, message);
    }

    private void SetHeldMovement(IEnumerable<BoundKeyFunction> requested)
    {
        var next = requested.ToHashSet();
        if (_players.LocalEntity is not { } controlled ||
            !TryComp(controlled, out TransformComponent? transform))
        {
            foreach (var function in _heldMovement)
            {
                SendInput(
                    function,
                    BoundKeyState.Up,
                    EntityCoordinates.Invalid,
                    EntityUid.Invalid);
            }
            _heldMovement.Clear();
            return;
        }

        foreach (var function in _heldMovement.Except(next).ToArray())
        {
            SendInput(
                function,
                BoundKeyState.Up,
                transform.Coordinates,
                EntityUid.Invalid);
            _heldMovement.Remove(function);
        }

        foreach (var function in next.Except(_heldMovement))
        {
            SendInput(
                function,
                BoundKeyState.Down,
                transform.Coordinates,
                EntityUid.Invalid);
            _heldMovement.Add(function);
        }
    }

    private static IEnumerable<BoundKeyFunction> DirectionFunctions(Vector2 delta)
    {
        if (delta.X > 0.12f)
            yield return EngineKeyFunctions.MoveRight;
        else if (delta.X < -0.12f)
            yield return EngineKeyFunctions.MoveLeft;

        if (delta.Y > 0.12f)
            yield return EngineKeyFunctions.MoveUp;
        else if (delta.Y < -0.12f)
            yield return EngineKeyFunctions.MoveDown;
    }

    private static bool TryGetDirectionFunctions(
        string direction,
        out IReadOnlyList<BoundKeyFunction> functions)
    {
        functions = direction.ToLowerInvariant() switch
        {
            "north" => new[] { EngineKeyFunctions.MoveUp },
            "south" => new[] { EngineKeyFunctions.MoveDown },
            "east" => new[] { EngineKeyFunctions.MoveRight },
            "west" => new[] { EngineKeyFunctions.MoveLeft },
            "northeast" => new[] { EngineKeyFunctions.MoveUp, EngineKeyFunctions.MoveRight },
            "northwest" => new[] { EngineKeyFunctions.MoveUp, EngineKeyFunctions.MoveLeft },
            "southeast" => new[] { EngineKeyFunctions.MoveDown, EngineKeyFunctions.MoveRight },
            "southwest" => new[] { EngineKeyFunctions.MoveDown, EngineKeyFunctions.MoveLeft },
            _ => Array.Empty<BoundKeyFunction>(),
        };
        return functions.Count > 0;
    }

    private bool TryResolveObservedTarget(
        AiPilotPipeArguments arguments,
        out EntityUid target,
        out string error)
    {
        target = EntityUid.Invalid;
        PruneObservedTargets();
        if (!arguments.TryGetInt32("targetId", out var targetId))
        {
            error = "Pilot target action requires an integer targetId.";
            return false;
        }

        if (!_observedTargets.TryGetValue(targetId, out var lease) ||
            lease.ExpiresAt < _timing.CurTime ||
            !Exists(lease.Entity))
        {
            error = $"Pilot target {targetId} is not in the recent bounded observation.";
            return false;
        }

        target = lease.Entity;
        error = string.Empty;
        return true;
    }

    private int LeaseObservedTarget(EntityUid entity)
    {
        if (_observedEntityIds.TryGetValue(entity, out var existing) &&
            _observedTargets.TryGetValue(existing, out var lease))
        {
            lease.ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(TargetLeaseSeconds);
            return existing;
        }

        if (_nextObservedId == int.MaxValue)
            _nextObservedId = 0;
        var id = ++_nextObservedId;
        var created = new ObservedTargetLease(
            entity,
            _timing.CurTime + TimeSpan.FromSeconds(TargetLeaseSeconds));
        _observedTargets[id] = created;
        _observedEntityIds[entity] = id;
        return id;
    }

    private void PruneObservedTargets()
    {
        var now = _timing.CurTime;
        foreach (var (id, lease) in _observedTargets.ToArray())
        {
            if (lease.ExpiresAt >= now && Exists(lease.Entity))
                continue;
            _observedTargets.Remove(id);
            _observedEntityIds.Remove(lease.Entity);
        }
    }

    private static bool TryReadString(
        AiPilotPipeArguments arguments,
        string property,
        out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetString(property, out value))
            return false;

        value = value.Trim();
        return value.Length > 0;
    }

    private static bool TryReadFinite(
        AiPilotPipeArguments arguments,
        string property,
        out float value)
    {
        value = default;
        return arguments.TryGetSingle(property, out value) &&
               float.IsFinite(value);
    }

    private static string GoalStateName(AiPilotGoalState state)
    {
        return state switch
        {
            AiPilotGoalState.None => "none",
            AiPilotGoalState.Planning => "planning",
            AiPilotGoalState.Moving => "moving",
            AiPilotGoalState.Completed => "completed",
            AiPilotGoalState.Failed => "failed",
            AiPilotGoalState.Cancelled => "cancelled",
            _ => "failed",
        };
    }

    private sealed record ObservedCandidate(
        EntityUid Uid,
        Vector2 Position,
        float Distance);

    private sealed record PilotObservedSpeech(
        TimeSpan ObservedAt,
        string Speaker,
        string Message,
        string Channel);

    private sealed class ObservedTargetLease(EntityUid entity, TimeSpan expiresAt)
    {
        public EntityUid Entity { get; } = entity;
        public TimeSpan ExpiresAt = expiresAt;
    }

    private sealed class ActivePilotGoal
    {
        public int RequestId;
        public string Kind = string.Empty;
        public AiPilotGoalState State;
        public string Error = string.Empty;
        public EntityCoordinates Destination;
        public EntityUid? Target;
        public AiPilotGoalAction FinalAction;
        public float Range;
        public TimeSpan StartedAt;
        public TimeSpan Deadline;
        public TimeSpan LastProgressAt;
        public float BestWaypointDistance = float.PositiveInfinity;
        public List<EntityCoordinates> Waypoints = new();
        public int WaypointIndex;
    }
}
