using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The colony kit's exteriors (#1940, docs/plans/moon-surface.md section 4): the hub, cans, stacks, moondust domes,
/// the glass biodome, the hangar, the tubes and alleys between doors, the landing pads and the road. Realistic sci-fi in
/// the Cartoon kit's flat colours: a module is a few tubes and boxes in its theme's colours, its windows on the glass
/// surface lit by <see cref="WindowMaterial"/>. A module is authored along +Z with its floor on y = 0 and built once per
/// size and theme (MeshScratch turns it to face −Z). All in the model viewer under "Moon colony".
/// </summary>
public static class ColonyKit
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    /// <summary>A theme's colours: the shell, the trim, a darker band, the legs and frames, the windows.</summary>
    public readonly record struct Palette(Color Shell, Color Trim, Color Band, Color Frame, Color Window, Color Accent);

    public static Palette Of(ColonyTheme t) => t switch
    {
        ColonyTheme.Artemis => new(C("eef1f3"), C("1f4fa8"), C("c9cfd6"), C("8e959c"), C("ffd98a"), C("d33a2c")),
        ColonyTheme.MoonVillage => new(C("e7dfcb"), C("163a8c"), C("cbbf9f"), C("9aa0a6"), C("e8f2ff"), C("f0b429")),
        ColonyTheme.Ilrs => new(C("d6dadf"), C("c4262e"), C("b9a46a"), C("7f868d"), C("ffc977"), C("e8c547")),
        ColonyTheme.ColdWar => new(C("6b7048"), C("3f4430"), C("8b8d74"), C("4a4d48"), C("f5d26b"), C("b03a2e")),
        _ => new(C("5a5c5e"), C("3b3d40"), C("4a4b4c"), C("35373a"), C("3a3020"), C("7a2a22")),
    };

    private static readonly Color Regolith = C("8d8a84"), RegolithDark = C("77746e"), Sinter = C("6e6b66"),
        Paint = C("e8e6df"), PadLight = C("ff5a3c"), Glass = C("bfe3ff"), Leaf = C("3f8f3a"), LeafDark = C("2c6b2c"),
        Bark = C("6b4a2c"), Grass = C("5aa344"), Soil = C("5a4632"), Lamp = C("f4f1e6"), AmberLamp = C("ffb347");

    /// <summary>A tube's axis over the plateau, metres: every door's centre is at this height.</summary>
    public const float TubeY = 2.6f;

    /// <summary>Every floor a walker uses inside (the doorways' sill), metres over the plateau.</summary>
    public const float FloorY = TubeY - DoorH / 2;

    /// <summary>A doorway's width and height, metres.</summary>
    public const float DoorW = 1.3f, DoorH = 2.1f;

    /// <summary>A walk-in tube's radius, metres: its floor at the sill is 1.9 m wide, 2.45 m under its crown.</summary>
    public const float TubeR = 1.4f;

    private static readonly Dictionary<string, ArrayMesh> Cache = new();
    private static readonly object Lock = new();
    private static ShaderMaterial? _window, _glass;

    /// <summary>A module's mesh, built once per kind, size and theme (any thread).</summary>
    public static ArrayMesh Module(ModuleKind kind, double length, double width, double height, int storeys, ColonyTheme theme)
    {
        string key = FormattableString.Invariant($"{kind}/{length:F1}/{width:F1}/{height:F1}/{storeys}/{theme}");
        lock (Lock)
        {
            if (Cache.TryGetValue(key, out var m)) return m;
            var s = new MeshScratch();
            var p = Of(theme);
            float l = (float)length, w = (float)width, h = (float)height;
            switch (kind)
            {
                case ModuleKind.Hub: Hub(s, p, w / 2, h, storeys); break;
                case ModuleKind.Stack: Stack(s, p, w / 2, h, storeys); break;
                case ModuleKind.Can: Can(s, p, l, w / 2); break;
                case ModuleKind.Dome: Dome(s, w / 2, theme); break;
                case ModuleKind.Biodome: Biodome(s, p, w / 2, theme == ColonyTheme.ColdWar); break;
                case ModuleKind.Hangar: Hangar(s, p, l, w, h); break;
                case ModuleKind.Airlock: Can(s, p, l, w / 2, airlock: true); break;
            }
            m = s.Build();
            Cache[key] = m;
            return m;
        }
    }

    /// <summary>A module in its theme: the shell's material, its windows lit.</summary>
    public static MeshInstance3D Instance(ArrayMesh mesh)
    {
        var mi = new MeshInstance3D { Mesh = mesh };
        MeshScratch.Paint(mi, HumanMeshBuilder.Material(), WindowMaterial());
        return mi;
    }

    /// <summary>
    /// The windows and lamps: unshaded, their vertex colour lifted, so a colony's windows glow through the lunar night
    /// and read from afar. A biodome's panes use <see cref="GlassMaterial"/> instead.
    /// </summary>
    public static ShaderMaterial WindowMaterial() => _window ??= new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, shadows_disabled;
                void fragment() {
                    ALBEDO = COLOR.rgb * 1.35;
                }
                """,
        },
    };

    /// <summary>A biodome's glass: a faint blue tint you see the garden through, a little brighter at grazing angles.</summary>
    public static ShaderMaterial GlassMaterial() => _glass ??= new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode blend_mix, cull_disabled, depth_draw_never, shadows_disabled, unshaded;
                void fragment() {
                    float edge = 1.0 - abs(dot(NORMAL, VIEW));
                    ALBEDO = COLOR.rgb;
                    ALPHA = 0.10 + 0.30 * edge * edge;
                }
                """,
        },
    };

    // ---- the viewer --------------------------------------------------------------------------------------------------

    /// <summary>Every kind of module, Artemis' colours, plus the other themes' cans and the moondust and glass domes.</summary>
    [Showcase("Moon colony")]
    public static IEnumerable<(string Name, Func<Node3D> Make)> Modules()
    {
        yield return ("Hub (Artemis)", () => Show(Module(ModuleKind.Hub, 10, 10, 7.5, 2, ColonyTheme.Artemis)));
        foreach (ColonyTheme t in Enum.GetValues<ColonyTheme>())
        {
            var tt = t;
            yield return ($"Can ({t})", () => Show(Module(ModuleKind.Can, 14, 4.6, 5.5, 1, tt)));
        }
        yield return ("Stack (Moon Village)", () => Show(Module(ModuleKind.Stack, 9, 9, 10.5, 3, ColonyTheme.MoonVillage)));
        yield return ("Moondust dome", () => Show(Module(ModuleKind.Dome, 18, 18, 9, 1, ColonyTheme.Artemis)));
        yield return ("Biodome", () => Glazed(Module(ModuleKind.Biodome, 50, 50, 25, 1, ColonyTheme.MoonVillage)));
        yield return ("Biodome (Cold War, overgrown)", () => Glazed(Module(ModuleKind.Biodome, 64, 64, 32, 1, ColonyTheme.ColdWar)));
        yield return ("Hangar (Artemis)", () => Show(Module(ModuleKind.Hangar, 26, 14, 8, 1, ColonyTheme.Artemis)));
        yield return ("Tube and alley", () => Show(Links([(new(-6, 0, 0), new(6, 0, 0), false), (new(-6, 0, 6), new(6, 0, 6), true)], ColonyTheme.Artemis)));
        yield return ("Landing pad", () => Show(PadMesh(20, 1)));
    }

    private static Node3D Show(ArrayMesh m) => Instance(m);

    private static Node3D Glazed(ArrayMesh m)
    {
        var mi = new MeshInstance3D { Mesh = m };
        MeshScratch.Paint(mi, HumanMeshBuilder.Material(), GlassMaterial());
        return mi;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static Basis Yaw(float rad) => new(Vector3.Up, rad);

    /// <summary>A window: a lit pane on a wall, facing out along <paramref name="normal"/>, 4 mm proud of it.</summary>
    private static void Window(MeshScratch s, Vector3 centre, Vector3 normal, Vector3 up, float w, float h, Color c)
    {
        var right = up.Cross(normal).Normalized() * (w / 2);
        var u = up.Normalized() * (h / 2);
        var o = centre + normal.Normalized() * 0.004f;
        s.Pane([o - right - u, o + right - u, o + right + u, o - right + u], c);
    }

    // ---- modules -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The hub: a drum of wall panels on a plinth (seen from both sides: you walk in), a doorway each way at the floor
    /// (<see cref="FloorY"/>), a floor and a ceiling, a band, windows, a low dome roof with its mast and flag.
    /// </summary>
    private static void Hub(MeshScratch s, Palette p, float r, float h, int storeys) => Drum(s, p, r, h, storeys, ribbed: false);

    /// <summary>A stack: the same drum, ribbed (the inflatable look), storeys of windows, a domed top.</summary>
    private static void Stack(MeshScratch s, Palette p, float r, float h, int storeys) => Drum(s, p, r, h, storeys, ribbed: true);

    private static void Drum(MeshScratch s, Palette p, float r, float h, int storeys, bool ribbed)
    {
        const int panels = 20;
        s.Tube(new(0, 0, 0), new(0, FloorY - 0.15f, 0), r + 0.4f, p.Frame, panels);
        s.Tube(new(0, FloorY - 0.15f, 0), new(0, FloorY, 0), r - 0.05f, p.Band, panels);
        float pw = 2 * r * Mathf.Sin(Mathf.Pi / panels) * 1.04f;
        for (int i = 0; i < panels; i++)
        {
            float a = Mathf.Tau * i / panels;
            var n = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            var b = Yaw(a);
            if (i % (panels / 4) == 0)
            {
                // a doorway: the panel above and below it, the frame round it
                s.Box(n * r + Vector3.Up * ((FloorY + DoorH + h) / 2), new(pw, h - FloorY - DoorH, 0.2f), p.Shell, b);
                s.Box(n * r + Vector3.Up * (FloorY / 2), new(pw, FloorY, 0.2f), p.Shell, b);
                DoorFrame(s, n * r, b, p);
                continue;
            }
            s.Box(n * r + Vector3.Up * (h / 2), new(pw, h, 0.2f), p.Shell, b);
        }
        float storey = (h - FloorY) / storeys;
        for (int k = 1; k < storeys; k++)
            s.Ring(new(0, FloorY + k * storey, 0), Vector3.Up, r + 0.08f, r + 0.22f, ribbed ? 0.24f : 0.3f, ribbed ? p.Band : p.Trim, panels);
        if (ribbed)
            for (int k = 0; k < storeys; k++) s.Ring(new(0, FloorY + (k + 0.5f) * storey, 0), Vector3.Up, r + 0.08f, r + 0.16f, 0.12f, p.Band, panels);
        for (int k = 0; k < storeys; k++)
            for (int i = 0; i < panels; i++)
            {
                if (i % (panels / 4) == 0 || i % 2 == 1) continue;
                float a = Mathf.Tau * i / panels;
                var n = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                Window(s, n * (r + 0.1f) + Vector3.Up * (FloorY + (k + 0.55f) * storey), n, Vector3.Up, 1.0f, 0.7f, p.Window);
            }
        // the ceiling (its underside seen from the hall), the roof over it, the mast and flag
        s.Tube(new(0, h - 0.15f, 0), new(0, h, 0), r + 0.1f, p.Band, panels);
        s.Tube(new(0, h, 0), new(0, h + r * 0.35f, 0), r + 0.1f, r * 0.45f, ribbed ? p.Shell : p.Band, panels);
        s.Tube(new(0, h + r * 0.35f, 0), new(0, h + r * 0.35f + 3.5f, 0), 0.06f, p.Frame, 5);
        s.Box(new(0.6f, h + r * 0.35f + 3.0f, 0), new(1.0f, 0.6f, 0.02f), p.Trim);
        // the hall's lamps: a ring of panes under the ceiling
        for (int i = 0; i < 8; i++)
        {
            float a = Mathf.Tau * i / 8;
            Window(s, new Vector3(Mathf.Sin(a) * r * 0.55f, h - 0.16f, Mathf.Cos(a) * r * 0.55f), Vector3.Down, Vector3.Forward, 0.8f, 0.3f, Lamp);
        }
    }

    /// <summary>A doorway's frame on a wall at <paramref name="at"/> (its floor-level centre), turned by <paramref name="b"/>: posts, lintel, a sill strip.</summary>
    private static void DoorFrame(MeshScratch s, Vector3 at, Basis b, Palette p)
    {
        var right = b * Vector3.Right;
        foreach (float side in new[] { -1f, 1f })
            s.Box(at + right * side * (DoorW / 2 + 0.06f) + Vector3.Up * (FloorY + DoorH / 2), new(0.12f, DoorH, 0.34f), p.Frame, b);
        s.Box(at + Vector3.Up * (FloorY + DoorH + 0.06f), new(DoorW + 0.24f, 0.12f, 0.34f), p.Trim, b);
        s.Box(at + Vector3.Up * (FloorY + 0.01f), new(DoorW, 0.02f, 0.34f), p.Frame, b);
    }

    /// <summary>
    /// A can: a double-sided shell 4.6 m across lying on legs and cradles (you walk in), end walls with a doorway at the
    /// floor, a floor, lamps along the ceiling, trim rings, portholes, a radiator on top. An airlock is a short one in
    /// hazard colours (its two door leaves are nodes: <see cref="ColonyAirlocks"/>).
    /// </summary>
    private static void Can(MeshScratch s, Palette p, float l, float r, bool airlock = false)
    {
        float y = r + 0.9f;
        s.Skirt(new(0, y, -l / 2), new(0, y, l / 2), r, r, airlock ? p.Band : p.Shell, 18);
        float top = FloorY + DoorH;
        foreach (float z in new[] { -l / 2, l / 2 })
        {
            // the end wall: a ring to the shell, the doorway's surround filling its hole
            s.Ring(new(0, y, z), Vector3.Back, 1.55f, r + 0.12f, 0.2f, p.Band, 18);
            s.Box(new(-(1.55f + DoorW / 2) / 2, y, z), new(1.55f - DoorW / 2, 3.1f, 0.2f), p.Band);
            s.Box(new((1.55f + DoorW / 2) / 2, y, z), new(1.55f - DoorW / 2, 3.1f, 0.2f), p.Band);
            s.Box(new(0, (top + y + 1.55f) / 2, z), new(DoorW, y + 1.55f - top, 0.2f), p.Band);
            DoorFrame(s, new Vector3(0, 0, z), Basis.Identity, p);
            if (airlock)
                for (int k = 0; k < 4; k++)
                    s.Box(new(-0.9f + k * 0.6f, top + 0.3f, z + (z > 0 ? 0.11f : -0.11f)), new(0.3f, 0.2f, 0.02f), p.Accent);
        }
        // the floor: a deck across the shell at the doorways' sill
        float chord = 2 * Mathf.Sqrt(Mathf.Max(0, r * r - (y - FloorY) * (y - FloorY)));
        s.Box(new(0, FloorY - 0.06f, 0), new(chord, 0.12f, l), p.Frame);
        foreach (float z in new[] { -l * 0.25f, l * 0.25f })
            s.Ring(new(0, y, z), Vector3.Back, r - 0.01f, r + 0.07f, 0.4f, p.Trim, 18);
        foreach (float z in new[] { -l * 0.32f, l * 0.32f })
        {
            s.Box(new(0, 0.55f, z), new(r * 1.7f, 0.25f, 0.5f), p.Frame);
            foreach (float x in new[] { -r * 0.75f, r * 0.75f }) s.Tube(new(x, 0, z), new(x, 0.6f, z), 0.12f, p.Frame, 6);
        }
        int n = Math.Max(1, (int)(l / 2.6f));
        for (int i = 0; i < n; i++)
        {
            float z = -l / 2 + (i + 0.5f) * l / n;
            foreach (float side in new[] { 1f, -1f })
            {
                var nrm = new Vector3(side, 0.25f, 0).Normalized();
                Window(s, new Vector3(0, y, z) + nrm * (r + 0.01f), nrm, Vector3.Forward, 0.55f, 0.55f, p.Window);
            }
            // a lamp on the ceiling
            Window(s, new Vector3(0, y + r - 0.03f, z), Vector3.Down, Vector3.Forward, 0.4f, 1.2f, airlock ? AmberLamp : Lamp);
        }
        s.Box(new(0, y + r + 0.3f, 0), new(1.6f, 0.08f, l * 0.6f), p.Band);
        s.Tube(new(0, y + r, l * 0.3f), new(0, y + r + 0.4f, l * 0.3f), 0.05f, p.Frame, 4);
    }

    /// <summary>
    /// A moondust dome: a lining under bags of regolith, lumpy, its rim settled into the ground (seen from both sides: you
    /// walk in), a raised floor at the doorway's sill, an entrance arch at +Z with its doorway, lamps under the crown.
    /// </summary>
    private static void Dome(MeshScratch s, float r, ColonyTheme theme)
    {
        var rng = new Random(77 + (int)(r * 10));
        var pal = Of(theme);
        const int rings = 6, sides = 18;
        float prevR = r * 1.04f, prevY = -0.3f;
        for (int k = 1; k <= rings; k++)
        {
            float a = Mathf.Pi / 2 * k / rings;
            float rr = r * Mathf.Cos(a) * (1 + 0.04f * ((float)rng.NextDouble() - 0.5f));
            float yy = r * Mathf.Sin(a) * 0.92f + FloorY * 0.5f;
            s.Skirt(new(0, prevY, 0), new(0, yy, 0), prevR, Math.Max(rr, 0.05f), k % 2 == 0 ? Regolith : RegolithDark, sides);
            prevR = rr;
            prevY = yy;
        }
        for (int i = 0; i < 40; i++)
        {
            float az = (float)rng.NextDouble() * Mathf.Tau, el = 0.15f + (float)rng.NextDouble() * 1.1f;
            if (Mathf.Abs(Mathf.Wrap(az, -Mathf.Pi, Mathf.Pi)) < 0.35f && el < 0.6f) continue;   // clear of the entrance
            var n = new Vector3(Mathf.Cos(el) * Mathf.Sin(az), Mathf.Sin(el) * 0.92f, Mathf.Cos(el) * Mathf.Cos(az));
            s.Box(n * r * 1.02f + Vector3.Up * FloorY * 0.5f, new(1.6f, 0.5f, 1.0f), i % 3 == 0 ? RegolithDark : Regolith, Yaw(az) * new Basis(Vector3.Right, -el));
        }
        s.Tube(new(0, FloorY - 0.2f, 0), new(0, FloorY, 0), r * 0.97f, pal.Frame, sides);
        // the entrance arch at +Z: walls either side of the doorway, a lintel, the frame
        float ez = r * 0.98f;
        foreach (float side in new[] { -1f, 1f })
            s.Box(new(side * (DoorW / 2 + 0.6f), (FloorY + DoorH + 0.8f) / 2, ez), new(1.2f, FloorY + DoorH + 0.8f, 1.8f), Regolith);
        s.Box(new(0, FloorY + DoorH + 0.4f, ez), new(DoorW, 0.8f, 1.8f), Regolith);
        s.Box(new(0, FloorY / 2, ez + 0.4f), new(DoorW, FloorY, 1.0f), Regolith);
        DoorFrame(s, new Vector3(0, 0, ez + 0.85f), Basis.Identity, pal);
        for (int i = 0; i < 6; i++)
        {
            float a = Mathf.Tau * i / 6;
            Window(s, new Vector3(Mathf.Sin(a) * r * 0.3f, r * 0.85f, Mathf.Cos(a) * r * 0.3f), Vector3.Down, Vector3.Forward, 0.5f, 0.5f, Lamp);
        }
    }

    /// <summary>
    /// A glass biodome: a ring wall, a geodesic-looking frame of meridians and rings, glass panes between them (the glass
    /// surface), a garden inside: grass, beds, paths, trees. The Cold War one is overgrown and has cracked panes.
    /// </summary>
    private static void Biodome(MeshScratch s, Palette p, float r, bool overgrown)
    {
        var rng = new Random(overgrown ? 1970 : 2030);
        s.Tube(new(0, -0.2f, 0), new(0, 1.2f, 0), r + 0.3f, p.Shell, 32);
        s.Tube(new(0, 1.1f, 0), new(0, 1.25f, 0), r - 0.2f, Grass, 32);
        const int meridians = 16, rings = 6;
        Vector3 At(int i, int k)
        {
            float az = Mathf.Tau * i / meridians, el = Mathf.Pi / 2 * k / rings;
            return new Vector3(Mathf.Cos(el) * Mathf.Sin(az) * r, 1.2f + Mathf.Sin(el) * r, Mathf.Cos(el) * Mathf.Cos(az) * r);
        }
        for (int i = 0; i < meridians; i++)
            for (int k = 0; k < rings; k++)
            {
                var a = At(i, k);
                var b = At(i, k + 1);
                var c = At(i + 1, k);
                var d = At(i + 1, k + 1);
                s.Tube(a, b, 0.12f, p.Frame, 4);
                s.Tube(a, c, 0.1f, p.Frame, 4);
                if (overgrown && rng.NextDouble() < 0.12) continue;   // a cracked, missing pane
                if (k == rings - 1) s.Pane([a, c, b], Glass);
                else s.Pane([a, c, d, b], Glass);
            }
        // the garden: paths in a cross, beds, a pond, trees
        s.Box(new(0, 1.3f, 0), new(2.2f, 0.08f, 2 * r - 3), Paint);
        s.Box(new(0, 1.3f, 0), new(2 * r - 3, 0.08f, 2.2f), Paint);
        s.Tube(new(r * 0.4f, 1.25f, r * 0.4f), new(r * 0.4f, 1.32f, r * 0.4f), r * 0.12f, C("3d7fb8"), 16);
        int trees = (int)(r * (overgrown ? 2.2f : 1.0f));
        for (int i = 0; i < trees; i++)
        {
            float az = (float)rng.NextDouble() * Mathf.Tau, rr = (0.2f + 0.7f * (float)rng.NextDouble()) * r;
            var at = new Vector3(Mathf.Sin(az) * rr, 1.25f, Mathf.Cos(az) * rr);
            if (Math.Abs(at.X) < 1.6f || Math.Abs(at.Z) < 1.6f) continue;   // off the paths
            float th = (overgrown ? 7 : 4) + (float)rng.NextDouble() * (overgrown ? 9 : 4);
            float cr = 1.2f + (float)rng.NextDouble() * (overgrown ? 2.8f : 1.4f);
            // under the glass: the crown never pokes through
            float roof = 1.2f + Mathf.Sqrt(Mathf.Max(0, r * r - rr * rr)) - cr - 0.5f;
            th = Mathf.Min(th, roof);
            if (th < 2) continue;
            s.Tube(at, at + Vector3.Up * th, 0.18f, 0.12f, Bark, 6);
            s.Tube(at + Vector3.Up * (th - cr * 0.6f), at + Vector3.Up * (th + cr * 0.9f), cr, cr * 0.25f, i % 2 == 0 ? Leaf : LeafDark, 8);
        }
        for (int i = 0; i < (overgrown ? 30 : 10); i++)
        {
            float az = (float)rng.NextDouble() * Mathf.Tau, rr = (0.15f + 0.75f * (float)rng.NextDouble()) * r;
            var at = new Vector3(Mathf.Sin(az) * rr, 1.45f, Mathf.Cos(az) * rr);
            s.Box(at, new(1.6f, 0.35f, 1.0f), overgrown ? LeafDark : Soil, Yaw(az));
        }
        // the door through the ring wall at +Z
        s.Box(new(0, 1.3f, r + 0.3f), new(1.4f, 2.4f, 0.8f), p.Frame);
    }

    /// <summary>
    /// A hangar: a half-cylinder of panels on a floor slab (you walk and drive in), ribs, the vehicle door shut at +Z
    /// (phase 5 opens it), a doorway at −Z on a landing at the tubes' sill with steps down to the floor, lamps under the roof.
    /// </summary>
    private static void Hangar(MeshScratch s, Palette p, float l, float w, float h)
    {
        s.Box(new(0, 0.3f, 0), new(w + 0.6f, 0.6f, l + 0.6f), p.Frame);
        const int panels = 10;
        float r = w / 2;
        float sy = h / r;
        for (int i = 0; i < panels; i++)
        {
            float a0 = Mathf.Pi * i / panels, a1 = Mathf.Pi * (i + 1) / panels;
            var p0 = new Vector3(Mathf.Cos(a0) * r, 0.6f + Mathf.Sin(a0) * r * sy, 0);
            var p1 = new Vector3(Mathf.Cos(a1) * r, 0.6f + Mathf.Sin(a1) * r * sy, 0);
            var d = p1 - p0;
            var n = new Vector3(-d.Y, d.X, 0).Normalized();
            var basis = new Basis(d.Normalized(), n, Vector3.Back);
            s.Box((p0 + p1) / 2, new(d.Length() * 1.03f, 0.25f, l), i % 2 == 0 ? p.Shell : p.Band, basis);
        }
        for (int k = 0; k <= 4; k++)
        {
            float z = -l / 2 + k * l / 4;
            for (int i = 0; i < panels; i++)
            {
                float a0 = Mathf.Pi * i / panels, a1 = Mathf.Pi * (i + 1) / panels;
                s.Tube(new(Mathf.Cos(a0) * (r + 0.15f), 0.6f + Mathf.Sin(a0) * r * sy + 0.1f, z),
                    new(Mathf.Cos(a1) * (r + 0.15f), 0.6f + Mathf.Sin(a1) * r * sy + 0.1f, z), 0.12f, p.Trim, 4);
            }
        }
        // the end walls: six panels each to the roof line; at -Z the middle two leave a doorway at the tubes' sill
        foreach (float side in new[] { 1f, -1f })
            for (int i = 0; i < 6; i++)
            {
                float x0 = -r + i * w / 6, x1 = x0 + w / 6, x = (x0 + x1) / 2;
                float hh = 0.6f + Mathf.Sqrt(Mathf.Max(0, r * r - x * x)) * sy;
                float z = side * l / 2;
                if (side < 0 && (i == 2 || i == 3))
                {
                    float inner = i == 2 ? -DoorW / 2 : DoorW / 2;
                    float outer = i == 2 ? x0 : x1;
                    s.Box(new((inner + outer) / 2, hh / 2, z), new(Mathf.Abs(outer - inner), hh, 0.3f), p.Band);
                    float cx = i == 2 ? -DoorW / 4 : DoorW / 4;
                    s.Box(new(cx, (FloorY + DoorH + hh) / 2, z), new(DoorW / 2, hh - FloorY - DoorH, 0.3f), p.Band);
                    s.Box(new(cx, FloorY / 2, z), new(DoorW / 2, FloorY, 0.3f), p.Band);
                    continue;
                }
                s.Box(new(x, hh / 2, z), new(w / 6, hh, 0.3f), p.Band);
            }
        DoorFrame(s, new Vector3(0, 0, -l / 2), Basis.Identity, p);
        // inside at -Z: a landing at the sill, steps down to the floor
        s.Box(new(0, (FloorY + 0.6f) / 2, -l / 2 + 1.0f), new(2.4f, FloorY - 0.6f, 2.0f), p.Frame);
        const int steps = 5;
        float rise = (FloorY - 0.6f) / steps;
        for (int k = 1; k < steps; k++)
            s.Box(new(0, 0.6f + (FloorY - 0.6f - k * rise) / 2, -l / 2 + 2.0f + (k - 0.5f) * 0.3f), new(2.4f, FloorY - 0.6f - k * rise, 0.3f), p.Frame);
        // the vehicle door, shut, its frame and lamps
        s.Box(new(0, 0.6f + 2.4f, l / 2 + 0.2f), new(8f, 4.8f, 0.2f), p.Frame);
        s.Box(new(0, 0.6f + 4.95f, l / 2 + 0.25f), new(8.4f, 0.3f, 0.2f), p.Accent);
        for (int i = 0; i < 4; i++) Window(s, new(-3f + i * 2f, 0.6f + 5.4f, l / 2 + 0.3f), Vector3.Back, Vector3.Up, 0.5f, 0.25f, p.Window);
        for (int k = 0; k < 4; k++)
            Window(s, new(0, 0.6f + h - 0.4f, -l / 2 + (k + 0.5f) * l / 4), Vector3.Down, Vector3.Forward, 1.2f, 0.4f, Lamp);
        s.Box(new(0, 0.6f + h + 0.15f, 0), new(3.5f, 0.05f, 6f), p.Trim);
    }

    // ---- links, pads, roads (built in the colony's own frame) ---------------------------------------------------

    /// <summary>
    /// The tubes and alleys of a colony, one mesh in its frame (x east, z south): a tube 2.4 m across on short legs with
    /// rings every 3 m, or an open alley (a deck, low walls, a roof on posts). Points are pre-turned so MeshScratch's half
    /// turn lands them where the plan puts them.
    /// </summary>
    public static ArrayMesh Links(IEnumerable<(Vector3 A, Vector3 B, bool Alley)> links, ColonyTheme theme)
    {
        var s = new MeshScratch();
        var p = Of(theme);
        static Vector3 T(Vector3 v) => new(-v.X, v.Y, -v.Z);
        foreach (var (a0, b0, alley) in links)
        {
            var a = T(a0);
            var b = T(b0);
            var d = b - a;
            float len = d.Length();
            if (len < 0.5f) continue;
            var dir = d / len;
            var side = Vector3.Up.Cross(dir).Normalized();
            float yaw = Mathf.Atan2(dir.X, dir.Z);
            if (!alley)
            {
                // a walk-in tube: a double-sided shell 2.8 m across, ribs, a floor at the doors' sill, legs, lamps
                const float y = TubeY;
                s.Skirt(a + Vector3.Up * y, b + Vector3.Up * y, TubeR, TubeR, p.Band, 14);
                int n = Math.Max(1, (int)(len / 3));
                for (int i = 0; i <= n; i++)
                {
                    var c = a.Lerp(b, i / (float)n);
                    s.Ring(c + Vector3.Up * y, dir, TubeR - 0.01f, TubeR + 0.1f, 0.24f, i == 0 || i == n ? p.Trim : p.Shell, 14);
                    if (i % 2 == 1)
                        foreach (float sd in new[] { -0.7f, 0.7f })
                            s.Tube(c + side * sd, c + side * sd + Vector3.Up * (y - TubeR + 0.1f), 0.08f, p.Frame, 5);
                    if (i < n) Window(s, a.Lerp(b, (i + 0.5f) / n) + Vector3.Up * (y + TubeR - 0.03f), Vector3.Down, dir, 0.3f, 0.8f, Lamp);
                }
                float chord = 2 * Mathf.Sqrt(TubeR * TubeR - (y - FloorY) * (y - FloorY));
                s.Box((a + b) / 2 + Vector3.Up * (FloorY - 0.05f), new(chord, 0.1f, len), p.Frame, Yaw(yaw));
            }
            else
            {
                var mid = (a + b) / 2;
                s.Box(mid + Vector3.Up * 0.15f, new(2.6f, 0.3f, len), p.Frame, Yaw(yaw));
                foreach (float sd in new[] { -1.3f, 1.3f })
                {
                    s.Box(mid + side * sd + Vector3.Up * 0.8f, new(0.12f, 1.1f, len), p.Band, Yaw(yaw));
                    int n = Math.Max(1, (int)(len / 4));
                    for (int i = 0; i <= n; i++)
                        s.Tube(a.Lerp(b, i / (float)n) + side * sd, a.Lerp(b, i / (float)n) + side * sd + Vector3.Up * 2.8f, 0.07f, p.Frame, 4);
                }
                s.Box(mid + Vector3.Up * 2.85f, new(3.0f, 0.12f, len), p.Shell, Yaw(yaw));
            }
        }
        return s.Build();
    }

    /// <summary>A landing pad: a sintered disc 20 m across a side of its radius, a painted ring and number, a low blast berm, edge lights.</summary>
    public static ArrayMesh PadMesh(float r, int number)
    {
        string key = FormattableString.Invariant($"pad/{r:F1}/{number}");
        lock (Lock)
        {
            if (Cache.TryGetValue(key, out var m)) return m;
            var s = new MeshScratch();
            s.Tube(new(0, -0.3f, 0), new(0, 0.12f, 0), r, Sinter, 32);
            s.Ring(new(0, 0.13f, 0), Vector3.Up, r * 0.62f, r * 0.68f, 0.02f, Paint, 32);
            s.Ring(new(0, 0.13f, 0), Vector3.Up, r - 1.2f, r - 0.9f, 0.02f, Paint, 32);
            // the number in bars: n bars across the middle
            for (int i = 0; i < number; i++) s.Box(new((i - (number - 1) / 2f) * 2.2f, 0.135f, 0), new(1.0f, 0.02f, 6f), Paint);
            s.Ring(new(0, 0.3f, 0), Vector3.Up, r + 1.5f, r + 5f, 0.9f, RegolithDark, 32);
            for (int i = 0; i < 16; i++)
            {
                float a = Mathf.Tau * i / 16;
                var at = new Vector3(Mathf.Sin(a) * (r - 0.4f), 0.2f, Mathf.Cos(a) * (r - 0.4f));
                s.Box(at, new(0.3f, 0.16f, 0.3f), C("2e3236"));
                Window(s, at + Vector3.Up * 0.081f, Vector3.Up, Vector3.Forward, 0.22f, 0.22f, PadLight);
            }
            m = s.Build();
            Cache[key] = m;
            return m;
        }
    }

    /// <summary>The steps down from an airlock's outer door: how many, and each one's depth, metres.</summary>
    public const int Steps = 7;

    public const float Tread = 0.34f;

    /// <summary>The steps down from each airlock's outer door to the plateau, with rails, in the colony's frame (pre-turned).</summary>
    public static ArrayMesh Stairs(IEnumerable<(Vector3 Door, float Out)> doors, ColonyTheme theme)
    {
        var s = new MeshScratch();
        var p = Of(theme);
        static Vector3 T(Vector3 v) => new(-v.X, v.Y, -v.Z);
        foreach (var (door, @out) in doors)
        {
            var dir = new Vector3(Mathf.Sin(@out), 0, -Mathf.Cos(@out));
            var side = Vector3.Up.Cross(dir).Normalized();
            float yaw = Mathf.Atan2(-dir.X, -dir.Z);
            for (int k = 0; k < Steps; k++)
            {
                float top = FloorY - k * FloorY / Steps;
                var c = door + dir * (k * Tread + Tread / 2) + Vector3.Up * (top / 2);
                s.Box(T(c), new(1.6f, top, Tread), k % 2 == 0 ? p.Frame : p.Band, Yaw(yaw));
            }
            foreach (float sd in new[] { -0.85f, 0.85f })
            {
                var a = door + side * sd + Vector3.Up * (FloorY + 1.0f);
                var b = door + side * sd + dir * (Steps * Tread) + Vector3.Up * 1.0f;
                s.Tube(T(a), T(b), 0.035f, p.Trim, 5);
                s.Tube(T(b), T(b - Vector3.Up * 1.0f), 0.035f, p.Frame, 5);
            }
        }
        return s.Build();
    }

    /// <summary>The road: sintered strips 8 m wide along the points, in the colony's frame (pre-turned).</summary>
    public static ArrayMesh RoadMesh(IReadOnlyList<(double X, double Z)> pts)
    {
        var s = new MeshScratch();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = new Vector3(-(float)pts[i].X, 0.04f, -(float)pts[i].Z);
            var b = new Vector3(-(float)pts[i + 1].X, 0.04f, -(float)pts[i + 1].Z);
            var d = b - a;
            float yaw = Mathf.Atan2(d.X, d.Z);
            s.Box((a + b) / 2, new(8, 0.08f, d.Length()), Sinter, Yaw(yaw));
            for (float t = 0; t < d.Length(); t += 6)
                s.Box(a + d.Normalized() * (t + 1.5f) + Vector3.Up * 0.045f, new(0.25f, 0.02f, 3f), Paint, Yaw(yaw));
        }
        return s.Build();
    }
}
