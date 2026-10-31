using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --bowlingcheck --systems ui</c> (#1235): bowling centres planned in
/// hand-made buildings, no map and no server (the fixture world has no buildings, the generated world
/// no venues). Each building gets a bowling venue on its point (<see cref="Venues.Override"/>) and goes
/// the whole way: detection, the door, the plan, the furniture, the validator, the mesh. SVG plans in
/// <c>test_output/bowling/</c>.
/// </summary>
public partial class BowlingProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--bowlingcheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[bowling] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    /// <summary>A building to plan: its size, its height, its kind, and what is expected of it.</summary>
    private sealed record Case(string Name, float W, float D, float H, BuildingKind Kind, bool Bowling, bool Mini = false,
        bool Mezzanine = false, bool Spare = false,
        ToiletSet Toilets = ToiletSet.Shared, bool Accessible = false);

    private static readonly Case[] Cases =
    {
        // a classic box on two levels: 8 m tall, room for a mezzanine over the concourse
        new("two-levels", 30, 48, 8.5f, BuildingKind.Commercial, true, Mezzanine: true, Toilets: ToiletSet.Gendered, Accessible: true),
        // the same lanes on one level, the bar down on the concourse
        new("one-level", 24, 45, 6, BuildingKind.Commercial, true, Toilets: ToiletSet.Gendered, Accessible: true),
        // a bowl bar in a town building (Lausanne's Flon is 13 x 24 m): mini lanes
        new("bowl-bar", 13, 24, 5, BuildingKind.Commercial, true, Mini: true),
        // wider than deep: the lanes run along X from the end nearer the door
        new("along-x", 62, 34, 7, BuildingKind.Industrial, true, Toilets: ToiletSet.Gendered, Accessible: true),
        // a mall: the centre in a block at the entrance, plain halls beyond
        // (20 m tall, so not a box of its own: its storeys stay, and two of them give a mezzanine)
        new("mall", 180, 120, 20, BuildingKind.Commercial, true, Mezzanine: true, Spare: true, Toilets: ToiletSet.Gendered, Accessible: true),
        // a narrow alley of mini lanes: a single WC beside the concourse (#1285)
        new("alley", 8.6f, 24, 5, BuildingKind.Commercial, true, Mini: true, Toilets: ToiletSet.Pair),
        // a kiosk: no lanes fit, the ordinary rules furnish it
        new("too-small", 8, 12, 4, BuildingKind.Commercial, false),
    };

    public override void _Ready()
    {
        foreach (var c in Cases) Plan(c);
        Venues.Override = null;
        GD.Print($"[bowling] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private void Plan(Case c)
    {
        var id = new TileId(2600, 1200);
        var tile = new BuildingTile { Id = id, Buildings = new List<Building> { Box(500, 500, c.W, c.D, c.H, c.Kind) } };
        Venues.Override = new List<VenueRecord>
        {
            new("n1", VenueType.Bowling, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", $"Bowling {c.Name}"),
        };
        string what = $"{c.Name} ({c.W}x{c.D} m, {c.H} m, {c.Kind})";
        var map = BuildingTypes.For(tile);
        Check(Venues.HallOf(map, 0) == VenueType.Bowling, $"{what}: the solid is a bowling centre");
        if (c.Bowling)
        {
            var main = BuildingFootprint.ComputeDoors(tile, null, null).First(d => d.Index == 0 && d.Slot == 0);
            Check(main.Width >= 2.0f, $"{what}: its way in is a double door ({main.Width:F1} m)");
        }

        InteriorLayout? l;
        try { l = InteriorGenerator.Generate(tile, 0, null, null); }
        catch (Exception e) { Check(false, $"{what}: planning threw {e.GetType().Name}: {e.Message}"); return; }
        if (l == null) { Check(false, $"{what}: an interior was planned"); return; }

        var problems = InteriorValidator.Validate(l);
        Check(problems.Count == 0, $"{what}: the plan validates{(problems.Count == 0 ? "" : ": " + string.Join("; ", problems.Take(6)))}");
        Write(c.Name, l);
        if (problems.Any(x => x.Contains("doorway")))
            foreach (var room in l.Floors[0].Rooms)
            {
                GD.Print($"[bowling]      room {room.Type} {room.X0:F1},{room.Z0:F1}..{room.X1:F1},{room.Z1:F1}: "
                    + string.Join(" ", room.Openings.Select(o => $"{o.Kind}/{o.Side}@{o.Center:F1}w{o.Width:F1}")));
                foreach (var f in l.Furniture.Where(f => f.Floor == 0 && f.X > room.X0 && f.X < room.X1 && f.Z > room.Z0 && f.Z < room.Z1 && f.Type != FurnitureType.LanePair))
                    GD.Print($"[bowling]        {f.Type} {f.X:F1},{f.Z:F1} t{f.Turns} {f.W:F1}x{f.D:F1}");
            }
        if (problems.Any(x => x.Contains("pokes")))
            foreach (var f in l.Furniture.Where(f => f.Type == FurnitureType.LanePair).Take(2))
            {
                var room = l.Floors[f.Floor].Rooms.FirstOrDefault(r => f.X > r.X0 && f.X < r.X1 && f.Z > r.Z0 && f.Z < r.Z1);
                GD.Print($"[bowling]      pair at {f.X:F1},{f.Z:F1} turns {f.Turns} {f.W}x{f.D:F1}; room {room?.Type} {room?.X0:F1},{room?.Z0:F1}..{room?.X1:F1},{room?.Z1:F1}");
            }
        if (!c.Bowling)
        {
            Check(l.Type != BuildingType.Bowling, $"{what}: too small for lanes, planned by the ordinary rules ({l.Type})");
            return;
        }
        Check(l.Type == BuildingType.Bowling, $"{what}: the plan is a bowling centre ({l.Type})");
        Check(l.Group == $"Bowling{InteriorGenerator.BowlingRules}", $"{what}: fingerprinted with the bowling rules ({l.Group})");

        int pairs = l.Furniture.Count(f => f.Type == FurnitureType.LanePair);
        int settees = l.Furniture.Count(f => f.Type == FurnitureType.Settee);
        Check(pairs >= 1, $"{what}: {pairs} pair(s) of lanes");
        Check(settees == pairs, $"{what}: a settee bay for every pair ({settees})");
        bool mini = l.Furniture.Where(f => f.Type == FurnitureType.LanePair).All(f => f.D < 20f);
        Check(mini == c.Mini, $"{what}: {(c.Mini ? "mini" : "full")} lanes ({l.Furniture.FirstOrDefault(f => f.Type == FurnitureType.LanePair)?.D:F1} m blocks)");
        Check(l.Furniture.Any(f => f.Type == FurnitureType.ShoeCounter), $"{what}: a shoe counter");
        Check(l.Furniture.Any(f => f.Type == FurnitureType.Pinball), $"{what}: a pinball in the arcade corner");
        Check(l.Furniture.Any(f => f.Type == FurnitureType.BarCounter), $"{what}: a bar");
        // the public toilets the footprint has room for (#1285)
        var loo = ToiletCheck.Problems(l, c.Toilets, c.Accessible);
        Check(loo.Count == 0, $"{what}: toilets {ToiletCheck.Describe(l)}{(loo.Count == 0 ? "" : ": " + string.Join("; ", loo))}");
        bool mezz = l.Floors.Count > 1 && l.Floors[1].Rooms.Any(r => r.Type == RoomType.Mezzanine);
        Check(mezz == c.Mezzanine, $"{what}: {(c.Mezzanine ? "a mezzanine over the concourse" : "one level")} ({l.Floors.Count} floor(s))");
        if (mezz)
            Check(l.Furniture.Any(f => f.Floor == 1 && f.Type is FurnitureType.BarCounter or FurnitureType.PoolTable),
                $"{what}: the bar and the pool tables upstairs");
        if (c.Spare)
            Check(l.Floors[0].Rooms.Any(r => r.Type == RoomType.Hall), $"{what}: the rest of the solid is a plain hall");
        if (!c.Mini)
            Check(l.Furniture.Any(f => f.Type == FurnitureType.PoolTable), $"{what}: pool tables ({l.Furniture.Count(f => f.Type == FurnitureType.PoolTable)})");

        // every lane pair runs the long way and inside the lane hall
        var hall = l.Floors[0].Rooms.First(r => r.Type == RoomType.BowlingLanes);
        Check(l.Furniture.Where(f => f.Type == FurnitureType.LanePair).All(f => f.X > hall.X0 && f.X < hall.X1 && f.Z > hall.Z0 && f.Z < hall.Z1),
            $"{what}: every pair stands in the lane hall");

        var mesh = InteriorMeshBuilder.Build(l);
        Check(mesh.Vertices.Length > 0 && mesh.Collision.Length > 0, $"{what}: meshes ({mesh.Vertices.Length} vertices) with collision");
        Check(mesh.Vertices.Length < 400_000, $"{what}: under 400k vertices");
    }

    private static void Write(string name, InteriorLayout l)
    {
        try
        {
            string dir = ProjectSettings.GlobalizePath("res://test_output/bowling");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.svg"), InteriorValidator.ToSvg(l));
        }
        catch (Exception e) { GD.Print($"[bowling] could not write the plan: {e.Message}"); }
    }

    /// <summary>A flat-topped box: four walls and a roof (the model viewer's centres are built in one too).</summary>
    internal static Building Box(float cx, float cz, float w, float d, float h, BuildingKind kind)
    {
        float x0 = cx - w / 2, x1 = cx + w / 2, z0 = cz - d / 2, z1 = cz + d / 2;
        var t = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) => t.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
        var corners = new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z1) };
        for (int i = 0; i < 4; i++)
        {
            var (ax, az) = corners[i];
            var (bx, bz) = corners[(i + 1) % 4];
            Tri(new Vector3(ax, 0, az), new Vector3(bx, 0, bz), new Vector3(bx, h, bz));
            Tri(new Vector3(ax, 0, az), new Vector3(bx, h, bz), new Vector3(ax, h, az));
        }
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z0), new Vector3(x1, h, z1));
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z1), new Vector3(x0, h, z1));
        return new Building { Kind = kind, MinY = 0, MaxY = h, Triangles = t.ToArray() };
    }
}
