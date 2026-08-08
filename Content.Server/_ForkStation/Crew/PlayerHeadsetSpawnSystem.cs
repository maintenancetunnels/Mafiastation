using Content.Shared.GameTicking;
using Content.Shared.Inventory;

namespace Content.Server._ForkStation.Crew;

/// <summary>
/// Guarantees every player who spawns is wearing a radio headset. Sandbox (and other gearless
/// spawn paths) otherwise drop the player onto the station with an empty ears slot, which leaves
/// them off every radio channel: ';' silently falls back to local speech and they can neither
/// transmit to nor hear station comms — including the hybrid LLM crew, who talk on Common.
/// If a real job loadout already filled the ears slot, that headset is left in place.
/// </summary>
public sealed class PlayerHeadsetSpawnSystem : EntitySystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;

    // Common-channel headset — the same band the hybrid NPC crew use, so players share it.
    private const string DefaultHeadset = "ClothingHeadsetGrey";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawnComplete);
    }

    private void OnSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        // No inventory (e.g. spawned as a non-humanoid) means nowhere to put a headset.
        if (!HasComp<InventoryComponent>(ev.Mob))
        {
            Log.Info($"[headset] spawn {ToPrettyString(ev.Mob)}: no InventoryComponent, skipping.");
            return;
        }

        // Respect an existing headset from a job loadout.
        if (_inventory.TryGetSlotEntity(ev.Mob, "ears", out var existing))
        {
            Log.Info($"[headset] spawn {ToPrettyString(ev.Mob)}: ears already holds {ToPrettyString(existing.Value)}, leaving it.");
            return;
        }

        // force: true so species-specific ears-slot restrictions don't silently reject it.
        var equipped = _inventory.SpawnItemInSlot(ev.Mob, "ears", DefaultHeadset, silent: true, force: true);
        Log.Info($"[headset] spawn {ToPrettyString(ev.Mob)}: equipped grey headset -> {equipped}.");
    }
}
