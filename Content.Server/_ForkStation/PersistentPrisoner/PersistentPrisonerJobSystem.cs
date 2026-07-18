using Content.Server.GameTicking.Events;
using Content.Shared.Roles;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;

namespace Content.Server._ForkStation.PersistentPrisoner;

/// <summary>
/// Hooks into job assignment to force penalized players into the Prisoner role.
/// Restricts all non-Prisoner jobs for players with active penalties.
/// </summary>
public sealed class PersistentPrisonerJobSystem : EntitySystem
{
    [Dependency] private readonly PersistentPrisonerSystem _penalties = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;

    private static readonly ISawmill Log = Logger.GetSawmill("persistent.prisoner.jobs");

    private const string PrisonerJobId = "Prisoner";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GetDisallowedJobsEvent>(OnGetDisallowedJobs);
    }

    private void OnGetDisallowedJobs(ref GetDisallowedJobsEvent ev)
    {
        var userId = ev.Player.UserId.ToString();

        if (!_penalties.ShouldSpawnAsPrisoner(userId))
            return;

        // Block every job except Prisoner.
        foreach (var proto in _protoManager.EnumeratePrototypes<JobPrototype>())
        {
            if (proto.ID == PrisonerJobId)
                continue;

            ev.Jobs.Add(proto.ID);
        }

        var penaltyCount = _penalties.GetPenaltyRounds(userId);
        Log.Info($"Restricted {ev.Player.Name} to Prisoner role ({penaltyCount} penalty rounds remaining).");
    }
}
