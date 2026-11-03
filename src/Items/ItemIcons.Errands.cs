using UnitSport.Items;

namespace UnitSport.Items;

/// <summary>The errand cargo and the cantonal specialties' icons (#1267), painted in code.</summary>
public static partial class ItemIcons
{
    private static void AddErrandIcons(Dictionary<ItemId, string[]> d)
    {
        // a sack tied at the neck, with a yellow post horn mark
        d[ItemId.MailSack] = Round(p =>
        {
            p.Disc(7.5f, 9f, 6f, 5.5f, 't', 'n', 'N');
            p.Rect(6, 3, 9, 5, 'n');
            p.Rect(5, 5, 10, 5, 'N');
            p.Rect(6, 8, 9, 10, 'y');
            p.Set(7, 9, 'Y'); p.Set(8, 9, 'Y');
        });
        // a wooden crate with two slats and a blue ship band
        d[ItemId.FerryCrate] = Round(p =>
        {
            p.Rect(2, 4, 13, 13, 'n');
            p.Rect(2, 4, 13, 5, 't');
            p.Rect(2, 8, 13, 9, 'N');
            p.Rect(2, 11, 13, 12, 'N');
            p.Rect(6, 6, 9, 7, 'b');
        });
        // a green pack with straps
        d[ItemId.SupplyPack] = Round(p =>
        {
            p.Rect(3, 4, 12, 13, 'e');
            p.Rect(3, 4, 12, 5, 'u');
            p.Rect(3, 12, 12, 13, 'E');
            p.Rect(5, 4, 5, 13, 'n');
            p.Rect(10, 4, 10, 13, 'n');
            p.Rect(6, 7, 9, 10, 'E');
        });
        // a card with a stamp and a wavy line
        d[ItemId.Postcard] = Round(p =>
        {
            p.Rect(1, 3, 14, 12, 'w');
            p.Rect(1, 12, 14, 12, 'a');
            p.Rect(11, 4, 13, 6, 'r');
            for (int x = 3; x <= 9; x++) p.Set(x, 7 + (x % 2), 'g');
            p.Rect(3, 10, 8, 10, 'g');
        });
        d[ItemId.RacletteCheese] = Wedge('y', 'Y', 'l');
        d[ItemId.Appenzeller] = Wedge('o', 'O', 'y');
        d[ItemId.Sbrinz] = Wedge('l', 'y', 'T');
        d[ItemId.TeteDeMoine] = Round(p => { p.Disc(7.5f, 9f, 6f, 4f, 'l', 'y', 'Y'); p.Rect(6, 3, 9, 5, 'y'); p.Set(7, 8, 'Y'); p.Set(9, 10, 'Y'); });
        d[ItemId.BaslerLaeckerli] = Round(p => { p.Rect(2, 5, 13, 10, 'n'); p.Rect(2, 5, 13, 6, 't'); p.Rect(3, 8, 12, 8, 'w'); p.Set(5, 7, 'N'); p.Set(10, 9, 'N'); });
        d[ItemId.EngadinerNusstorte] = Round(p => { p.Disc(7.5f, 8f, 7f, 5f, 't', 'n', 'N'); p.Disc(7.5f, 7f, 4.5f, 3f, 'T', 't', 'n'); p.Set(6, 6, 'N'); p.Set(9, 8, 'N'); });
        d[ItemId.Marroni] = Round(p => { p.Disc(5.5f, 9f, 3.5f, 4f, 'n', 'N', 'N'); p.Disc(10f, 8f, 3.5f, 4f, 'n', 'N', 'N'); p.Set(5, 10, 'T'); p.Set(10, 9, 'T'); });
        d[ItemId.ZugerKirschtorte] = Round(p => { p.Disc(7.5f, 9f, 7f, 4.5f, 'q', 's', 'R'); p.Rect(3, 5, 12, 6, 'w'); p.Disc(7.5f, 4f, 1.6f, 1.6f, 'r', 'r', 'R'); });
        d[ItemId.StGallerBratwurst] = Round(p => { p.Rect(2, 6, 13, 10, 't'); p.Rect(2, 6, 13, 7, 'T'); p.Rect(2, 10, 13, 10, 'n'); p.Set(5, 8, 'n'); p.Set(9, 8, 'n'); });
        d[ItemId.SaucissonVaudois] = Round(p => { p.Disc(7.5f, 8.5f, 6.5f, 3.5f, 'q', 'R', 'R'); p.Set(3, 8, 'w'); p.Set(7, 9, 'w'); p.Set(11, 8, 'w'); p.Rect(1, 8, 1, 8, 'n'); p.Rect(14, 8, 14, 8, 'n'); });
        d[ItemId.ZuercherTirggel] = Round(p => { p.Rect(2, 4, 13, 12, 'n'); p.Rect(2, 4, 13, 5, 't'); p.Rect(5, 7, 10, 9, 'N'); p.Set(7, 8, 't'); p.Set(8, 8, 't'); });
    }

    /// <summary>A wedge of cheese: the rind on the right, a hole.</summary>
    private static string[] Wedge(char light, char mid, char rind) => Round(p =>
    {
        p.Rect(2, 7, 13, 12, mid);
        p.Rect(2, 7, 13, 8, light);
        p.Rect(3, 5, 12, 6, light);
        p.Rect(5, 3, 10, 4, light);
        p.Rect(12, 5, 13, 12, rind);
        p.Set(5, 10, 'k'); p.Set(9, 9, 'k');
    });
}
