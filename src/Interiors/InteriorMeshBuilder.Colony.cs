using Godot;
using UnitSport.Terrain.Bodies;
using Kit = UnitSport.World.Moon.ColonyKit;

namespace UnitSport.Interiors;

/// <summary>
/// A Moon colony module's pieces (#2000, placed by <c>InteriorGenerator.FurnishColony</c>): the boxes the kit-era
/// <c>ColonyInteriors</c> drew, its colours and its theme's, solid where it made them solid; screens, lamps and grow
/// lights self-lit panes (dead in the abandoned colony). A wall piece is authored against +X, its length along Z; at
/// Turns 2 it is the mirror image against -X. <see cref="InteriorGenerator.ColonyAlt"/> in Turns picks the second colour.
/// </summary>
public static partial class InteriorMeshBuilder
{
    /// <summary>A colony theme's colours, linear: its trim, band and frame, its lamps, and the dead lamps' colour when abandoned.</summary>
    private sealed record ColonyColours(Color Trim, Color Band, Color Frame, Color Lamp, Color? Dead);

    /// <summary>The colours of the colony layout being built (set by <see cref="Build"/>), null elsewhere. Per thread.</summary>
    [ThreadStatic] private static ColonyColours? _colony;

    private static readonly ColonyColours ArtemisColours = ColonyColoursOf((int)ColonyTheme.Artemis)!;

    private static Color Hex(string hex) => new Color(hex).SrgbToLinear() with { A = 1 };
    private static Color LinOf(Color srgb) => new Color(srgb.R, srgb.G, srgb.B).SrgbToLinear() with { A = 1 };

    /// <summary>A layout's colony colours, or null for a building on the Earth.</summary>
    private static ColonyColours? ColonyColoursOf(int? theme)
    {
        if (theme is not int t) return null;
        var p = Kit.Of((ColonyTheme)t);
        return new ColonyColours(LinOf(p.Trim), LinOf(p.Band), LinOf(p.Frame), LinOf(p.LampColour),
            p.DeadLamps is { } d ? LinOf(d) : null);
    }

    // the kit-era furniture's own colours (ColonyInteriors)
    private static readonly Color CMetal = Hex("9aa1a8"), CDark = Hex("3a3f45"), CMattress = Hex("5d7fb0"), CSheet = Hex("e9edf2"),
        CWood = Hex("b08a5e"), CScreen = Hex("7fe0ff"), CScreenAmber = Hex("ffcf6e"), CPlant = Hex("4fae4a"), CPlantDark = Hex("2f7a33"),
        CTray = Hex("c9cfd6"), CCrate = Hex("c7a46a"), CCrateDark = Hex("8d7348"), CRed = Hex("c9302c"), CWhite = Hex("eef1f3"),
        CSuit = Hex("e8ebee"), CVisor = Hex("c79a2a"), CRubber = Hex("2a2d31"), CGrow = Hex("ff7fe0"), CBayPaint = Hex("f2c21b"),
        CAmberLamp = Hex("ffb347");

    /// <summary>One piece's frame: its turn about its centre, its mirror at Turns 2, and the run it was cut from.</summary>
    private readonly struct ColonyFrame
    {
        private readonly Scratch _s;
        private readonly Basis _basis;
        private readonly Vector3 _at;
        private readonly bool _mirror;
        private readonly Color? _dead;

        public ColonyFrame(Scratch s, FurniturePlan p, float y0, Color? dead)
        {
            _s = s;
            _basis = new Basis(Vector3.Up, (p.Turns & 3) * Mathf.Pi / 2 + p.Angle);
            _at = new Vector3(p.X, y0, p.Z);
            _mirror = (p.Turns & 3) == 2;
            _dead = dead;
        }

        /// <summary>A box by its centre and size in the piece's frame (y over its base).</summary>
        public void Box(float cx, float cy, float cz, float sx, float sy, float sz, Color col, bool collide = false)
        {
            if (_mirror) cz = -cz;
            var c = new Vector3(cx, cy, cz);
            var h = new Vector3(sx, sy, sz) / 2;
            _s.Box(_at + c - h, _at + c + h, col, collide, _basis, _at);
        }

        /// <summary>
        /// A self-lit pane <paramref name="w"/> by <paramref name="h"/> at <paramref name="c"/> facing <paramref name="normal"/>,
        /// 5 mm proud; dead (drawn lit, dark) when the colony has no power.
        /// </summary>
        public void Pane(Vector3 c, Vector3 normal, float w, float h, Color col)
        {
            var right = Vector3.Up.Cross(normal);
            right = right.LengthSquared() < 1e-6f ? Vector3.Right * (w / 2) : right.Normalized() * (w / 2);
            var up = normal.Cross(right).Normalized() * (h / 2);
            var o = c + normal * 0.005f;
            Vector3 at = _at;
            Basis basis = _basis;
            bool mirror = _mirror;
            Vector3 W(Vector3 v) => at + basis * (mirror ? v with { Z = -v.Z } : v);
            var n = basis * (mirror ? normal with { Z = -normal.Z } : normal);
            Vector3 a = W(o - right - up), b = W(o + right - up), cc = W(o + right + up), d = W(o - right + up);
            // a face is seen from the side its corners run clockwise (Scratch.Box's top): turn them to face the normal
            if ((b - a).Cross(cc - a).Dot(n) > 0) (b, d) = (d, b);
            var lit = _dead is { } dead ? dead : col with { A = 0.3f };
            _s.Quad(a, b, cc, d, lit, collide: false);
        }
    }

