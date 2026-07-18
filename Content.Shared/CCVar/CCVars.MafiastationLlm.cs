using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

/// <summary>
/// Server-only configuration for Mafiastation's provider-neutral LLM gateway and advisory moderation.
/// Secrets must never be replicated to clients.
/// </summary>
public sealed partial class CCVars
{
    public static readonly CVarDef<bool> MafiaLlmEnabled =
        CVarDef.Create("mafia.llm.enabled", false, CVar.SERVERONLY);

    /// <summary>Supported values are "anthropic" and "openai-compatible".</summary>
    public static readonly CVarDef<string> MafiaLlmProvider =
        CVarDef.Create("mafia.llm.provider", "anthropic", CVar.SERVERONLY);

    public static readonly CVarDef<string> MafiaLlmEndpoint =
        CVarDef.Create("mafia.llm.endpoint", "https://api.anthropic.com/v1/messages", CVar.SERVERONLY);

    public static readonly CVarDef<string> MafiaLlmModel =
        CVarDef.Create("mafia.llm.model", "claude-haiku-4-5", CVar.SERVERONLY);

    /// <summary>MAFIA_LLM_KEY takes precedence over this confidential CVar.</summary>
    public static readonly CVarDef<string> MafiaLlmApiKey =
        CVarDef.Create("mafia.llm.api_key", string.Empty, CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>Only enable this for a trusted local endpoint.</summary>
    public static readonly CVarDef<bool> MafiaLlmAllowUnauthenticated =
        CVarDef.Create("mafia.llm.allow_unauthenticated", false, CVar.SERVERONLY);

    /// <summary>OpenAI-compatible mode: "none", "json_object", or "json_schema".</summary>
    public static readonly CVarDef<string> MafiaLlmResponseFormat =
        CVarDef.Create("mafia.llm.response_format", "json_object", CVar.SERVERONLY);

    public static readonly CVarDef<float> MafiaLlmTimeoutSeconds =
        CVarDef.Create("mafia.llm.timeout_seconds", 20f, CVar.SERVERONLY);

    public static readonly CVarDef<int> MafiaLlmMaxOutputTokens =
        CVarDef.Create("mafia.llm.max_output_tokens", 600, CVar.SERVERONLY);

    public static readonly CVarDef<int> MafiaLlmRequestsPerMinute =
        CVarDef.Create("mafia.llm.requests_per_minute", 2, CVar.SERVERONLY);

    /// <summary>Approximate shared input plus output token reservations per round.</summary>
    public static readonly CVarDef<int> MafiaLlmRoundTokenBudget =
        CVarDef.Create("mafia.llm.round_token_budget", 30000, CVar.SERVERONLY);

    public static readonly CVarDef<bool> MafiaModerationEnabled =
        CVarDef.Create("mafia.moderation.enabled", false, CVar.SERVERONLY);

    public static readonly CVarDef<float> MafiaModerationBatchSeconds =
        CVarDef.Create("mafia.moderation.batch_seconds", 30f, CVar.SERVERONLY);

    public static readonly CVarDef<int> MafiaModerationBatchMessages =
        CVarDef.Create("mafia.moderation.batch_messages", 40, CVar.SERVERONLY);

    public static readonly CVarDef<int> MafiaModerationMaxQueuedMessages =
        CVarDef.Create("mafia.moderation.max_queued_messages", 200, CVar.SERVERONLY);

    public static readonly CVarDef<float> MafiaModerationMinimumConfidence =
        CVarDef.Create("mafia.moderation.minimum_confidence", 0.75f, CVar.SERVERONLY);

    /// <summary>Valid moderation severities are 1 through 4.</summary>
    public static readonly CVarDef<int> MafiaModerationHighSeverity =
        CVarDef.Create("mafia.moderation.high_severity", 3, CVar.SERVERONLY);

    /// <summary>Relative path beneath the server user-data directory.</summary>
    public static readonly CVarDef<string> MafiaModerationIncidentPath =
        CVarDef.Create("mafia.moderation.incident_path", "moderation_incidents.json", CVar.SERVERONLY);

    /// <summary>
    /// Reserved safety gate. The current implementation never takes punitive automatic action.
    /// </summary>
    public static readonly CVarDef<bool> MafiaModerationAllowAutomaticActions =
        CVarDef.Create("mafia.moderation.allow_automatic_actions", false, CVar.SERVERONLY);

    /// <summary>Enables admin-triggered, allowlist-only NPC and event choice requests.</summary>
    public static readonly CVarDef<bool> MafiaDirectorEnabled =
        CVarDef.Create("mafia.director.enabled", false, CVar.SERVERONLY);

    public static readonly CVarDef<float> MafiaDirectorMinimumConfidence =
        CVarDef.Create("mafia.director.minimum_confidence", 0.7f, CVar.SERVERONLY);

    public static readonly CVarDef<int> MafiaDirectorMaxPendingRequests =
        CVarDef.Create("mafia.director.max_pending_requests", 4, CVar.SERVERONLY);

    /// <summary>Second opt-in gate for periodic contextual NPC decisions.</summary>
    public static readonly CVarDef<bool> MafiaDirectorNpcAutonomyEnabled =
        CVarDef.Create("mafia.director.npc_autonomy_enabled", false, CVar.SERVERONLY);

    /// <summary>Hard lower bound for each autonomous NPC's decision interval.</summary>
    public static readonly CVarDef<float> MafiaDirectorNpcMinimumDecisionSeconds =
        CVarDef.Create("mafia.director.npc_minimum_decision_seconds", 60f, CVar.SERVERONLY);

    public static readonly CVarDef<int> MafiaDirectorNpcMaximumSpeechMemories =
        CVarDef.Create("mafia.director.npc_maximum_speech_memories", 12, CVar.SERVERONLY);

    /// <summary>Independent default-off gate for contextual NPC dialogue proposals.</summary>
    public static readonly CVarDef<bool> MafiaDirectorNpcDialogueEnabled =
        CVarDef.Create("mafia.director.npc_dialogue_enabled", false, CVar.SERVERONLY);

    /// <summary>Separate default-off actuation gate for validated model text entering IC chat.</summary>
    public static readonly CVarDef<bool> MafiaDirectorNpcDialogueAllowSpeech =
        CVarDef.Create("mafia.director.npc_dialogue_allow_speech", false, CVar.SERVERONLY);

    /// <summary>Hard lower bound for each configured NPC's dialogue-proposal interval.</summary>
    public static readonly CVarDef<float> MafiaDirectorNpcDialogueMinimumDecisionSeconds =
        CVarDef.Create("mafia.director.npc_dialogue_minimum_seconds", 120f, CVar.SERVERONLY);

    /// <summary>Maximum normalized characters in one generated IC line.</summary>
    public static readonly CVarDef<int> MafiaDirectorNpcDialogueMaximumCharacters =
        CVarDef.Create("mafia.director.npc_dialogue_maximum_characters", 180, CVar.SERVERONLY);

    /// <summary>
    /// Second gate for llmevent --start. Preview selection remains available when this is false.
    /// </summary>
    public static readonly CVarDef<bool> MafiaDirectorAllowEventStart =
        CVarDef.Create("mafia.director.allow_event_start", false, CVar.SERVERONLY);

    /// <summary>Third opt-in gate for the recurring station narrative loop.</summary>
    public static readonly CVarDef<bool> MafiaDirectorNarrativeEnabled =
        CVarDef.Create("mafia.director.narrative_enabled", false, CVar.SERVERONLY);

    /// <summary>Comma-separated existing game-rule prototype IDs; empty means no candidates.</summary>
    public static readonly CVarDef<string> MafiaDirectorNarrativeEventIds =
        CVarDef.Create("mafia.director.narrative_event_ids", string.Empty, CVar.SERVERONLY);

    /// <summary>Game-authored tone and pacing guidance. It is prompt context, never authority.</summary>
    public static readonly CVarDef<string> MafiaDirectorNarrativeTheme =
        CVarDef.Create("mafia.director.narrative_theme", string.Empty, CVar.SERVERONLY);

    public static readonly CVarDef<float> MafiaDirectorNarrativeInitialDelaySeconds =
        CVarDef.Create("mafia.director.narrative_initial_delay_seconds", 300f, CVar.SERVERONLY);

    public static readonly CVarDef<float> MafiaDirectorNarrativeIntervalSeconds =
        CVarDef.Create("mafia.director.narrative_interval_seconds", 600f, CVar.SERVERONLY);

    public static readonly CVarDef<float> MafiaDirectorNarrativeRepeatCooldownSeconds =
        CVarDef.Create("mafia.director.narrative_repeat_cooldown_seconds", 1800f, CVar.SERVERONLY);

    public static readonly CVarDef<int> MafiaDirectorNarrativeMaximumMemories =
        CVarDef.Create("mafia.director.narrative_maximum_memories", 8, CVar.SERVERONLY);

    /// <summary>
    /// Third event-start gate for the autonomous narrative loop. Preview choices do not need it.
    /// </summary>
    public static readonly CVarDef<bool> MafiaDirectorNarrativeAllowEventStart =
        CVarDef.Create("mafia.director.narrative_allow_event_start", false, CVar.SERVERONLY);

    /// <summary>Third opt-in gate for recurring, coordinated multi-NPC scene decisions.</summary>
    public static readonly CVarDef<bool> MafiaDirectorNpcScenesEnabled =
        CVarDef.Create("mafia.director.npc_scenes_enabled", false, CVar.SERVERONLY);

    /// <summary>Hard lower bound for coordinated NPC scene-decision intervals.</summary>
    public static readonly CVarDef<float> MafiaDirectorNpcSceneMinimumDecisionSeconds =
        CVarDef.Create("mafia.director.npc_scene_minimum_decision_seconds", 60f, CVar.SERVERONLY);
}
