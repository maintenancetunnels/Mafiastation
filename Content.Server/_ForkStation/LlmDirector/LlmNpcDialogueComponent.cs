namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// Opt-in, server-side dialogue layer for an existing, unpossessed HTN NPC. Model output remains
/// a proposal until <c>mafia.director.npc_dialogue_allow_speech</c> is enabled and the server
/// revalidates the entity at completion time.
/// </summary>
[RegisterComponent]
public sealed partial class LlmNpcDialogueComponent : Component
{
    /// <summary>Game-authored character and voice guidance. Prompt context only, never authority.</summary>
    [DataField]
    public string Persona = string.Empty;

    /// <summary>Requested time between proposals. A server CVar supplies the hard lower bound.</summary>
    [DataField]
    public float DecisionIntervalSeconds = 120f;

    /// <summary>Radius in which accepted IC speech becomes short-lived context.</summary>
    [DataField]
    public float ObservationRadius = 12f;

    /// <summary>Maximum age of speech and prior-utterance memories.</summary>
    [DataField]
    public float MemorySeconds = 180f;

    public TimeSpan NextDecision;
    public ulong ObservationSequence;
    public ulong LastRequestedObservationSequence;
    public uint Revision;
    public bool ForceNextDecision;
    public string? RequestedReplyChannelId;
    public ulong RequestedReplyObservationSequence;
    public string LastOutcome = string.Empty;
    public readonly Queue<LlmNpcDialogueSpeechMemory> RecentSpeech = new();
    public readonly Queue<LlmNpcDialogueUtteranceMemory> RecentUtterances = new();
}

public sealed record LlmNpcDialogueSpeechMemory(
    TimeSpan ObservedAt,
    string Speaker,
    string Message,
    string? RadioChannelId = null);

public sealed record LlmNpcDialogueUtteranceMemory(
    TimeSpan ObservedAt,
    string Text,
    string Tone,
    bool Spoken);

public sealed record LlmNpcDialogueStatus(
    bool GenerationEnabled,
    bool SpeechEnabled,
    bool Configured,
    bool RequestPending,
    TimeSpan NextDecisionIn,
    int SpeechMemoryCount,
    int UtteranceMemoryCount,
    string LastOutcome);
