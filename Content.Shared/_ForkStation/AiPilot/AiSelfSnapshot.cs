using System.Linq;
using Content.Shared.Atmos.Components;
using Content.Shared.Body;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Standing;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Shared._ForkStation.AiPilot;

/// <summary>
/// A bounded, server-authored description of facts a character can reasonably know about itself.
/// It deliberately excludes objectives, antagonist roles, administrator state, and account data.
/// </summary>
public sealed record AiSelfSnapshot(
    AiSelfIdentity Identity,
    AiSelfAppearance Appearance,
    IReadOnlyList<AiSelfEquipmentSlot> Equipment,
    IReadOnlyList<AiSelfHand> Hands,
    AiSelfCondition Body,
    AiSelfActivity Activity);

public sealed record AiSelfIdentity(
    string Name,
    string? SpeciesId,
    string? Species,
    int? Age,
    string? Sex,
    string? Gender,
    string? Pronouns,
    AiSelfRole Role);

public sealed record AiSelfRole(string? Id, string? Title, string Source);

public sealed record AiSelfAppearance(
    bool Available,
    bool? Bald,
    IReadOnlyList<AiSelfMarking> Hair,
    bool? CleanShaven,
    IReadOnlyList<AiSelfMarking> FacialHair,
    string? EyeColor,
    string? SkinColor);

public sealed record AiSelfMarking(
    string Id,
    string Style,
    IReadOnlyList<string> Colors);

public sealed record AiSelfEquipmentSlot(string Slot, string? Item);

public sealed record AiSelfHand(string Id, bool Active, string? Item);

public sealed record AiSelfCondition(
    string? LifeState,
    string? Hunger,
    string? Thirst,
    bool? OnFire,
    bool? Standing,
    bool? Cuffed,
    float? TotalDamage = null,
    string? DamageSeverity = null);

public sealed record AiSelfActivity(
    string Controller,
    string State,
    string? Goal,
    string? Target,
    string? Detail)
{
    public static AiSelfActivity Idle(string controller) =>
        new(controller, "idle", null, null, null);
}

/// <summary>
/// Reads live component state into a compact self snapshot. This is shared by connected pilots and
/// server-owned NPCs so both receive the same identity, body, appearance, equipment, and hand facts.
/// </summary>
public sealed class AiSelfSnapshotSystem : EntitySystem
{
    private const int MaximumNameCharacters = 80;
    private const int MaximumEquipmentSlots = 24;
    private const int MaximumHands = 8;
    private const int MaximumMarkingsPerLayer = 8;

    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedVisualBodySystem _visualBody = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;

    public AiSelfSnapshot Capture(
        EntityUid uid,
        AiSelfActivity activity,
        string? configuredRoleId = null)
    {
        return new AiSelfSnapshot(
            BuildIdentity(uid, configuredRoleId),
            BuildAppearance(uid),
            BuildEquipment(uid),
            BuildHands(uid),
            BuildCondition(uid),
            activity);
    }

    private AiSelfIdentity BuildIdentity(EntityUid uid, string? configuredRoleId)
    {
        string? speciesId = null;
        string? species = null;
        int? age = null;
        string? sex = null;
        string? gender = null;
        string? pronouns = null;
        if (TryComp<HumanoidProfileComponent>(uid, out var profile))
        {
            speciesId = profile.Species.Id;
            species = _prototypes.TryIndex(profile.Species, out SpeciesPrototype? prototype)
                ? Normalize(Loc.GetString(prototype.Name), MaximumNameCharacters)
                : speciesId;
            age = profile.Age;
            sex = EnumName(profile.Sex);
            gender = EnumName(profile.Gender);
            pronouns = profile.Gender switch
            {
                Gender.Male => "he/him",
                Gender.Female => "she/her",
                Gender.Neuter => "it/its",
                _ => "they/them",
            };
        }

        return new AiSelfIdentity(
            Normalize(Name(uid), MaximumNameCharacters),
            speciesId,
            species,
            age,
            sex,
            gender,
            pronouns,
            BuildRole(uid, configuredRoleId));
    }

    private AiSelfRole BuildRole(EntityUid uid, string? configuredRoleId)
    {
        if (TryComp<MindContainerComponent>(uid, out var mind) &&
            mind.Mind is { } mindId &&
            _jobs.MindTryGetJobId(mindId, out var jobId) &&
            jobId is { } actualId)
        {
            _jobs.MindTryGetJobName(mindId, out var title);
            return new AiSelfRole(
                actualId.Id,
                Normalize(title, MaximumNameCharacters),
                "mind");
        }

        var fallback = configuredRoleId?.Trim();
        if (!string.IsNullOrEmpty(fallback) &&
            _prototypes.TryIndex<JobPrototype>(fallback, out var job))
        {
            return new AiSelfRole(
                job.ID,
                Normalize(job.LocalizedName, MaximumNameCharacters),
                "configured");
        }

        return new AiSelfRole(null, null, "none");
    }

