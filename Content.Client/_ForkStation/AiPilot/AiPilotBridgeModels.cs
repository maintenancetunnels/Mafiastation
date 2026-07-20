namespace Content.Client._ForkStation.AiPilot;

internal enum AiPilotGoalState : byte
{
    None,
    Planning,
    Moving,
    Completed,
    Failed,
    Cancelled,
}

internal sealed class AiPilotAuthorizationSnapshot
{
    public bool Authorized;
    public string Reason = "Authorization has not been checked.";
    public bool AllowJoin;
    public bool AllowSpeech;
    public int SpeechMaxCharacters = 1;
    public float SpeechCooldownSeconds = 60f;
    public float ObservationRadius = 1f;
    public int MaximumObservedEntities = 1;
    public float MaximumGoalDistance = 1f;
    public int MaximumPathWaypoints = 4;
    public float GoalTimeoutSeconds = 5f;
    public TimeSpan UpdatedAt;
}
