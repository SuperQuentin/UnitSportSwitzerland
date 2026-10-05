using Godot;

namespace UnitSport.Items;

/// <summary>Silhouette of a procedurally generated fallback icon.</summary>
public enum IconShape { Box, Can, Bottle, Pouch, Scrap, Tool }

/// <summary>
/// 16x16 pixel-art icons for every item. Authored icons are string grids (one char per pixel,
/// '.' transparent, colours in <see cref="Palette"/>); an item with no grid gets a generated one
/// (<see cref="Generic"/>: category silhouette + tint + a 3x5 pixel glyph). See
/// <c>docs/notes/items/pixel-icons.md</c>.
/// </summary>
public static class ItemIcons
{
    public const int Size = 16;

    /// <summary>Palette chars used by the grids. Light comes from the top left.</summary>
    public static readonly Dictionary<char, Color> Palette = new()
    {
        ['k'] = C("1c1a22"),                                                                   // outline
        ['w'] = C("f6f6f2"), ['a'] = C("c8ccd2"), ['g'] = C("8a9098"), ['G'] = C("545a64"), ['d'] = C("2c3038"),
        ['r'] = C("d83a30"), ['R'] = C("8a1e22"), ['q'] = C("f27a68"),
        ['o'] = C("f28a22"), ['O'] = C("b25a12"), ['y'] = C("f6d446"), ['Y'] = C("c29a22"), ['l'] = C("fff2a6"),
        ['n'] = C("a86a34"), ['N'] = C("6a4022"), ['t'] = C("dcac6c"), ['T'] = C("f4d8a0"),
        ['b'] = C("3c78d0"), ['B'] = C("24408a"), ['c'] = C("7cc8f0"), ['C'] = C("cbeefa"),
        ['e'] = C("48a840"), ['E'] = C("2a6a2c"), ['u'] = C("98d472"),
        ['p'] = C("8a5ab0"), ['P'] = C("4e2e70"), ['v'] = C("b894dc"), ['s'] = C("f2a4b4"),
    };

    private static Color C(string hex) => new(hex);

    private static readonly Dictionary<ItemId, ImageTexture?> Cache = new();
    private static readonly Dictionary<ItemId, Image?> Images = new();
    private static readonly Dictionary<string, ImageTexture> GenericCache = new();
    private static Dictionary<ItemId, string[]>? _grids;

    /// <summary>The icon of an item (authored, else generated), cached. Null only for <see cref="ItemId.None"/>/unknown.</summary>
    public static Texture2D? Get(ItemId id)
    {
        if (Cache.TryGetValue(id, out var cached)) return cached;
        var img = GetImage(id);
        var tex = img == null ? null : ImageTexture.CreateFromImage(img);
        Cache[id] = tex;
        return tex;
    }

    /// <summary>True when the item has a hand-drawn grid (not the generated fallback).</summary>
    public static bool IsAuthored(ItemId id) => Grids().ContainsKey(id);

    /// <summary>The icon's pixels (16x16 RGBA), cached; also used to build the held card mesh.</summary>
    public static Image? GetImage(ItemId id)
    {
        if (Images.TryGetValue(id, out var cached)) return cached;
        Image? img = null;
        if (Grids().TryGetValue(id, out var rows)) img = FromRows(rows);
        else if (Avatar.Garments.Get(id) is { } look) img = ClothingIcons.Image(look);
        else if (ItemDefs.Get(id) is { } def) img = GenericImage(def.Tint, def.Glyph, ShapeFor(def));
        Images[id] = img;
        return img;
    }

    /// <summary>A generated icon for other systems (map markers, loot UI): silhouette, tint, glyph.</summary>
    public static Texture2D Generic(Color tint, string glyph, IconShape shape)
    {
        string key = $"{tint.ToHtml()}|{glyph}|{shape}";
        if (GenericCache.TryGetValue(key, out var t)) return t;
        return GenericCache[key] = ImageTexture.CreateFromImage(GenericImage(tint, glyph, shape));
    }

    /// <summary>The pixels of <see cref="Generic"/>, for callers that compose images.</summary>
    public static Image GenericPixels(Color tint, string glyph, IconShape shape) => GenericImage(tint, glyph, shape);

    /// <summary>The silhouette the fallback picks for an item, from its use and category.</summary>
    public static IconShape ShapeFor(ItemDef def) => def.Category switch
    {
        ItemCategory.Water => IconShape.Bottle,
        ItemCategory.Food => IconShape.Pouch,
        ItemCategory.Medical => IconShape.Box,
        ItemCategory.Scrap or ItemCategory.Mineral => IconShape.Scrap,
        ItemCategory.Part => IconShape.Tool,
        ItemCategory.Money or ItemCategory.Cosmetic => IconShape.Pouch,
        _ => def.Use is ItemUse.Shoot or ItemUse.Place ? IconShape.Tool
            : def.Use == ItemUse.Material ? IconShape.Can : IconShape.Box,
    };

    /// <summary>Problems in the authored grids (wrong row length or count, unknown char, missing item).</summary>
    public static List<string> Validate()
    {
        var bad = new List<string>();
        foreach (var (id, rows) in Grids())
        {
            if (rows.Length > Size) bad.Add($"{id}: {rows.Length} rows");
            for (int y = 0; y < rows.Length; y++)
            {
                if (rows[y].Length != Size) bad.Add($"{id} row {y}: {rows[y].Length} chars");
                foreach (char ch in rows[y])
                    if (ch != '.' && !Palette.ContainsKey(ch)) { bad.Add($"{id} row {y}: unknown '{ch}'"); break; }
            }
        }
        foreach (var d in ItemDefs.All)
            if (d.Id != ItemId.None && !Grids().ContainsKey(d.Id) && Avatar.Garments.Get(d.Id) == null)
                bad.Add($"{d.Id}: no authored icon (generic fallback)");
        bad.AddRange(ClothingIcons.Validate());
        return bad;
    }

    // ------------------------------------------------------------------------------------
    // grid -> image
    // ------------------------------------------------------------------------------------

    private static Image FromRows(string[] rows)
    {
        var px = new char[Size, Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[x, y] = y < rows.Length && x < rows[y].Length ? rows[y][x] : '.';
        Recentre(px);
        var img = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                img.SetPixel(x, y, Palette.TryGetValue(px[x, y], out var c) ? c : new Color(0, 0, 0, 0));
        return img;
    }

    /// <summary>Shifts the drawing so its bounding box sits in the middle of the 16x16 canvas.</summary>
    private static void Recentre(char[,] px)
    {
        int x0 = Size, x1 = -1, y0 = Size, y1 = -1;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (px[x, y] != '.') { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) return;
        int dx = (Size - (x1 - x0 + 1)) / 2 - x0, dy = (Size - (y1 - y0 + 1)) / 2 - y0;
        if (dx == 0 && dy == 0) return;
        var copy = (char[,])px.Clone();
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int sx = x - dx, sy = y - dy;
                px[x, y] = sx >= 0 && sx < Size && sy >= 0 && sy < Size ? copy[sx, sy] : '.';
            }
    }

    // ------------------------------------------------------------------------------------
    // generic fallback
    // ------------------------------------------------------------------------------------

    private static bool InShape(IconShape s, int x, int y)
    {
        switch (s)
        {
            case IconShape.Box:
                return x >= 2 && x <= 13 && y >= 3 && y <= 12;
            case IconShape.Can:
                return x >= 4 && x <= 11 && y >= 2 && y <= 13;
            case IconShape.Bottle:
                return (x >= 6 && x <= 9 && y >= 1 && y <= 4) || (x >= 4 && x <= 11 && y >= 5 && y <= 14);
            case IconShape.Pouch:
            {
                if (y < 3 || y > 13 || x < 2 || x > 13) return false;
                if (y == 3) return x % 2 == 0;                      // crimped top
                return !(y == 13 && (x == 2 || x == 13));
            }
            case IconShape.Scrap:
                return Dist(x, y, 5, 9, 4.5f, 3.5f) <= 1 || Dist(x, y, 10, 8, 4f, 4.5f) <= 1 || Dist(x, y, 8, 5, 3f, 3f) <= 1;
            default: // Tool: a wrench
            {
                bool handle = x >= 3 && x <= 10 && Math.Abs(x + y - 16) <= 1;
                bool head = Dist(x, y, 11, 4.5f, 3.3f, 3.3f) <= 1 && !(x >= 12 && y <= 4 && y >= 2 && x + 0 >= 13);
                return handle || head;
            }
        }
    }

