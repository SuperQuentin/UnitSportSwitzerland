namespace UnitSport.Core;

/// <summary>
/// Static state a <see cref="ClientWorld"/> leaves behind when it is freed, reset when the player
/// leaves to the title screen so the next world starts clean (<c>docs/notes/ui/teardown.md</c>).
///
/// <para>
/// The singletons null themselves in their own <c>_ExitTree</c>; what is here is the rest:
/// static caches keyed by tiles of the old world, delegates that captured its nodes, and static
/// events whose subscribers died with it. Events that shell-owned UI listens to
/// (<see cref="GameSettings.Changed"/>, <see cref="PlayerInput.DeviceChanged"/>,
/// <see cref="Permissions.Changed"/>) are deliberately left alone. <c>--leavecheck</c> verifies
/// the result.
/// </para>
/// </summary>
public static class WorldStatics
{
    public static void Reset()
    {
        UiFocus.Clear();
        Permissions.Reset();
        Interiors.DoorIndex.Clear();
        Vehicles.GarageUi.GarageNear = null;
        Audio.Surfaces.Origin = null;
        Audio.Surfaces.Forget();
        Occasions.OccasionTowns.UseGenerated(null);

        Items.ItemEvents.ResetEvents();
        Items.PhotoTransfer.ResetEvents();
        Items.RadioManager.ResetEvents();
        Items.DroppedItems.ResetEvents();
        Audio.Hearing.Ground = null;
        Net.PlayerInfo.ResetEvents();
        Occasions.OccasionTowns.ResetEvents();
        Vehicles.Explosion.ResetEvents();
        Vehicles.PassengerService.ResetEvents();
        Vehicles.VehicleManager.ResetEvents();
        Vehicles.DormantLooks.Reset();
    }
}