    /// <summary>
    /// The span along Z (in the piece's frame, before its mirror) of the run a wall piece was cut from: a can's runs are
    /// centred on its middle, so a piece off it is one side of a doorway's cut. <paramref name="inner"/> is the cut end
    /// (-1 its -Z end, +1 its +Z end, 0 uncut).
    /// </summary>
    private static (float Lo, float Hi, int Inner) ColonyRun(FurniturePlan p)
    {
        if (MathF.Abs(p.Z) < 0.01f) return (-p.D / 2, p.D / 2, 0);
        float ext = MathF.Abs(p.Z) + p.D / 2;
        return (-ext - p.Z, ext - p.Z, p.Z > 0 ? -1 : 1);
    }

    /// <summary>Whether a fitting at <paramref name="z"/> (half <paramref name="half"/> long) stays on this piece of a cut run.</summary>
    private static bool OnRun(FurniturePlan p, int inner, float z, float half) => inner switch
    {
        -1 => z - half >= -p.D / 2 - 1e-4f,
        1 => z + half <= p.D / 2 + 1e-4f,
        _ => true,
    };

    /// <summary>Draws a colony piece; false for any other type.</summary>
    private static bool ColonyPiece(Scratch s, FurniturePlan p, float y0)
    {
        switch (p.Type)
        {
            case FurnitureType.ColonyBunk: case FurnitureType.ColonyLocker: case FurnitureType.GalleyCounter:
            case FurnitureType.MessTable: case FurnitureType.LabBench: case FurnitureType.MedBed: case FurnitureType.MedCabinet:
            case FurnitureType.GymMat: case FurnitureType.GymMachine: case FurnitureType.GymFrame: case FurnitureType.ColonyWorkbench:
            case FurnitureType.ColonyCrates: case FurnitureType.SuitRack: case FurnitureType.StoreRack: case FurnitureType.ColonyConsole:
            case FurnitureType.MapTable: case FurnitureType.HydroRack: case FurnitureType.CrateStack: case FurnitureType.BayPaint:
            case FurnitureType.HangarBench: case FurnitureType.ColonyLamp: case FurnitureType.ColonyScreen:
                break;
            case FurnitureType.Solid:
                // something the building draws itself (a landing, a tree's trunk): only walked against
                s.Collider(new Vector3(p.X - p.W / 2, y0, p.Z - p.D / 2), new Vector3(p.X + p.W / 2, y0 + p.H, p.Z + p.D / 2));
                return true;
            default:
                return false;
        }

        var pal = _colony ?? ArtemisColours;
        var f = new ColonyFrame(s, p, y0, pal.Dead);
        bool alt = (p.Turns & InteriorGenerator.ColonyAlt) != 0;
        float D = p.D;
        switch (p.Type)
        {
            case FurnitureType.ColonyBunk:
                // two bunks one over the other, mattress and sheet, a reading lamp each, the ladder toward -Z
                foreach (float y in new[] { 0.35f, 1.35f })
                {
                    f.Box(0, y, 0, 0.85f, 0.1f, 2.0f, CMetal, collide: y < 1);
                    f.Box(0, y + 0.11f, 0, 0.8f, 0.12f, 1.9f, CMattress);
                    f.Box(0, y + 0.18f, 0.4f, 0.8f, 0.02f, 1.0f, CSheet);
                    f.Pane(new Vector3(0.315f, y + 0.55f, -0.85f), Vector3.Left, 0.15f, 0.1f, CScreenAmber);
                }
                f.Box(-0.43f, 0.9f, -0.9f, 0.04f, 1.8f, 0.04f, CMetal);
                break;
            case FurnitureType.ColonyLocker:
                f.Box(0, 0.9f, 0, 0.45f, 1.8f, 0.2f, pal.Trim, collide: true);
                break;
            case FurnitureType.GalleyCounter:
                f.Box(0, 0.45f, 0, p.W, 0.9f, D, CWhite, collide: true);
                break;
            case FurnitureType.MessTable:
                f.Box(0, 0.75f, 0, 0.8f, 0.06f, 1.4f, CWood, collide: true);
                f.Box(0, 0.37f, 0, 0.1f, 0.74f, 0.1f, CMetal);
                f.Box(0, 0.45f, -0.95f, 0.8f, 0.08f, 0.35f, pal.Trim);
                f.Box(0, 0.45f, 0.95f, 0.8f, 0.08f, 0.35f, pal.Trim);
                break;
            case FurnitureType.LabBench:
            {
                // the bench, a screen every 1.6 m from its +Z end, a tray on the bench beside each
                f.Box(0, 0.45f, 0, p.W, 0.9f, D, CWhite, collide: true);
                var (lo, hi, inner) = ColonyRun(p);
                for (float z = hi - 0.6f; z > lo + 0.4f; z -= 1.6f)
                {
                    if (OnRun(p, inner, z, 0.375f))
                    {
                        f.Box(0.11f, 1.2f, z, 0.08f, 0.5f, 0.75f, CDark);
                        f.Pane(new Vector3(0.065f, 1.2f, z), Vector3.Left, 0.68f, 0.42f, CScreen);
                    }
                    if (OnRun(p, inner, z - 0.5f, 0.1f)) f.Box(0, 1.0f, z - 0.5f, 0.2f, 0.2f, 0.2f, CTray);
                }
                break;
            }
            case FurnitureType.MedBed:
                f.Box(0, 0.6f, 0, 0.8f, 0.12f, 2.0f, CSheet, collide: true);
                f.Box(0, 0.3f, 0, 0.7f, 0.6f, 1.8f, CMetal, collide: true);
                break;
            case FurnitureType.MedCabinet:
            {
                // the wall of cupboards, a red cross on every 1.2 m
                f.Box(0, 0.9f, 0, p.W, 1.8f, D, CWhite, collide: true);
                var (lo, hi, inner) = ColonyRun(p);
                for (float z = hi - 0.5f; z > lo + 0.5f; z -= 1.2f)
                    if (OnRun(p, inner, z, 0.125f)) f.Box(-0.27f, 1.3f, z, 0.02f, 0.25f, 0.25f, CRed);
                break;
            }
            case FurnitureType.GymMat:
                // the treadmill's belt, its bar, and the harness overhead that holds a runner down at a sixth of a g
                f.Box(0, 0.15f, 0, 0.7f, 0.3f, 1.8f, CRubber, collide: true);
                f.Box(0, 1.0f, 0.8f, 0.7f, 0.08f, 0.08f, CMetal);
                f.Box(0, 1.9f, 0, 0.05f, 0.05f, 1.2f, pal.Trim);
                break;
            case FurnitureType.GymMachine:
                f.Box(0, 0.5f, 0, 0.4f, 1.0f, 1.0f, CDark, collide: true);
                break;
            case FurnitureType.GymFrame:
                f.Box(0, 0.9f, 0, 0.3f, 1.8f, 0.3f, CMetal, collide: true);
                break;
            case FurnitureType.ColonyWorkbench:
            {
                // the bench, the tool board over it (0.1 m short of the run's ends) and the tools on it every 0.6 m
                f.Box(0, 0.45f, 0, p.W, 0.9f, D, CWood, collide: true);
                var (lo, hi, inner) = ColonyRun(p);
                float b0 = Math.Max(lo + 0.1f, -D / 2), b1 = Math.Min(hi - 0.1f, D / 2);
                if (b1 - b0 > 0.1f) f.Box(0.35f, 1.4f, (b0 + b1) / 2, 0.05f, 0.9f, b1 - b0, CDark);
                for (float z = hi - 0.4f; z > lo + 0.4f; z -= 0.6f)
                    if (OnRun(p, inner, z, 0.03f)) f.Box(0.3f, 1.4f, z, 0.05f, 0.4f, 0.06f, CRed);
                break;
            }
            case FurnitureType.ColonyCrates:
                f.Box(0, 0.35f, 0, 0.7f, 0.7f, 0.9f, CCrate, collide: true);
                break;
            case FurnitureType.SuitRack:
                f.Box(0, 0.95f, 0, 0.5f, 1.3f, 0.5f, CSuit, collide: true);
                f.Box(0, 1.8f, 0, 0.36f, 0.36f, 0.36f, CSuit);
                f.Box(-0.17f, 1.8f, 0, 0.04f, 0.24f, 0.28f, CVisor);
                f.Box(0.115f, 1.1f, 0, 0.08f, 1.6f, 0.9f, CMetal);
                break;
            case FurnitureType.StoreRack:
                f.Box(0, 0.9f, 0, 0.7f, 1.8f, 1.0f, CMetal, collide: true);
                f.Box(0, 0.35f, 0, 0.6f, 0.5f, 0.8f, CCrate);
                f.Box(0, 1.05f, 0, 0.6f, 0.5f, 0.8f, CCrateDark);
                break;
            case FurnitureType.ColonyConsole:
            {
                // its desk in the theme's band, its screen over it facing +Z; walked against as a 0.9 m square
                f.Box(0, 0.4f, 0, 1.08f, 0.8f, 0.6f, pal.Band);
                var basis = new Basis(Vector3.Up, (p.Turns & 3) * Mathf.Pi / 2 + p.Angle);
                CollisionBox(s, new Vector3(p.X, y0, p.Z), basis, new Vector3(-0.45f, 0, -0.45f), new Vector3(0.45f, 0.8f, 0.45f));
                f.Pane(new Vector3(0, 1.15f, 0.05f), Vector3.Back, 0.9f, 0.5f, alt ? CScreenAmber : CScreen);
                break;
            }
            case FurnitureType.MapTable:
                f.Box(0, 0.45f, 0, 2.2f, 0.9f, 2.2f, pal.Frame, collide: true);
                f.Pane(new Vector3(0, 0.905f, 0), Vector3.Up, 1.8f, 1.8f, CScreen);
                break;
            case FurnitureType.HydroRack:
                // two tiers of trays and plants under a pink grow light
                f.Box(0, 0.5f, 0, 0.9f, 1.0f, D, CTray, collide: true);
                f.Box(0, 1.05f, 0, 0.8f, 0.15f, D - 0.2f, CPlant);
                f.Box(0, 1.55f, 0, 0.8f, 0.05f, D, CTray);
                f.Box(0, 1.68f, 0, 0.7f, 0.2f, D - 0.3f, CPlantDark);
                f.Pane(new Vector3(0, 2.3f, 0), Vector3.Down, 0.4f, D - 0.4f, CGrow);
                break;
            case FurnitureType.CrateStack:
                f.Box(0, 0.5f, 0, 0.9f, 1.0f, 1.0f, alt ? CCrateDark : CCrate, collide: true);
                break;
            case FurnitureType.BayPaint:
                // a rover's parking box painted on the slab
                f.Box(0, 0.005f, -2.5f, 4.4f, 0.01f, 0.12f, CBayPaint);
                f.Box(0, 0.005f, 2.5f, 4.4f, 0.01f, 0.12f, CBayPaint);
                f.Box(-2.2f, 0.005f, 0, 0.12f, 0.01f, 5f, CBayPaint);
                f.Box(2.2f, 0.005f, 0, 0.12f, 0.01f, 5f, CBayPaint);
                break;
            case FurnitureType.HangarBench:
            {
                // the bench along the wall, a crate on it every 2.5 m (the kit's run from its -Z quarter, turned half round)
                f.Box(0, 0.45f, 0, 0.8f, 0.9f, D, CWood, collide: true);
                float q = D / 2.4f;
                for (float z = -q; z <= q; z += 2.5f) f.Box(0, 1.2f, -z, 0.5f, 0.6f, 0.6f, CCrate);
                break;
            }
            case FurnitureType.ColonyLamp:
                f.Pane(Vector3.Zero, Vector3.Down, p.W, D, alt ? CAmberLamp : pal.Lamp);
                break;
            case FurnitureType.ColonyScreen:
                f.Pane(new Vector3(0, p.H / 2, 0), Vector3.Left, D, p.H, alt ? CScreenAmber : CScreen);
                break;
        }
        return true;
    }
}
