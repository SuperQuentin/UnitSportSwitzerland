using Godot;
using UnitSport.Avatar;

namespace UnitSport.Items;

/// <summary>
/// Icons for the clothes (#251): one 16×16 template per kind of garment, painted in each look's
/// own colours, so 85 looks need a dozen drawings. In a template 'k' is the outline, '1' the
/// look's main colour, '2' its second, '3' its third; a look with a finish gets a sparkle in the
/// corner, and a rainbow one its main colour in hue bands. See docs/notes/items/pixel-icons.md.
/// </summary>
public static class ClothingIcons
{
    private static readonly string[] Tee =
    {
        "................",
        "...kkkk..kkkk...",
        "..k1111kk1111k..",
        ".k111111111111k.",
        "k11111111111111k",
        "k11k11111111k11k",
        ".kkk11111111kkk.",
        "...k11111111k...",
        "...k11122111k...",
        "...k11122111k...",
        "...k11111111k...",
        "...k11111111k...",
        "...k11111111k...",
        "...kkkkkkkkkk...",
    };

    private static readonly string[] Tank =
    {
        "................",
        "....k......k....",
        "....k1k..k1k....",
        "....k1k..k1k....",
        "...k11kkkk11k...",
        "...k11111111k...",
        "...k11111111k...",
        "...k12111121k...",
        "...k11111111k...",
        "...k12111121k...",
        "...k11111111k...",
        "...k12111121k...",
        "...k11111111k...",
        "...kkkkkkkkkk...",
    };

    private static readonly string[] Long =
    {
        "................",
        "...kkkk..kkkk...",
        "..k1111kk1111k..",
        ".k111111111111k.",
        "k11k11111111k11k",
        "k11k11111111k11k",
        "k11k11111111k11k",
        "k11k11122111k11k",
        "k11k11122111k11k",
        "k22k11111111k22k",
        "kkkk11111111kkkk",
        "...k11111111k...",
        "...k11111111k...",
        "...kkkkkkkkkk...",
    };

    private static readonly string[] Crop =
    {
        "................",
        "................",
        "...kkkk..kkkk...",
        "..k1111kk1111k..",
        ".k111111111111k.",
        "k11111111111111k",
        "k11k11111111k11k",
        ".kkk11133111kkk.",
        "...k11111111k...",
        "...kkkkkkkkkk...",
        "...2..2..2..2...",
    };

    private static readonly string[] Dress =
    {
        "................",
        "....kk....kk....",
        "...k11k..k11k...",
        "...k11kkkk11k...",
        "...k11133111k...",
        "...k11111111k...",
        "....k111111k....",
        "...k11111111k...",
        "..k1111111111k..",
        "..k1111111111k..",
        ".k111111111111k.",
        ".k111111111111k.",
        "k11111111111111k",
        "k22222222222222k",
        "kkkkkkkkkkkkkkkk",
    };

    private static readonly string[] Skirt =
    {
        "................",
        "................",
        "................",
        "....kkkkkkkk....",
        "....k222222k....",
        "....k111111k....",
        "...k11111111k...",
        "...k11111111k...",
        "..k1111111111k..",
        "..k1111111111k..",
        ".k111111111111k.",
        ".k111111111111k.",
        "k11111111111111k",
        "kkkkkkkkkkkkkkkk",
    };

    private static readonly string[] Shorts =
    {
        "................",
        "................",
        "................",
        "..kkkkkkkkkkkk..",
        "..k2222222222k..",
        "..k1111221111k..",
        "..k1111111111k..",
        "..k1111111111k..",
        "..k11111k1111k..",
        "..k1111kk1111k..",
        "..k1111kk1111k..",
        "..kkkkk..kkkkk..",
    };

    private static readonly string[] Pants =
    {
        "................",
        "..kkkkkkkkkkkk..",
        "..k2222222222k..",
        "..k1111111111k..",
        "..k1111111111k..",
        "..k11111k1111k..",
        "..k1111kk1111k..",
        "..k1111kk1111k..",
        "..k1111kk1111k..",
        "..k1111kk1111k..",
        "..k1111kk1111k..",
        "..k1111kk1111k..",
        "..k1111kk1111k..",
        "..k3333kk3333k..",
        "..kkkkk..kkkkk..",
    };

