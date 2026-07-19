namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Station event: random crew members receive whispers nobody sent.
/// </summary>
[RegisterComponent]
public sealed partial class WhisperRuleComponent : Component
{
    /// <summary>
    /// Seconds between whispers while the event runs.
    /// </summary>
    [DataField]
    public float Interval = 18f;

    public float Accumulator;

    /// <summary>
    /// Chance a whisper uses the victim's own first name.
    /// </summary>
    [DataField]
    public float PersonalizedChance = 0.3f;

    /// <summary>
    /// Locale ids of the anonymous whisper lines.
    /// </summary>
    [DataField]
    public List<string> Lines = new()
    {
        "mafiastation-whisper-1",
        "mafiastation-whisper-2",
        "mafiastation-whisper-3",
        "mafiastation-whisper-4",
        "mafiastation-whisper-5",
    };
}