    private static float Dist(int x, int y, float cx, float cy, float rx, float ry)
    {
        float u = (x - cx) / rx, v = (y - cy) / ry;
        return Mathf.Sqrt(u * u + v * v);
    }

    private static Image GenericImage(Color tint, string glyph, IconShape shape)
    {
        var img = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        var outline = tint.Darkened(0.78f);
        var light = tint.Lightened(0.35f);
        var dark = tint.Darkened(0.35f);

        bool At(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size && InShape(shape, x, y);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (!At(x, y)) continue;
                bool edge = !At(x - 1, y) || !At(x, y - 1) || !At(x + 1, y) || !At(x, y + 1);
                if (edge) { img.SetPixel(x, y, outline); continue; }
                var c = tint;
                if (!At(x - 2, y) || !At(x, y - 2)) c = light;           // light from the top left
                else if (!At(x + 2, y) || !At(x, y + 2)) c = dark;
                if (shape == IconShape.Can && (y == 4 || y == 12)) c = light.Lightened(0.2f);
                if (shape == IconShape.Bottle && y == 4) c = light;
                img.SetPixel(x, y, c);
            }

        DrawGlyph(img, glyph, tint);
        return img;
    }

    private static void DrawGlyph(Image img, string glyph, Color behind)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        glyph = glyph.ToUpperInvariant();
        if (glyph.Length > 3) glyph = glyph[..3];
        int width = glyph.Length * 4 - 1;
        int ox = (Size - width) / 2, oy = 6;
        var ink = behind.Luminance > 0.5f ? new Color("1c1a22") : Colors.White;
        for (int i = 0; i < glyph.Length; i++)
        {
            if (!Font3x5.TryGetValue(glyph[i], out var bits)) continue;
            for (int j = 0; j < 15; j++)
                if (bits[j] == '1')
                    img.SetPixel(ox + i * 4 + j % 3, oy + j / 3, ink);
        }
    }

    /// <summary>A 3x5 pixel font: 15 bits, row by row, top first.</summary>
    private static readonly Dictionary<char, string> Font3x5 = new()
    {
        ['A'] = "010101111101101", ['B'] = "110101110101110", ['C'] = "011100100100011", ['D'] = "110101101101110",
        ['E'] = "111100110100111", ['F'] = "111100110100100", ['G'] = "011100101101011", ['H'] = "101101111101101",
        ['I'] = "111010010010111", ['J'] = "001001001101010", ['K'] = "101101110101101", ['L'] = "100100100100111",
        ['M'] = "101111111101101", ['N'] = "110101101101101", ['O'] = "010101101101010", ['P'] = "110101110100100",
        ['Q'] = "010101101111011", ['R'] = "110101110101101", ['S'] = "011100010001110", ['T'] = "111010010010010",
        ['U'] = "101101101101111", ['V'] = "101101101101010", ['W'] = "101101111111101", ['X'] = "101101010101101",
        ['Y'] = "101101010010010", ['Z'] = "111001010100111",
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "110001010100111", ['3'] = "110001010001110",
        ['4'] = "101101111001001", ['5'] = "111100110001110", ['6'] = "011100111101111", ['7'] = "111001010010010",
        ['8'] = "111101111101111", ['9'] = "111101111001110", ['+'] = "000010111010000", ['-'] = "000000111000000",
    };

    // ------------------------------------------------------------------------------------
    // authored icons
    // ------------------------------------------------------------------------------------

    private static Dictionary<ItemId, string[]> Grids() => _grids ??= BuildGrids();

    private static string[] Map(string[] src, string from, string to) =>
        src.Select(r => new string(r.Select(ch => from.IndexOf(ch) is var i && i >= 0 ? to[i] : ch).ToArray())).ToArray();

    private static string[] Mirror(string[] half) =>
        half.Select(r => r + new string(r.Reverse().ToArray())).ToArray();

    /// <summary>A tiny painter for the round things: shaded discs, rects, auto outline.</summary>
    private sealed class Painter
    {
        public readonly char[,] G = new char[Size, Size];
        public Painter() { for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) G[x, y] = '.'; }
        public void Set(int x, int y, char c) { if (x >= 0 && x < Size && y >= 0 && y < Size) G[x, y] = c; }
        public void Rect(int x0, int y0, int x1, int y1, char c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }
        public void Disc(float cx, float cy, float rx, float ry, char light, char mid, char dark)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float u = (x - cx) / rx, v = (y - cy) / ry;
                    if (u * u + v * v > 1f) continue;
                    float d = -0.7f * u - 0.7f * v;
                    G[x, y] = d > 0.45f ? light : d < -0.35f ? dark : mid;
                }
        }
        public void Outline()
        {
            var copy = (char[,])G.Clone();
            bool Solid(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size && copy[x, y] != '.' && copy[x, y] != 'k';
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (copy[x, y] == '.' && (Solid(x - 1, y) || Solid(x + 1, y) || Solid(x, y - 1) || Solid(x, y + 1)))
                        G[x, y] = 'k';
        }
        public string[] Rows()
        {
            var rows = new string[Size];
            for (int y = 0; y < Size; y++)
            {
                var cs = new char[Size];
                for (int x = 0; x < Size; x++) cs[x] = G[x, y];
                rows[y] = new string(cs);
            }
            return rows;
        }
    }

    private static string[] Pumpkin(bool face)
    {
        var rows = new List<string>
        {
            "......kEEk......",
            ".....kkEEkk.....",
            "..kkkkOooOkkkk..",
        };
        string[] body = face
            ? new[]
            {
                "ooOooooooOoo", "oOooooooooOo", "oOookkooookO", "ooOkkoooOkko", "ooOkkoooOkko",
                "oooooOoooooo", "oOkyykyykoOo", "ooOkkkkkkOoo", "OOooooooooOO",
            }
            : new[]
            {
                "ooOooolooOoo", "oOoolooooOoo", "oOoolooooOoo", "ooOloooooOoo", "ooOooooooOoo",
                "ooOooooooOoo", "oOooooooooOo", "ooOooooooOoo", "OOooooooooOO",
            };
        foreach (var b in body) rows.Add(".k" + b + "k.");
        rows.Add("..kkkkkkkkkkkk..");
        return rows.ToArray();
    }

    /// <summary>A sack with a tied neck: <paramref name="body"/> and <paramref name="shade"/> palette chars, a patch of <paramref name="mark"/> on the front.</summary>
    private static string[] Sack(char body, char shade, char mark) => new[]
    {
        "................",
        ".....kk..kk.....",
        "....kXXkkXXk....",
        "....kXXXXXXk....",
        "...kkXXXXXXkk...",
        "..kXXXXXXXXXXk..",
        ".kXXXXMMMMXXXXk.",
        ".kXXXMmmmmMXXXk.",
        ".kXXXXMMMMXXXXk.",
        ".kXXXXXXXXXXXZk.",
        ".kXXXXXXXXXXXZk.",
        ".kXXXXXXXXXXZZk.",
        "..kZZZZZZZZZZk..",
        "...kkkkkkkkkk...",
    }.Select(r => r.Replace('X', body).Replace('Z', shade).Replace('M', mark).Replace('m', mark)).ToArray();

    /// <summary>A seed packet: white paper, a picture of <paramref name="mark"/> over a band of <paramref name="band"/>.</summary>
    private static string[] Packet(char mark, char band) => new[]
    {
        "................",
        "...kkkkkkkkkk...",
        "...kaaaaaaaak...",
        "...kkkkkkkkkk...",
        "...kwwwwwwwwk...",
        "...kwwMMMMwwk...",
        "...kwMMMMMMwk...",
        "...kwMMMMMMwk...",
        "...kwwMMMMwwk...",
        "...kwwwwwwwwk...",
        "...kBBBBBBBBk...",
        "...kBBBBBBBBk...",
        "...kwwwwwwwwk...",
        "...kkkkkkkkkk...",
    }.Select(r => r.Replace('M', mark).Replace('B', band)).ToArray();

    private static string[] Round(Action<Painter> draw)
    {
        var p = new Painter();
        draw(p);
        p.Outline();
        return p.Rows();
    }

    private static Dictionary<ItemId, string[]> BuildGrids()
    {
        var d = new Dictionary<ItemId, string[]>();

        d[ItemId.Binoculars] = new[]
        {
            "................",
            "...kkk....kkk...",
            "..kGGGk..kGGGk..",
            "..kGaGk..kGaGk..",
            "..kGGGkkkkGGGk..",
            ".kkeeekggkeeekk.",
            ".keaeEkggkeaeEk.",
            ".keeeEEkkEEeeEk.",
            ".keeeEEEEEEeeEk.",
            ".keEEEEEEEEEEEk.",
            ".kEEEEEkkEEEEEk.",
            "..kcCck..kcCck..",
            "..kkkkk..kkkkk..",
            "................",
        };

        d[ItemId.Camera] = new[]
        {
            "....kkkk...kkk..",
            ".kkkkkkkkkkkkkk.",
            ".kgggggggggggGk.",
            ".kgGGkkkkkkGGGk.",
            ".kGGkbbbbbbkGGk.",
            ".kGkbcCbbbbBkGk.",
            ".kGkbCbbbbbBkGk.",
            ".kGkbbbbbbbBkGk.",
            ".kGGkBBBBBBkGGk.",
            ".kGGGkkkkkkGGGk.",
            ".kGGGGGGGGGGGGk.",
            ".kkkkkkkkkkkkkk.",
        };

        d[ItemId.Gps] = new[]
        {
            "...........kk...",
            "...........kG...",
            "..kkkkkkkkkkGk..",
            "..kyllyyyyyyYk..",
            "..kykkkkkkkkYk..",
            "..kykEuuuuEkYk..",
            "..kykEuEuuEkYk..",
            "..kykEuuEuEkYk..",
            "..kykEEEEEEkYk..",
            "..kykkkkkkkkYk..",
            "..kyyyyoyyyyYk..",
            "..kyyyyyyyyYYk..",
            "..kYYYYYYYYYYk..",
            "..kkkkkkkkkkkk..",
        };

        // Swiss flag: a square red cloth with a white cross, on a pole
        d[ItemId.SwissFlag] = new[]
        {
            ".knkkkkkkkkkkkk.",
            ".knkqrrrrrrrrRk.",
            ".knkrrrrrrrrrRk.",
            ".knkrrrrwwrrrRk.",
            ".knkrrrrwwrrrRk.",
            ".knkrrwwwwwwrRk.",
            ".knkrrwwwwwwrRk.",
            ".knkrrrrwwrrrRk.",
            ".knkrrrrwwrrrRk.",
            ".knkrrrrrrrrrRk.",
            ".knkRRRRRRRRRRk.",
            ".knkkkkkkkkkkkk.",
            ".kn.............",
            ".kn.............",
            ".kn.............",
            ".kk.............",
        };

        d[ItemId.EnergyBar] = new[]
        {
            ".kkkkkkkkkkkkkk.",
            ".kYooooooooooYk.",
            ".kYolllllllloYk.",
            ".kYolrrrrrrloYk.",
            ".kYooooooooooYk.",
            ".kYOOOOOOOOOOYk.",
            ".kkkkkkkkkkkkkk.",
        };

        d[ItemId.WaterBottle] = new[]
        {
            "......kkkk......",
            "......kwwk......",
            "......kwwk......",
            ".....kkkkkk.....",
            "....kCcccbBk....",
            "....kCcccbBk....",
            "....kwwwwwwk....",
            "....kwbbbbwk....",
            "....kwwwwwwk....",
            "....kCcccbBk....",
            "....kccccbBk....",
            "....kcccbbBk....",
            "....kBBBBBBk....",
            ".....kkkkkk.....",
        };
        d[ItemId.MineralWater] = Map(d[ItemId.WaterBottle], "cCbBw", "CwcbC");
        {
            // the binoculars in slate, with a small cyan screen on the bridge
            var smart = Map(d[ItemId.Binoculars], "eE", "Gd");
            smart[8] = ".kGGGdCcCCdGGdk.";
            d[ItemId.SmartBinoculars] = smart;
        }

        d[ItemId.MineralWater][7] = "....kCeeeeCk....";

        d[ItemId.Bread] = new[]
        {
            "...kkkkkkkkkk...",
            "..kTTtttttttnk..",
            ".kTtnttnttnttnk.",
            ".kTttnttnttnttk.",
            ".kttttttttttttk.",
            ".kttttttttttnnk.",
            "..knnnnnnnnnnk..",
            "...kkkkkkkkkk...",
        };

        d[ItemId.CannedFood] = new[]
        {
            "...kkkkkkkkkk...",
            "...kwaaaaaaGk...",
            "...kkkkkkkkkk...",
            "...kaggggggGk...",
            "...krrrrrrrRk...",
            "...kqwwwwwwRk...",
            "...krrrrrrrRk...",
            "...kaggggggGk...",
            "...kgggggggGk...",
            "...kkkkkkkkkk...",
        };

        d[ItemId.Apple] = Round(p =>
        {
            p.Disc(7.5f, 9f, 6f, 5.5f, 'q', 'r', 'R');
            p.Set(4, 7, 'w'); p.Set(5, 6, 'w');
            p.Rect(7, 2, 7, 4, 'N');
            p.Set(8, 3, 'e'); p.Set(9, 2, 'u'); p.Set(10, 2, 'e'); p.Set(9, 3, 'E');
        });

        d[ItemId.Cheese] = new[]
        {
            "....kkkkkkkkkkk.",
            "...kllllllllllyk",
            "..kkkkkkkkkkkkYk",
            "..kyyyyyyyyyYYk.",
            "..kyyOOyyyyyYYk.",
            "..kyyOOyyyOOYYk.",
            "..kyyyyyyyOOYYk.",
            "..kyyyOOyyyyYYk.",
            "..kyyyOOyyyyYYk.",
            "..kYYYYYYYYYYYk.",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.Chocolate] = new[]
        {
            ".kkkkkkkkkkkkkk.",
            ".knnNNknnNNknnk.",
            ".knNNNknNNNknNk.",
            ".knNNNknNNNknNk.",
            ".kkkkkkkkkkkkkk.",
            ".knnNNknnNNknnk.",
            ".knNNNknNNNknNk.",
            ".knNNNknNNNknNk.",
            ".kkkkkkkkkkkkkk.",
            ".krrrrrrrrrrrrk.",
            ".kkkkkkkkkkkkkk.",
        };

        // francs: a purple banknote with a gold coin over its corner
        d[ItemId.Francs] = Round(p =>
        {
            p.Rect(1, 3, 12, 10, 'p');
            p.Rect(1, 3, 12, 3, 'v'); p.Rect(1, 3, 1, 10, 'v');
            p.Rect(12, 3, 12, 10, 'P'); p.Rect(1, 10, 12, 10, 'P');
            p.Rect(3, 5, 5, 6, 'v'); p.Rect(7, 6, 9, 7, 'P');
            p.Outline();
            p.Disc(11f, 10.5f, 4.2f, 4.2f, 'l', 'y', 'Y');
            p.Rect(10, 8, 11, 12, 'w'); p.Rect(9, 10, 13, 11, 'w');
        });

        d[ItemId.Bandage] = new[]
        {
            "..kkkkkkkkkkkk..",
            ".kwwwwwwwwwwwak.",
            ".kwwttttttttwak.",
            ".kwwtTTTTTTtwak.",
            ".kwwttttttttwak.",
            ".kwwwwwwwwwwaak.",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.FirstAidKit] = new[]
        {
            "....kkkkkkkk....",
            "....kg....gk....",
            ".kkkkkkkkkkkkkk.",
            ".kqrrrrwwrrrrRk.",
            ".kqrrrrwwrrrrRk.",
            ".kqrrwwwwwwrrRk.",
            ".kqrrwwwwwwrrRk.",
            ".kqrrrrwwrrrrRk.",
            ".kqrrrrwwrrrrRk.",
            ".kRRRRRRRRRRRRk.",
            ".kkkkkkkkkkkkkk.",
        };

        var scrap = new[]
        {
            ".......kkkk.....",
            "......kwaagk....",
            "...kkkkaaggkkk..",
            "..kwaaagggGGgkk.",
            ".kaagggkkkgGGGgk",
            ".kggGGkwaaggGGk.",
            "..kkkkkgggGGGGk.",
            "......kkkkkkkk..",
        };
        d[ItemId.ScrapMetal] = scrap;
        d[ItemId.Coal] = Map(scrap, "wagG", "gGdd");

        d[ItemId.Plastic] = new[]
        {
            "..kkkkkkkkkkkk..",
            ".kCwwCCCCCCCcCk.",
            ".kwCCCCCCCCCCck.",
            ".kCCCaCCCCCCCck.",
            ".kCCaaCCCCCaCck.",
            ".kCCCCCCCCaaCck.",
            ".kCCCCaCCCCCcck.",
            ".kcccccccccccck.",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.WoodPlanks] = new[]
        {
            ".kkkkkkkkkkkkkk.",
            ".kTttttttttttnk.",
            ".kttnttttttnttk.",
            ".kkkkkkkkkkkkkk.",
            ".kTttttttttttnk.",
            ".kttttnttttnttk.",
            ".kkkkkkkkkkkkkk.",
            ".kTttttttttttnk.",
            ".kttnttttttnttk.",
            ".kkkkkkkkkkkkkk.",
        };

        d[ItemId.Cloth] = new[]
        {
            "..kkkkkkkkkkkk..",
            ".kvvpppppppppPk.",
            ".kvppppppppppPk.",
            ".kkkkkkkkkkkkkk.",
            ".kvppppppppppPk.",
            ".kpppppppppppPk.",
            ".kppppppppPPPPk.",
            "..kPPPPPPPPPPk..",
            "...kkkkkkkkkk...",
        };

        d[ItemId.Glass] = new[]
        {
            "..........kk....",
            ".........kwck...",
            "........kwCCck..",
            ".......kwCCCcck.",
            "......kwCCCCccck",
            ".....kwCCCCccck.",
            "....kCCCCCcccck.",
            "...kCCCCCccccck.",
            "..kwCCCCcccck...",
            ".kkCCCcccck.....",
            ".kkkcccck.......",
            "..kkkck.........",
        };

        d[ItemId.CopperWire] = new[]
        {
            "..kkkkkkkkkkkk..",
            "..kaaaaaaaaaGk..",
            "..kkkkkkkkkkkk..",
            "...kyoyoyoyOk...",
            "...kooOooOoOk...",
            "...kyoyoyoyOk...",
            "...kooOooOoOk...",
            "...kyoyoyoyOk...",
            "..kkkkkkkkkkkk..",
            "..kaaaaaaaaaGk..",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.Screws] = new[]
        {
            "..kkkkkk.kkkkkk.",
            "..kaaaGk.kaaaGk.",
            "...kgGk...kgGk..",
            "...kgGk...kgGk..",
            "...kGgk...kGgk..",
            "...kgGk...kgGk..",
            "...kGgk...kGgk..",
            "...kgGk...kgGk..",
            "...kGgk....kgk..",
            "....kgk.....kk..",
            "....kkk.........",
        };

        d[ItemId.Rubber] = new[]
        {
            "...kkkkkkkkkkk..",
            "..kGggggggggGdk.",
            "..kkkkkkkkkkkkk.",
            "..kGGGGGGGGGddk.",
            "..kGGGGGGGGGddk.",
            "..kGGGGGGGGGddk.",
            "..kddddddddddk..",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.DuctTape] = new[]
        {
            "....kkkkkkkk....",
            "..kkwaaaaaaakk..",
            ".kwwaaaaaaaaaak.",
            ".kwakkkkkkkkgak.",
            ".kwak......kgGk.",
            ".kwakkkkkkkkgGk.",
            ".kwaaaGGGGGggGk.",
            "..kkGGGGGGGGkk..",
            "....kkkkkkkk....",
        };

        d[ItemId.Rope] = new[]
        {
            "...kkkkkkkkkk...",
            "..kTtTtTtTtTtk..",
            ".kTtnnnnnnnnTtk.",
            ".kTnkkkkkkkkntk.",
            ".kTnk......knTk.",
            ".ktnkkkkkkkknTk.",
            ".kTtnnnnnnnnTtk.",
            "..kTtTtTtTtTtk..",
            "...kkkkkkkkkk...",
        };

        d[ItemId.Stone] = new[]
        {
            ".....kkkkkk.....",
            "...kkwwaaggkk...",
            "..kwwaaaggggGk..",
            ".kwaaaaagggggGk.",
            ".kaaaaagggggGGk.",
            ".kaaaggggggGGGk.",
            ".kggggggggGGGGk.",
            "..kkGGGGGGGGkk..",
            "....kkkkkkkk....",
        };
        d[ItemId.RockSalt] = Map(d[ItemId.Stone], "aggGw", "wasgw");

        d[ItemId.SandBag] = new[]
        {
            "....kkkkkkkk....",
            ".....kTttnk.....",
            "....krrrrrrk....",
            "..kkTTttttttkk..",
            ".kTTttttttttttk.",
            ".kTttttttttttnk.",
            ".kTttntttttttnk.",
            ".kttttttttnttnk.",
            "..kttnttttnnnk..",
            "...kkkkkkkkkk...",
        };

        // firewood: three logs end on
        string[] log = { ".kkkk.", "kNTtNk", "kTtntk", "kttnNk", "kNttNk", ".kkkk." };
        var wood = new List<string>();
        foreach (var l in log) wood.Add("....." + l + ".....");
        foreach (var l in log) wood.Add(".." + l + l + "..");
        d[ItemId.Firewood] = wood.ToArray();

        d[ItemId.BikeChain] = new[]
        {
            "...kkkkkkkkkk...",
            "..kgaggaggaggk..",
            ".kgkkkkkkkkkkgk.",
            ".kakk......kkak.",
            ".kgk........kgk.",
            ".kakk......kkak.",
            ".kgkkkkkkkkkkgk.",
            "..kgaggaggaggk..",
            "...kkkkkkkkkk...",
        };

        d[ItemId.Tyre] = Round(p =>
        {
            p.Disc(7.5f, 7.5f, 7.2f, 7.2f, 'G', 'd', 'd');
            for (int a = 0; a < 16; a++)
            {
                double t = a * Math.PI / 8;
                p.Set((int)Math.Round(7.5 + 6.2 * Math.Cos(t)), (int)Math.Round(7.5 + 6.2 * Math.Sin(t)), 'G');
            }
            p.Disc(7.5f, 7.5f, 3.6f, 3.6f, 'a', 'g', 'G');
            p.Disc(7.5f, 7.5f, 1.3f, 1.3f, 'k', 'k', 'k');
        });

        d[ItemId.CarBattery] = new[]
        {
            "..kkk......kkk..",
            "..krk......kdk..",
            ".kkkkkkkkkkkkkk.",
            ".kbbbbbbbbbbbBk.",
            ".kbbwBwBwBwbbBk.",
            ".kbbwwwwwwwwbBk.",
            ".kbbbbbbbbbbbBk.",
            ".kBBBBBBBBBBBBk.",
            ".kkkkkkkkkkkkkk.",
        };

        d[ItemId.Electronics] = new[]
        {
            ".kkkkkkkkkkkkkk.",
            ".keeeeeeeeeeeEk.",
            ".keekkkkkkeeeEk.",
            ".kyyddddddyyeEk.",
            ".kyyddGGddyyeEk.",
            ".kyyddddddyyeEk.",
            ".keekkkkkkeeeEk.",
            ".keuuuueeeeeeEk.",
            ".kkkkkkkkkkkkkk.",
        };

        d[ItemId.FuelCan] = new[]
        {
            "...kkkkk...kk...",
            "...kRRRk..kyk...",
            "..kkkkkkkkkkkk..",
            "..krqrrrrrrrRk..",
            "..kqrrrrrrrrRk..",
            "..krrkkkkkkrRk..",
            "..krrkyyyykrRk..",
            "..krrkkkkkkrRk..",
            "..krrrrrrrrRRk..",
            "..kRRRRRRRRRRk..",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.EnginePart] = new[]
        {
            "...kkkkkkkkkk...",
            "...kwaaaaaaGk...",
            "...kkkkkkkkkk...",
            "...kaaaaaaaGk...",
            "...kkkkkkkkkk...",
            "...kgggggggGk...",
            "...kggkkkkgGk...",
            "...kgggggggGk...",
            "...kkkkkkkkkk...",
            "......kgGk......",
            "......kgGk......",
            ".....kkkkkk.....",
        };

        d[ItemId.Shotgun] = new[]
        {
            ".......kkkkkkkkk",
            "..kkkkkkwaaaagGk",
            ".knnTnnkkggggGGk",
            "knnTnnnNkkkkkkkk",
            "knTnnnNk.knnnNk.",
            "knnnnnNk.kNNNNk.",
            "knnnnNk..kkkkkk.",
            ".knnNNk.........",
            "..kkkk..........",
        };

        d[ItemId.Shells] = new[]
        {
            "..kkkkk..kkkkk..",
            "..kqrRk..kqrRk..",
            "..kqrRk..kqrRk..",
            "..kqrRk..kqrRk..",
            "..kqrRk..kqrRk..",
            "..kqrRk..kqrRk..",
            "..kqrRk..kqrRk..",
            "..kkkkk..kkkkk..",
            "..kyyYk..kyyYk..",
            "..kYYYk..kYYYk..",
            "..kkkkk..kkkkk..",
        };

        d[ItemId.Candy] = new[]
        {
            "......kkkk......",
            ".kk..kollok..kk.",
            ".krkkkollooOkkrk",
            ".krrkooooooOkrrk",
            ".krkkooooOOkkrk.",
            ".kk..kooOOk..kk.",
            "......kkkk......",
        };
        d[ItemId.Candy][4] = ".krkkooooOOkkrk.";

        d[ItemId.Pumpkin] = Pumpkin(false);
        d[ItemId.PumpkinHead] = Pumpkin(true);

        d[ItemId.CaramelApple] = Round(p =>
        {
            p.Disc(7.5f, 9.5f, 5.5f, 5f, 't', 'n', 'N');
            p.Rect(3, 12, 12, 12, 'N');
            p.Set(5, 12, 'n'); p.Set(9, 13, 'n'); p.Set(11, 13, 'n');
            p.Set(5, 7, 'T'); p.Set(6, 6, 'T');
            p.Rect(7, 1, 7, 4, 'T'); p.Set(8, 1, 't');
        });

        d[ItemId.Biberli] = new[]
        {
            ".kkkkkkkkkkkkkk.",
            ".knnnnnnnnnnnNk.",
            ".knwwwwwwwwwwNk.",
            ".knwnnnnnnnnwNk.",
            ".knwnnnwwnnnwNk.",
            ".knwnnnnnnnnwNk.",
            ".knwwwwwwwwwwNk.",
            ".kNNNNNNNNNNNNk.",
            ".kkkkkkkkkkkkkk.",
        };

        d[ItemId.Mandarin] = Round(p =>
        {
            p.Disc(7.5f, 9f, 5.6f, 5f, 'y', 'o', 'O');
            p.Set(5, 7, 'l'); p.Set(6, 6, 'l'); p.Set(5, 8, 'l');
            p.Rect(7, 3, 8, 3, 'N');
            p.Set(9, 2, 'u'); p.Set(10, 2, 'e'); p.Set(11, 3, 'E'); p.Set(10, 3, 'e'); p.Set(9, 3, 'u');
            p.Set(6, 3, 'e'); p.Set(5, 3, 'E');
        });

        d[ItemId.Grittibaenz] = new[]
        {
            ".....kkkkkk.....",
            ".....kTtttk.....",
            ".....kTNtNk.....",
            ".....kttttk.....",
            "..kkkkkkkkkkkk..",
            "..kTttnttnttnk..",
            "..kkkkkttkkkkk..",
            ".....ktttnk.....",
            ".....ktNtnk.....",
            ".....ktttnk.....",
            ".....kttnnk.....",
            ".....ktnktnk....",
            ".....ktnktnk....",
            ".....kkkkkkk....",
        };

        d[ItemId.Gluehwein] = new[]
        {
            "....a..a........",
            ".....a..a.......",
            "..kkkkkkkkkk....",
            "..kqrrrrrrRk....",
            "..kTTTTTTTtkkkk.",
            "..kTTTTTTTtkk.k.",
            "..kTTTTTTTtkk.k.",
            "..kTTTTTTTtkkkk.",
            "..kTTTTTTTtk....",
            "...kttttttttk...",
            "....kkkkkkkk....",
        };

        d[ItemId.WitchHat] = new[]
        {
            ".......kk.......",
            "......kPPk......",
            "......kPpk......",
            ".....kPppPk.....",
            ".....kPppPk.....",
            "....kPpppPPk....",
            "....kPpppPPk....",
            "....kyyYYyyk....",
            "..kkkPPPPPPkkk..",
            ".kPPppppppppPPk.",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.SantaHat] = new[]
        {
            "......kkkk......",
            ".....kwwwwk.....",
            ".....kwwaak.....",
            "......kkkk......",
            "......krrk......",
            ".....kqrrRk.....",
            ".....kqrrRk.....",
            "....kqrrrrRk....",
            "....kqrrrrRk....",
            "...kqrrrrrrRk...",
            "..kwwwwwwwwwwk..",
            "..kwwaaaaaawwk..",
            "..kkkkkkkkkkkk..",
        };

        d[ItemId.ReindeerAntlers] = Mirror(new[]
        {
            "kn..kn..",
            "kNkkNk..",
            ".kNNk...",
            "..kNk...",
            "..kNkkn.",
            "..kNNNk.",
            "...kNk..",
            "...kNk..",
            "...kNNk.",
            "....kNk.",
            "....kNNk",
            "kkkkkkkk",
            "kNnnnnnn",
            "kkkkkkkk",
        });

        // a Polaroid: white frame, a small landscape (sky, sun, hills), the wide bottom margin
        d[ItemId.Photo] = new[]
        {
            "..kkkkkkkkkkkk..",
            "..kwwwwwwwwwwk..",
            "..kwkkkkkkkkak..",
            "..kwkccccyckak..",
            "..kwkcccccckak..",
            "..kwkceccEckak..",
            "..kwkeeeEEEkak..",
            "..kwkEEEEEEkak..",
            "..kwkkkkkkkkak..",
            "..kwwwwwwwwwak..",
            "..kwwwwwwwwwak..",
            "..kwaaaaaaaaak..",
            "..kkkkkkkkkkkk..",
        };

        // boombox: handle and aerial on top, two speakers either side of the tape deck
        // weapons (#178)
        d[ItemId.Pistol] = new[]
        {
            "................",
            "................",
            "..kkkkkkkkkkkkk.",
            "..kwaaaaaaaaaGk.",
            "..kgggggggggGGk.",
            "..kkkkkkkkkkkkk.",
            "..kddddk.kGk....",
            "..kddddkkkk.....",
            "..kddddk........",
            "..kddddk........",
            "..kddddk........",
            "..kkkkkk........",
        };

        d[ItemId.Rifle] = new[]
        {
            "................",
            "...........kk...",
            "kkkkkkkkkkkykk..",
            "kEeekkkkGaaaaaak",
            "keeEkaagggGGGGGk",
            "kEEEkkkkkkkkkkkk",
            ".kEEk.kGk.......",
            "..kkk.kGk.......",
            "......kGk.......",
            "......kkk.......",
        };

        d[ItemId.HuntingRifle] = new[]
        {
            "................",
            "....kkkkkkk.....",
            "....kddddddk....",
            "....kkkkkkk.....",
            "kkk..k...k......",
            "knnkkkkkkkkkkkkk",
            "kTnnnnnnnnnaaaGk",
            "knnNNnnNNNkkkkkk",
            "kNNk.kGk........",
            ".kkk.kkk........",
        };

        d[ItemId.Knife] = new[]
        {
            "................",
            "................",
            "................",
            "...........kkk..",
            "..........kwak..",
            ".........kwak...",
            "........kwak....",
            ".......kwak.....",
            "......kaak......",
            "....kkkkk.......",
            "...krrRk........",
            "..krwrRk........",
            ".krrrRk.........",
            ".kRRRk..........",
            "..kkk...........",
        };

        // building (#274): a claw hammer, steel head top right, wooden handle down to the left
        d[ItemId.Hammer] = new[]
        {
            "................",
            "................",
            "..........kkk...",
            ".........kwagk..",
            "........kwaaggk.",
            ".......kkkagGGk.",
            "......kNnkkGGk..",
            ".....kNnk..kk...",
            "....kNnk........",
            "...kNnk.........",
            "..kNnk..........",
            ".kNnk...........",
            ".knk............",
            "..k.............",
            "................",
            "................",
        };

        d[ItemId.Ammo9mm] = new[]
        {
            "................",
            "................",
            "..kk..kk..kk....",
            ".kook.kook.kook.",
            ".kOOk.kOOk.kOOk.",
            ".kyyk.kyyk.kyyk.",
            ".kyYk.kyYk.kyYk.",
            ".kyYk.kyYk.kyYk.",
            ".kYYk.kYYk.kYYk.",
            ".kkkk.kkkk.kkkk.",
        };

        d[ItemId.Ammo75] = new[]
        {
            "..kk...kk...kk..",
            ".kook.kook.kook.",
            ".kOOk.kOOk.kOOk.",
            ".kyyk.kyyk.kyyk.",
            ".kyYk.kyYk.kyYk.",
            "kyyYkkyyYkkyyYk.",
            "kyyYkkyyYkkyyYk.",
            "kyyYkkyyYkkyyYk.",
            "kyyYkkyyYkkyyYk.",
            "kYYYkkYYYkkYYYk.",
            ".kkk..kkk..kkk..",
        };

        d[ItemId.ArmorVest] = new[]
        {
            "...kkk....kkk...",
            "..kEeek..keeEk..",
            "..kEeeekkeeeEk..",
            ".kEeeeeeeeeeeEk.",
            ".kEeeuuuuuueeEk.",
            ".kEeeuEEEEueeEk.",
            ".kEeeuuuuuueeEk.",
            ".kEeeeeeeeeeeEk.",
            ".kEeEEEEEEEEeEk.",
            ".kEeeeeeeeeeeEk.",
            ".kEEEEEEEEEEEEk.",
            ".kkkkkkkkkkkkkk.",
        };

        // Swiss match items (#478): a long wooden horn, a red caquelon of cheese, a grey canister
        d[ItemId.Alphorn] = new[]
        {
            "kk..............",
            "kTk.............",
            ".ktk............",
            "..ktk...........",
            "...ktk..........",
            "....ktk.........",
            ".....ktk........",
            "......ktk.......",
            ".......kNtk.....",
            "........kNtk....",
            ".........kNnkkk.",
            "..........kNnnTk",
            "..........kNNnnk",
            "...........kNNNk",
            "............kkk.",
        };

        d[ItemId.FonduePot] = new[]
        {
            "................",
            "........kk......",
            ".......kwk......",
            "......kwk.......",
            "..kkkkkkkkkkk...",
            ".kylyyyyyyyyYk..",
            ".kkkkkkkkkkkkk..",
            ".kqrrrrrrrrrRk..",
            "kqrrrrrrrrrrrRk.",
            "kqrrrrrrrrrrrRkk",
            "krrrrrrrrrrrrRRk",
            ".kRrrrrrrrrrRkk.",
            "..kRRRRRRRRRk...",
            "...kkkkkkkkk....",
        };

        d[ItemId.SmokeCanister] = new[]
        {
            "......kkkk......",
            ".....kddddk.kk..",
            "......kkkk.kyk..",
            ".....kaaggkkyk..",
            "....kaaggggkYk..",
            "....kagggggGk...",
            "....kagggggGk...",
            "....kaddddddk...",
            "....kagggggGk...",
            "....kagggggGk...",
            "....kagggggGk...",
            "....kagggggGk...",
            "....kGGGGGGGk...",
            ".....kkkkkkk....",
        };

        d[ItemId.Dogtag] = new[]
        {
            "......kk........",
            ".....k..k.......",
            "....k....k......",
            "...kkkkkkkkkk...",
            "..kwaaaaaaaagk..",
            "..kaGaGaGaaagk..",
            "..kaaaaaaaaagk..",
            "..kaGGGaGGaagk..",
            "..kaaaaaaaaagk..",
            "..kaGaGGaGaagk..",
            "..kaaaaaaaaagk..",
            "..kgggggggggGk..",
            "...kkkkkkkkkk...",
        };

        d[ItemId.FlareGun] = new[]
        {
            "................",
            "................",
            "..kkkkkkkkkkkk..",
            ".koooooooooodk..",
            ".kqoooooooooOdk.",
            ".kOOOOOOOOOOOdk.",
            "..kkkkkkkkkkkk..",
            "..koooOk.kGk....",
            "..kooOOkkkk.....",
            "..kooOk.........",
            "..kooOk.........",
            "..kkkkk.........",
        };

        d[ItemId.Radio] = new[]
        {
            "............kw..",
            "....kkkkkk.kw...",
            "....kw..wk.kw...",
            ".kkkkkkkkkkkkkk.",
            ".kgwgwgRgggggdk.",
            ".kGGGGGwwGGGGGk.",
            ".kGkkkGkkGkkkGk.",
            ".kGkwkGkkGkwkGk.",
            ".kGkkkGggGkkkGk.",
            ".kGGGGGggGGGGGk.",
            ".kddddddddddddk.",
            ".kkkkkkkkkkkkkk.",
        };

        // bags (#208)
        d[ItemId.BeltPouch] = new[]
        {
            "................",
            "................",
            "................",
            "..kkkkkkkkkkkk..",
            "..kNNNNNNNNNNk..",
            "..kkkkkkkkkkkk..",
            "...knnnnnnnnk...",
            "..knttttttttnk..",
            "..knnnnyynnnNk..",
            "..knnnnnnnnnNk..",
            "..knnnnnnnnnNk..",
            "..knnnnnnnnnNk..",
            "..kNnnnnnnnNNk..",
            "...kNNNNNNNNk...",
            "....kkkkkkkk....",
            "................",
        };

        d[ItemId.Handbag] = new[]
        {
            "................",
            "......kkkk......",
            ".....k....k.....",
            "....k......k....",
            "....k......k....",
            "..kkkkkkkkkkkk..",
            ".kqrrrrrrrrrrRk.",
            ".krrrrrrrrrrrRk.",
            ".krrrrryyrrrrRk.",
            ".krrrrrrrrrrrRk.",
            ".krrrrrrrrrrrRk.",
            ".krrrrrrrrrrRRk.",
            ".kRrrrrrrrrRRRk.",
            "..kRRRRRRRRRRk..",
            "...kkkkkkkkkk...",
            "................",
        };

        d[ItemId.Backpack] = new[]
        {
            "................",
            "......kkkk......",
            ".....k....k.....",
            "...kkkkkkkkkk...",
            "..kcbbbbbbbbBk..",
            "..kbbbbbbbbbBk..",
            "..kbkkkkkkkkBk..",
            "..kbkcbbbbBkBk..",
            "..kbkbbyybBkBk..",
            "..kbkbbbbbBkBk..",
            "..kbkkkkkkkkBk..",
            "..kbbbbbbbbbBk..",
            "..kBbbbbbbbBBk..",
            "..kkBBBBBBBBkk..",
            "...kk......kk...",
            "................",
        };

        d[ItemId.HikingPack] = new[]
        {
            "................",
            "...kkkkkkkkkk...",
            "..kaggggggggGk..",
            "..kgGGGGGGGGGk..",
            "...kkkkkkkkkk...",
            "...koooooooOk...",
            "..kooooooooOOk..",
            "..kokkkkkkkkOk..",
            "..kokooooOOkOk..",
            "..kokoyyoOOkOk..",
            "..kokkkkkkkkOk..",
            "..koooooooooOk..",
            "..kOoooooooOOk..",
            "..kOkkkkkkkkOk..",
            "..kkOOOOOOOOkk..",
            "...kk......kk...",
        };

        // ---- fire and placeables (#272) ----
        d[ItemId.Fondue] = new[]
        {
            "..........k.....",
            "..........gk....",
            ".........kgk....",
            "........kgk.....",
            ".kkkkkkkgkkkkk..",
            "kllllyyygyyyyyk.",
            "kryyyyyyyyyyYrk.",
            ".krrrrrrrrrrRk..",
            ".krwrrrwrrrrRk..",
            ".krrrrrrrrrrRk..",
            "..krrrwrrrwRk...",
            "...kRRRRRRRk....",
            "....kkkkkkk.....",
        };

        d[ItemId.HotChocolate] = new[]
        {
            "...a...a........",
            "....a...a.......",
            "...a...a........",
            ".kkkkkkkkk......",
            ".kNnnnnnNkkk....",
            ".kwwwwwwwk..k...",
            ".kwwrrwwwk..k...",
            ".kwwrrwwwk..k...",
            ".kwwwwwwak.k....",
            ".kwwwwwwakk.....",
            "..kaaaaak.......",
            "...kkkkk........",
        };

        d[ItemId.ToastedBread] = new[]
        {
            "...kkkkkkkkk....",
            "..kNNNNNNNNNk...",
            ".kNnttttttttNk..",
            ".kNtnttttnttNk..",
            ".kNttttnttttNk..",
            "..kNtnttttnNk...",
            "..kNttttnttNk...",
            "..kNttntttnNk...",
            "..kNtttttttNk...",
            "..kNNNNNNNNNk...",
            "...kkkkkkkkk....",
        };

        d[ItemId.Campfire] = new[]
        {
            ".......o........",
            "......oyo.......",
            ".....oyly.o.....",
            "....ooyllyoo....",
            "....oyllllyo....",
            ".....oyllyo.....",
            "...kNnNkkNnNk...",
            "..knNnNnnNnNnk..",
            ".kgakNnNNnNkagk.",
            "kgaGkkkkkkkkGagk",
            ".kGgkgaGgakgGk..",
            "..kkk.kkk.kkk...",
        };

        d[ItemId.Torch] = new[]
        {
            "..........o.....",
            ".........oyo....",
            "........oylyo...",
            "........oyly....",
            ".........oo.....",
            "........kNNk....",
            ".......kNNk.....",
            "......knnk......",
            ".....knnk.......",
            "....knnk........",
            "...knnk.........",
            "..knnk..........",
            "..kkk...........",
        };

        d[ItemId.FieldWorkbench] = new[]
        {
            "..........kkk...",
            "..........kgk...",
            ".kkkkkkkkkkgkkk.",
            ".kttttttttttttk.",
            ".kNnnnnnnnnnnNk.",
            ".kkkkkkkkkkkkkk.",
            "..knk......knk..",
            "..knk......knk..",
            "..knkkkkkkkknk..",
            "..knNNNNNNNNnk..",
            "..knkkkkkkkknk..",
            "..knk......knk..",
            "..kkk......kkk..",
        };

        // ---- gadgets (#275, drawn in #359) ----
        // two posts and the cable between them, the pulley on it
        d[ItemId.Zipline] = new[]
        {
            "................",
            ".kk.............",
            ".kNkk...........",
            ".kNkgkkk........",
            ".kNk...gkkk.....",
            ".kNk......gkkk..",
            ".kNk......kak.kk",
            ".kNk......kgk.kN",
            ".kNk.......k..kN",
            ".kNk..........kN",
            ".kNk..........kN",
            ".kNk..........kN",
            ".kNk..........kN",
            "kkkkk........kkk",
            "eeeeeeeeeeeeeeee",
            "................",
        };

        // two ropes and wooden rungs
        d[ItemId.RopeLadder] = new[]
        {
            "...gg......gg...",
            "...kTk....kTk...",
            "...kTkkkkkkTk...",
            "...kTnnnnnnTk...",
            "...kTkkkkkkTk...",
            "...kTk....kTk...",
            "...kTkkkkkkTk...",
            "...kTnnnnnnTk...",
            "...kTkkkkkkTk...",
            "...kTk....kTk...",
            "...kTkkkkkkTk...",
            "...kTnnnnnnTk...",
            "...kTkkkkkkTk...",
            "...kTk....kTk...",
            "...ktk....ktk...",
            "................",
        };

        // the mat in its tyre rim on plank legs
        d[ItemId.Trampoline] = new[]
        {
            "................",
            "................",
            "................",
            ".....kkkkkk.....",
            "...kkddddddkk...",
            "..kddGGGGGGddk..",
            ".kdGGGyyyyGGGdk.",
            ".kdGGyGGGGyGGdk.",
            ".kdGGGyyyyGGGdk.",
            "..kddGGGGGGddk..",
            "...kkddddddkk...",
            "...kNk....kNk...",
            "...kNk....kNk...",
            "...kkk....kkk...",
            "................",
            "................",
        };

        // the pad and its chevrons pointing up
        d[ItemId.LaunchPad] = new[]
        {
            "................",
            "................",
            ".......kk.......",
            "......kyyk......",
            ".....kyyyyk.....",
            "......kyyk......",
            ".....kkyykk.....",
            "....kyykkyyk....",
            "...kkkkkkkkkk...",
            "..kcccccccccck..",
            ".kcCCCCCCCCCCck.",
            ".kGGGGGGGGGGGGk.",
            ".kGgGgGgGgGgGGk.",
            "..kkkkkkkkkkkk..",
            "................",
            "................",
        };

        // the patched net on its four poles
        d[ItemId.CamoNet] = new[]
        {
            "................",
            "................",
            "..kkkkkkkkkkkk..",
            ".keEneEnEeneEek.",
            ".kEneEeEnEeEnek.",
            ".knEeEnEeEneEnk.",
            "..kkkkkkkkkkkk..",
            "..kN........Nk..",
            "..kN........Nk..",
            "..kN........Nk..",
            "..kN........Nk..",
            "..kN........Nk..",
            "..kN........Nk..",
            ".kkkk......kkkk.",
            "................",
            "................",
        };

        // a bale hut with its dark doorway
        d[ItemId.HayHideout] = new[]
        {
            "................",
            "................",
            "..kkkkkkkkkkkk..",
            ".kyyyyyyyyyyyyk.",
            ".kYYYYYYYYYYYYk.",
            ".kyyyyyyyyyyyyk.",
            ".kyyykkkkkkyyyk.",
            ".kYYYkddddkYYYk.",
            ".kyyykddddkyyyk.",
            ".kyyykddddkyyyk.",
            ".kYYYkddddkYYYk.",
            ".kyyykddddkyyyk.",
            ".kyyykddddkyyyk.",
            ".kkkkkkkkkkkkkk.",
            "................",
            "................",
        };

        // ---- shops and PAUSA machines (#273) ----
        d[ItemId.IceTea] = new[]
        {
            "......kkkk......",
            "......krrk......",
            "......kRRk......",
            ".....kkkkkk.....",
            "....koooooOk....",
            "....kooooOOk....",
            "....kyyyyyYk....",
            "....kylyyyYk....",
            "....kyyeyyYk....",
            "....kyyyyyYk....",
            "....kooooOOk....",
            "....koooOOOk....",
            "....kOOOOOOk....",
            ".....kkkkkk.....",
        };

        d[ItemId.IsotonicDrink] = new[]
        {
            "......kkkk......",
            "......kwwk......",
            "......kaak......",
            ".....kkkkkk.....",
            "....kCcccbBk....",
            "....kccccbBk....",
            "....kwwwwwwk....",
            "....kwwyywwk....",
            "....kwyywwwk....",
            "....kwwwwwwk....",
            "....kccccbBk....",
            "....kcccbbBk....",
            "....kBBBBBBk....",
            ".....kkkkkk.....",
        };

        d[ItemId.Crisps] = new[]
        {
            "...kkkkkkkkkk...",
            "...kGaGaGaGak...",
            "..kyyyyyyyyyyk..",
            "..kyllyyyyyyYk..",
            "..krrrrrrrrrRk..",
            "..kyyyTTTTyyYk..",
            "..kyyTtttTyyYk..",
            "..kyyyTTTTyyYk..",
            "..krrrrrrrrrRk..",
            "..kyyyyyyyyyYk..",
            "..kYYYYYYYYYYk..",
            "...kGaGaGaGak...",
            "...kkkkkkkkkk...",
        };

        d[ItemId.GummyBears] = new[]
        {
            "....kk....kk....",
            "...krrk..krrk...",
            "...krrkkkkrrk...",
            "....krrrrrrk....",
            "...krwrrrrwrk...",
            "...krrrrrrrrk...",
            "....krrrrrrk....",
            "..kkkrrrrrrkkk..",
            ".krrkrrqrrrkrrk.",
            ".kRRkrrrrrrkRRk.",
            "..kkkrrrrrrkkk..",
            "....krrrrrrk....",
            "...krrrkkrrrk...",
            "...kRRk..kRRk...",
            "....kk....kk....",
        };

        // the red handle with the white cross, the big blade out
        d[ItemId.SwissArmyKnife] = new[]
        {
            "................",
            "..........kk....",
            ".........kwak...",
            "........kwak....",
            ".......kwak.....",
            "......kaGk......",
            "..kkkkkkkkkkk...",
            ".krrrrrrrrrrRk..",
            ".krrrrwrrrrrRk..",
            ".krrrwwwrrrrRk..",
            ".krrrrwrrrrrRk..",
            ".kRRRRRRRRRRRk..",
            "..kkkkkkkkkkk...",
        };

        // ---- farming (#494): seed packets, sacks of harvest, bales, what is made of them ----
        d[ItemId.WheatSeed] = Packet('y', 't');
        d[ItemId.BarleySeed] = Packet('t', 'n');
        d[ItemId.MaizeSeed] = Packet('y', 'o');
        d[ItemId.SeedPotato] = Packet('n', 't');
        d[ItemId.RapeSeed] = Packet('y', 'd');
        d[ItemId.SunflowerSeed] = Packet('o', 'd');
        d[ItemId.SugarBeetSeed] = Packet('v', 'p');
        d[ItemId.VegetableSeeds] = Packet('o', 'e');
        d[ItemId.PeaSeed] = Packet('e', 'u');
        d[ItemId.Wheat] = Sack('t', 'n', 'y');
        d[ItemId.Barley] = Sack('T', 't', 'n');
        d[ItemId.Maize] = Sack('y', 'Y', 'o');
        d[ItemId.Potato] = Sack('n', 'N', 't');
        d[ItemId.Rapeseed] = Sack('y', 'Y', 'd');
        d[ItemId.SunflowerSeeds] = Sack('G', 'd', 'y');
        d[ItemId.SugarBeet] = Sack('T', 'a', 'v');
        d[ItemId.Carrot] = Sack('o', 'O', 'e');
        d[ItemId.Peas] = Sack('u', 'e', 'E');
        d[ItemId.Flour] = Sack('w', 'a', 't');
        d[ItemId.Sugar] = Sack('w', 'a', 'c');
        d[ItemId.MaizeMeal] = Sack('l', 'Y', 'y');
        d[ItemId.Fertiliser] = Sack('b', 'B', 'w');
        d[ItemId.RapeseedOil] = Map(d[ItemId.WaterBottle], "cCbBw", "yloOT");

        d[ItemId.HayBale] = Round(p =>
        {
            p.Disc(7.5f, 7.5f, 7f, 7f, 'l', 'y', 'Y');
            p.Disc(7.5f, 7.5f, 4.5f, 4.5f, 'y', 'Y', 'n');
            p.Disc(7.5f, 7.5f, 2f, 2f, 'Y', 'n', 'N');
        });

        d[ItemId.BakedPotato] = Round(p =>
        {
            p.Disc(7.5f, 9f, 6.5f, 4.5f, 't', 'n', 'N');
            p.Rect(4, 6, 11, 6, 'T');
            p.Rect(5, 7, 10, 7, 'l');
            p.Set(7, 7, 'y'); p.Set(8, 7, 'y');
        });

        d[ItemId.Roesti] = Round(p =>
        {
            p.Disc(7.5f, 9f, 7f, 4.5f, 'w', 'a', 'g');
            p.Disc(7.5f, 8.5f, 5.5f, 3.2f, 'y', 'o', 'O');
            foreach (var (x, y) in new[] { (5, 8), (8, 7), (10, 9), (7, 10), (6, 7) }) p.Set(x, y, 'Y');
        });

        d[ItemId.Polenta] = Round(p =>
        {
            p.Disc(7.5f, 10f, 7f, 4f, 'w', 'a', 'g');
            p.Rect(4, 5, 11, 9, 'y');
            p.Rect(4, 5, 11, 5, 'l');
            p.Rect(4, 9, 11, 9, 'Y');
            p.Set(6, 7, 'Y'); p.Set(9, 6, 'Y');
        });

        d[ItemId.Popcorn] = Round(p =>
        {
            for (int x = 3; x <= 12; x++) p.Rect(x, 8, x, 14, x % 2 == 0 ? 'r' : 'w');
            p.Disc(5f, 6f, 2.5f, 2.5f, 'w', 'l', 'T');
            p.Disc(8f, 4.5f, 2.5f, 2.5f, 'w', 'l', 'T');
            p.Disc(11f, 6f, 2.5f, 2.5f, 'w', 'l', 'T');
            p.Set(8, 5, 'y'); p.Set(5, 6, 'y');
        });

        d[ItemId.VegetableSoup] = Round(p =>
        {
            p.Disc(7.5f, 6f, 7f, 7.5f, 'a', 'g', 'G');
            p.Rect(0, 0, 15, 5, '.');
            p.Rect(1, 6, 14, 6, 'w');
            p.Disc(7.5f, 7f, 6f, 1.6f, 'o', 'o', 'O');
            p.Set(5, 6, 'e'); p.Set(9, 7, 'e'); p.Set(7, 6, 'y'); p.Set(11, 6, 'y');
        });

        d[ItemId.Raclette] = new[]
        {
            "................",
            "................",
            "....kkkkkkkk....",
            "..kkyyyyyyllkk..",
            ".kyyyyyyyyyllyk.",
            ".kylyyyyyyyyyyk.",
            ".kyyyyoyyyyoyYk.",
            ".kyyyyyyyyyyyYk.",
            ".kYYYYYYYYYYYYk.",
            ".kyyyyk.........",
            ".kYYYk..........",
            "..kkk...........",
        };

        d[ItemId.Hoe] = Round(p =>
        {
            for (int i = 0; i < 11; i++) { p.Set(2 + i, 14 - i, 'n'); p.Set(3 + i, 14 - i, 'N'); }
            p.Rect(9, 1, 14, 2, 'a');
            p.Rect(9, 3, 14, 3, 'g');
            p.Rect(13, 4, 14, 6, 'g');
        });

        return d;
    }
}