    private static readonly string[] Stocking =
    {
        "................",
        "......kkkkk.....",
        "......k333k.....",
        "......k111k.....",
        "......k222k.....",
        "......k111k.....",
        "......k222k.....",
        "......k111k.....",
        "......k222k.....",
        "......k111k.....",
        "......k222k.....",
        ".....k1111k.....",
        "...kk22222k.....",
        "..k1111111k.....",
        "..kkkkkkkkk.....",
    };

    private static readonly string[] Boot =
    {
        "................",
        "................",
        ".....kkkkkk.....",
        ".....k1111k.....",
        ".....k2222k.....",
        ".....k1111k.....",
        ".....k2222k.....",
        ".....k1111k.....",
        ".....k11111kk...",
        "....k111111111k.",
        "....k111111111k.",
        "....kkkkkkkkkkk.",
        "....k333333333k.",
        "....kkkkkkkkkkk.",
    };

    private static readonly string[] Ears =
    {
        "................",
        "................",
        "..k..........k..",
        "..kk........kk..",
        "..k1k......k1k..",
        "..k21k....k12k..",
        "..k221k..k122k..",
        "..k1111kk1111k..",
        "..k1111111111k..",
        "...kkkkkkkkkk...",
        "...k33333333k...",
        "....kkkkkkkk....",
    };

    private static readonly string[] Headset =
    {
        "................",
        ".....kkkkkk.....",
        "...kk111111kk...",
        "..k11kkkkkk11k..",
        "..k1k......k1k..",
        ".kk1k......k1kk.",
        "k221k......k122k",
        "k221k......k122k",
        "k221k......k122k",
        ".kk1k......k1kk.",
        "...k3k.....k....",
        "....k3k.........",
        ".....k33k.......",
        "......kk........",
    };

    private static readonly string[] Glasses =
    {
        "................",
        "................",
        "................",
        "................",
        "................",
        ".111111..111111.",
        "1122221111222211",
        ".122221..122221.",
        ".122221..122221.",
        "..1111....1111..",
    };

    private static readonly string[] Mask =
    {
        "................",
        "................",
        "................",
        "................",
        "..kkkkkkkkkkkk..",
        "kk131111111131kk",
        "k.k1111111111k.k",
        "k.k1112112111k.k",
        ".kk1111221111kk.",
        "...k11111111k...",
        "....kkkkkkkk....",
    };

    private static readonly string[] Piercing =
    {
        "................",
        "................",
        "......kkkk......",
        ".....k1111k.....",
        "....k1k..k1k....",
        "....k1k..k1k....",
        "....k1k..k1k....",
        ".....k1111k.....",
        "......kkkk......",
        "................",
        "...k1k....k2k...",
        "...kkk....kkk...",
    };

    private static readonly string[] Choker =
    {
        "................",
        "................",
        "................",
        "..kkkkkkkkkkkk..",
        ".k111111111111k.",
        ".k111111111111k.",
        "..kkkkk22kkkkk..",
        ".......22.......",
        "......k22k......",
        "......k22k......",
        ".......kk.......",
    };

    private static readonly string[] Glove =
    {
        "................",
        "....k.k.k.k.....",
        "...k1k1k1k1k....",
        "...k1k1k1k1k....",
        "...k1k1k1k1k.k..",
        "...k11111111k1k.",
        "...k1111111111k.",
        "...k111111111k..",
        "...k11111111k...",
        "...k11111111k...",
        "...k22222222k...",
        "...k11111111k...",
        "...k11111111k...",
        "...kkkkkkkkkk...",
    };

