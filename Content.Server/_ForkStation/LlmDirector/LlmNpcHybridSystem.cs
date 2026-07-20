using System.Linq;
using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.NPC.HTN;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Hands.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// Supervises ordinary HTN NPCs without replacing their planner. Routine roots consume no model
/// calls. Missing plans, authored speech context, or an explicit admin request may escalate to one
/// bounded model choice among currently-capable complex roots.
/// </summary>
public sealed class LlmNpcHybridSystem : EntitySystem
{
    private const int MaximumComplexGoals = 24;
    private const int MaximumPersonaCharacters = 800;
    private const int MaximumDescriptionCharacters = 300;
    private const float MaximumObservationRadius = 30f;
    private const int MaximumSpeechMemories = 12;

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly LlmGameplayDirectorSystem _director = default!;
    [Dependency] private readonly IAdminLogManager _adminLogs = default!;

    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LlmNpcHybridComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<LlmNpcHybridComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);
        Subs.CVar(_cfg, CCVars.MafiaDirectorNpcHybridEnabled, OnHybridEnabledChanged, true);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;
        _nextUpdate = now + TimeSpan.FromSeconds(0.5);

        var enabled = _cfg.GetCVar(CCVars.MafiaDirectorNpcHybridEnabled);
        var query = EntityQueryEnumerator<LlmNpcHybridComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var hybrid, out var htn))
        {
            if (HasComp<ActorComponent>(uid))
            {
                SuspendForPlayer(uid, hybrid, htn);
                continue;
            }

            ResumeAfterPlayer(uid, hybrid, htn);

            PruneSpeech(hybrid, now);
            if (!enabled)
            {
                ReturnToRoutine(uid, hybrid, htn, "Hybrid LLM escalation is disabled.");
                continue;
            }

            if (now < hybrid.NextEvaluation)
                continue;
            hybrid.NextEvaluation =
                now +
                TimeSpan.FromSeconds(
                    ClampFinite(hybrid.EvaluationIntervalSeconds, 0.25f, 10f, 1f));

            if (!TryValidateConfiguration(hybrid, out var validationError))
            {
                hybrid.LastOutcome = validationError;
                continue;
            }

            var available = GetAvailableCapabilities(uid, hybrid);
            if (hybrid.ComplexGoalActive)
            {
                var active = hybrid.ComplexGoals.FirstOrDefault(
                    goal => goal.Id == hybrid.ActiveGoalId);
                if (active == null ||
                    !HasCapabilities(available, active.RequiredCapabilities))
                {
                    ReturnToRoutine(
                        uid,
                        hybrid,
                        htn,
                        "Complex goal lost a required capacity.");
                    continue;
                }

                if (now >= hybrid.LeaseEnds)
                {
                    ReturnToRoutine(
                        uid,
                        hybrid,
                        htn,
                        "Complex goal lease completed; routine HTN resumed.");
                }
                continue;
            }

            if (htn.Plan != null)
            {
                hybrid.NoPlanSince = TimeSpan.Zero;
            }
            else if (hybrid.NoPlanSince == TimeSpan.Zero)
            {
                hybrid.NoPlanSince = now;
            }

            string? reason = null;
            var noPlanSeconds =
                ClampFinite(hybrid.NoPlanEscalationSeconds, 2f, 120f, 8f);
            if (hybrid.NoPlanSince != TimeSpan.Zero &&
                now - hybrid.NoPlanSince >= TimeSpan.FromSeconds(noPlanSeconds))
            {
                reason =
                    $"routine root '{hybrid.RoutineTask}' produced no executable plan for " +
                    $"{noPlanSeconds:0.#} seconds";
            }
            else if (hybrid.EscalateOnSpeech && hybrid.SpeechDirty)
            {
                reason = "new nearby IC speech requires a contextual decision";
            }

            if (reason == null ||
                hybrid.PendingDecision ||
                now < hybrid.NextEscalation)
            {
                continue;
            }

            TryRequestEscalation(uid, hybrid, htn, reason, ignoreCooldown: false, out _);
        }
    }

    public bool TryConfigureNpc(
        EntityUid target,
        string routineTask,
        IReadOnlyCollection<HybridNpcCapability> capabilities,
        IReadOnlyList<HybridNpcComplexGoal> complexGoals,
        string persona,
        float decisionCooldownSeconds,
        float complexGoalLeaseSeconds,
        out string error)
    {
        if (!TryComp<HTNComponent>(target, out var htn))
        {
            error = "Target entity does not have an HTN NPC component.";
            return false;
        }

        if (HasComp<ActorComponent>(target))
        {
            error = "Hybrid server NPC control cannot be attached to a player-controlled entity.";
            return false;
        }

        if (HasComp<LlmNpcAutonomyComponent>(target))
        {
            error = "Disable periodic LLM NPC autonomy before enabling hybrid control.";
            return false;
        }

        var configuration = new LlmNpcHybridComponent
        {
            RoutineTask = routineTask.Trim(),
            Capabilities = capabilities.ToHashSet(),
            ComplexGoals = complexGoals.Select(CloneGoal).ToList(),
            Persona = persona.Trim(),
            DecisionCooldownSeconds = decisionCooldownSeconds,
            ComplexGoalLeaseSeconds = complexGoalLeaseSeconds,
        };
        if (!TryValidateConfiguration(configuration, out error))
            return false;

        var component = EnsureComp<LlmNpcHybridComponent>(target);
        component.RoutineTask = configuration.RoutineTask;
        component.Capabilities = configuration.Capabilities;
        component.ComplexGoals = configuration.ComplexGoals;
        component.Persona = configuration.Persona;
        component.DecisionCooldownSeconds = configuration.DecisionCooldownSeconds;
        component.ComplexGoalLeaseSeconds = configuration.ComplexGoalLeaseSeconds;

        component.ConfigurationGeneration++;
        ResetRuntime(component);
        SetRoot(target, htn, component.RoutineTask);
        error = string.Empty;
        return true;
    }

    public bool TryDisableNpc(EntityUid target, out string error)
    {
        if (!TryComp<LlmNpcHybridComponent>(target, out var component) ||
            !TryComp<HTNComponent>(target, out var htn))
        {
            error = "Target entity does not have hybrid NPC control configured.";
            return false;
        }

        component.ConfigurationGeneration++;
        ReturnToRoutine(target, component, htn, "Hybrid NPC control was removed.");
        RemComp<LlmNpcHybridComponent>(target);
        error = string.Empty;
        return true;
    }

    public bool TryEscalateNow(EntityUid target, string reason, out string error)
    {
        if (!TryComp<LlmNpcHybridComponent>(target, out var component) ||
            !TryComp<HTNComponent>(target, out var htn))
        {
            error = "Target entity does not have hybrid NPC control configured.";
            return false;
        }

        reason = string.IsNullOrWhiteSpace(reason)
            ? "an administrator requested a complex decision"
            : reason.Trim();
        return TryRequestEscalation(
            target,
            component,
            htn,
            reason,
            ignoreCooldown: true,
            out error);
    }

    public HybridNpcStatus GetStatus(EntityUid target)
    {
        if (!TryComp<LlmNpcHybridComponent>(target, out var component) ||
            !TryComp<HTNComponent>(target, out var htn))
        {
            return new HybridNpcStatus(false, string.Empty, string.Empty, false, false, string.Empty);
        }

        return new HybridNpcStatus(
            true,
            component.RoutineTask,
            htn.RootTask.Task,
            component.ComplexGoalActive,
            component.PendingDecision,
            component.LastOutcome);
    }

    private void OnMapInit(
        EntityUid uid,
        LlmNpcHybridComponent component,
        MapInitEvent args)
    {
        if (!TryComp<HTNComponent>(uid, out var htn))
        {
            component.LastOutcome = "Hybrid NPC component requires an HTN component.";
            return;
        }

        if (!TryValidateConfiguration(component, out var error))
        {
            component.LastOutcome = error;
            return;
        }

        component.ConfigurationGeneration++;
        ResetRuntime(component);
        SetRoot(uid, htn, component.RoutineTask);
    }

    private void OnShutdown(
        EntityUid uid,
        LlmNpcHybridComponent component,
        ComponentShutdown args)
    {
        component.ConfigurationGeneration++;
        component.PendingDecision = false;
    }

    private void OnHybridEnabledChanged(bool enabled)
    {
        if (enabled)
            return;

        var query = EntityQueryEnumerator<LlmNpcHybridComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var hybrid, out var htn))
        {
            hybrid.ConfigurationGeneration++;
            hybrid.PendingDecision = false;
            ReturnToRoutine(uid, hybrid, htn, "Hybrid LLM escalation was disabled.");
        }
    }

    private void OnEntitySpoke(EntitySpokeEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.MafiaDirectorNpcHybridEnabled) ||
            !TryComp(ev.Source, out TransformComponent? speakerTransform))
        {
            return;
        }

        var message = NormalizeSpeech(ev.Message);
        if (message.Length == 0)
            return;

        var speakerMap = _transform.GetMapCoordinates(ev.Source, speakerTransform);
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LlmNpcHybridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var hybrid, out var transform))
        {
            if (uid == ev.Source ||
                HasComp<ActorComponent>(uid) ||
                !hybrid.EscalateOnSpeech)
                continue;

            var npcMap = _transform.GetMapCoordinates(uid, transform);
            var radius =
                ClampFinite(
                    hybrid.ObservationRadius,
                    0f,
                    MaximumObservationRadius,
                    0f);
            if (npcMap.MapId != speakerMap.MapId ||
                Vector2.DistanceSquared(npcMap.Position, speakerMap.Position) > radius * radius)
            {
                continue;
            }

            while (hybrid.RecentSpeech.Count >= MaximumSpeechMemories)
                hybrid.RecentSpeech.Dequeue();
            hybrid.RecentSpeech.Enqueue(
                new LlmNpcSpeechMemory(now, Name(ev.Source), message));
            hybrid.SpeechDirty = true;
        }
    }

    private bool TryRequestEscalation(
        EntityUid uid,
        LlmNpcHybridComponent component,
        HTNComponent htn,
        string reason,
        bool ignoreCooldown,
        out string error)
    {
        if (HasComp<ActorComponent>(uid))
        {
            error = "Hybrid escalation cannot run on a player-controlled entity.";
            return false;
        }

        if (!_cfg.GetCVar(CCVars.MafiaDirectorNpcHybridEnabled))
        {
            error = "Hybrid NPC escalation is disabled.";
            return false;
        }

        if (component.PendingDecision || component.ComplexGoalActive)
        {
            error = "Hybrid NPC already has a pending or active complex decision.";
            return false;
        }

        var now = _timing.CurTime;
        if (!ignoreCooldown && now < component.NextEscalation)
        {
            error = "Hybrid NPC escalation is still on cooldown.";
            return false;
        }

        if (!TryValidateConfiguration(component, out error))
            return false;

        var available = GetAvailableCapabilities(uid, component);
        var eligible = component.ComplexGoals
            .Where(goal => HasCapabilities(available, goal.RequiredCapabilities))
            .ToArray();
        if (eligible.Length < 2)
        {
            component.LastOutcome =
                "Fewer than two complex goals are currently compatible with NPC capacity.";
            component.NextEscalation =
                now +
                TimeSpan.FromSeconds(
                    ClampFinite(component.DecisionCooldownSeconds, 15f, 3600f, 60f));
            error = component.LastOutcome;
            return false;
        }

        var options = eligible
            .Select(goal => new DirectorChoiceOption(goal.Id, goal.Description))
            .ToArray();
        var capabilityText = string.Join(", ", available.OrderBy(capability => capability));
        var context =
            LlmNpcContextBuilder.Build(
                component.Persona,
                htn.RootTask.Task,
                component.RecentSpeech.ToArray(),
                Array.Empty<LlmNpcGoalMemory>(),
                Array.Empty<LlmNpcDecisionMemory>(),
                now) +
            $"\nEscalation reason: {reason}.\nAvailable capacities: {capabilityText}.";
        if (context.Length > 2000)
            context = context[..2000];

        var generation = component.ConfigurationGeneration;
        if (!_director.TryRequestPlan(
                requester: null,
                options,
                "Choose one capability-compatible complex NPC goal; ordinary HTN remains the executor.",
                context,
                outcome => OnDecisionCompleted(uid, component, generation, outcome),
                () => IsStillAuthorized(uid, component, generation),
                out error))
        {
            component.LastOutcome = error;
            component.NextEscalation =
                now +
                TimeSpan.FromSeconds(
                    ClampFinite(component.DecisionCooldownSeconds, 15f, 3600f, 60f));
            return false;
        }

        component.PendingDecision = true;
        component.SpeechDirty = false;
        component.LastEscalationReason = reason;
        component.LastOutcome = "Complex decision requested.";
        component.NextEscalation =
            now +
            TimeSpan.FromSeconds(
                ClampFinite(component.DecisionCooldownSeconds, 15f, 3600f, 60f));
        error = string.Empty;
        return true;
    }

    private void OnDecisionCompleted(
        EntityUid uid,
        LlmNpcHybridComponent expected,
        int generation,
        LlmDirectorOutcome outcome)
    {
        if (!IsStillAuthorized(uid, expected, generation) ||
            !TryComp<HTNComponent>(uid, out var htn))
        {
            return;
        }

        expected.PendingDecision = false;
        expected.LastOutcome = outcome.Summary;
        if (outcome.Choice == null)
            return;

        var selected = expected.ComplexGoals.FirstOrDefault(
            goal => goal.Id == outcome.Choice.Id);
        if (selected == null ||
            !_prototypes.HasIndex<HTNCompoundPrototype>(selected.Task))
        {
            expected.LastOutcome = "Selected hybrid goal mapping no longer exists.";
            return;
        }

        var available = GetAvailableCapabilities(uid, expected);
        if (!HasCapabilities(available, selected.RequiredCapabilities))
        {
            expected.LastOutcome = "Selected hybrid goal lost a required capacity.";
            return;
        }

        SetRoot(uid, htn, selected.Task);
        expected.ComplexGoalActive = true;
        expected.ActiveGoalId = selected.Id;
        expected.NoPlanSince = TimeSpan.Zero;
        expected.LeaseEnds =
            _timing.CurTime +
            TimeSpan.FromSeconds(
                ClampFinite(expected.ComplexGoalLeaseSeconds, 5f, 600f, 30f));
        _adminLogs.Add(
            LogType.Action,
            LogImpact.Medium,
            $"Hybrid LLM executive leased {ToPrettyString(uid):entity} goal {selected.Id} -> " +
            $"{selected.Task}; reason={expected.LastEscalationReason}; " +
            $"confidence={outcome.Choice.Confidence:P0}");
    }

    private bool IsStillAuthorized(
        EntityUid uid,
        LlmNpcHybridComponent expected,
        int generation)
    {
        return _cfg.GetCVar(CCVars.MafiaDirectorNpcHybridEnabled) &&
               Exists(uid) &&
               !HasComp<ActorComponent>(uid) &&
               TryComp<LlmNpcHybridComponent>(uid, out var current) &&
               ReferenceEquals(current, expected) &&
               current.ConfigurationGeneration == generation;
    }

    private HashSet<HybridNpcCapability> GetAvailableCapabilities(
        EntityUid uid,
        LlmNpcHybridComponent component)
    {
        var result = component.Capabilities.ToHashSet();
        if (result.Contains(HybridNpcCapability.Move) && !_actionBlocker.CanMove(uid))
        {
            result.Remove(HybridNpcCapability.Move);
            result.Remove(HybridNpcCapability.OpenDoors);
        }

        if (result.Contains(HybridNpcCapability.Interact) &&
            !_actionBlocker.CanInteract(uid, null))
        {
            result.Remove(HybridNpcCapability.Interact);
            result.Remove(HybridNpcCapability.Hands);
            result.Remove(HybridNpcCapability.OpenDoors);
        }

        if (result.Contains(HybridNpcCapability.Hands) && !HasComp<HandsComponent>(uid))
            result.Remove(HybridNpcCapability.Hands);

        if (result.Contains(HybridNpcCapability.Speak) && !_actionBlocker.CanSpeak(uid))
            result.Remove(HybridNpcCapability.Speak);

        if (result.Contains(HybridNpcCapability.OpenDoors) &&
            (!result.Contains(HybridNpcCapability.Move) ||
             !result.Contains(HybridNpcCapability.Interact)))
        {
            result.Remove(HybridNpcCapability.OpenDoors);
        }

        return result;
    }

    private bool TryValidateConfiguration(
        LlmNpcHybridComponent component,
        out string error)
    {
        if (component.RoutineTask.Length is < 1 or > 128 ||
            !_prototypes.HasIndex<HTNCompoundPrototype>(component.RoutineTask))
        {
            error = $"Hybrid NPC routine HTN root is missing: '{component.RoutineTask}'.";
            return false;
        }

        if (component.Persona.Length > MaximumPersonaCharacters)
        {
            error =
                $"Hybrid NPC persona cannot exceed {MaximumPersonaCharacters} characters.";
            return false;
        }

        if (component.ComplexGoals.Count is < 2 or > MaximumComplexGoals)
        {
            error =
                $"Hybrid NPC requires 2-{MaximumComplexGoals} complex goal mappings.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var goal in component.ComplexGoals)
        {
            if (goal.Id.Length is < 1 or > 128 ||
                !string.Equals(goal.Id, goal.Id.Trim(), StringComparison.Ordinal) ||
                goal.Id.Any(char.IsControl) ||
                goal.Id == "none" ||
                !ids.Add(goal.Id))
            {
                error = $"Hybrid NPC goal id is invalid, duplicate, or reserved: '{goal.Id}'.";
                return false;
            }

            if (goal.Task.Length is < 1 or > 128 ||
                !_prototypes.HasIndex<HTNCompoundPrototype>(goal.Task))
            {
                error = $"Hybrid NPC HTN goal is missing: '{goal.Task}'.";
                return false;
            }

            if (goal.Description.Length > MaximumDescriptionCharacters)
            {
                error =
                    $"Hybrid NPC goal description for '{goal.Id}' exceeds " +
                    $"{MaximumDescriptionCharacters} characters.";
                return false;
            }

            if (!goal.RequiredCapabilities.IsSubsetOf(component.Capabilities))
            {
                error =
                    $"Hybrid NPC goal '{goal.Id}' requires a capability outside its profile.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private void ReturnToRoutine(
        EntityUid uid,
        LlmNpcHybridComponent component,
        HTNComponent htn,
        string outcome)
    {
        if (component.RoutineTask.Length > 0 &&
            _prototypes.HasIndex<HTNCompoundPrototype>(component.RoutineTask))
        {
            SetRoot(uid, htn, component.RoutineTask);
        }

        component.ComplexGoalActive = false;
        component.ActiveGoalId = string.Empty;
        component.LeaseEnds = TimeSpan.Zero;
        component.NoPlanSince = TimeSpan.Zero;
        component.LastOutcome = outcome;
    }

    private void SuspendForPlayer(
        EntityUid uid,
        LlmNpcHybridComponent component,
        HTNComponent htn)
    {
        if (component.PlayerSuspended)
            return;

        component.ConfigurationGeneration++;
        ResetRuntime(component);
        component.PlayerSuspended = true;
        if (component.RoutineTask.Length > 0 &&
            _prototypes.HasIndex<HTNCompoundPrototype>(component.RoutineTask))
        {
            SetRoot(uid, htn, component.RoutineTask);
        }
        component.LastOutcome = "Hybrid supervision suspended while a player controls the entity.";
    }

    private void ResumeAfterPlayer(
        EntityUid uid,
        LlmNpcHybridComponent component,
        HTNComponent htn)
    {
        if (!component.PlayerSuspended)
            return;

        component.ConfigurationGeneration++;
        ResetRuntime(component);
        if (component.RoutineTask.Length > 0 &&
            _prototypes.HasIndex<HTNCompoundPrototype>(component.RoutineTask))
        {
            SetRoot(uid, htn, component.RoutineTask);
        }
        component.LastOutcome = "Player detached; routine HTN supervision resumed.";
    }

    private void SetRoot(EntityUid uid, HTNComponent htn, string task)
    {
        if (htn.RootTask.Task == task)
            return;

        var wasEnabled = htn.Enabled;
        if (wasEnabled)
            _htn.SetHTNEnabled((uid, htn), false);
        htn.RootTask = new HTNCompoundTask { Task = task };
        if (wasEnabled)
            _htn.SetHTNEnabled((uid, htn), true);
    }

    private static void ResetRuntime(LlmNpcHybridComponent component)
    {
        component.NextEvaluation = TimeSpan.Zero;
        component.NoPlanSince = TimeSpan.Zero;
        component.NextEscalation = TimeSpan.Zero;
        component.LeaseEnds = TimeSpan.Zero;
        component.PendingDecision = false;
        component.ComplexGoalActive = false;
        component.PlayerSuspended = false;
        component.SpeechDirty = false;
        component.ActiveGoalId = string.Empty;
        component.LastEscalationReason = string.Empty;
        component.LastOutcome = "Routine HTN active.";
        component.RecentSpeech.Clear();
    }

    private static HybridNpcComplexGoal CloneGoal(HybridNpcComplexGoal goal)
    {
        return new HybridNpcComplexGoal
        {
            Id = goal.Id.Trim(),
            Task = goal.Task.Trim(),
            Description = goal.Description.Trim(),
            RequiredCapabilities = goal.RequiredCapabilities.ToHashSet(),
        };
    }

    private static void PruneSpeech(LlmNpcHybridComponent component, TimeSpan now)
    {
        var memorySeconds =
            ClampFinite(component.MemorySeconds, 15f, 900f, 180f);
        var oldest = now - TimeSpan.FromSeconds(memorySeconds);
        while (component.RecentSpeech.TryPeek(out var memory) &&
               memory.ObservedAt < oldest)
        {
            component.RecentSpeech.Dequeue();
        }
    }

    private static bool HasCapabilities(
        HashSet<HybridNpcCapability> available,
        HashSet<HybridNpcCapability> required)
    {
        return required.IsSubsetOf(available);
    }

    private static string NormalizeSpeech(string message)
    {
        var normalized = string.Join(
            " ",
            message.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private static float ClampFinite(
        float value,
        float minimum,
        float maximum,
        float fallback)
    {
        return float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}

public sealed record HybridNpcStatus(
    bool Configured,
    string RoutineTask,
    string CurrentTask,
    bool ComplexGoalActive,
    bool PendingDecision,
    string LastOutcome);
