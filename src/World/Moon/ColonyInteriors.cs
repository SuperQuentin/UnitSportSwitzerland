using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// What is inside a colony's modules (#1941, docs/plans/moon-surface.md section 5): furniture by room, laid along the
/// walls of the module the outside shows (a can's aisle down its middle, a hub's console in the round, a dome's racks
/// in rows), its screens and lamps lit on the glass surface. Built once per kind, room, size and theme, in the module's
/// author space (+Z along its axis, the floor at <see cref="ColonyKit.FloorY"/>); the solid parts come back as boxes in
/// the space the module's node uses (after <c>MeshScratch</c>'s half turn). In the model viewer under "Moon rooms".
/// </summary>
public static class ColonyInteriors
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Metal = C("9aa1a8"), Dark = C("3a3f45"), Mattress = C("5d7fb0"), Sheet = C("e9edf2"),
        Wood = C("b08a5e"), Screen = C("7fe0ff"), ScreenAmber = C("ffcf6e"), Plant = C("4fae4a"), PlantDark = C("2f7a33"),
        Tray = C("c9cfd6"), Crate = C("c7a46a"), CrateDark = C("8d7348"), Red = C("c9302c"), White = C("eef1f3"),
        Suit = C("e8ebee"), Visor = C("c79a2a"), Rubber = C("2a2d31");

    private static readonly Dictionary<string, (ArrayMesh, (Vector3, Vector3)[])> Cache = new();
    private static readonly object Lock = new();

    /// <summary>A module's furniture mesh and its solid boxes (centre, size: node space), cached.</summary>
    public static (ArrayMesh Mesh, (Vector3 Centre, Vector3 Size)[] Solid) Furnish(ModuleKind kind, RoomKind room, double length, double width, ColonyTheme theme)
    {
        string key = FormattableString.Invariant($"{kind}/{room}/{length:F1}/{width:F1}/{theme}");
        lock (Lock)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
            var f = new Furnisher(ColonyKit.Of(theme));
            float l = (float)length, w = (float)width;
            switch (kind)
            {
                case ModuleKind.Can: case ModuleKind.Airlock: f.Can(room, l, w / 2); break;
                case ModuleKind.Hub: case ModuleKind.Stack: f.Round(room, w / 2); break;
                case ModuleKind.Dome: f.Dome(room, w / 2); break;
                case ModuleKind.Hangar: f.Hangar(l, w); break;
            }
            var result = (f.S.Build(), f.Solids.ToArray());
            Cache[key] = result;
            return result;
        }
    }

    /// <summary>Each room in a 12 m can, Artemis' colours; the hub's control room; a greenhouse dome; the hangar.</summary>
    [Showcase("Moon rooms")]
    public static IEnumerable<(string Name, Func<Node3D> Make)> Rooms()
    {
        foreach (RoomKind r in Enum.GetValues<RoomKind>())
        {
            if (r is RoomKind.Hall or RoomKind.Control or RoomKind.Garage) continue;
            var rr = r;
            var kind = r == RoomKind.Airlock ? ModuleKind.Airlock : ModuleKind.Can;
            double len = r == RoomKind.Airlock ? 4 : 12;
            yield return ($"Can: {r}", () => Show(kind, rr, len, 4.6));
        }
        yield return ("Hub: control", () => Show(ModuleKind.Hub, RoomKind.Control, 10, 10));
        yield return ("Dome: greenhouse", () => Show(ModuleKind.Dome, RoomKind.Greenhouse, 18, 18));
        yield return ("Hangar: garage", () => Show(ModuleKind.Hangar, RoomKind.Garage, 26, 14));
    }

    private static Node3D Show(ModuleKind kind, RoomKind room, double l, double w)
    {
        var root = new Node3D();
        var shell = ColonyKit.Instance(ColonyKit.Module(kind, l, w, kind == ModuleKind.Hub ? 7.5 : kind == ModuleKind.Hangar ? 8 : w / 2, kind == ModuleKind.Hub ? 2 : 1, ColonyTheme.Artemis));
        root.AddChild(shell);
        root.AddChild(ColonyKit.Instance(Furnish(kind, room, l, w, ColonyTheme.Artemis).Mesh));
        return root;
    }

    /// <summary>One module's furnishing: the scratch, and the solid boxes in node space.</summary>
    private sealed class Furnisher(ColonyKit.Palette p)
    {
        public readonly MeshScratch S = new();
        public readonly List<(Vector3, Vector3)> Solids = new();
        private readonly ColonyKit.Palette _p = p;
        private const float F = ColonyKit.FloorY;

        /// <summary>A box, solid when <paramref name="solid"/> (the walker bumps into it).</summary>
        private void Box(Vector3 c, Vector3 size, Color col, bool solid = false)
        {
            S.Box(c, size, col);
            // MeshScratch turns the mesh half a turn: the solid goes where the drawn box ends up
            if (solid) Solids.Add((new Vector3(-c.X, c.Y, -c.Z), size));
        }

        private void Lit(Vector3 c, Vector3 normal, float w, float h, Color col)
        {
            var right = Vector3.Up.Cross(normal).Normalized() * (w / 2);
            if (right.LengthSquared() < 1e-6f) right = Vector3.Right * (w / 2);
            var up = normal.Cross(right).Normalized() * (h / 2);
            var o = c + normal * 0.005f;
            S.Pane([o - right - up, o + right - up, o + right + up, o - right + up], col);
        }

        // ---- cans: an aisle down the middle, things along both walls ------------------------------------------------

        public void Can(RoomKind room, float l, float r)
        {
            float z0 = -l / 2 + 1.0f, z1 = l / 2 - 1.0f;
            switch (room)
            {
                case RoomKind.Quarters:
                    for (float z = z0 + 1.0f; z + 1.0f <= z1 + 0.01f; z += 2.2f)
                        foreach (float x in new[] { -1.05f, 1.05f })
                        {
                            Bunk(new Vector3(x, F, z));
                            Box(new Vector3(x * 1.05f, F + 0.9f, z + 1.15f), new(0.45f, 1.8f, 0.2f), _p.Trim, solid: true);
                        }
                    break;
                case RoomKind.Galley:
                    Box(new Vector3(-1.15f, F + 0.45f, 0), new(0.6f, 0.9f, l - 2.4f), White, solid: true);
                    for (float z = -l / 2 + 1.6f; z < l / 2 - 1.6f; z += 1.2f) Lit(new Vector3(-1.44f, F + 1.45f, z), Vector3.Right, 0.45f, 0.3f, ScreenAmber);
                    for (float z = z0 + 0.8f; z + 0.8f <= z1; z += 2.4f)
                    {
                        Box(new Vector3(0.95f, F + 0.75f, z), new(0.8f, 0.06f, 1.4f), Wood, solid: true);
                        Box(new Vector3(0.95f, F + 0.37f, z), new(0.1f, 0.74f, 0.1f), Metal);
                        Box(new Vector3(0.95f, F + 0.45f, z - 0.95f), new(0.8f, 0.08f, 0.35f), _p.Trim);
                        Box(new Vector3(0.95f, F + 0.45f, z + 0.95f), new(0.8f, 0.08f, 0.35f), _p.Trim);
                    }
                    break;
                case RoomKind.Lab:
                    foreach (float x in new[] { -1.1f, 1.1f })
                    {
                        Box(new Vector3(x, F + 0.45f, 0), new(0.65f, 0.9f, l - 2.4f), White, solid: true);
                        for (float z = -l / 2 + 1.8f; z < l / 2 - 1.6f; z += 1.6f)
                        {
                            Box(new Vector3(x * 1.1f, F + 1.2f, z), new(0.08f, 0.5f, 0.75f), Dark);
                            Lit(new Vector3(x * 1.1f - Mathf.Sign(x) * 0.045f, F + 1.2f, z), new Vector3(-Mathf.Sign(x), 0, 0), 0.68f, 0.42f, Screen);
                            Box(new Vector3(x, F + 1.0f, z + 0.5f), new(0.2f, 0.2f, 0.2f), Tray);
                        }
                    }
                    break;
                case RoomKind.Medbay:
                    Box(new Vector3(-1.0f, F + 0.6f, -l / 4), new(0.8f, 0.12f, 2.0f), Sheet, solid: true);
                    Box(new Vector3(-1.0f, F + 0.3f, -l / 4), new(0.7f, 0.6f, 1.8f), Metal, solid: true);
                    Box(new Vector3(-1.0f, F + 0.6f, l / 4), new(0.8f, 0.12f, 2.0f), Sheet, solid: true);
                    Box(new Vector3(-1.0f, F + 0.3f, l / 4), new(0.7f, 0.6f, 1.8f), Metal, solid: true);
                    Box(new Vector3(1.15f, F + 0.9f, 0), new(0.5f, 1.8f, l - 3f), White, solid: true);
                    for (float z = -l / 2 + 2f; z < l / 2 - 2f; z += 1.2f) Box(new Vector3(0.88f, F + 1.3f, z), new(0.02f, 0.25f, 0.25f), Red);
                    Lit(new Vector3(-1.45f, F + 1.5f, -l / 4), Vector3.Right, 0.5f, 0.35f, Screen);
                    break;
                case RoomKind.Gym:
                    Box(new Vector3(-1.0f, F + 0.15f, -l / 4), new(0.7f, 0.3f, 1.8f), Rubber, solid: true);
                    Box(new Vector3(-1.0f, F + 1.0f, -l / 4 - 0.8f), new(0.7f, 0.08f, 0.08f), Metal);
                    Box(new Vector3(1.0f, F + 0.5f, -l / 4), new(0.4f, 1.0f, 1.0f), Dark, solid: true);
                    Box(new Vector3(1.0f, F + 0.9f, l / 4), new(0.3f, 1.8f, 0.3f), Metal, solid: true);
                    // the harness that holds a runner down at a sixth of a g
                    Box(new Vector3(-1.0f, F + 1.9f, -l / 4), new(0.05f, 0.05f, 1.2f), _p.Trim);
                    break;
                case RoomKind.Workshop:
                    Box(new Vector3(-1.1f, F + 0.45f, 0), new(0.7f, 0.9f, l - 2.4f), Wood, solid: true);
                    Box(new Vector3(-1.45f, F + 1.4f, 0), new(0.05f, 0.9f, l - 2.6f), Dark);
                    for (float z = -l / 2 + 1.6f; z < l / 2 - 1.6f; z += 0.6f) Box(new Vector3(-1.4f, F + 1.4f, z), new(0.05f, 0.4f, 0.06f), Red);
                    for (float z = z0 + 0.5f; z <= z1 - 0.5f; z += 1.6f) Box(new Vector3(1.05f, F + 0.35f, z), new(0.7f, 0.7f, 0.9f), Crate, solid: true);
                    break;
                case RoomKind.Airlock:
                    // the suits waiting on their racks either side, helmets over them
                    foreach (float x in new[] { -1.15f, 1.15f })
                    {
                        Box(new Vector3(x, F + 0.95f, 0), new(0.5f, 1.3f, 0.5f), Suit, solid: true);
                        Box(new Vector3(x, F + 1.8f, 0), new(0.36f, 0.36f, 0.36f), Suit);
                        Box(new Vector3(x - Mathf.Sign(x) * 0.17f, F + 1.8f, 0), new(0.04f, 0.24f, 0.28f), Visor);
                        Box(new Vector3(x * 1.1f, F + 1.1f, 0), new(0.08f, 1.6f, 0.9f), Metal);
                    }
                    break;
                default:
                    // storage: racks of crates along both walls
                    foreach (float x in new[] { -1.1f, 1.1f })
                        for (float z = z0 + 0.5f; z <= z1 - 0.4f; z += 1.1f)
                        {
                            Box(new Vector3(x, F + 0.9f, z), new(0.7f, 1.8f, 1.0f), Metal, solid: true);
                            Box(new Vector3(x, F + 0.35f, z), new(0.6f, 0.5f, 0.8f), Crate);
                            Box(new Vector3(x, F + 1.05f, z), new(0.6f, 0.5f, 0.8f), CrateDark);
                        }
                    break;
            }
        }

        /// <summary>Two bunks one over the other along the wall, mattress and sheet, a ladder, a reading lamp.</summary>
        private void Bunk(Vector3 at)
        {
            float x = at.X, z = at.Z;
            foreach (float y in new[] { 0.35f, 1.35f })
            {
                Box(new Vector3(x, F + y, z), new(0.85f, 0.1f, 2.0f), Metal, solid: y < 1);
                Box(new Vector3(x, F + y + 0.11f, z), new(0.8f, 0.12f, 1.9f), Mattress);
                Box(new Vector3(x, F + y + 0.18f, z - 0.4f), new(0.8f, 0.02f, 1.0f), Sheet);
                Lit(new Vector3(x * 1.3f, F + y + 0.55f, z + 0.85f), new Vector3(-Mathf.Sign(x), 0, 0), 0.15f, 0.1f, ScreenAmber);
            }
            Box(new Vector3(x - Mathf.Sign(x) * 0.43f, F + 0.9f, z + 0.9f), new(0.04f, 1.8f, 0.04f), Metal);
        }

        // ---- the hub: the control room in the round ---------------------------------------------------------------

        public void Round(RoomKind room, float r)
        {
            // a ring of consoles round the middle, screens facing in, a table in the centre with the colony's map
            // eight consoles, 30 degrees either side of the diagonals: clear of the four doorways
            for (int i = 0; i < 8; i++)
            {
                float a = Mathf.DegToRad(45 + 90 * (i / 2) + (i % 2 == 0 ? -15 : 15));
                var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                var c = dir * (r - 0.9f);
                Box(c + Vector3.Up * (F + 0.4f), new Vector3(1.08f, 0.8f, 0.6f), _p.Band);
                Solids.Add((new Vector3(-c.X, F + 0.4f, -c.Z), new Vector3(0.9f, 0.8f, 0.9f)));
                Lit(c + Vector3.Up * (F + 1.15f) - dir * 0.05f, -dir, 0.9f, 0.5f, i % 2 == 0 ? Screen : ScreenAmber);
            }
            Box(new Vector3(0, F + 0.45f, 0), new(2.2f, 0.9f, 2.2f), _p.Frame, solid: true);
            Lit(new Vector3(0, F + 0.905f, 0), Vector3.Up, 1.8f, 1.8f, Screen);
            _ = room;
        }

        // ---- a dome: hydroponic racks or storage in rows -------------------------------------------------------------

        public void Dome(RoomKind room, float r)
        {
            for (float x = -r * 0.5f; x <= r * 0.5f + 0.01f; x += 2.2f)
            {
                float half = Mathf.Sqrt(Mathf.Max(0, r * r * 0.55f - x * x));
                if (half < 1.2f) continue;
                if (Mathf.Abs(x) < 0.8f) continue;   // the middle aisle from the door
                if (room == RoomKind.Greenhouse)
                {
                    Box(new Vector3(x, F + 0.5f, 0), new(0.9f, 1.0f, half * 2), Tray, solid: true);
                    Box(new Vector3(x, F + 1.05f, 0), new(0.8f, 0.15f, half * 2 - 0.2f), Plant);
                    Box(new Vector3(x, F + 1.55f, 0), new(0.8f, 0.05f, half * 2), Tray);
                    Box(new Vector3(x, F + 1.68f, 0), new(0.7f, 0.2f, half * 2 - 0.3f), PlantDark);
                    Lit(new Vector3(x, F + 2.3f, 0), Vector3.Down, 0.4f, half * 2 - 0.4f, C("ff7fe0"));
                }
                else
                    for (float z = -half + 0.6f; z <= half - 0.6f; z += 1.2f)
                        Box(new Vector3(x, F + 0.5f, z), new(0.9f, 1.0f, 1.0f), (int)(z * 3) % 2 == 0 ? Crate : CrateDark, solid: true);
            }
        }

        // ---- the hangar: the workshop along its sides, the bay free for rovers -----------------------------------------

        public void Hangar(float l, float w)
        {
            foreach (float x in new[] { -w / 2 + 1.0f, w / 2 - 1.0f })
            {
                Box(new Vector3(x, 0.6f + 0.45f, 0), new(0.8f, 0.9f, l * 0.6f), Wood, solid: true);
                for (float z = -l * 0.25f; z <= l * 0.25f; z += 2.5f) Box(new Vector3(x, 0.6f + 1.2f, z), new(0.5f, 0.6f, 0.6f), Crate);
            }
            // the bay's paint: a box outline where a rover parks
            foreach (float z in new[] { -l * 0.2f, l * 0.2f })
            {
                Box(new Vector3(0, 0.605f, z - 2.5f), new(4.4f, 0.01f, 0.12f), C("f2c21b"));
                Box(new Vector3(0, 0.605f, z + 2.5f), new(4.4f, 0.01f, 0.12f), C("f2c21b"));
                Box(new Vector3(-2.2f, 0.605f, z), new(0.12f, 0.01f, 5f), C("f2c21b"));
                Box(new Vector3(2.2f, 0.605f, z), new(0.12f, 0.01f, 5f), C("f2c21b"));
            }
        }
    }
}
