using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The models at the Moon's historic and invented sites (#1939, docs/plans/moon-surface.md section 1): Apollo's descent
/// stages, flags, packages and rovers, Surveyor 3, the Luna landers and Lunokhods, the Chang'e landers and Yutus, the
/// S-IVBs' pieces, invented wrecks and flags. Built once each and shared; authored facing +Z, feet on y = 0, at real
/// size. Each has a few boxes for what is solid (<see cref="Solids"/>). All in the model viewer under "Moon".
/// </summary>
public static class MoonSiteMeshes
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Gold = C("c99a2e"), GoldDark = C("8a6418"), Foil = C("d8c8a0"), Silver = C("c4c8cc"),
        Steel = C("8e959c"), Dark = C("2e3236"), Black = C("16181a"), White = C("eceff1"), Panel = C("23345e"),
        PanelDark = C("161f3a"), Red = C("c62a2a"), Blue = C("1f3a8a"), Yellow = C("f2c21b"), Green = C("2f8a3a"),
        Saffron = C("f29933"), Navy = C("10206a"), Burnt = C("3a332c"), Olive = C("5d6440"), Cream = C("e6dcc2");

    private static readonly Dictionary<(MoonPart, MoonFlag), ArrayMesh> Cache = new();
    private static readonly object Lock = new();

    /// <summary>A part's mesh (any thread: built once, shared).</summary>
    public static ArrayMesh Mesh(MoonPart part, MoonFlag flag = MoonFlag.Us)
    {
        if (part != MoonPart.Flag) flag = MoonFlag.Us;
        lock (Lock)
        {
            if (Cache.TryGetValue((part, flag), out var m)) return m;
            m = Build(part, flag);
            Cache[(part, flag)] = m;
            return m;
        }
    }

    /// <summary>Every site model, in the viewer.</summary>
    [Showcase("Moon")]
    public static IEnumerable<(string Name, Func<Node3D> Make)> Sites()
    {
        foreach (MoonPart p in Enum.GetValues<MoonPart>())
        {
            if (p == MoonPart.Flag)
            {
                foreach (MoonFlag f in Enum.GetValues<MoonFlag>())
                {
                    var ff = f;
                    yield return ($"Flag ({f})", () => Instance(MoonPart.Flag, ff));
                }
                continue;
            }
            var pp = p;
            yield return (p.ToString(), () => Instance(pp, MoonFlag.Us));
        }
    }

    /// <summary>A part drawn with the figures' vertex-colour material (the style kit's).</summary>
    public static MeshInstance3D Instance(MoonPart part, MoonFlag flag) =>
        new() { Name = part.ToString(), Mesh = Mesh(part, flag), MaterialOverride = HumanMeshBuilder.Material() };

    /// <summary>The boxes that are solid (centre and size, model space): what a walker or a rover bumps into.</summary>
    public static (Vector3 Centre, Vector3 Size)[] Solids(MoonPart part) => part switch
    {
        MoonPart.DescentStage => [(new(0, 2.15f, 0), new(4.2f, 1.7f, 4.2f)),
            (Foot(45), new(1f, 0.3f, 1f)), (Foot(135), new(1f, 0.3f, 1f)), (Foot(225), new(1f, 0.3f, 1f)), (Foot(315), new(1f, 0.3f, 1f))],
        MoonPart.Lrv => [(new(0, 0.55f, 0), new(2.3f, 0.9f, 3.1f))],
        MoonPart.Surveyor => [(new(0, 1.0f, 0), new(1.6f, 1.6f, 1.6f))],
        MoonPart.Luna9 => [(new(0, 0.3f, 0), new(0.6f, 0.6f, 0.6f))],
        MoonPart.LunaStage or MoonPart.LunaRampStage => [(new(0, 1.0f, 0), new(3.2f, 1.6f, 3.2f))],
        MoonPart.Lunokhod => [(new(0, 0.75f, 0), new(1.6f, 1.3f, 2.2f))],
        MoonPart.ChangeLander => [(new(0, 1.6f, 0), new(2.4f, 1.6f, 2.4f))],
        MoonPart.Yutu => [(new(0, 0.6f, 0), new(1.0f, 1.0f, 1.5f))],
        MoonPart.RocketWreck => [(new(0, 1.6f, 2), new(4.6f, 3.4f, 16f)), (new(1.8f, 1.6f, -14), new(4.6f, 3.4f, 13f))],
        MoonPart.Alsep => [(new(0, 0.3f, 0), new(0.8f, 0.6f, 0.6f))],
        MoonPart.SivbDebris => [(new(0, 0.6f, 0), new(3f, 1.2f, 2.5f))],
        MoonPart.WreckDebris => [(new(0, 0.9f, 0), new(1.8f, 1.8f, 1.8f))],
        _ => [],
    };

    private static Vector3 Foot(float deg)
    {
        float a = Mathf.DegToRad(deg);
        return new Vector3(Mathf.Sin(a) * 4.4f, 0.12f, Mathf.Cos(a) * 4.4f);
    }

    private static ArrayMesh Build(MoonPart part, MoonFlag flag)
    {
        var s = new MeshScratch();
        switch (part)
        {
            case MoonPart.DescentStage: DescentStage(s); break;
            case MoonPart.FlagUs: FlagOnPole(s, MoonFlag.Us, standing: true); break;
            case MoonPart.FlagFallen: FlagOnPole(s, MoonFlag.Us, standing: false); break;
            case MoonPart.Alsep: Alsep(s); break;
            case MoonPart.Lrv: Lrv(s); break;
            case MoonPart.Surveyor: Surveyor(s); break;
            case MoonPart.Luna9: Luna9(s); break;
            case MoonPart.LunaStage: LunaStage(s, ramps: false); break;
            case MoonPart.LunaRampStage: LunaStage(s, ramps: true); break;
            case MoonPart.Lunokhod: Lunokhod(s); break;
            case MoonPart.ChangeLander: ChangeLander(s); break;
            case MoonPart.Yutu: Yutu(s); break;
            case MoonPart.SivbDebris: SivbDebris(s); break;
            case MoonPart.RocketWreck: RocketWreck(s); break;
            case MoonPart.WreckDebris: WreckDebris(s); break;
            case MoonPart.Flag: FlagOnPole(s, flag, standing: true); Plaque(s); break;
        }
        return s.Build();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A ball from a few tapered rings (no sphere in the scratch).</summary>
    private static void Ball(MeshScratch s, Vector3 c, float r, Color col, int sides = 10)
    {
        float[] lat = [-90, -55, -20, 20, 55, 90];
        for (int i = 0; i + 1 < lat.Length; i++)
        {
            float a = Mathf.DegToRad(lat[i]), b = Mathf.DegToRad(lat[i + 1]);
            s.Tube(c + Vector3.Up * r * Mathf.Sin(a), c + Vector3.Up * r * Mathf.Sin(b), Mathf.Max(r * Mathf.Cos(a), 0.001f), Mathf.Max(r * Mathf.Cos(b), 0.001f), col, sides);
        }
    }

    private static Basis Yaw(float deg) => new(Vector3.Up, Mathf.DegToRad(deg));

    private static Vector3 Polar(float deg, float r, float y) =>
        new(Mathf.Sin(Mathf.DegToRad(deg)) * r, y, Mathf.Cos(Mathf.DegToRad(deg)) * r);

    /// <summary>A wheel on an axle along x: a dark tyre ring, a rim.</summary>
    private static void Wheel(MeshScratch s, Vector3 c, float r, float w, Color tyre, Color hub)
    {
        s.Ring(c, Vector3.Right, r * 0.55f, r, w, tyre, 14);
        s.Tube(c - Vector3.Right * w * 0.4f, c + Vector3.Right * w * 0.4f, r * 0.55f, hub, 10);
    }

    // ---- Apollo --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The lunar module's descent stage as the ascent left it: an octagonal body in gold and black foil (4.2 m across,
    /// 1.7 m tall) on four legs to footpads 9.4 m apart diagonally, the ladder down the front leg, the porch over it, the
    /// plaque on the ladder, the descent engine's bell under it, the top deck bare where the ascent stage stood.
    /// </summary>
    private static void DescentStage(MeshScratch s)
    {
        const float y0 = 1.3f, y1 = 3.0f;
        s.Tube(new(0, y0, 0), new(0, y1, 0), 2.25f, Gold, 8);
        // black foil panels on four of the eight sides
        for (int k = 0; k < 4; k++)
            s.Box(Polar(45 + 90 * k + 22.5f, 2.08f, 2.15f), new(1.5f, 1.4f, 0.06f), Black, Yaw(45 + 90 * k + 22.5f));
        // the top deck where the ascent stage stood, and its four bare mounts
        s.Box(new(0, y1 + 0.03f, 0), new(3.2f, 0.06f, 3.2f), Silver, Yaw(22.5f));
        for (int k = 0; k < 4; k++) s.Tube(Polar(45 + 90 * k, 1.3f, y1), Polar(45 + 90 * k, 1.3f, y1 + 0.35f), 0.07f, Steel, 5);
        // the descent engine's bell
        s.Tube(new(0, y0, 0), new(0, y0 - 0.8f, 0), 0.4f, 0.75f, Dark, 12);
        // the legs: a primary strut from the body's upper corner, two secondaries from below, a footpad and a probe
        for (int k = 0; k < 4; k++)
        {
            float a = 45 + 90 * k;
            var top = Polar(a, 2.1f, 2.7f);
            var foot = Polar(a, 4.4f, 0.25f);
            s.Tube(top, foot, 0.1f, 0.08f, Gold, 6);
            s.Tube(Polar(a - 20, 2.0f, 1.4f), foot + Vector3.Up * 0.3f, 0.05f, Silver, 5);
            s.Tube(Polar(a + 20, 2.0f, 1.4f), foot + Vector3.Up * 0.3f, 0.05f, Silver, 5);
            s.Tube(foot - Vector3.Up * 0.15f, foot + Vector3.Up * 0.02f, 0.47f, 0.42f, Foil, 12);
            if (k != 0) s.Tube(foot, foot + new Vector3(0, -0.2f, 0) + Polar(a, 0.6f, 0) * 0.4f, 0.015f, Steel, 4);
        }
        // the front (+Z: leg 0 at 45° is turned; the ladder goes on the leg nearest +Z): porch and ladder
        var porch = new Vector3(0, 2.8f, 2.45f);
        s.Box(porch, new(1.0f, 0.06f, 0.7f), Silver);
        var ladderTop = new Vector3(0, 2.75f, 2.8f);
        var ladderFoot = new Vector3(0, 0.9f, 3.9f);
        s.Tube(ladderTop + Vector3.Right * 0.3f, ladderFoot + Vector3.Right * 0.3f, 0.035f, Gold, 5);
        s.Tube(ladderTop - Vector3.Right * 0.3f, ladderFoot - Vector3.Right * 0.3f, 0.035f, Gold, 5);
        for (int r = 1; r < 8; r++)
        {
            var p = ladderTop.Lerp(ladderFoot, r / 8f);
            s.Tube(p - Vector3.Right * 0.3f, p + Vector3.Right * 0.3f, 0.02f, Silver, 4);
        }
        // the front strut to the ladder's foot and its pad
        s.Tube(new(0, 2.6f, 2.2f), new(0, 0.25f, 4.3f), 0.1f, Gold, 6);
        s.Tube(new(0, 0.1f, 4.3f), new(0, 0.27f, 4.3f), 0.47f, 0.42f, Foil, 12);
        // the plaque on the ladder's strut
        s.Box(new(0, 1.6f, 3.38f), new(0.25f, 0.2f, 0.02f), Silver, new Basis(Vector3.Right, -0.5f));
    }

    /// <summary>A flag on its pole and the horizontal rod that holds it out (no air): 2.4 m; or knocked flat.</summary>
    private static void FlagOnPole(MeshScratch s, MoonFlag flag, bool standing)
    {
        var b = standing ? Basis.Identity : new Basis(Vector3.Right, Mathf.DegToRad(84)) * Yaw(0);
        Vector3 T(Vector3 p) => standing ? p : b * p + new Vector3(0, 0.05f, 0);
        s.Tube(T(new(0, -0.3f, 0)), T(new(0, 2.4f, 0)), 0.02f, Silver, 6);
        s.Tube(T(new(0, 2.35f, 0)), T(new(1.5f, 2.35f, 0)), 0.012f, Silver, 4);
        Cloth(s, flag, T(new(0.05f, 2.33f, 0)), b);
    }

    /// <summary>The flag's cloth, 1.45 by 0.9 m, hanging from the rod in the model's x-y plane (or along <paramref name="b"/>).</summary>
    private static void Cloth(MeshScratch s, MoonFlag flag, Vector3 topLeft, Basis b)
    {
        const float w = 1.45f, h = 0.9f, t = 0.012f;
        void R(float x0, float y0, float x1, float y1, Color c, float dz = 0) =>
            s.Box(topLeft + b * new Vector3((x0 + x1) / 2 * w, -(y0 + y1) / 2 * h, dz), new Vector3((x1 - x0) * w, (y1 - y0) * h, t), c, b);
        switch (flag)
        {
            case MoonFlag.Us:
                for (int i = 0; i < 13; i++) R(0, i / 13f, 1, (i + 1) / 13f, i % 2 == 0 ? Red : White);
                R(0, 0, 0.4f, 7 / 13f, Blue, 0.002f);
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 5; c++)
                        R(0.03f + c * 0.075f, 0.04f + r * 0.12f, 0.05f + c * 0.075f, 0.07f + r * 0.12f, White, 0.004f);
                break;
            case MoonFlag.Switzerland:
                // square, as the Swiss one is
                R(0.2f, 0, 0.82f, 1, Red);
                R(0.45f, 0.2f, 0.57f, 0.8f, White, 0.002f);
                R(0.32f, 0.42f, 0.7f, 0.58f, White, 0.002f);
                break;
            case MoonFlag.Esa:
                R(0, 0, 1, 1, Navy);
                R(0.35f, 0.3f, 0.65f, 0.7f, White, 0.002f);
                R(0.42f, 0.38f, 0.58f, 0.62f, Navy, 0.004f);
                R(0.48f, 0.15f, 0.52f, 0.85f, White, 0.004f);
                break;
            case MoonFlag.Japan:
                R(0, 0, 1, 1, White);
                R(0.38f, 0.25f, 0.62f, 0.75f, Red, 0.002f);
                R(0.34f, 0.33f, 0.66f, 0.67f, Red, 0.002f);
                break;
            case MoonFlag.India:
                R(0, 0, 1, 1 / 3f, Saffron);
                R(0, 1 / 3f, 1, 2 / 3f, White);
                R(0, 2 / 3f, 1, 1, Green);
                R(0.46f, 0.38f, 0.54f, 0.62f, Navy, 0.002f);
                break;
            case MoonFlag.China:
                R(0, 0, 1, 1, Red);
                R(0.06f, 0.1f, 0.18f, 0.3f, Yellow, 0.002f);
                for (int i = 0; i < 4; i++) R(0.24f + (i % 2) * 0.06f, 0.06f + i * 0.08f, 0.28f + (i % 2) * 0.06f, 0.12f + i * 0.08f, Yellow, 0.002f);
                break;
            case MoonFlag.Ussr:
                R(0, 0, 1, 1, Red);
                R(0.06f, 0.1f, 0.16f, 0.3f, Yellow, 0.002f);
                break;
        }
    }

    /// <summary>A small plaque on a stake beside an invented flag.</summary>
    private static void Plaque(MeshScratch s)
    {
        s.Tube(new(0.5f, 0, 0.6f), new(0.5f, 0.6f, 0.6f), 0.015f, Silver, 4);
        s.Box(new(0.5f, 0.65f, 0.6f), new(0.35f, 0.25f, 0.02f), Silver, new Basis(Vector3.Right, -0.6f));
    }

    /// <summary>The surface package: the central station under its sunshade, the antenna, the RTG with its fins, a seismometer.</summary>
    private static void Alsep(MeshScratch s)
    {
        s.Box(new(0, 0.2f, 0), new(0.6f, 0.4f, 0.45f), Silver);
        s.Box(new(0, 0.55f, 0), new(0.75f, 0.03f, 0.6f), Foil);
        for (int k = 0; k < 4; k++) s.Tube(new(k < 2 ? -0.35f : 0.35f, 0.4f, k % 2 == 0 ? -0.28f : 0.28f), new(k < 2 ? -0.35f : 0.35f, 0.55f, k % 2 == 0 ? -0.28f : 0.28f), 0.01f, Steel, 4);
        s.Tube(new(0.2f, 0.4f, 0), new(0.2f, 1.2f, 0), 0.02f, Silver, 4);
        s.Tube(new(0.2f, 1.0f, 0), new(0.2f, 1.45f, 0), 0.05f, Steel, 8);
        // the RTG three metres off
        s.Tube(new(-2.4f, 0, 1.2f), new(-2.4f, 0.46f, 1.2f), 0.2f, Dark, 8);
        for (int k = 0; k < 8; k++) s.Box(new Vector3(-2.4f, 0.23f, 1.2f) + Polar(k * 45, 0.25f, 0), new(0.02f, 0.44f, 0.12f), Dark, Yaw(k * 45));
        // the passive seismometer under its skirt, further out
        s.Tube(new(2.8f, 0, -2.2f), new(2.8f, 0.05f, -2.2f), 0.6f, Foil, 12);
        s.Tube(new(2.8f, 0.05f, -2.2f), new(2.8f, 0.4f, -2.2f), 0.18f, 0.12f, Silver, 8);
    }

    /// <summary>The Lunar Roving Vehicle: 3.1 m long, its chassis, four wire-mesh wheels under fenders, two seats, the console, the umbrella antenna.</summary>
    private static void Lrv(MeshScratch s)
    {
        s.Box(new(0, 0.5f, 0), new(1.4f, 0.12f, 3.0f), Steel);
        s.Box(new(0, 0.42f, 0), new(1.1f, 0.1f, 2.4f), Silver);
        foreach (float z in new[] { 1.1f, -1.1f })
            foreach (float x in new[] { 0.95f, -0.95f })
            {
                Wheel(s, new(x, 0.41f, z), 0.41f, 0.23f, C("5a5e62"), Silver);
                s.Box(new(x, 0.78f, z), new(0.32f, 0.04f, 0.95f), Cream);
            }
        // the seats: two open frames
        foreach (float x in new[] { 0.35f, -0.35f })
        {
            s.Box(new(x, 0.62f, -0.15f), new(0.5f, 0.05f, 0.5f), Steel);
            s.Box(new(x, 0.9f, -0.42f), new(0.5f, 0.55f, 0.05f), Steel, new Basis(Vector3.Right, -0.2f));
        }
        // the console between them, the high-gain umbrella and the TV camera at the front
        s.Box(new(0, 0.95f, 0.45f), new(0.35f, 0.35f, 0.15f), Silver);
        s.Tube(new(0.45f, 0.56f, 1.2f), new(0.45f, 1.7f, 1.2f), 0.02f, Silver, 4);
        s.Tube(new(0.45f, 1.7f, 1.2f), new(0.45f, 1.85f, 1.2f), 0.5f, 0.05f, Cream, 12);
        s.Tube(new(-0.4f, 0.56f, 1.3f), new(-0.4f, 1.2f, 1.3f), 0.02f, Silver, 4);
        s.Box(new(-0.4f, 1.25f, 1.3f), new(0.15f, 0.15f, 0.3f), White);
        // the tool pallet at the back
        s.Box(new(0, 0.75f, -1.35f), new(0.9f, 0.45f, 0.35f), Silver);
    }

    /// <summary>Surveyor 3: a triangular frame on three legs, its solar panel and antenna on a mast, the scoop.</summary>
    private static void Surveyor(MeshScratch s)
    {
        s.Tube(new(0, 0.6f, 0), new(0, 1.4f, 0), 0.75f, 0.6f, Silver, 3);
        for (int k = 0; k < 3; k++)
        {
            var foot = Polar(k * 120, 2.1f, 0.15f);
            s.Tube(Polar(k * 120, 0.7f, 1.1f), foot, 0.05f, Steel, 5);
            s.Tube(foot - Vector3.Up * 0.1f, foot + Vector3.Up * 0.05f, 0.25f, Foil, 10);
        }
        s.Box(new(0, 1.2f, 0), new(0.5f, 0.5f, 0.5f), White);
        s.Tube(new(0, 1.4f, 0), new(0, 3.2f, 0), 0.04f, Silver, 5);
        s.Box(new(0.3f, 3.1f, 0), new(0.9f, 0.03f, 0.75f), Panel, new Basis(Vector3.Forward, 0.4f));
        s.Box(new(-0.35f, 2.9f, 0), new(0.6f, 0.6f, 0.03f), Silver);
        s.Box(new(0.4f, 0.9f, 0.55f), new(0.25f, 0.2f, 0.4f), Steel);
    }

    // ---- the Soviet landers ------------------------------------------------------------------------------------------

    /// <summary>Luna 9's capsule, 58 cm, its four petals open on the ground and four antenna rods up.</summary>
    private static void Luna9(MeshScratch s)
    {
        Ball(s, new(0, 0.3f, 0), 0.29f, Silver);
        for (int k = 0; k < 4; k++)
        {
            s.Box(Polar(k * 90, 0.5f, 0.05f), new(0.35f, 0.03f, 0.5f), Steel, Yaw(k * 90));
            s.Tube(Polar(k * 90 + 45, 0.15f, 0.45f), Polar(k * 90 + 45, 0.45f, 1.0f), 0.008f, Silver, 3);
        }
        s.Box(new(0, 0.62f, 0), new(0.08f, 0.1f, 0.08f), Dark);
    }

    /// <summary>The Luna sample-return descent stage: four round tanks and four cylinders, four legs, the drill arm; with the Luna 17/21 ramps down both sides.</summary>
    private static void LunaStage(MeshScratch s, bool ramps)
    {
        for (int k = 0; k < 4; k++)
        {
            Ball(s, Polar(45 + 90 * k, 0.75f, 1.0f), 0.45f, Olive);
            s.Tube(Polar(90 * k, 0.9f, 0.7f), Polar(90 * k, 0.9f, 1.4f), 0.22f, Olive, 8);
            var foot = Polar(45 + 90 * k, 1.9f, 0.1f);
            s.Tube(Polar(45 + 90 * k, 1.0f, 0.9f), foot, 0.05f, Steel, 5);
            s.Tube(foot - Vector3.Up * 0.08f, foot + Vector3.Up * 0.05f, 0.3f, Silver, 10);
        }
        s.Box(new(0, 1.55f, 0), new(1.6f, 0.15f, 1.6f), Steel);
        if (ramps)
        {
            // the Lunokhod's tub sat here: an empty deck, a ramp pair down each side
            s.Box(new(0, 1.65f, 0), new(2.2f, 0.06f, 1.6f), Silver);
            foreach (float side in new[] { 1f, -1f })
                foreach (float z in new[] { 0.45f, -0.45f })
                    s.Box(new(side * 2.2f, 0.85f, z), new(2.6f, 0.05f, 0.4f), Steel, new Basis(Vector3.Back, -side * 0.62f));
        }
        else
        {
            s.Tube(new(0.7f, 1.6f, 0), new(1.6f, 0.4f, 0.3f), 0.04f, Silver, 5);
            s.Tube(new(1.6f, 0.4f, 0.3f), new(1.6f, 0.0f, 0.3f), 0.05f, Dark, 5);
        }
    }

    /// <summary>Lunokhod: the convex tub on eight wheels, its lid (the solar panel) open at the back, the antennas and the cameras' ports at the front.</summary>
    private static void Lunokhod(MeshScratch s)
    {
        s.Tube(new(0, 0.55f, -0.85f), new(0, 0.55f, 0.85f), 0.62f, 0.62f, Steel, 10);
        s.Box(new(0, 0.9f, 0), new(1.5f, 0.12f, 1.9f), Silver);
        for (int i = 0; i < 4; i++)
            foreach (float x in new[] { 0.9f, -0.9f })
                Wheel(s, new(x, 0.26f, -0.8f + i * 0.53f), 0.26f, 0.2f, Dark, Steel);
        // the lid tipped up behind, its cells on the inside
        var hinge = new Vector3(0, 0.96f, -0.95f);
        s.Box(hinge + new Vector3(0, 0.75f, -0.2f), new(1.5f, 1.5f, 0.05f), Panel, new Basis(Vector3.Right, 0.25f));
        s.Box(new(0, 0.85f, 1.05f), new(0.8f, 0.2f, 0.15f), Dark);
        s.Tube(new(0.5f, 0.96f, 0.6f), new(0.5f, 1.7f, 0.6f), 0.015f, Silver, 4);
        s.Tube(new(-0.4f, 0.96f, 0.4f), new(-0.4f, 1.4f, 0.4f), 0.015f, Silver, 4);
        s.Tube(new(-0.4f, 1.4f, 0.4f), new(-0.4f, 1.48f, 0.4f), 0.22f, 0.02f, Silver, 10);
    }

    // ---- the Chinese landers -----------------------------------------------------------------------------------------

    /// <summary>A Chang'e lander: the gold body, four legs, its two solar wings, the camera mast, the ramp (3 and 4) or the drill (5 and 6) left down.</summary>
    private static void ChangeLander(MeshScratch s)
    {
        s.Tube(new(0, 0.9f, 0), new(0, 2.3f, 0), 1.25f, Gold, 8);
        s.Box(new(0, 2.33f, 0), new(1.9f, 0.06f, 1.9f), GoldDark);
        s.Tube(new(0, 0.9f, 0), new(0, 0.4f, 0), 0.25f, 0.45f, Dark, 10);
        for (int k = 0; k < 4; k++)
        {
            float a = 45 + 90 * k;
            var foot = Polar(a, 2.4f, 0.15f);
            s.Tube(Polar(a, 1.2f, 1.9f), foot, 0.07f, Silver, 6);
            s.Tube(Polar(a, 1.1f, 0.9f), foot + Vector3.Up * 0.2f, 0.04f, Silver, 5);
            s.Tube(foot - Vector3.Up * 0.12f, foot + Vector3.Up * 0.04f, 0.38f, Foil, 10);
        }
        foreach (float x in new[] { 1f, -1f })
        {
            s.Tube(new(x * 1.2f, 2.0f, 0), new(x * 1.5f, 2.0f, 0), 0.03f, Silver, 4);
            s.Box(new(x * 2.3f, 2.0f, 0), new(1.6f, 0.04f, 1.2f), Panel, new Basis(Vector3.Back, x * 0.15f));
        }
        s.Tube(new(0.5f, 2.35f, 0.5f), new(0.5f, 3.3f, 0.5f), 0.04f, Silver, 5);
        s.Box(new(0.5f, 3.35f, 0.5f), new(0.25f, 0.15f, 0.2f), White);
        // the ramp the rover drove down
        s.Box(new(0, 1.0f, 1.9f), new(0.9f, 0.05f, 2.2f), Steel, new Basis(Vector3.Right, 0.55f));
    }

    /// <summary>A Yutu rover: the gold box on six wheels, its two panels out, the mast with the cameras and the antenna.</summary>
    private static void Yutu(MeshScratch s)
    {
        s.Box(new(0, 0.6f, 0), new(0.9f, 0.5f, 1.2f), Gold);
        for (int i = 0; i < 3; i++)
            foreach (float x in new[] { 0.6f, -0.6f })
                Wheel(s, new(x, 0.15f, -0.5f + i * 0.5f), 0.15f, 0.15f, Silver, Steel);
        foreach (float x in new[] { 1f, -1f })
            s.Box(new(x * 0.95f, 0.9f, 0), new(1.0f, 0.03f, 1.0f), Panel, new Basis(Vector3.Back, x * 0.25f));
        s.Tube(new(0, 0.85f, 0.45f), new(0, 1.45f, 0.45f), 0.03f, Silver, 4);
        s.Box(new(0, 1.5f, 0.45f), new(0.45f, 0.12f, 0.15f), White);
        s.Tube(new(0.2f, 0.85f, -0.4f), new(0.2f, 1.3f, -0.4f), 0.015f, Silver, 4);
        s.Tube(new(0.2f, 1.3f, -0.4f), new(0.2f, 1.36f, -0.4f), 0.15f, 0.02f, White, 8);
    }

    // ---- crashes -------------------------------------------------------------------------------------------------------

    /// <summary>What is left of an S-IVB on its crater's rim: torn skin panels, a crushed ring, the J-2's bell on its side.</summary>
    private static void SivbDebris(MeshScratch s)
    {
        var rng = new Random(4113);
        for (int i = 0; i < 16; i++)
        {
            float a = (float)rng.NextDouble() * 360, r = 1 + (float)rng.NextDouble() * 9;
            var b = Yaw(a) * new Basis(Vector3.Right, ((float)rng.NextDouble() - 0.5f) * 1.4f) * new Basis(Vector3.Back, ((float)rng.NextDouble() - 0.5f) * 1.2f);
            s.Box(Polar(a, r, 0.2f), new(1.0f + (float)rng.NextDouble() * 2.2f, 0.06f, 0.8f + (float)rng.NextDouble() * 1.4f), i % 2 == 0 ? Black : White, b);
        }
        // a crushed piece of the 6.6 m ring, half in the ground, and the J-2's bell on its side
        s.Ring(new(0, 0.5f, 0), new Vector3(0.3f, 1, 0.5f), 2.6f, 3.0f, 1.2f, White, 16);
        s.Tube(new(3.5f, 0.6f, -2.2f), new(6.0f, 1.2f, -1.8f), 0.4f, 1.3f, Burnt, 12);
    }

    /// <summary>An invented booster crashed at a low angle: a 4.6 m white cylinder broken in two, half buried, engine bells and fins at the back.</summary>
    private static void RocketWreck(MeshScratch s)
    {
        // the front piece, nose crumpled, sunk a third of its width
        s.Tube(new(0, 1.4f, 10), new(0, 1.5f, -6), 2.3f, White, 14);
        s.Tube(new(0, 1.4f, 10), new(0, 1.0f, 12.5f), 2.3f, 1.2f, Burnt, 14);
        for (int i = 0; i < 3; i++) s.Tube(new(0, 1.42f, 6 - i * 5), new(0, 1.43f, 5.2f - i * 5), 2.33f, Black, 14);
        // the back piece, turned and dropped behind
        var b0 = new Vector3(1.8f, 1.5f, -8);
        var b1 = new Vector3(2.6f, 1.6f, -20);
        s.Tube(b0, b1, 2.3f, White, 14);
        s.Tube(b0, b0 + new Vector3(-0.1f, 0, 1.0f), 2.2f, 1.6f, Burnt, 14);
        for (int k = 0; k < 4; k++)
        {
            var c = b1 + Polar(45 + 90 * k, 1.1f, 0).Rotated(Vector3.Up, 0) + new Vector3(0, 0, -0.1f);
            s.Tube(c, c + new Vector3(0, 0, -1.8f), 0.35f, 0.75f, Dark, 10);
            s.Box(b1 + Polar(90 * k, 2.6f, 0) + new Vector3(0, 0, 1.2f), new(0.08f, 2.0f, 2.6f), Red, new Basis(Vector3.Back, Mathf.DegToRad(90 * k)));
        }
    }

    /// <summary>A wreck's scattered pieces: a round tank, a panel, a strut.</summary>
    private static void WreckDebris(MeshScratch s)
    {
        Ball(s, new(0, 0.7f, 0), 0.9f, White);
        s.Box(new(1.8f, 0.2f, 0.8f), new(2.2f, 0.05f, 1.3f), White, Yaw(30) * new Basis(Vector3.Right, 0.3f));
        s.Tube(new(-1.4f, 0.1f, -0.6f), new(-3.2f, 0.4f, -1.4f), 0.12f, Steel, 6);
        s.Box(new(-0.6f, 0.15f, 2.0f), new(1.1f, 0.06f, 0.9f), Black, Yaw(70) * new Basis(Vector3.Back, 0.4f));
    }
}
