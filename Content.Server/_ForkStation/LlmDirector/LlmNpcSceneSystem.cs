using System.Linq;
using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.GameTicking.Events;
using Content.Server.NPC.HTN;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// Coordinates small casts of existing HTN NPCs. The model selects one server-authored beat ID;
/// each beat maps every cast slot to an existing HTN root, which is revalidated and applied as a
/// batch on the main thread.
/// </summary>
public sealed class LlmNpcSceneSystem : EntitySystem
{
    private const int MaximumScenes = 8;
    private const float MaximumIntervalSeconds = 3600f;
    private const float ObservationRadius = 15f;
    private const float WhisperRadius = 2f;
    private const float SpeechMemorySeconds = 180f;
    private const int MaximumBeatMemories = 8;

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly IAdminLogManager _adminLogs = default!;
    [Dependency] private readonly LlmGameplayDirectorSystem _director = default!;

    private readonly Dictionary<string, SceneDefinition> _scenes =
        new(StringComparer.Ordinal);
    private TimeSpan _nextUpdate;
    private int _nextGeneration;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundReset);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundReset);
        Subs.CVar(
            _cfg,
            CCVars.MafiaDirectorNpcScenesEnabled,
            OnScenesEnabledChanged,
            true);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_cfg.GetCVar(CCVars.MafiaDirectorNpcScenesEnabled))
            return;

        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;

        _nextUpdate = now + TimeSpan.FromSeconds(1);
        foreach (var scene in _scenes.Values.ToArray())
        {
            PruneSpeech(scene, now);
            if (scene.RequestPending || now < scene.NextDecision)
                continue;

            scene.NextDecision = now + scene.Interval;
            if (!TryQueueScene(scene, now, out var error))
                scene.LastError = error;
        }
    }

    public bool TryConfigureScene(
        string sceneId,
        float intervalSeconds,
        IReadOnlyList<EntityUid> members,
        string beatSpecification,
        string premise,
        NetUserId? configuredBy,
        out string error)
    {
        sceneId = sceneId.Trim();
        premise = premise.Trim();
        if (!LlmNpcSceneBeatParser.IsSafeId(sceneId))
        {
            error =
                "Scene ID must contain 1 to 64 ASCII letters, digits, underscores, or hyphens.";
            return false;
        }

        if (_scenes.ContainsKey(sceneId))
        {
            error = $"Scene '{sceneId}' already exists; disable it before replacing it.";
            return false;
        }

        if (_scenes.Count >= MaximumScenes)
        {
            error = $"At most {MaximumScenes} LLM NPC scenes may be configured.";
            return false;
        }

        if (members.Count is < 2 or > 8 ||
            members.Distinct().Count() != members.Count)
        {
            error = "Supply between 2 and 8 distinct NPC entities.";
            return false;
        }

        if (!float.IsFinite(intervalSeconds))
        {
            error = "Scene interval must be a finite number of seconds.";
            return false;
        }

        var minimumInterval = Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaDirectorNpcSceneMinimumDecisionSeconds),
            15f,
            600f);
        if (intervalSeconds < minimumInterval ||
            intervalSeconds > MaximumIntervalSeconds)
        {
            error =
                $"Scene interval must be between {minimumInterval:0} and " +
                $"{MaximumIntervalSeconds:0} seconds.";
            return false;
        }

        if (premise.Length > 800)
        {
            error = "Scene premise cannot exceed 800 characters.";
            return false;
        }

        if (!LlmNpcSceneBeatParser.TryParse(
                beatSpecification,
                members.Count,
                out var beats,
                out error))
        {
            return false;
        }

        foreach (var member in members)
        {
            if (!Exists(member) || !HasComp<HTNComponent>(member))
            {
                error = $"{ToPrettyString(member)} is not an existing HTN NPC.";
                return false;
            }
        }

        foreach (var beat in beats)
        {
            foreach (var goal in beat.Goals)
            {
                if (!_prototypes.HasIndex<HTNCompoundPrototype>(goal))
                {
                    error = $"Scene beat '{beat.Id}' references missing HTN goal '{goal}'.";
                    return false;
                }
            }
        }

        var scene = new SceneDefinition(
            sceneId,
            premise,
            members.ToArray(),
            beats.ToDictionary(beat => beat.Id, StringComparer.Ordinal),
            TimeSpan.FromSeconds(intervalSeconds),
            ++_nextGeneration);
        scene.NextDecision = _cfg.GetCVar(CCVars.MafiaDirectorNpcScenesEnabled)
            ? _timing.CurTime + TimeSpan.FromSeconds(1)
            : TimeSpan.Zero;
        _scenes.Add(sceneId, scene);
        _adminLogs.Add(
            LogType.Action,
            LogImpact.Medium,
            $"Configured bounded LLM NPC scene {sceneId} with {members.Count} members and {beats.Count} beats; requester={configuredBy}");
        error = string.Empty;
        return true;
    }

    public bool TryDisableScene(
        string sceneId,
        NetUserId? disabledBy,
        out string error)
    {
        if (!_scenes.Remove(sceneId, out var scene))
        {
            error = $"No LLM NPC scene named '{sceneId}' exists.";
            return false;
        }

        scene.Generation = ++_nextGeneration;
        scene.RequestPending = false;
        _adminLogs.Add(
            LogType.Action,
            LogImpact.Medium,
            $"Disabled bounded LLM NPC scene {sceneId}; requester={disabledBy}");
        error = string.Empty;
        return true;
    }

    public IReadOnlyList<LlmNpcSceneStatus> GetStatuses(string? sceneId = null)
    {
        var now = _timing.CurTime;
        return _scenes.Values
            .Where(scene => sceneId == null || scene.Id == sceneId)
            .OrderBy(scene => scene.Id, StringComparer.Ordinal)
            .Select(scene => new LlmNpcSceneStatus(
                scene.Id,
                scene.Members.Count,
                scene.Beats.Count,
                scene.RequestPending,
                scene.NextDecision > now ? scene.NextDecision - now : TimeSpan.Zero,
                scene.RecentBeats.LastOrDefault()?.BeatId,
                scene.LastError))
            .ToArray();
    }

    private bool TryQueueScene(
        SceneDefinition scene,
        TimeSpan now,
        out string error)
    {
        if (!TryBuildSnapshots(scene, out var snapshots, out error))
            return false;

        var options = scene.Beats.Values
            .Select(beat => new DirectorChoiceOption(
                beat.Id,
                DescribeBeat(scene, beat)))
            .ToArray();
        var context = LlmNpcSceneContextBuilder.Build(
            scene.Id,
            scene.Premise,
            snapshots,
            scene.RecentSpeech.ToArray(),
            scene.RecentBeats.ToArray(),
            now);
        var generation = scene.Generation;
        if (!_director.TryRequestPlan(
                requester: null,
                options,
                $"Choose the next coordinated beat for NPC scene '{scene.Id}'.",
                context,
                outcome => OnSceneChoiceCompleted(scene.Id, generation, outcome),
                () => IsStillAuthorized(scene.Id, generation),
                out error))
        {
            return false;
        }

        scene.RequestPending = true;
        scene.LastError = string.Empty;
        return true;
    }

    private void OnSceneChoiceCompleted(
        string sceneId,
        int generation,
        LlmDirectorOutcome outcome)
    {
        if (!_scenes.TryGetValue(sceneId, out var scene) ||
            scene.Generation != generation)
        {
            return;
        }

        scene.RequestPending = false;
        if (outcome.Choice == null)
        {
            scene.LastError = outcome.Status is
                LlmDirectorOutcomeStatus.Failed or LlmDirectorOutcomeStatus.Rejected
                ? outcome.Summary
                : string.Empty;
            return;
        }

        if (outcome.Choice.Id == "none")
        {
            RememberBeat(scene, outcome.Choice);
            scene.LastError = string.Empty;
            return;
        }

        if (outcome.Status != LlmDirectorOutcomeStatus.Selected)
        {
            scene.LastError = outcome.Summary;
            return;
        }

        if (!TryApplyBeat(scene, outcome.Choice, out var error))
        {
            scene.LastError = error;
            return;
        }

        RememberBeat(scene, outcome.Choice);
        scene.LastError = string.Empty;
    }

    private bool TryApplyBeat(
        SceneDefinition scene,
        DirectorChoice choice,
        out string error)
    {
        if (!scene.Beats.TryGetValue(choice.Id, out var beat) ||
            beat.Goals.Count != scene.Members.Count)
        {
            error = "Selected scene beat no longer exists or has an invalid cast mapping.";
            return false;
        }

        var assignments = new List<NpcAssignment>(scene.Members.Count);
        for (var index = 0; index < scene.Members.Count; index++)
        {
            var member = scene.Members[index];
            var goal = beat.Goals[index];
            if (!Exists(member) ||
                !TryComp<HTNComponent>(member, out var htn) ||
                !_prototypes.HasIndex<HTNCompoundPrototype>(goal))
            {
                error =
                    $"Scene member {index} or HTN goal '{goal}' no longer exists; " +
                    "no scene goals were changed.";
                return false;
            }

            assignments.Add(new NpcAssignment(
                member,
                htn,
                goal,
                htn.RootTask.Task,
                htn.Enabled));
        }

        foreach (var assignment in assignments)
        {
            if (assignment.WasEnabled)
                _htn.SetHTNEnabled((assignment.Uid, assignment.Component), false);
        }

        try
        {
            foreach (var assignment in assignments)
            {
                assignment.Component.RootTask = new HTNCompoundTask
                {
                    Task = assignment.Goal,
                };
            }
        }
        catch (Exception exception)
        {
            foreach (var assignment in assignments)
            {
                assignment.Component.RootTask = new HTNCompoundTask
                {
                    Task = assignment.PreviousGoal,
                };
            }

            error =
                $"Scene goal batch failed ({exception.GetType().Name}); previous goals restored.";
            return false;
        }
        finally
        {
            foreach (var assignment in assignments)
            {
                if (assignment.WasEnabled)
                    _htn.SetHTNEnabled((assignment.Uid, assignment.Component), true);
            }
        }

        var mapping = string.Join(
            ", ",
            assignments.Select((assignment, index) =>
                $"slot{index}:{assignment.PreviousGoal}->{assignment.Goal}"));
        _adminLogs.Add(
            LogType.Action,
            LogImpact.Medium,
            $"LLM NPC scene {scene.Id} applied allowlisted beat {choice.Id}; confidence={choice.Confidence:P0}; {mapping}");
        error = string.Empty;
        return true;
    }

    private bool TryBuildSnapshots(
        SceneDefinition scene,
        out IReadOnlyList<LlmNpcSceneMemberSnapshot> snapshots,
        out string error)
    {
        var result = new List<LlmNpcSceneMemberSnapshot>(scene.Members.Count);
        for (var index = 0; index < scene.Members.Count; index++)
        {
            var member = scene.Members[index];
            if (!Exists(member) || !TryComp<HTNComponent>(member, out var htn))
            {
                snapshots = Array.Empty<LlmNpcSceneMemberSnapshot>();
                error = $"Scene member in slot {index} no longer exists or lacks an HTN component.";
                return false;
            }

            result.Add(new LlmNpcSceneMemberSnapshot(
                index,
                Name(member),
                htn.RootTask.Task));
        }

        snapshots = result;
        error = string.Empty;
        return true;
    }

    private string DescribeBeat(SceneDefinition scene, LlmNpcSceneBeat beat)
    {
        var mapping = string.Join(
            "; ",
            scene.Members.Select((member, index) =>
                $"slot {index} ({Name(member)}) uses HTN goal {beat.Goals[index]}"));
        var description = $"Coordinated beat '{beat.Id}': {mapping}.";
        return description.Length <= 300 ? description : description[..300];
    }

    private bool IsStillAuthorized(string sceneId, int generation)
    {
        return _cfg.GetCVar(CCVars.MafiaDirectorNpcScenesEnabled) &&
               _scenes.TryGetValue(sceneId, out var scene) &&
               scene.Generation == generation;
    }

    private void RememberBeat(SceneDefinition scene, DirectorChoice choice)
    {
        while (scene.RecentBeats.Count >= MaximumBeatMemories)
            scene.RecentBeats.Dequeue();

        scene.RecentBeats.Enqueue(new LlmNpcSceneBeatMemory(
            _timing.CurTime,
            choice.Id,
            choice.Confidence,
            choice.Reason));
    }

    private void OnEntitySpoke(EntitySpokeEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.MafiaDirectorNpcScenesEnabled) ||
            !TryComp(ev.Source, out TransformComponent? speakerTransform))
        {
            return;
        }

        var message = NormalizeSpeech(ev.Message);
        if (message.Length == 0)
            return;

        var speakerCoordinates = _transform.GetMapCoordinates(ev.Source, speakerTransform);
        var radius = ev.ObfuscatedMessage != null ? WhisperRadius : ObservationRadius;
        var radiusSquared = radius * radius;
        var now = _timing.CurTime;
        var maximumMemories = Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaDirectorNpcMaximumSpeechMemories),
            1,
            32);
        foreach (var scene in _scenes.Values)
        {
            var audible = false;
            foreach (var member in scene.Members)
            {
                if (!TryComp(member, out TransformComponent? memberTransform))
                    continue;

                var memberCoordinates = _transform.GetMapCoordinates(member, memberTransform);
                if (memberCoordinates.MapId == speakerCoordinates.MapId &&
                    Vector2.DistanceSquared(
                        memberCoordinates.Position,
                        speakerCoordinates.Position) <= radiusSquared)
                {
                    audible = true;
                    break;
                }
            }

            if (!audible)
                continue;

            while (scene.RecentSpeech.Count >= maximumMemories)
                scene.RecentSpeech.Dequeue();

            scene.RecentSpeech.Enqueue(new LlmNpcSceneSpeechMemory(
                now,
                Name(ev.Source),
                message));
        }
    }

    private static void PruneSpeech(SceneDefinition scene, TimeSpan now)
    {
        var oldest = now - TimeSpan.FromSeconds(SpeechMemorySeconds);
        while (scene.RecentSpeech.TryPeek(out var memory) &&
               memory.ObservedAt < oldest)
        {
            scene.RecentSpeech.Dequeue();
        }
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

    private void OnScenesEnabledChanged(bool enabled)
    {
        foreach (var scene in _scenes.Values)
        {
            scene.Generation = ++_nextGeneration;
            scene.RequestPending = false;
            scene.NextDecision = enabled
                ? _timing.CurTime + TimeSpan.FromSeconds(1)
                : TimeSpan.Zero;
        }
    }

    private void OnRoundReset(RoundStartingEvent ev)
    {
        _scenes.Clear();
        _nextUpdate = TimeSpan.Zero;
    }

    private void OnRoundReset(RoundRestartCleanupEvent ev)
    {
        _scenes.Clear();
        _nextUpdate = TimeSpan.Zero;
    }

    private sealed class SceneDefinition
    {
        public readonly string Id;
        public readonly string Premise;
        public readonly IReadOnlyList<EntityUid> Members;
        public readonly IReadOnlyDictionary<string, LlmNpcSceneBeat> Beats;
        public readonly TimeSpan Interval;
        public readonly Queue<LlmNpcSceneSpeechMemory> RecentSpeech = new();
        public readonly Queue<LlmNpcSceneBeatMemory> RecentBeats = new();

        public int Generation;
        public TimeSpan NextDecision;
        public bool RequestPending;
        public string LastError = string.Empty;

        public SceneDefinition(
            string id,
            string premise,
            IReadOnlyList<EntityUid> members,
            IReadOnlyDictionary<string, LlmNpcSceneBeat> beats,
            TimeSpan interval,
            int generation)
        {
            Id = id;
            Premise = premise;
            Members = members;
            Beats = beats;
            Interval = interval;
            Generation = generation;
        }
    }

    private sealed record NpcAssignment(
        EntityUid Uid,
        HTNComponent Component,
        string Goal,
        string PreviousGoal,
        bool WasEnabled);
}

public sealed record LlmNpcSceneStatus(
    string Id,
    int MemberCount,
    int BeatCount,
    bool RequestPending,
    TimeSpan NextDecisionIn,
    string? LastBeat,
    string LastError);
