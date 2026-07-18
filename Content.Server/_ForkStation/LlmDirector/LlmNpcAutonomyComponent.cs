namespace Content.Server._ForkStation.LlmDirector;

/// <summary>
/// Opt-in, server-side executive layer for an existing HTN NPC. The model may select only one of
/// <see cref="AllowedGoals"/>; ordinary HTN operators remain solely responsible for game actions.
/// </summary>
[RegisterComponent]
public sealed partial class LlmNpcAutonomyComponent : Component
{
    /// <summary>
    /// Existing HTN compound-root prototype IDs. At least two are required.
    /// </summary>
    [DataField]
    public List<string> AllowedGoals = new();

    /// <summary>
    /// Game-authored character purpose and temperament. This is prompt context, not authority.
    /// </summary>
    [DataField]
    public string Persona = string.Empty;

    /// <summary>
    /// Requested time between executive decisions. A server CVar supplies the hard lower bound.
    /// </summary>
    [DataField]
    public float DecisionIntervalSeconds = 90f;

    /// <summary>
    /// Radius in which accepted IC speech becomes short-lived context.
    /// </summary>
    [DataField]
    public float ObservationRadius = 12f;

    /// <summary>
    /// Maximum age of nearby-speech memories.
    /// </summary>
    [DataField]
    public float MemorySeconds = 180f;

    public TimeSpan NextDecision;
    public string LastObservedGoal = string.Empty;
    public readonly Queue<LlmNpcSpeechMemory> RecentSpeech = new();
    public readonly Queue<LlmNpcGoalMemory> RecentGoals = new();
}

public sealed record LlmNpcSpeechMemory(
    TimeSpan ObservedAt,
    string Speaker,
    string Message);

public sealed record LlmNpcGoalMemory(
    TimeSpan ObservedAt,
    string Goal);
