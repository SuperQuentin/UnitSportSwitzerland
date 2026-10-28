using System;
using UnitSport.Adventure.Navigation;
using UnitSport.Items;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>
/// What a Postkarte card pays (#1267 part 4.4), worked out from the travel fares (<c>assets/adventure/fares.json</c>), so a
/// December fare change moves the rewards with it. Every card pays at least its full rail or ship fare plus CHF 15: the
/// train is always worth taking, and walking or driving pays best.
/// </summary>
public static class ErrandRewards
{
    public const int Min = 20, Max = 180, FareMargin = 15;

    public static int Base(CardTemplate t) => t switch
    {
        CardTemplate.AlpCheese => 20, CardTemplate.MailRun => 25, CardTemplate.LostHiker => 30, CardTemplate.FerryRun => 15,
        CardTemplate.Specialty => 15, CardTemplate.HutSupplies => 20, CardTemplate.FarmerRequest => 10, _ => 20,
    };

    /// <summary>The full (no card) fare of the route a card sends the player on: a ship tariff for a ferry, the rail and bus tariff for the rest.</summary>
    public static double FullFare(CardTemplate t, double straightKm, TravelRules rules)
    {
        if (straightKm <= 0) return 0;
        double km = straightKm * rules.Detour;
        return rules.Round(t == CardTemplate.FerryRun ? rules.Ship.Price(km) : rules.Rail.Price(km));
    }

    /// <summary>Worth of the items a card handles, CHF (a farm item's price, a specialty's).</summary>
    public static float ValueOf(ItemId id) => id switch
    {
        ItemId.Potato => 2, ItemId.Carrot => 2, ItemId.Flour => 4, ItemId.HayBale => 6, ItemId.WheatSeed => 3,
        ItemId.Cheese => 8, ItemId.RacletteCheese => 14, ItemId.BaslerLaeckerli => 10, ItemId.EngadinerNusstorte => 18, ItemId.Marroni => 8,
        ItemId.ZugerKirschtorte => 16, ItemId.Appenzeller => 13, ItemId.TeteDeMoine => 15, ItemId.StGallerBratwurst => 9,
        ItemId.SaucissonVaudois => 12, ItemId.ZuercherTirggel => 8, ItemId.Sbrinz => 17,
        _ => 0,
    };

    /// <summary>The card's reward, CHF, rounded up to 5 and clamped to [20, 180].</summary>
    public static int For(CardTemplate t, double straightKm, double climbM, ItemId? item, int count, TravelRules rules)
    {
        double chf = Base(t) + 1.5 * FullFare(t, straightKm, rules) + 0.05 * Math.Max(0, climbM);
        if (t == CardTemplate.FarmerRequest && item is { } i) chf += 1.5 * ValueOf(i) * count;
        return Math.Clamp((int)Math.Ceiling(chf / 5) * 5, Min, Max);
    }

    /// <summary>The friend bonus: +50 %, rounded to 5.</summary>
    public static int Bonus(int chf) => (int)Math.Round(chf * 0.5 / 5) * 5;
}
