namespace Content.Server._ForkStation.Horror;

/// <summary>
/// Station event: one official announcement that is subtly wrong.
/// </summary>
[RegisterComponent]
public sealed partial class PhantomAnnouncementRuleComponent : Component
{
    /// <summary>
    /// Locale ids of the static phantom announcements. One is picked at random
    /// (when the dynamic crew-census line isn't rolled instead).
    /// </summary>
    [DataField]
    public List<string> Lines = new()
    {
        "mafiastation-phantom-1",
        "mafiastation-phantom-2",
        "mafiastation-phantom-3",
        "mafiastation-phantom-4",
        "mafiastation-phantom-5",
        "mafiastation-phantom-6",
    };
}
