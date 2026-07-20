namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// Coarse capacities exposed to the hybrid executive. These are an authoring allowlist, not a
/// replacement for normal SS14 action blockers or HTN preconditions; both are checked again at
/// runtime.
/// </summary>
public enum HybridNpcCapability : byte
{
    Move,
    Interact,
    Hands,
    Speak,
    Combat,
    OpenDoors,
}

[DataDefinition]
public sealed partial class HybridNpcComplexGoal
{
    /// <summary>Opaque decision ID sent to the model.</summary>
    [DataField(required: true)]
    public string Id = string.Empty;

    /// <summary>Existing server-authored HTN compound root applied after selection.</summary>
    [DataField(required: true)]
    public string Task = string.Empty;

    [DataField]
    public string Description = string.Empty;

    [DataField]
    public HashSet<HybridNpcCapability> RequiredCapabilities = new();
}

/// <summary>
/// Runs one ordinary HTN root for routine behavior and consults the bounded LLM director only when
/// authored escalation conditions are met. A selected complex root receives a finite lease, then
/// control automatically returns to the routine root.
/// </summary>
[RegisterComponent]
public sealed partial class LlmNpcHybridComponent : Component
{
    [DataField(required: true)]
    public string RoutineTask = string.Empty;

    [DataField]
    public HashSet<HybridNpcCapability> Capabilities = new();

    [DataField]
    public List<HybridNpcComplexGoal> ComplexGoals = new();

    [DataField]
    public string Persona = string.Empty;

    [DataField]
    public float EvaluationIntervalSeconds = 1f;

    [DataField]
    public float NoPlanEscalationSeconds = 8f;

    [DataField]
    public float DecisionCooldownSeconds = 60f;

    [DataField]
    public float ComplexGoalLeaseSeconds = 30f;

    [DataField]
    public float ObservationRadius = 12f;

    [DataField]
    public float MemorySeconds = 180f;

    [DataField]
    public bool EscalateOnSpeech = true;

    public TimeSpan NextEvaluation;
    public TimeSpan NoPlanSince;
    public TimeSpan NextEscalation;
    public TimeSpan LeaseEnds;
    public bool PendingDecision;
    public bool ComplexGoalActive;
    public bool PlayerSuspended;
    public bool SpeechDirty;
    public int ConfigurationGeneration;
    public string ActiveGoalId = string.Empty;
    public string LastEscalationReason = string.Empty;
    public string LastOutcome = string.Empty;
    public readonly Queue<LlmNpcSpeechMemory> RecentSpeech = new();
}
