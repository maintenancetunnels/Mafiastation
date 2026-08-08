using System.Linq;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Prototypes;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// Slow, opt-in station narrative loop. It supplies bounded telemetry and an operator-authored
/// theme to the gameplay director, which may select only an existing game rule from the configured
/// allowlist. Preview is the default; automatic starts require three independent gates.
/// </summary>
public sealed class LlmNarrativeDirectorSystem : EntitySystem
{
    private const float MinimumIntervalSeconds = 60f;
    private const float MaximumIntervalSeconds = 7200f;
    private const float MaximumInitialDelaySeconds = 3600f;
    private const float MaximumRepeatCooldownSeconds = 14400f;

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly LlmGameplayDirectorSystem _director = default!;


    private readonly Queue<LlmNarrativeMemory> _history = new();
    private TimeSpan _nextUpdate;
    private TimeSpan _nextDecision;
    private bool _requestPending;
    private int _configurationGeneration;
    private string _lastError = string.Empty;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        Subs.CVar(
            _cfg,
            CCVars.MafiaDirectorNarrativeEnabled,
            OnNarrativeEnabledChanged,
            true);
        Subs.CVar(
            _cfg,
            CCVars.MafiaDirectorNarrativeEventIds,
            OnNarrativeEventIdsChanged,
            true);
        Subs.CVar(
            _cfg,
            CCVars.MafiaDirectorNarrativeTheme,
            OnNarrativeThemeChanged,
            true);
        Subs.CVar(
            _cfg,
            CCVars.MafiaDirectorNarrativeAllowEventStart,
            OnNarrativeStartGateChanged,
            true);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_cfg.GetCVar(CCVars.MafiaDirectorNarrativeEnabled) ||
            _ticker.RunLevel != GameRunLevel.InRound)
        {
            return;
        }

        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;

        _nextUpdate = now + TimeSpan.FromSeconds(1);
        if (_requestPending || now < _nextDecision)
            return;

        if (!TryQueueDecision(now, out var error))
        {
            _lastError = error;
            Log.Warning($"Narrative decision was not queued: {error}");
        }
    }

    public bool TryRequestNow(out string error)
    {
        if (!_cfg.GetCVar(CCVars.MafiaDirectorNarrativeEnabled))
        {
            error = "Autonomous LLM narrative mode is disabled.";
            return false;
        }

        if (_ticker.RunLevel != GameRunLevel.InRound)
        {
            error = "Autonomous LLM narrative decisions are available only during a round.";
            return false;
        }

        if (_requestPending)
        {
            error = "An autonomous LLM narrative request is already pending.";
            return false;
        }

        return TryQueueDecision(_timing.CurTime, out error);
    }

    public LlmNarrativeStatus GetStatus()
    {
        var configured = ParseConfiguredIds().Count;
        var now = _timing.CurTime;
        var dueIn = _nextDecision > now ? _nextDecision - now : TimeSpan.Zero;
        return new LlmNarrativeStatus(
            _cfg.GetCVar(CCVars.MafiaDirectorNarrativeEnabled),
            _requestPending,
            _cfg.GetCVar(CCVars.MafiaDirectorNarrativeAllowEventStart) &&
            _cfg.GetCVar(CCVars.MafiaDirectorAllowEventStart),
            configured,
            _history.Count,
            dueIn,
            _lastError);
    }

    private bool TryQueueDecision(TimeSpan now, out string error)
    {
        var interval = GetInterval();
        _nextDecision = now + interval;

        var activeRules = _ticker.GetActiveGameRules()
            .Select(uid => MetaData(uid).EntityPrototype?.ID)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var activeSet = activeRules.ToHashSet(StringComparer.Ordinal);
        var configured = ParseConfiguredIds()
            .Where(IsGameRulePrototype)
            .ToArray();
        var repeatCooldown = TimeSpan.FromSeconds(Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaDirectorNarrativeRepeatCooldownSeconds),
            0f,
            MaximumRepeatCooldownSeconds));
        var candidates = LlmNarrativeCandidateSelector.Select(
            configured,
            activeSet,
            _history.ToArray(),
            now,
            repeatCooldown);
        if (candidates.Count < 2)
        {
            error =
                "Configure at least two valid, non-active game-rule IDs in " +
                "mafia.director.narrative_event_ids.";
            return false;
        }

        var options = candidates
            .Select(id => new DirectorChoiceOption(id, DescribeGameRule(id)))
            .ToArray();
        var context = LlmNarrativeContextBuilder.Build(
            _cfg.GetCVar(CCVars.MafiaDirectorNarrativeTheme),
            _ticker.RoundId,
            _ticker.RoundDuration(),
            _players.Sessions.Count(session => session.Status == SessionStatus.InGame),
            activeRules,
            _history.ToArray(),
            now);
        var startSelectedRule =
            _cfg.GetCVar(CCVars.MafiaDirectorNarrativeAllowEventStart) &&
            _cfg.GetCVar(CCVars.MafiaDirectorAllowEventStart);
        var generation = _configurationGeneration;
        var roundId = _ticker.RoundId;
        if (!_director.TryRequestGameRule(
                requester: null,
                options,
                context,
                startSelectedRule,
                outcome => OnDecisionCompleted(generation, outcome),
                () => IsStillAuthorized(generation, roundId, startSelectedRule),
                out error))
        {
            return false;
        }

        _requestPending = true;
        _lastError = string.Empty;
        return true;
    }

    private void OnDecisionCompleted(int generation, LlmDirectorOutcome outcome)
    {
        if (generation != _configurationGeneration)
            return;

        _requestPending = false;
        if (outcome.Choice == null)
        {
            _lastError = outcome.Status is
                LlmDirectorOutcomeStatus.Failed or LlmDirectorOutcomeStatus.Rejected
                ? outcome.Summary
                : string.Empty;
            return;
        }

        var maximumMemories = Math.Clamp(
            _cfg.GetCVar(CCVars.MafiaDirectorNarrativeMaximumMemories),
            1,
            24);
        while (_history.Count >= maximumMemories)
            _history.Dequeue();

        _history.Enqueue(new LlmNarrativeMemory(
            _timing.CurTime,
            outcome.Choice.Id,
            outcome.Status,
            outcome.Choice.Confidence,
            outcome.Choice.Reason));
        _lastError = outcome.Status is
            LlmDirectorOutcomeStatus.Failed or LlmDirectorOutcomeStatus.Rejected
            ? outcome.Summary
            : string.Empty;
    }

    private bool IsStillAuthorized(int generation, int roundId, bool startSelectedRule)
    {
        return generation == _configurationGeneration &&
               _cfg.GetCVar(CCVars.MafiaDirectorNarrativeEnabled) &&
               (!startSelectedRule ||
                _cfg.GetCVar(CCVars.MafiaDirectorNarrativeAllowEventStart)) &&
               _ticker.RunLevel == GameRunLevel.InRound &&
               _ticker.RoundId == roundId;
    }

    private IReadOnlyList<string> ParseConfiguredIds()
    {
        return _cfg.GetCVar(CCVars.MafiaDirectorNarrativeEventIds)
            .Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.Length is > 0 and <= 128 && id != "none")
            .Distinct(StringComparer.Ordinal)
            .Take(48)
            .ToArray();
    }

    private bool IsGameRulePrototype(string id)
    {
        return _prototypes.TryIndex<EntityPrototype>(id, out var prototype) &&
               !prototype.Abstract &&
               prototype.HasComponent<GameRuleComponent>();
    }

    private string DescribeGameRule(string id)
    {
        if (!_prototypes.TryIndex<EntityPrototype>(id, out var prototype))
            return $"Existing game-rule prototype {id}.";

        var description =
            $"Existing game-rule prototype {id}. Name={prototype.Name}; " +
            $"description={prototype.Description}";
        description = string.Join(
            " ",
            description.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
        return description.Length <= 300 ? description : description[..300];
    }

    private TimeSpan GetInterval()
    {
        var configured = _cfg.GetCVar(CCVars.MafiaDirectorNarrativeIntervalSeconds);
        var seconds = float.IsFinite(configured)
            ? Math.Clamp(configured, MinimumIntervalSeconds, MaximumIntervalSeconds)
            : MinimumIntervalSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _configurationGeneration++;
        _history.Clear();
        _requestPending = false;
        _lastError = string.Empty;
        ScheduleInitialDecision();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _configurationGeneration++;
        _history.Clear();
        _requestPending = false;
        _nextDecision = TimeSpan.Zero;
        _nextUpdate = TimeSpan.Zero;
        _lastError = string.Empty;
    }

    private void OnNarrativeEnabledChanged(bool enabled)
    {
        ResetConfigurationSchedule(enabled);
    }

    private void OnNarrativeEventIdsChanged(string value)
    {
        ResetConfigurationSchedule(_cfg.GetCVar(CCVars.MafiaDirectorNarrativeEnabled));
    }

    private void OnNarrativeThemeChanged(string value)
    {
        ResetConfigurationSchedule(_cfg.GetCVar(CCVars.MafiaDirectorNarrativeEnabled));
    }

    private void OnNarrativeStartGateChanged(bool enabled)
    {
        ResetConfigurationSchedule(_cfg.GetCVar(CCVars.MafiaDirectorNarrativeEnabled));
    }

    private void ResetConfigurationSchedule(bool enabled)
    {
        _configurationGeneration++;
        _requestPending = false;
        _lastError = string.Empty;
        if (enabled)
            ScheduleInitialDecision();
        else
            _nextDecision = TimeSpan.Zero;
    }

    private void ScheduleInitialDecision()
    {
        var configured = _cfg.GetCVar(CCVars.MafiaDirectorNarrativeInitialDelaySeconds);
        var seconds = float.IsFinite(configured)
            ? Math.Clamp(configured, 0f, MaximumInitialDelaySeconds)
            : 0f;
        _nextDecision = _timing.CurTime + TimeSpan.FromSeconds(seconds);
        _nextUpdate = TimeSpan.Zero;
    }
}

public sealed record LlmNarrativeStatus(
    bool Enabled,
    bool RequestPending,
    bool AutomaticEventStartEnabled,
    int ConfiguredRuleCount,
    int RememberedChoiceCount,
    TimeSpan NextDecisionIn,
    string LastError);