    private AiSelfAppearance BuildAppearance(EntityUid uid)
    {
        var layers = new HashSet<HumanoidVisualLayers>
        {
            HumanoidVisualLayers.Hair,
            HumanoidVisualLayers.FacialHair,
        };
        if (!_visualBody.TryGatherMarkingsData(
                uid,
                layers,
                out var profiles,
                out _,
                out var applied))
        {
            return new AiSelfAppearance(
                false,
                null,
                Array.Empty<AiSelfMarking>(),
                null,
                Array.Empty<AiSelfMarking>(),
                null,
                null);
        }

        var hair = BuildMarkings(applied, HumanoidVisualLayers.Hair);
        var facialHair = BuildMarkings(applied, HumanoidVisualLayers.FacialHair);
        string? eyeColor = null;
        string? skinColor = null;
        if (profiles.Count > 0)
        {
            var profile = profiles.Values.First();
            eyeColor = profile.EyeColor.ToHex();
            skinColor = profile.SkinColor.ToHex();
        }

        return new AiSelfAppearance(
            true,
            hair.Count == 0,
            hair,
            facialHair.Count == 0,
            facialHair,
            eyeColor,
            skinColor);
    }

    private IReadOnlyList<AiSelfMarking> BuildMarkings(
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>,
            Dictionary<HumanoidVisualLayers, List<Marking>>> applied,
        HumanoidVisualLayers layer)
    {
        var result = new List<AiSelfMarking>();
        foreach (var organ in applied.Values)
        {
            if (!organ.TryGetValue(layer, out var markings))
                continue;

            foreach (var marking in markings)
            {
                if (result.Count >= MaximumMarkingsPerLayer)
                    return result;

                var id = marking.MarkingId.Id;
                var style = _prototypes.TryIndex(marking.MarkingId, out MarkingPrototype? prototype)
                    ? Normalize(Loc.GetString(prototype.Name), MaximumNameCharacters)
                    : id;
                result.Add(new AiSelfMarking(
                    id,
                    style,
                    marking.MarkingColors.Select(color => color.ToHex()).ToArray()));
            }
        }

        return result;
    }

    private IReadOnlyList<AiSelfEquipmentSlot> BuildEquipment(EntityUid uid)
    {
        if (!TryComp<InventoryComponent>(uid, out var inventory))
            return Array.Empty<AiSelfEquipmentSlot>();

        var result = new List<AiSelfEquipmentSlot>();
        var slots = _inventory.GetSlotEnumerator((uid, inventory));
        while (result.Count < MaximumEquipmentSlots && slots.MoveNext(out var container))
        {
            var item = container.ContainedEntity is { } contained && Exists(contained)
                ? Normalize(Name(contained), MaximumNameCharacters)
                : null;
            result.Add(new AiSelfEquipmentSlot(container.ID, item));
        }

        result.Sort(static (left, right) => string.CompareOrdinal(left.Slot, right.Slot));
        return result;
    }

    private IReadOnlyList<AiSelfHand> BuildHands(EntityUid uid)
    {
        if (!TryComp<HandsComponent>(uid, out var hands))
            return Array.Empty<AiSelfHand>();

        return _hands.EnumerateHands((uid, hands))
            .Take(MaximumHands)
            .Select(handId =>
            {
                var held = _hands.GetHeldItem((uid, hands), handId);
                var item = held is { } entity && Exists(entity)
                    ? Normalize(Name(entity), MaximumNameCharacters)
                    : null;
                return new AiSelfHand(handId, handId == hands.ActiveHandId, item);
            })
            .ToArray();
    }

    private AiSelfCondition BuildCondition(EntityUid uid)
    {
        string? lifeState = null;
        if (TryComp<MobStateComponent>(uid, out var mobState))
        {
            lifeState = _mobState.IsAlive(uid, mobState)
                ? "alive"
                : _mobState.IsCritical(uid, mobState)
                    ? "critical"
                    : _mobState.IsDead(uid, mobState)
                        ? "dead"
                        : "unknown";
        }

        var hunger = TryComp<HungerComponent>(uid, out var hungerComponent)
            ? EnumName(hungerComponent.CurrentThreshold)
            : null;
        var thirst = TryComp<ThirstComponent>(uid, out var thirstComponent)
            ? EnumName(thirstComponent.CurrentThirstThreshold)
            : null;
        bool? onFire = TryComp<FlammableComponent>(uid, out var flammable)
            ? flammable.OnFire
            : null;
        bool? standing = TryComp<StandingStateComponent>(uid, out var standingState)
            ? standingState.Standing
            : null;
        bool? cuffed = TryComp<CuffableComponent>(uid, out var cuffable)
            ? cuffable.CuffedHandCount > 0
            : null;

        float? totalDamage = null;
        string? damageSeverity = null;
        if (TryComp<DamageableComponent>(uid, out var damageable))
        {
            totalDamage = _damageable.GetTotalDamage((uid, damageable)).Float();
            damageSeverity = lifeState switch
            {
                "dead" => "dead",
                "critical" => "critical",
                _ when totalDamage <= 0.01f => "uninjured",
                _ when totalDamage < 25f => "minor",
                _ when totalDamage < 50f => "moderate",
                _ => "severe",
            };
        }

        return new AiSelfCondition(
            lifeState,
            hunger,
            thirst,
            onFire,
            standing,
            cuffed,
            totalDamage,
            damageSeverity);
    }

    private static string Normalize(string value, int maximumCharacters)
    {
        var normalized = string.Join(
            " ",
            value.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters];
    }

    private static string EnumName<T>(T value) where T : struct, Enum =>
        value.ToString().ToLowerInvariant();
}
