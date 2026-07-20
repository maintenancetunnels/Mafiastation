using System.Linq;
using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// Low-frequency, contextual executive loop over existing HTN NPCs. It records a small window of
/// nearby IC speech, asks the bounded director for a high-level goal, and leaves all concrete
/// behavior to the normal HTN tree.
/// </summary>
public sealed class LlmNpcAutonomySystem : EntitySystem
{
    private const int MaximumGoals = 24;
    private const int MaximumPersonaCharacters = 800;
    private const float MaximumDecisionSeconds = 3600f;
    private const float MaximumObservationRadius = 30f;
    private const int MaximumGoalMemories = 8;
    private const int MaximumDecisionMemories = 8;

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly LlmGameplayDirectorSystem _director = default!;
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);
        Subs.CVar(_cfg, CCVars.MafiaDirectorNpcAutonomyEnabled, OnAutonomyEnabledChanged, true);
    }

    private void OnAutonomyEnabledChanged(bool enabled)
    {
        if (enabled)
            return;

        var query = EntityQueryEnumerator<LlmNpcAutonomyComponent>();
        while (query.MoveNext(out var uid, out _))
            _director.CancelPendingNpcGoal(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_cfg.GetCVar(CCVars.MafiaDirectorNpcAutonomyEnabled))
            return;

        var now = _timing.CurTime;

        if (now < _nextUpdate)
            return;
        _nextUpdate = now + TimeSpan.FromSeconds(1);
        var minimumSeconds = Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaDirectorNpcMinimumDecisionSeconds),
            15f,
            600f);
        var query = EntityQueryEnumerator<LlmNpcAutonomyComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var autonomy, out var htn))
        {
            // Hybrid goal/capacity supervision owns escalation for this NPC. Never run both
            // executive loops against the same HTN root.
            if (HasComp<LlmNpcHybridComponent>(uid))
                continue;

            if (autonomy.NextDecision == TimeSpan.Zero)
                autonomy.NextDecision = now + TimeSpan.FromSeconds(1);

            PruneMemories(autonomy, now);
            ObserveGoal(autonomy, htn.RootTask.Task, now);

            if (now < autonomy.NextDecision)
                continue;

            var intervalSeconds = float.IsFinite(autonomy.DecisionIntervalSeconds)
                ? Math.Clamp(
                    autonomy.DecisionIntervalSeconds,
                    minimumSeconds,
                    MaximumDecisionSeconds)
                : minimumSeconds;
            autonomy.NextDecision = now + TimeSpan.FromSeconds(intervalSeconds);

            if (!TryValidateGoals(autonomy.AllowedGoals, out var goals, out _))
                continue;

            var options = goals
                .Select(id => new DirectorChoiceOption(
                    id,
                    $"Configured high-level HTN goal '{id}' for this NPC."))
                .ToArray();
            var context = LlmNpcContextBuilder.Build(
                autonomy.Persona,
                htn.RootTask.Task,
                autonomy.RecentSpeech.ToArray(),
                autonomy.RecentGoals.ToArray(),
                autonomy.RecentDecisions.ToArray(),
                now);

            _director.TryRequestNpcGoal(
                requester: null,
                uid,
                options,
                context,
                outcome => OnDecisionCompleted(uid, autonomy, outcome),
                () => IsStillAuthorized(uid, autonomy),
                out _);
        }
    }

    /// <summary>
    /// Admin/runtime configuration seam. It independently validates every goal before attaching
    /// the component; the director validates them again immediately before every request/action.
    /// </summary>
    public bool TryConfigureNpc(
        EntityUid target,
        IReadOnlyList<string> goalIds,
        float decisionIntervalSeconds,
        string persona,
        out string error)
    {
        if (!TryComp<HTNComponent>(target, out var htn))
        {
            error = "Target entity does not have an HTN NPC component.";
            return false;
        }

        if (HasComp<LlmNpcHybridComponent>(target))
        {
            error = "Disable hybrid goal/capacity control before enabling periodic LLM autonomy.";
            return false;
        }

        if (!float.IsFinite(decisionIntervalSeconds))
        {
            error = "Decision interval must be a finite number of seconds.";
            return false;
        }

        var minimumSeconds = Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaDirectorNpcMinimumDecisionSeconds),
            15f,
            600f);
        if (decisionIntervalSeconds < minimumSeconds ||
            decisionIntervalSeconds > MaximumDecisionSeconds)
        {
            error =
                $"Decision interval must be between {minimumSeconds:0} and " +
                $"{MaximumDecisionSeconds:0} seconds.";
            return false;
        }

        if (persona.Length > MaximumPersonaCharacters)
        {
            error = $"Persona/context cannot exceed {MaximumPersonaCharacters} characters.";
            return false;
        }

        if (!TryValidateGoals(goalIds, out var goals, out error))
            return false;

        var autonomy = EnsureComp<LlmNpcAutonomyComponent>(target);
        _director.CancelPendingNpcGoal(target);
        autonomy.AllowedGoals = goals.ToList();
        autonomy.Persona = persona.Trim();
        autonomy.DecisionIntervalSeconds = decisionIntervalSeconds;
        autonomy.NextDecision = _timing.CurTime + TimeSpan.FromSeconds(1);
        autonomy.LastObservedGoal = htn.RootTask.Task;
        autonomy.RecentSpeech.Clear();
        autonomy.RecentGoals.Clear();
        autonomy.RecentDecisions.Clear();
        error = string.Empty;
        return true;
    }

    public bool TryDisableNpc(EntityUid target, out string error)
    {
        if (!HasComp<LlmNpcAutonomyComponent>(target))
        {
            error = "Target entity does not have LLM NPC autonomy configured.";
            return false;
        }

        RemComp<LlmNpcAutonomyComponent>(target);
        _director.CancelPendingNpcGoal(target);
        error = string.Empty;
        return true;
    }

    private void OnEntitySpoke(EntitySpokeEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.MafiaDirectorNpcAutonomyEnabled) ||
            !TryComp(ev.Source, out TransformComponent? speakerTransform))
        {
            return;
        }

        var text = NormalizeSpeech(ev.Message);
        if (text.Length == 0)
            return;

        var speakerCoordinates = _transform.GetMapCoordinates(ev.Source, speakerTransform);
        var speakerName = Name(ev.Source);
        var now = _timing.CurTime;
        var maximumMemories = Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaDirectorNpcMaximumSpeechMemories),
            1,
            32);
        var query = EntityQueryEnumerator<LlmNpcAutonomyComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var autonomy, out var npcTransform))
        {
            if (uid == ev.Source)
                continue;

            var npcCoordinates = _transform.GetMapCoordinates(uid, npcTransform);
            if (npcCoordinates.MapId != speakerCoordinates.MapId)
                continue;

            var radius = float.IsFinite(autonomy.ObservationRadius)
                ? Math.Clamp(autonomy.ObservationRadius, 0f, MaximumObservationRadius)
                : 0f;
            var audibleRadius = ev.ObfuscatedMessage != null
                ? Math.Min(radius, 2f)
                : radius;
            if (Vector2.DistanceSquared(
                    npcCoordinates.Position,
                    speakerCoordinates.Position) > audibleRadius * audibleRadius)
            {
                continue;
            }

            while (autonomy.RecentSpeech.Count >= maximumMemories)
                autonomy.RecentSpeech.Dequeue();

            autonomy.RecentSpeech.Enqueue(new LlmNpcSpeechMemory(
                now,
                speakerName,
                text));
        }
    }

    private bool IsStillAuthorized(
        EntityUid uid,
        LlmNpcAutonomyComponent expected)
    {
        return _cfg.GetCVar(CCVars.MafiaDirectorNpcAutonomyEnabled) &&
               Exists(uid) &&
               TryComp<LlmNpcAutonomyComponent>(uid, out var current) &&
               ReferenceEquals(current, expected);
    }

    private void OnDecisionCompleted(
        EntityUid uid,
        LlmNpcAutonomyComponent expected,
        LlmDirectorOutcome outcome)
    {
        if (!Exists(uid) ||
            !TryComp<LlmNpcAutonomyComponent>(uid, out var current) ||
            !ReferenceEquals(current, expected) ||
            outcome.Choice == null)
        {
            return;
        }

        while (current.RecentDecisions.Count >= MaximumDecisionMemories)
            current.RecentDecisions.Dequeue();

        current.RecentDecisions.Enqueue(new LlmNpcDecisionMemory(
            _timing.CurTime,
            outcome.Choice.Id,
            outcome.Status,
            outcome.Choice.Confidence,
            outcome.Choice.Reason));
    }

    private bool TryValidateGoals(
        IReadOnlyList<string> goalIds,
        out IReadOnlyList<string> validated,
        out string error)
    {
        validated = Array.Empty<string>();
        if (goalIds.Count is < 2 or > MaximumGoals)
        {
            error = $"Supply between 2 and {MaximumGoals} HTN goals.";
            return false;
        }

        var result = new List<string>(goalIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawId in goalIds)
        {
            var id = rawId.Trim();
            if (id.Length is < 1 or > 128 ||
                id == "none" ||
                !seen.Add(id) ||
                !_prototypes.HasIndex<HTNCompoundPrototype>(id))
            {
                error = $"Invalid, duplicate, reserved, or missing HTN goal: '{id}'.";
                return false;
            }

            result.Add(id);
        }

        validated = result;
        error = string.Empty;
        return true;
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

    private static void ObserveGoal(
        LlmNpcAutonomyComponent autonomy,
        string currentGoal,
        TimeSpan now)
    {
        if (currentGoal == autonomy.LastObservedGoal)
            return;

        autonomy.LastObservedGoal = currentGoal;
        while (autonomy.RecentGoals.Count >= MaximumGoalMemories)
            autonomy.RecentGoals.Dequeue();

        autonomy.RecentGoals.Enqueue(new LlmNpcGoalMemory(now, currentGoal));
    }

    private static void PruneMemories(
        LlmNpcAutonomyComponent autonomy,
        TimeSpan now)
    {
        var memorySeconds = float.IsFinite(autonomy.MemorySeconds)
            ? Math.Clamp(autonomy.MemorySeconds, 15f, 900f)
            : 180f;
        var oldest = now - TimeSpan.FromSeconds(memorySeconds);
        while (autonomy.RecentSpeech.TryPeek(out var memory) &&
               memory.ObservedAt < oldest)
        {
            autonomy.RecentSpeech.Dequeue();
        }

        while (autonomy.RecentDecisions.TryPeek(out var decision) &&
               decision.ObservedAt < oldest)
        {
            autonomy.RecentDecisions.Dequeue();
        }
    }
}
