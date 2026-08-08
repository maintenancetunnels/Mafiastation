using Content.Shared._ForkStation.MultiZ;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;

namespace Content.Server._ForkStation.MultiZ;

/// <summary>
/// The way back up: activating a shaft landing (a <see cref="MultiZLinkComponent"/> with an
/// <c>Up</c> target) climbs the ladder to the shaft on the level above. Falling is involuntary and
/// fast; climbing is a deliberate interaction.
/// </summary>
public sealed class MultiZClimbSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private static readonly SoundSpecifier ClimbSound = new SoundPathSpecifier("/Audio/Effects/thudswoosh.ogg");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MultiZLinkComponent, ActivateInWorldEvent>(OnActivate);
    }

    private void OnActivate(Entity<MultiZLinkComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled)
            return;

        if (ent.Comp.Up is not { } up || Deleted(up))
            return;

        var upXform = Transform(up);
        _transform.SetCoordinates(args.User, upXform.Coordinates);
        _audio.PlayPvs(ClimbSound, args.User);
        _popup.PopupEntity(Loc.GetString("mafiastation-multiz-climb"), args.User, args.User);
        args.Handled = true;
    }
}
