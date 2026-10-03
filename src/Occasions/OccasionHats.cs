using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions;

/// <summary>
/// Decides what the local player wears and writes it to the replicated
/// <see cref="FootPlayer.HeadwearId"/> (the hat), <see cref="FootPlayer.OutfitBits"/> (the
/// clothes in the body slots, #251) and <see cref="FootPlayer.AppearanceBits"/> (the figure itself,
/// #394), so every other player sees the same figure.
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
        long outfit = _inventory.Outfit.Bits;
        if (p.OutfitBits != outfit) p.OutfitBits = outfit;
        // and who the figure is (#394), as chosen in the inventory's Body row
        int appearance = Core.GameSettings.Current.AppearanceBits;
        if (p.AppearanceBits != appearance) p.AppearanceBits = appearance;
    }
}
