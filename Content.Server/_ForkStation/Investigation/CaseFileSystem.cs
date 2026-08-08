using System.Linq;
using Content.Shared._ForkStation.Investigation;
using Content.Shared.Access.Components;
using Robust.Shared.Utility;

namespace Content.Server._ForkStation.Investigation;

/// <summary>
/// One entry on a suspect's timeline.
/// </summary>
/// <param name="Time">Station time, so entries from different sources line up.</param>
/// <param name="Source">Where the evidence came from: a camera, a door, a body.</param>
/// <param name="Detail">What it says.</param>
public readonly record struct CaseFileEntry(TimeSpan Time, string Source, string Detail);

/// <summary>
/// Assembles scattered evidence into something a detective can read.
///
/// The station already produces plenty of evidence — door swipes are timestamped and named,
/// cameras now remember who passed them, bodies now remember who killed them — but it is spread
/// across dozens of separate logs that nobody can practically collate by hand during a shift.
/// The design doc's whole argument is that persistent penalties are only legitimate if security
/// can actually build a case; a pile of unreadable logs is not a case.
///
/// This deliberately reports only what the station recorded. It does not decide guilt, and it can
/// be misled: every name here is an <c>Identity</c>, so a killer in a mask carrying someone
/// else's ID appears as that someone else. Framing stays possible, which the doc explicitly wants.
/// </summary>
public sealed class CaseFileSystem : EntitySystem
{
    [Dependency] private readonly CameraSightingsSystem _cameras = default!;

    /// <summary>
    /// Everything the station recorded about <paramref name="subject"/>, in time order.
    /// </summary>
    public List<CaseFileEntry> BuildTimeline(string subject)
    {
        var entries = new List<CaseFileEntry>();

        if (string.IsNullOrWhiteSpace(subject))
            return entries;

        CollectCameraSightings(subject, entries);
        CollectDoorAccesses(subject, entries);
        CollectDeaths(subject, entries);

        return entries.OrderBy(entry => entry.Time).ToList();
    }

    private void CollectCameraSightings(string subject, List<CaseFileEntry> entries)
    {
        foreach (var (camera, sighting) in _cameras.TrackSubject(subject))
        {
            entries.Add(new CaseFileEntry(
                sighting.SightingTime,
                "camera",
                $"seen by {camera}"));
        }
    }

    private void CollectDoorAccesses(string subject, List<CaseFileEntry> entries)
    {
        var query = EntityQueryEnumerator<AccessReaderComponent>();
        while (query.MoveNext(out var uid, out var reader))
        {
            foreach (var record in reader.AccessLog)
            {
                // Door logs store "Name, Job" via the identity short-info path, so match loosely.
                if (!record.Accessor.Contains(subject, StringComparison.OrdinalIgnoreCase))
                    continue;

                entries.Add(new CaseFileEntry(
                    record.AccessTime,
                    "door",
                    $"opened {Name(uid)}"));
            }
        }
    }

    private void CollectDeaths(string subject, List<CaseFileEntry> entries)
    {
        var query = EntityQueryEnumerator<DeathCircumstancesComponent>();
        while (query.MoveNext(out var uid, out var death))
        {
            if (death.TimeOfDeath is not { } time)
                continue;

            var victim = Name(uid);

            // The subject as the accused.
            if (death.HasSuspect &&
                death.SuspectName != null &&
                death.SuspectName.Contains(subject, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new CaseFileEntry(
                    time,
                    "body",
                    $"implicated in the death of {victim} ({death.KillingDamageType ?? "unknown cause"})"));
            }

            // The subject as the victim.
            if (victim.Contains(subject, StringComparison.OrdinalIgnoreCase))
            {
                var attribution = death.HasSuspect
                    ? $"attributed to {death.SuspectName}"
                    : "no suspect identified";

                entries.Add(new CaseFileEntry(
                    time,
                    "body",
                    $"died of {death.KillingDamageType ?? "unknown causes"}, {attribution}"));
            }
        }
    }

    /// <summary>
    /// The timeline as lines of text, for a console or a printout.
    /// </summary>
    public List<string> FormatTimeline(string subject)
    {
        var timeline = BuildTimeline(subject);

        if (timeline.Count == 0)
            return new List<string> { $"No station records mention {subject}." };

        var lines = new List<string> { $"Station records for {subject}:" };

        foreach (var entry in timeline)
        {
            lines.Add($"  [{entry.Time:hh\\:mm\\:ss}] {entry.Source,-6} {entry.Detail}");
        }

        lines.Add($"{timeline.Count} record(s). Records show movement and proximity, not guilt.");
        return lines;
    }
}
