using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --cinemacheck --systems ui</c> (#1238): cinemas planned in hand-made
/// buildings, as <see cref="BowlingProbe"/> plans bowling centres: detection, the door, the plan, the
/// furniture, the validator, the mesh. SVG plans in <c>test_output/cinema/</c>.
/// </summary>
public partial class CinemaProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--cinemacheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[cinema] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    private sealed record Case(string Name, float W, float D, float H, BuildingKind Kind, bool Cinema, int MinHalls = 1,
        bool Upstairs = false);

    private static readonly Case[] Cases =
    {
        // a town cinema of its own, tall enough for the booth upstairs
        new("town", 18, 34, 9, BuildingKind.Commercial, true, Upstairs: true),
        // a small one on one level: the booth behind the top rows at floor level
        new("small", 14, 26, 5, BuildingKind.Commercial, true),
        // a multiplex: halls side by side
        new("multiplex", 72, 52, 12, BuildingKind.Commercial, true, MinHalls: 4, Upstairs: true),
        // in the bottom of a block of flats (Lausanne's Pathé Galeries): the facade's storeys stay
        new("under-flats", 26, 32, 20, BuildingKind.Apartment, true, Upstairs: true),
        // wider than deep: the halls run along X from the end nearer the door
        new("along-x", 44, 22, 8, BuildingKind.Commercial, true, Upstairs: true),
        // a kiosk: no hall fits, the ordinary rules furnish it
        new("too-small", 8, 10, 4, BuildingKind.Commercial, false),
    };

    public override void _Ready()
    {
        foreach (var c in Cases) Plan(c);
        Venues.Override = null;
        GD.Print($"[cinema] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private void Plan(Case c)
    {
        var id = new TileId(2600, 1200);
        var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, c.W, c.D, c.H, c.Kind) } };
        Venues.Override = new List<VenueRecord>
        {
            new("n1", VenueType.Cinema, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", $"Cinéma {c.Name}"),
        };
        string what = $"{c.Name} ({c.W}x{c.D} m, {c.H} m, {c.Kind})";
        Check(Venues.HallOf(BuildingTypes.For(tile), 0) == VenueType.Cinema, $"{what}: the solid is a cinema");

        InteriorLayout? l;
        try { l = InteriorGenerator.Generate(tile, 0, null, null); }
        catch (Exception e) { Check(false, $"{what}: planning threw {e.GetType().Name}: {e.Message}"); return; }
        if (l == null) { Check(false, $"{what}: an interior was planned"); return; }
        var problems = InteriorValidator.Validate(l);
        Check(problems.Count == 0, $"{what}: the plan validates{(problems.Count == 0 ? "" : ": " + string.Join("; ", problems.Take(6)))}");
        Write(c.Name, l);
        if (!c.Cinema)
        {
            Check(l.Type != BuildingType.Cinema, $"{what}: too small for a hall, planned by the ordinary rules ({l.Type})");
            return;
        }
        Check(l.Type == BuildingType.Cinema, $"{what}: the plan is a cinema ({l.Type})");
        Check(l.Group == $"Cinema{InteriorGenerator.CinemaRules}", $"{what}: fingerprinted with the cinema rules ({l.Group})");

        var halls = l.Floors[0].Rooms.Where(r => r.Type == RoomType.Auditorium).ToList();
        Check(halls.Count >= c.MinHalls, $"{what}: {halls.Count} auditorium(s)");
        int Count(FurnitureType t) => l.Furniture.Count(f => f.Type == t);
        Check(Count(FurnitureType.SeatingBank) == halls.Count, $"{what}: a seating bank in every hall ({Count(FurnitureType.SeatingBank)})");
        Check(Count(FurnitureType.MovieScreen) == halls.Count, $"{what}: a screen in every hall");
        // each porthole twice: on the hall's face of the wall and on the booth's (#1250)
        Check(Count(FurnitureType.Porthole) == 2 * halls.Count, $"{what}: a porthole in every hall, seen from the booth too ({Count(FurnitureType.Porthole)})");
        // real holes through the wall (#1250): each hall's open projection port and two dark viewing windows into the booth, matched on the booth's side
        var projRoom = l.Floors.SelectMany(fl => fl.Rooms).FirstOrDefault(r => r.Type == RoomType.ProjectionRoom);
        Check(halls.All(h => h.Openings.Count(o => o.Kind == OpeningKind.Window && o.Other >= 0 && o.Open) == 1
            && h.Openings.Count(o => o.Kind == OpeningKind.Window && o.Other >= 0 && !o.Open) == 2), $"{what}: every hall has its projection port and viewing windows");
        Check(projRoom != null && projRoom.Openings.Count(o => o.Kind == OpeningKind.Window && o.Other >= 0) == 3 * halls.Count,
            $"{what}: the booth has them all from its side ({projRoom?.Openings.Count(o => o.Kind == OpeningKind.Window && o.Other >= 0)})");
        // the beam over the back row's heads: every hall's porthole at least 2 m over its seating's top tread (#1250)
        var banks = l.Furniture.Where(f => f.Type == FurnitureType.SeatingBank).ToList();
        var hallPorts = l.Furniture.Where(f => f.Type == FurnitureType.Porthole && f.Floor == 0).ToList();
        Check(banks.All(b => hallPorts.Any(p => p.Lift + p.H / 2 - b.H >= 1.99f)),
            $"{what}: the projector's lens 2 m over the back row ({string.Join(", ", banks.Select(b => b.H.ToString("F2")))} m treads, {string.Join(", ", hallPorts.Select(p => (p.Lift + p.H / 2).ToString("F2")))} m lenses)");
        Check(Count(FurnitureType.ExitSign) >= halls.Count, $"{what}: exit signs over the doors");
        Check(Count(FurnitureType.Projector) == halls.Count, $"{what}: a projector for every hall ({Count(FurnitureType.Projector)})");
        Check(Count(FurnitureType.BoothPanel) == 1, $"{what}: the booth's control panel");
        Check(Count(FurnitureType.BoxOffice) == 1, $"{what}: a box office");
        Check(Count(FurnitureType.ConcessionStand) == 1, $"{what}: a concession stand");
        Check(l.Floors[0].Rooms.Any(r => r.Type == RoomType.WC), $"{what}: a WC");
        bool upstairs = l.Floors.Count > 1 && l.Floors[1].Rooms.Any(r => r.Type == RoomType.ProjectionRoom);
        Check(upstairs == c.Upstairs, $"{what}: the booth {(c.Upstairs ? "upstairs" : "at floor level")} ({l.Floors.Count} floor(s))");
        var booth = l.Floors.SelectMany(f => f.Rooms).FirstOrDefault(r => r.Type == RoomType.ProjectionRoom);
        Check(booth != null, $"{what}: a projection room");

        // the screen leaves the hall's door clear, and the seats face it: the bank's front is toward the screen
        foreach (var screen in l.Furniture.Where(f => f.Type == FurnitureType.MovieScreen))
        {
            var bank = l.Furniture.Where(f => f.Type == FurnitureType.SeatingBank)
                .MinBy(f => (f.X - screen.X) * (f.X - screen.X) + (f.Z - screen.Z) * (f.Z - screen.Z));
            if (bank == null) continue;
            Check((bank.Turns + 2) % 4 == screen.Turns, $"{what}: a bank faces its screen (turns {bank.Turns} / {screen.Turns})");
        }
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
            string dir = ProjectSettings.GlobalizePath("res://test_output/cinema");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.svg"), InteriorValidator.ToSvg(l));
        }
        catch (Exception e) { GD.Print($"[cinema] could not write the plan: {e.Message}"); }
    }
}
