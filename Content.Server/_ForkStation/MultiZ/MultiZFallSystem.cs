using Content.Shared._ForkStation.MultiZ;
using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Events;
using Content.Shared.StepTrigger.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;

namespace Content.Server._ForkStation.MultiZ;

/// <summary>
/// Ports SS13's "fall down a z-level": stepping onto an open <see cref="MultiZLinkComponent"/> shaft
/// makes an entity fall, and a moment later it is teleported to the paired link on the level below.
/// Uses the existing StepTrigger seam (the same one chasms use), but instead of deleting the faller
/// it relocates them, aligned to the shaft's landing marker.
/// </summary>
public sealed class MultiZFallSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private static readonly TimeSpan FallDuration = TimeSpan.FromSeconds(0.9);
    private static readonly SoundSpecifier FallSound = new SoundPathSpecifier("/Audio/Effects/falling.ogg");
    private static readonly SoundSpecifier LandSound = new SoundPathSpecifier("/Audio/Effects/thudswoosh.ogg");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MultiZLinkComponent, StepTriggeredOffEvent>(OnStepTriggered);
        SubscribeLocalEvent<MultiZLinkComponent, StepTriggerAttemptEvent>(OnStepAttempt);
        SubscribeLocalEvent<MultiZFallingComponent, UpdateCanMoveEvent>(OnUpdateCanMove);
    }

    private void OnStepAttempt(Entity<MultiZLinkComponent> ent, ref StepTriggerAttemptEvent args)
    {
        // Only an open shaft with a level below can be fallen through.
        if (ent.Comp.Open && ent.Comp.Down is { } down && !Deleted(down))
            args.Continue = true;
    }

    private void OnStepTriggered(Entity<MultiZLinkComponent> ent, ref StepTriggeredOffEvent args)
    {
        var faller = args.Tripper;
        if (HasComp<MultiZFallingComponent>(faller))
            return;

        if (!ent.Comp.Open || ent.Comp.Down is not { } down || Deleted(down))
            return;

        var falling = AddComp<MultiZFallingComponent>(faller);
        falling.Target = down;
        falling.LandTime = _timing.CurTime + FallDuration;
        _blocker.UpdateCanMove(faller);
        _audio.PlayPvs(FallSound, faller);
    }

    private void OnUpdateCanMove(Entity<MultiZFallingComponent> ent, ref UpdateCanMoveEvent args)
    {
        args.Cancel(); // you cannot walk while falling
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<MultiZFallingComponent>();
        while (query.MoveNext(out var uid, out var falling))
        {
            if (now < falling.LandTime)
                continue;

            if (!Deleted(falling.Target))
            {
                var targetXform = Transform(falling.Target);
                _transform.SetCoordinates(uid, targetXform.Coordinates);
                _audio.PlayPvs(LandSound, uid);
            }

            RemComp<MultiZFallingComponent>(uid);
            _blocker.UpdateCanMove(uid);
        }
    }
}
