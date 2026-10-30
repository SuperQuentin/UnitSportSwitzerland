using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions;

/// <summary>
/// Decides what the local player wears and writes it to the replicated
/// <see cref="FootPlayer.HeadwearId"/> (the hat) and <see cref="FootPlayer.OutfitBits"/> (the
/// clothes in the body slots, #251), so every other player sees the same figure. The figure itself
/// (<see cref="FootPlayer.AppearanceBits"/>, #394) is <c>ClientWorld.PublishAppearance</c>'s, Occasions or not.
///
/// <para>
/// A hat the player chose to wear (in the head slot, a hunt find) comes first; else, with nothing
/// on the head, the hat of the highest-priority running occasion with the Hats facet. That facet
/// is cosmetic, so a player who turned the occasion off simply has none — no separate hat setting
/// needed. Clothes on the head (cat ears) are drawn from the outfit, so they take the head too.
/// </para>
/// </summary>
public partial class OccasionHats : Node
{
    private readonly Func<FootPlayer?> _player;
    private readonly Inventory _inventory;
    private double _next;

    public OccasionHats(Func<FootPlayer?> player, Inventory inventory)
    {
        Name = "OccasionHats";
        _player = player;
        _inventory = inventory;
    }

    public OccasionHats() : this(() => null, new Inventory()) { }

    public static Headwear ForItem(ItemId id) => id switch
    {
        ItemId.WitchHat => Headwear.WitchHat,
        ItemId.PumpkinHead => Headwear.PumpkinHead,
        ItemId.SantaHat => Headwear.SantaHat,
        ItemId.ReindeerAntlers => Headwear.ReindeerAntlers,
        ItemId.LarveWaggis => Headwear.LarveWaggis,
        ItemId.LarveDante => Headwear.LarveDante,
        ItemId.LarveHarlekin => Headwear.LarveHarlekin,
        ItemId.LarveTeufel => Headwear.LarveTeufel,
        _ => Headwear.None,
    };

    public override void _Process(double delta)
    {
        _next -= delta;
        if (_next > 0) return;
        _next = 0.25;

        if (_player() is not { } p || !IsInstanceValid(p) || !p.IsMultiplayerAuthority()) return;
        var hat = ForItem(_inventory.Worn);
        if (_inventory.Worn == ItemId.None && OccasionManager.Instance?.Top(OccasionFacets.Hats) is { } top)
            hat = top.Content.Hat;
        if (p.HeadwearId != (int)hat) p.HeadwearId = (int)hat;
        var worn = _inventory.Outfit;
        // signed in at a bowling centre's counter: the hire shoes, whatever was on the feet (#1323)
        if (Interiors.BowlingLanes.Instance?.WearsShoesHere() == true && Avatar.Garments.Get(ItemId.BowlingShoes) is { } shoes)
            worn = worn.With(shoes.Slot, shoes.Code);
        long outfit = worn.Bits;
        if (p.OutfitBits != outfit) p.OutfitBits = outfit;
    }
}
