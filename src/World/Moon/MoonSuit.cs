using UnitSport.Player;

namespace UnitSport.World.Moon;

/// <summary>
/// The suit on the Moon (#1941, docs/plans/moon-surface.md section 6): worn automatically while the anchor is on the
/// Moon; its helmet on outside a colony's air and off inside (<see cref="ColonyAirlocks.Pressurised"/>, read at the
/// head). No oxygen timer, as the user decided. Read every 0.25 s by <c>OccasionHats</c>, which sets the replicated
/// outfit and headwear: other players see it. A bot's body is dressed the same through <see cref="Dress"/>.
/// </summary>
public static class MoonSuit
{
    /// <summary>The suit a walker wears now, or null off the Moon.</summary>
    public static (bool Helmet, bool Suit)? Wanted(FootPlayer p)
    {
        if (MoonSurface.Active is not { OnMoon: true }) return null;
        // in the pressurised rover's cabin (#1943) the helmet is off too
        if (p.Ride == MoonRovers.Cabin.Kind) return (false, true);
        var head = p.GlobalPosition + Godot.Vector3.Up * 1.6f;
        return (!ColonyAirlocks.Pressurised(head), true);
    }

    /// <summary>
    /// A body the server runs (a colonist bot, #1976) dressed as <c>OccasionHats</c> dresses the player: the silver suit on
    /// the Moon, the helmet on outside the air and off inside. Its replicated outfit and headwear, set only on a change.
    /// </summary>
    public static void Dress(FootPlayer p)
    {
        if (Wanted(p) is not { } suit) return;
        var hat = suit.Helmet ? Avatar.Headwear.SpaceHelmet : Avatar.Headwear.None;
        if (p.HeadwearId != (int)hat) p.HeadwearId = (int)hat;
        var worn = new Avatar.Outfit(p.OutfitBits);
        if (Avatar.Garments.Get(Items.ItemId.SilverSuit) is { } silver) worn = worn.With(silver.Slot, silver.Code);
        if (suit.Helmet) worn = worn.With(Avatar.WearSlot.Head, 0);
        if (p.OutfitBits != worn.Bits) p.OutfitBits = worn.Bits;
    }
}
