using Godot;
using UnitSport.Avatar;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions;

/// <summary>
/// Halloween: late-October light, a violet dusk and a pale moon, mist pooling in the valley,
/// jack-o'-lanterns at the doors, pumpkin patches on the village edges and a treat hunt.
/// </summary>
public sealed class HalloweenOccasion : Occasion
{
    public override string Id => OccasionIds.Halloween;
    public override string Title => "Halloween";

    // ---- atmosphere ----------------------------------------------------------------------------

    /// <summary>Late October in the Valais: up at quarter past seven, down by half past six, a low noon.</summary>
    public override OccasionAtmosphere Atmosphere { get; } = new()
    {
        Sunrise = 7.25f,
        Sunset = 18.5f,
        NoonElevation = 34f,
        MistColor = new Color(0.66f, 0.64f, 0.72f),
        MistDay = 0.00012f,
        MistNight = 0.00045f,
        MistHeight = 45f,
    };

    public override (Color Tint, Color Sky) Grade(float el, Color tint, Color sky)
    {
        float day = Mathf.SmoothStep(15f, 30f, el);
        float golden = Mathf.SmoothStep(-2f, 2f, el) * (1f - Mathf.SmoothStep(8f, 22f, el));
        float dusk = Mathf.SmoothStep(-11f, -5f, el) * (1f - Mathf.SmoothStep(-2f, 1f, el));
        float night = 1f - Mathf.SmoothStep(-12f, -7f, el);

        // an autumn day: warmer light under a paler, greyer sky
        tint *= new Color(1f, 1f - 0.03f * day, 1f - 0.10f * day);
        sky = sky.Lerp(new Color(0.74f, 0.74f, 0.77f), 0.35f * day);

        // the low sun burns orange rather than gold — gently: an orange light on green ground is
        // already close to black, so the tint is only nudged and the sky does the work
        tint = tint.Lerp(new Color(1f, 0.80f, 0.60f), 0.3f * golden);
        sky = sky.Lerp(new Color(0.95f, 0.45f, 0.20f), 0.5f * golden);

        // the blue hour turns violet
        tint = tint.Lerp(new Color(0.58f, 0.42f, 0.72f), 0.6f * dusk);
        sky = sky.Lerp(new Color(0.34f, 0.17f, 0.42f), 0.7f * dusk);

        // and the night is a pale, cold moonlight
        tint = tint.Lerp(new Color(0.32f, 0.40f, 0.46f), 0.7f * night);
        sky = sky.Lerp(new Color(0.04f, 0.06f, 0.08f), 0.8f * night);
        return (tint, sky);
    }

    // ---- loot, hunt, hat -----------------------------------------------------------------------

    private static readonly Items.ItemId[] Sweets = [Items.ItemId.Candy, Items.ItemId.Candy, Items.ItemId.CaramelApple];

    /// <summary>Sweets in the kitchens and bedside drawers, a pumpkin or two in the larder.</summary>
    public override (float Chance, Items.ItemId[] Items)? Treats(FurnitureType type) => type switch
    {
        FurnitureType.Counter or FurnitureType.Nightstand or FurnitureType.ShopCounter => (0.45f, Sweets),
        FurnitureType.Fridge or FurnitureType.Stove => (0.25f, [Items.ItemId.CaramelApple, Items.ItemId.Pumpkin]),
        FurnitureType.Crate or FurnitureType.HayBale => (0.2f, [Items.ItemId.Pumpkin]),
        _ => null,
    };