    private static string[] TemplateFor(GarmentShape shape) => shape switch
    {
        GarmentShape.TShirt or GarmentShape.PrintTee or GarmentShape.Polo => Tee,
        GarmentShape.Marcel or GarmentShape.Corset => Tank,
        GarmentShape.Longsleeve or GarmentShape.Hoodie or GarmentShape.CroppedJacket or GarmentShape.FieldJacket => Long,
        GarmentShape.CropTop => Crop,
        GarmentShape.Robe or GarmentShape.Dress => Dress,
        GarmentShape.Shorts => Shorts,
        GarmentShape.Pants or GarmentShape.Cargo => Pants,
        GarmentShape.HighLowSkirt or GarmentShape.SlitMaxi or GarmentShape.RuffleMini
            or GarmentShape.PleatedSkirt or GarmentShape.LongPleated => Skirt,
        GarmentShape.ThighHigh or GarmentShape.StripedThighHigh or GarmentShape.KneeSock or GarmentShape.Fishnets => Stocking,
        GarmentShape.PlatformBoots or GarmentShape.CombatBoots or GarmentShape.Sneakers or GarmentShape.MaryJanes => Boot,
        GarmentShape.Headset or GarmentShape.CatHeadset => Headset,
        GarmentShape.CatEars or GarmentShape.BunnyEars or GarmentShape.Horns or GarmentShape.Bow
            or GarmentShape.LaceHeadband or GarmentShape.Beanie => Ears,
        GarmentShape.RoundGlasses or GarmentShape.RoundShades or GarmentShape.HeartShades
            or GarmentShape.Shades or GarmentShape.StarGlasses => Glasses,
        GarmentShape.Mask => Mask,
        GarmentShape.Studs or GarmentShape.Hoops or GarmentShape.Industrial or GarmentShape.Spikes or GarmentShape.StarStuds => Piercing,
        GarmentShape.SpikedChoker or GarmentShape.HeartChoker or GarmentShape.Chain or GarmentShape.BellCollar => Choker,
        _ => Glove,
    };

    /// <summary>Template rows that are not 16 wide, or more than 16 of them: what <c>--iconsheet</c> reports.</summary>
    public static List<string> Validate()
    {
        var bad = new List<string>();
        foreach (var shape in Enum.GetValues<GarmentShape>())
        {
            var rows = TemplateFor(shape);
            if (rows.Length > ItemIcons.Size) bad.Add($"clothes {shape}: {rows.Length} rows");
            for (int y = 0; y < rows.Length; y++)
                if (rows[y].Length != ItemIcons.Size) bad.Add($"clothes {shape} row {y}: {rows[y].Length} chars");
        }
        return bad;
    }

    /// <summary>A pixel of the TAZ 90 camouflage on <paramref name="light"/> (#716): dark green, brown and black blotches, fixed by the pixel.</summary>
    private static Color CamoPixel(Color light, int x, int y)
    {
        // a few hand-picked cells, not noise: the icon should look the same on every machine
        uint h = (uint)(x * 73856093 ^ y * 19349663) % 11u;
        return h switch
        {
            0 or 1 or 2 => light.Darkened(0.35f),
            3 or 4 => new Color("56402a"),
            5 => new Color("1a1c14"),
            _ => light,
        };
    }

    /// <summary>The icon of a look: its garment's template in its colours.</summary>
    public static Image Image(Garment g)
    {
        var rows = TemplateFor(g.Shape);
        var outline = ItemIcons.Palette['k'];
        // a finish shows as its own colour at a glance, not the plain base it is painted over
        var main = g.IsSpecial ? ItemDefs.Get(g.Item)?.Tint ?? g.A : g.A;
        // so a black look still shows against the slot's dark ground
        if (main.Luminance < 0.08f) main = main.Lightened(0.18f);
        var second = g.B.Luminance < 0.08f ? g.B.Lightened(0.25f) : g.B;
        var third = g.C.Luminance < 0.08f ? g.C.Lightened(0.25f) : g.C;
        int top = (ItemIcons.Size - rows.Length) / 2;

        var img = Godot.Image.CreateEmpty(ItemIcons.Size, ItemIcons.Size, false, Godot.Image.Format.Rgba8);
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < ItemIcons.Size && x < rows[y].Length; x++)
            {
                var colour = rows[y][x] switch
                {
                    'k' => outline,
                    '1' => g.Finish == Finish.Rainbow ? Color.FromHsv((float)y / rows.Length, 0.8f, 1f)
                        : g.Finish == Finish.Camo ? CamoPixel(main, x, y) : main,
                    '2' => g.IsSpecial ? main.Lightened(0.35f) : second,
                    '3' => g.IsSpecial ? main.Darkened(0.25f) : third,
                    _ => new Color(0, 0, 0, 0),
                };
                img.SetPixel(x, y + top, colour);
            }
        if (g.IsSpecial)
            foreach (var (x, y) in new[] { (13, 0), (12, 1), (13, 1), (14, 1), (13, 2), (2, 13), (1, 14), (3, 14), (2, 15) })
                img.SetPixel(x, y, Colors.White);
        return img;
    }
}
