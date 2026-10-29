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
        Net.PlayerIds.Server = null;
        Interiors.DoorIndex.Clear();
        Vehicles.GarageUi.GarageNear = null;
        Audio.Surfaces.Origin = null;
        Audio.Surfaces.Forget();
        Occasions.OccasionTowns.UseGenerated(null);
        WorldChoice.Active = null;   // back to the open world for the title (#999)

        Items.ItemEvents.ResetEvents();
        Items.PhotoTransfer.ResetEvents();
        Items.RadioManager.ResetEvents();
        Items.DroppedItems.ResetEvents();
        Audio.Hearing.Ground = null;
        Net.PlayerInfo.ResetEvents();
        Occasions.OccasionTowns.ResetEvents();
        Occasions.Festivals.FestivalOccasion.ResetSites();
        Vehicles.Explosion.ResetEvents();
        Vehicles.PassengerService.ResetEvents();
        Vehicles.VehicleManager.ResetEvents();
        Vehicles.DormantLooks.Reset();
        Adventure.Places.PoiCatalogueHost.Reset();
        Adventure.Places.FogTexture.Reset();
        Adventure.Places.AtlasFactsStore.Reset();
        Adventure.Places.AdventureMap.Me = null;
        Adventure.Places.AdventureMap.PlaceSearch = null;
    }
}