    public override (Items.ItemId Id, int Count) HuntReward(Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.03) return (Items.ItemId.PumpkinHead, 1);
        if (r < 0.30) return (Items.ItemId.CaramelApple, 1);
        return (Items.ItemId.Candy, rng.Next(2, 5));
    }

    public override Headwear Hat => Headwear.WitchHat;

    /// <summary>Bats round the church from dusk, crows over the fields by day, wisps in the woods at night.</summary>
    public override Flock[] Flocks { get; } =
    [
        new(CritterKind.Bat, 14, 0.3f, 1f),
        new(CritterKind.Crow, 9, 0f, 0.5f),
        new(CritterKind.Wisp, 7, 0.75f, 1f),
    ];

    // ---- sound ---------------------------------------------------------------------------------

    private double _owlNext, _howlNext, _windNext;
    private int _tolledOn = -1;

    /// <summary>
    /// Owls in the woods and a wolf far off once it is dark, wind gusting more by night, and at
    /// real midnight the nearest church tolls three times on a low, detuned bell.
    /// </summary>
    public override void Ambience(OccasionAudio a)
    {
        if (a.Night > 0.5f)
        {
            if (a.Now >= _owlNext)
            {
                _owlNext = a.Now + a.Rand(12f, 35f);
                if (a.Bank("owl", OccasionSounds.Owl) is { } owl && a.Spot(25f, 110f, CoverFormat.IsWooded) is { } s)
                    a.Speak(owl, s + Vector3.Up * a.Rand(6f, 14f), a.Rand(0.95f, 1.05f), -6f, 12f, 260f);
            }
            if (a.Now >= _howlNext)
            {
                _howlNext = a.Now + a.Rand(50f, 120f);
                if (a.Bank("howl", OccasionSounds.Howl, 2) is { } howl && a.Spot(300f, 700f) is { } s)
                    a.Speak(howl, s + Vector3.Up * 5f, a.Rand(0.92f, 1.06f), -3f, 70f, 2000f);
            }
        }
        if (a.Now >= _windNext)
        {
            _windNext = a.Now + a.Rand(15f, 40f) / (0.4f + a.Night);
            if (a.Bank("wind", OccasionSounds.Wind) is { } wind && a.Spot(15f, 50f) is { } s)
                a.Speak(wind, s + Vector3.Up * 8f, a.Rand(0.9f, 1.1f), -14f + 6f * a.Night, 30f, 400f);
        }

        var clock = a.Clock;
        if (clock is { Hour: 0, Minute: 0, Second: >= 40 } && _tolledOn != clock.DayOfYear
            && a.Bank("toll", OccasionSounds.Toll, 1) is { } toll)
        {
            // after the ordinary twelve strokes, which finish around 30 s past
            _tolledOn = clock.DayOfYear;
            if (a.NearestTown(2400f) is { } church)
                for (int i = 0; i < 3; i++)
                    a.At(i * 5.0, () => a.Speak(toll, church + Vector3.Up * 22f, 1f, 5f, 90f, 2400f));
        }
    }

    public override float[] Jingle() => OccasionSounds.HalloweenJingle();

    // ---- props ---------------------------------------------------------------------------------

    private static readonly Color Orange = Matte(0.93f, 0.44f, 0.07f);
    private static readonly Color DarkOrange = Matte(0.72f, 0.30f, 0.05f);
    private static readonly Color Stalk = Matte(0.28f, 0.26f, 0.10f);
    /// <summary>Alpha 1 marks a light source for ps1_prop: dark by day, this colour at night.</summary>
    private static readonly Color Candle = Lamp(1.0f, 0.62f, 0.16f);

    /// <summary>An octagonal pumpkin, 0.34 m wide, authored facing +Z (MeshScratch turns it to −Z).</summary>
    private static void Pumpkin(MeshScratch s, float width, float height, Color skin)
    {
        float r = width * 0.5f;
        s.Tube(new Vector3(0, 0.00f, 0), new Vector3(0, height * 0.45f, 0), r * 0.55f, r, skin, 8);
        s.Tube(new Vector3(0, height * 0.45f, 0), new Vector3(0, height, 0), r, r * 0.45f, skin, 8);
        // the ribs: a slimmer, darker body turned half a facet, so the silhouette is lobed
        var turn = new Basis(Vector3.Up, Mathf.Pi / 8f);
        s.Box(new Vector3(0, height * 0.47f, 0), new Vector3(width * 0.98f, height * 0.62f, width * 0.40f), DarkOrange, turn);
        s.Box(new Vector3(0, height * 0.47f, 0), new Vector3(width * 0.40f, height * 0.62f, width * 0.98f), DarkOrange, turn);
        s.Tube(new Vector3(0, height * 0.95f, 0), new Vector3(0.02f, height * 1.18f, 0.01f), 0.022f, 0.015f, Stalk, 5);
    }

    public static readonly PropKind Lantern = new("JackOLantern", () =>
    {
        var s = new MeshScratch();
        Pumpkin(s, 0.36f, 0.30f, Orange);
        // The carved face, proud of the skin so it is never inside it: two eyes, a nose, a grin.
        // The ribs reach 0.19 m out, so the face sits in front of them; big enough to read at
        // 640 x 480 from across a street.
        float z = 0.205f;
        s.Box(new Vector3(-0.075f, 0.190f, z), new Vector3(0.075f, 0.060f, 0.04f), Candle, new Basis(Vector3.Back, 0.35f));
        s.Box(new Vector3(0.075f, 0.190f, z), new Vector3(0.075f, 0.060f, 0.04f), Candle, new Basis(Vector3.Back, -0.35f));
        s.Box(new Vector3(0, 0.145f, z), new Vector3(0.035f, 0.035f, 0.04f), Candle, new Basis(Vector3.Back, Mathf.Pi / 4));
        s.Box(new Vector3(0, 0.090f, z - 0.01f), new Vector3(0.200f, 0.045f, 0.04f), Candle);
        s.Box(new Vector3(-0.055f, 0.112f, z - 0.01f), new Vector3(0.030f, 0.025f, 0.04f), Candle);
        s.Box(new Vector3(0.055f, 0.112f, z - 0.01f), new Vector3(0.030f, 0.025f, 0.04f), Candle);
        return s.Build();
    });

    public static readonly PropKind FieldPumpkin = new("Pumpkin", () =>
    {
        var s = new MeshScratch();
        Pumpkin(s, 0.50f, 0.34f, Orange);
        // a leaf or two lying beside it
        s.Box(new Vector3(0.22f, 0.02f, 0.05f), new Vector3(0.20f, 0.02f, 0.16f), Matte(0.20f, 0.36f, 0.12f), new Basis(Vector3.Up, 0.5f));
        s.Box(new Vector3(-0.16f, 0.02f, -0.17f), new Vector3(0.16f, 0.02f, 0.14f), Matte(0.24f, 0.40f, 0.14f), new Basis(Vector3.Up, -0.9f));
        return s.Build();
    });

    /// <summary>A black cauldron of sweets on the doorstep: the treat hunt's spot.</summary>
    public static readonly PropKind TreatBowl = new("TreatBowl", () =>
    {
        var s = new MeshScratch();
        var iron = Matte(0.10f, 0.09f, 0.11f);
        s.Tube(new Vector3(0, 0.03f, 0), new Vector3(0, 0.13f, 0), 0.10f, 0.16f, iron, 8);
        s.Tube(new Vector3(0, 0.13f, 0), new Vector3(0, 0.20f, 0), 0.16f, 0.13f, iron, 8);
        s.Ring(new Vector3(0, 0.20f, 0), Vector3.Up, 0.11f, 0.145f, 0.02f, iron, 10);
        for (int i = 0; i < 3; i++)
        {
            float a = i * Mathf.Tau / 3;
            s.Tube(new Vector3(Mathf.Cos(a) * 0.09f, 0.04f, Mathf.Sin(a) * 0.09f),
                new Vector3(Mathf.Cos(a) * 0.11f, 0f, Mathf.Sin(a) * 0.11f), 0.015f, iron, 4);
        }
        // sweets heaped above the rim, in wrappers that catch the light
        Color[] wrappers = [Matte(0.95f, 0.55f, 0.05f), Matte(0.55f, 0.15f, 0.65f), Matte(0.25f, 0.75f, 0.20f), Matte(0.90f, 0.10f, 0.15f)];
        for (int i = 0; i < 9; i++)
        {
            float a = i * 2.4f, r = 0.03f + (i % 3) * 0.035f;
            s.Box(new Vector3(Mathf.Cos(a) * r, 0.20f + (i % 2) * 0.02f, Mathf.Sin(a) * r),
                new Vector3(0.05f, 0.03f, 0.03f), wrappers[i % wrappers.Length], new Basis(Vector3.Up, a));
        }
        return s.Build();
    });

    // ---- decorations ---------------------------------------------------------------------------

    private const uint Salt = 0x4A11;

    /// <summary>
    /// Whether a door gets a lantern, and on which side. One door in three; a third of those get a
    /// pair, one either side. Shared by <see cref="Decorate"/> and <see cref="PlaceHunt"/>, so a
    /// treat bowl is only ever at a door that has a lantern.
    /// </summary>
    private static bool LanternDoor(TileContext t, DoorSpot d, out int side, out bool pair)
    {
        var (a, b, c) = t.DoorKey(d);
        side = OccasionHash.Unit(a, b, c, Salt + 2) < 0.5f ? -1 : 1;
        pair = OccasionHash.Unit(a, b, c, Salt + 1) < 0.33f;
        return d.Width > 0 && OccasionHash.Unit(a, b, c, Salt) < 0.34f;
    }

    private static Vector3 BesideDoor(TileContext t, DoorSpot d, int side, float out_, float extra)
    {
        var outward = new Vector3(d.Outward.X, 0, d.Outward.Z).Normalized();
        var along = new Vector3(-outward.Z, 0, outward.X);
        var p = d.Position + outward * out_ + along * side * (d.Width * 0.5f + extra);
        // stand it on the ground, unless the ground there is far below the sill (steps up to a door)
        if (t.HeightAt(p) is { } g && g > d.Position.Y - 0.6f) p.Y = g;
        return p;
    }

    public override void Decorate(TileContext t, DecorBuilder into)
    {
        foreach (var d in t.Doors)
        {
            if (!LanternDoor(t, d, out int side, out bool pair)) continue;
            var (a, b, c) = t.DoorKey(d);
            for (int k = 0; k < (pair ? 2 : 1); k++)
            {
                int s = k == 0 ? side : -side;
                var p = BesideDoor(t, d, s, 0.5f, 0.32f);
                float jitter = (OccasionHash.Unit(a, b, c + k * 7919, Salt + 3) - 0.5f) * 0.8f;
                float scale = 1f + OccasionHash.Unit(a, b, c + k * 7919, Salt + 4) * 0.4f;
                into.Add(Lantern, new Transform3D(TileContext.Facing(d.Outward, jitter, scale), p),
                    seed: OccasionHash.Unit(a, b, c + k, Salt + 5));
            }
        }
        PumpkinPatches(t, into);
    }

    public override void PlaceHunt(TileContext t, DecorBuilder into)
    {
        foreach (var d in t.Doors)
        {
            if (!LanternDoor(t, d, out int side, out bool pair)) continue;
            var (a, b, c) = t.DoorKey(d);
            // a quarter of the lantern doors, so about one door in twelve
            if (OccasionHash.Unit(a, b, c, Salt + 6) >= 0.25f) continue;
            // on the side without a lantern; with a pair, a little further out
            var p = BesideDoor(t, d, -side, pair ? 0.95f : 0.55f, 0.35f);
            into.AddHuntSpot(new HuntSpot($"{Id}:{a}_{b}_{c}", Id, p, "a treat"),
                TreatBowl, new Transform3D(TileContext.Facing(d.Outward), p));
        }
    }

    // ---- pumpkin patches -----------------------------------------------------------------------

    private const float PatchCell = 160f;
    private const float HalfW = 11f, HalfD = 7f;

    /// <summary>
    /// Fields of pumpkins on the edge of each village: on a world-anchored 160 m grid, a cell in
    /// three near a town tries for a 22 × 14 m patch, and keeps it only on open, gently sloping,
    /// unmapped ground — the farmland TLM does not map — clear of roads and houses.
    /// </summary>
    private static void PumpkinPatches(TileContext t, DecorBuilder into)
    {
        if (t.Towns.Count == 0) return;
        int c0 = (int)Math.Floor(t.Id.MinE / PatchCell), c1 = (int)Math.Floor((t.Id.MinE + 1000) / PatchCell);
        int r0 = (int)Math.Floor((t.Id.MaxN - 1000) / PatchCell), r1 = (int)Math.Floor(t.Id.MaxN / PatchCell);
        for (int ce = c0; ce <= c1; ce++)
            for (int cn = r0; cn <= r1; cn++)
            {
                if (OccasionHash.Unit(ce, cn, 0, Salt + 10) > 0.33f) continue;
                double e = (ce + 0.2 + 0.6 * OccasionHash.Unit(ce, cn, 1, Salt + 11)) * PatchCell;
                double n = (cn + 0.2 + 0.6 * OccasionHash.Unit(ce, cn, 2, Salt + 11)) * PatchCell;
                if (e < t.Id.MinE + HalfW || e >= t.Id.MinE + 1000 - HalfW
                    || n <= t.Id.MaxN - 1000 + HalfD || n > t.Id.MaxN - HalfD) continue;   // whole patch in this tile
                double town = t.Towns.Min(tw => Math.Sqrt((tw.E - e) * (tw.E - e) + (tw.N - n) * (tw.N - n)));
                if (town > 1500 || town < 180) continue;   // the village edge, not the square

                var centre = t.Local(e, n);
                centre = new Vector3(Mathf.Round(centre.X), 0, Mathf.Round(centre.Z));
                if (!Suitable(t, centre)) continue;
                Plant(t, into, centre, ce, cn);
            }
    }

    private static bool Suitable(TileContext t, Vector3 centre)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var p = centre + new Vector3(i * HalfW, 0, j * HalfD);
                if (t.CoverAt(p) is not CoverClass.Open) return false;
                if (t.HeightAt(p) is not { } h) return false;
                lo = Mathf.Min(lo, h);
                hi = Mathf.Max(hi, h);
            }
        if (hi - lo > 2.4f) return false;                              // about 10% across the field
        if (t.DistanceToRoad(centre) < Mathf.Max(HalfW, HalfD) + 6f) return false;
        foreach (var d in t.Doors)
            if (new Vector2(d.Position.X - centre.X, d.Position.Z - centre.Z).Length() < 24f) return false;
        return true;
    }

    private static void Plant(TileContext t, DecorBuilder into, Vector3 centre, int ce, int cn)
    {
        int w = (int)(HalfW * 2), d = (int)(HalfD * 2);
        var corner = centre - new Vector3(HalfW, 0, HalfD);
        into.AddMesh(Soil(t, corner, w, d));
        into.AddPatch(new Rect2(corner.X, corner.Z, w, d));

        int i = 0;
        for (float x = 0.8f; x < w - 0.5f; x += 1.6f)
            for (float z = 0.8f; z < d - 0.5f; z += 1.6f, i++)
            {
                if (OccasionHash.Unit(ce, cn, i, Salt + 20) > 0.6f) continue;
                var p = corner + new Vector3(
                    x + (OccasionHash.Unit(ce, cn, i, Salt + 21) - 0.5f) * 0.6f, 0,
                    z + (OccasionHash.Unit(ce, cn, i, Salt + 22) - 0.5f) * 0.6f);
                if (t.HeightAt(p) is not { } h) continue;
                p.Y = h + 0.03f;
                float scale = 0.8f + OccasionHash.Unit(ce, cn, i, Salt + 23) * 0.6f;
                var basis = new Basis(Vector3.Up, OccasionHash.Unit(ce, cn, i, Salt + 24) * Mathf.Tau).Scaled(Vector3.One * scale);
                into.Add(FieldPumpkin, new Transform3D(basis, p), seed: 0f, glow: 0f);
            }
    }

    /// <summary>
    /// Tilled earth over the patch, one quad per 1 m lattice cell with heights read from the same
    /// terrain surface the ground mesh draws (CLAUDE.md: anything that meets the terrain is built
    /// from its lattice), lifted a few centimetres. Furrows alternate by row.
    /// </summary>
    private static ArrayMesh Soil(TileContext t, Vector3 corner, int w, int d)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var dark = Matte(0.36f, 0.25f, 0.15f).SrgbToLinear();
        var light = Matte(0.45f, 0.32f, 0.19f).SrgbToLinear();
        float H(int x, int z) => (t.HeightAt(corner + new Vector3(x, 0, z)) ?? 0f) + 0.06f;
        for (int z = 0; z < d; z++)
        {
            var colour = z % 2 == 0 ? dark : light;
            for (int x = 0; x < w; x++)
            {
                var a = corner + new Vector3(x, H(x, z), z);
                var b = corner + new Vector3(x + 1, H(x + 1, z), z);
                var c = corner + new Vector3(x + 1, H(x + 1, z + 1), z + 1);
                var e = corner + new Vector3(x, H(x, z + 1), z + 1);
                foreach (var v in new[] { a, b, c, a, c, e })
                {
                    st.SetColor(colour);
                    st.AddVertex(new Vector3(v.X, v.Y - corner.Y, v.Z));
                }
            }
        }
        return st.Commit();
    }
}
