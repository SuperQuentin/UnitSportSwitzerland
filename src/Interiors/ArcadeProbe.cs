using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --arcadecheck --systems ui</c> (#1240): arcades planned in hand-made
/// buildings, as <see cref="BowlingProbe"/> plans bowling centres: detection, the door, the plan, the
/// machines, the validator, the mesh. SVG plans in <c>test_output/arcade/</c>.
/// </summary>
public partial class ArcadeProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--arcadecheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[arcade] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    private sealed record Case(string Name, float W, float D, float H, BuildingKind Kind, bool Arcade, int MinMachines = 0);

    private static readonly Case[] Cases =
    {
        // a high-street arcade of its own
        new("small", 12, 18, 5, BuildingKind.Commercial, true, 15),
        // a big hall: islands of uprights in the middle
        new("big", 28, 36, 7, BuildingKind.Commercial, true, 60),
        // wider than deep
        new("along-x", 30, 16, 5, BuildingKind.Commercial, true, 30),
        // in a mall: the arcade keeps to 32 m a side, the rest a plain hall
        new("mall", 80, 60, 10, BuildingKind.Commercial, true, 80),
        // under flats: the ground floor
        new("under-flats", 16, 22, 15, BuildingKind.Apartment, true, 20),
        // a kiosk: no arcade fits, the ordinary rules furnish it
        new("too-small", 5, 6, 4, BuildingKind.Commercial, false),
        // an arcade by OSM that its name says is a billiard hall (Bluff Billard, Geneva, #1250)
        new("billard", 20, 26, 4.5f, BuildingKind.Commercial, true),
    };

    public override void _Ready()
    {
        foreach (var c in Cases) Plan(c);
        Venues.Override = null;
        GD.Print($"[arcade] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private void Plan(Case c)
    {
        var id = new TileId(2600, 1200);
        var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, c.W, c.D, c.H, c.Kind) } };
        Venues.Override = new List<VenueRecord>
        {
            new("n1", VenueType.Arcade, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", $"Arcade {c.Name}"),
        };
        string what = $"{c.Name} ({c.W}x{c.D} m, {c.H} m, {c.Kind})";
        Check(Venues.HallOf(BuildingTypes.For(tile), 0) == VenueType.Arcade, $"{what}: the solid is an arcade");

        InteriorLayout? l;
        try { l = InteriorGenerator.Generate(tile, 0, null, null); }
        catch (Exception e) { Check(false, $"{what}: planning threw {e.GetType().Name}: {e.Message}"); return; }
        if (l == null) { Check(false, $"{what}: an interior was planned"); return; }
        var problems = InteriorValidator.Validate(l);
        Check(problems.Count == 0, $"{what}: the plan validates{(problems.Count == 0 ? "" : ": " + string.Join("; ", problems.Take(6)))}");
        Write(c.Name, l);
        if (!c.Arcade)
        {
            Check(l.Type != BuildingType.Arcade, $"{what}: too small for an arcade, planned by the ordinary rules ({l.Type})");
            return;
        }
        Check(l.Type == BuildingType.Arcade, $"{what}: the plan is an arcade ({l.Type})");
        Check(l.Group == $"Arcade{InteriorGenerator.ArcadeRules}", $"{what}: fingerprinted with the arcade rules ({l.Group})");

        int Count(FurnitureType t) => l.Furniture.Count(f => f.Type == t);
        if (InteriorGenerator.IsBilliard($"Arcade {c.Name}"))
        {
            Check(l.Floors[0].Rooms.Any(r => r.Type == RoomType.BilliardHall), $"{what}: a billiard hall on the ground floor");
            Check(Count(FurnitureType.PoolTable) >= 4, $"{what}: pool tables ({Count(FurnitureType.PoolTable)})");
            Check(Count(FurnitureType.BarCounter) == 1 && Count(FurnitureType.BarStool) >= 3, $"{what}: the bar and its stools");
            Check(Count(FurnitureType.CueRack) >= 2, $"{what}: cue racks ({Count(FurnitureType.CueRack)})");
            Check(Count(FurnitureType.HighTable) + Count(FurnitureType.Sofa) >= 1, $"{what}: somewhere to sit between the tables");
            // every pool table keeps a cue's swing clear of every other piece
            foreach (var t in l.Furniture.Where(x => x.Type == FurnitureType.PoolTable))
            {
                float ew = t.Turns % 2 == 0 ? t.W : t.D, ed = t.Turns % 2 == 0 ? t.D : t.W;
                var swing = new RectPlan(t.X - ew / 2 - 1.2f, t.Z - ed / 2 - 1.2f, t.X + ew / 2 + 1.2f, t.Z + ed / 2 + 1.2f);
                var hit = l.Furniture.FirstOrDefault(x => x != t && x.Floor == t.Floor && x.Type is not (FurnitureType.Confetti)
                    && swing.Overlaps(new RectPlan(x.X - 0.01f, x.Z - 0.01f, x.X + 0.01f, x.Z + 0.01f)));
                Check(hit == null, $"{what}: a pool table at {t.X:F1},{t.Z:F1} keeps its swing clear ({hit?.Type})");
            }
            return;
        }
        var hall = l.Floors[0].Rooms.FirstOrDefault(r => r.Type == RoomType.ArcadeHall);
        Check(hall != null, $"{what}: an arcade hall on the ground floor");
        if (hall == null) return;
        Check(hall.Openings.All(o => o.Kind != OpeningKind.Window), $"{what}: the hall is dark (no windows)");
        int machines = l.Furniture.Count(f => f.Type is FurnitureType.ArcadeCabinet or FurnitureType.Pinball or FurnitureType.ClawMachine
            or FurnitureType.Racer or FurnitureType.SimPod or FurnitureType.DanceMachine or FurnitureType.DrumGame
            or FurnitureType.BasketballGame or FurnitureType.AirHockey);
        Check(machines >= c.MinMachines, $"{what}: {machines} machines (at least {c.MinMachines})");
        Check(machines <= 110, $"{what}: at most 110 machines");
        Check(machines <= hall.Area * 0.17f, $"{what}: at most ~16 machines to 100 m2 ({machines} in {hall.Area:F0} m2)");
        Check(Count(FurnitureType.PrizeCounter) == 1, $"{what}: a prize counter");
        Check(Count(FurnitureType.ClawMachine) >= 2, $"{what}: claw machines ({Count(FurnitureType.ClawMachine)})");
        Check(Count(FurnitureType.Racer) >= 1, $"{what}: a racer ({Count(FurnitureType.Racer)})");
        Check(Count(FurnitureType.Pinball) >= 2, $"{what}: a row of pinballs ({Count(FurnitureType.Pinball)})");
        Check(Count(FurnitureType.ArcadeCabinet) >= 2, $"{what}: uprights ({Count(FurnitureType.ArcadeCabinet)})");
        int kinds = new[] { FurnitureType.Racer, FurnitureType.SimPod, FurnitureType.DanceMachine, FurnitureType.DrumGame,
            FurnitureType.BasketballGame, FurnitureType.AirHockey }.Count(t => Count(t) > 0);
        Check(kinds >= 3, $"{what}: {kinds} kinds of big machine");
        Check(l.Floors[0].Rooms.Any(r => r.Type == RoomType.WC) || hall.Area < 60, $"{what}: a WC");
        var main = BuildingFootprint.ComputeDoors(tile, null, null).First(d => d.Index == 0 && d.Slot == 0);
        Check(main.Width >= 2.0f, $"{what}: its way in is a double door ({main.Width:F1} m)");

        var mesh = InteriorMeshBuilder.Build(l);
        Check(mesh.Vertices.Length > 0 && mesh.Collision.Length > 0, $"{what}: meshes ({mesh.Vertices.Length} vertices) with collision");
        Check(mesh.Vertices.Length < 400_000, $"{what}: under 400k vertices");
    }

    private static void Write(string name, InteriorLayout l)
    {
        try
        {
            string dir = ProjectSettings.GlobalizePath("res://test_output/arcade");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.svg"), InteriorValidator.ToSvg(l));
        }
        catch (Exception e) { GD.Print($"[arcade] could not write the plan: {e.Message}"); }
    }
}
