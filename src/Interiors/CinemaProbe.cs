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
        bool Upstairs = false, float MinClear = 0, int MinService = 1,
        ToiletSet Toilets = ToiletSet.Shared, bool Accessible = false);

    /// <summary>A piece's floor rectangle, turned as it stands.</summary>
    private static RectPlan PieceRect(FurniturePlan f)
    {
        float hx = (f.Turns & 1) == 1 ? f.D / 2 : f.W / 2, hz = (f.Turns & 1) == 1 ? f.W / 2 : f.D / 2;
        return new RectPlan(f.X - hx, f.Z - hz, f.X + hx, f.Z + hz);
    }

    private static readonly Case[] Cases =
    {
        // a town cinema of its own, tall enough for the booth upstairs
        new("town", 18, 34, 9, BuildingKind.Commercial, true, Upstairs: true),
        // a small one on one level: the booth behind the top rows at floor level
        new("small", 14, 26, 5, BuildingKind.Commercial, true),
        // a multiplex: halls side by side
        new("multiplex", 72, 52, 12, BuildingKind.Commercial, true, MinHalls: 4, Upstairs: true, Toilets: ToiletSet.Gendered, Accessible: true),
        // in the bottom of a block of flats (Lausanne's Pathé Galeries): the facade's storeys stay
        new("under-flats", 26, 32, 20, BuildingKind.Apartment, true, Upstairs: true),
        // a multiplex too low for an upstairs booth: a long floor-level booth on its deck, the panel at its far end
        new("low-multiplex", 72, 40, 6, BuildingKind.Commercial, true, MinHalls: 4, Toilets: ToiletSet.Gendered),
        // Grütli's footprint too low for an upstairs booth: a 42 m booth whose platter and chair once shut off the panel (#1279)
        new("low-three", 34, 43, 6, BuildingKind.Civic, true, MinHalls: 2, Toilets: ToiletSet.Gendered),
        new("low-three-x", 43, 34, 6, BuildingKind.Civic, true, MinHalls: 2),
        // a tall civic building of low storeys (Geneva's Grütli, 28 m of 2.76 m storeys, #1279): halls three storeys high
        new("tall-civic", 34, 43, 28, BuildingKind.Civic, true, MinHalls: 2, Upstairs: true, MinClear: 6f, Toilets: ToiletSet.Gendered),
        // tall, with no storey grid (Geneva's Cinérama Empire, 32 m tagged a garage, #1279): halls at their own height
        new("tall-garage", 40, 60, 30, BuildingKind.Garage, true, MinHalls: 2, Upstairs: true, MinClear: 6f, Toilets: ToiletSet.Gendered, Accessible: true),
        // wider than deep: the halls run along X from the end nearer the door
        // (six rows only: the booth's floor 1.6 m over the top one, more than a short flight, so no service door)
        new("along-x", 44, 22, 8, BuildingKind.Commercial, true, Upstairs: true, MinService: 0),
        // a narrow town cinema: one hall, the toilets a single WC beside the lobby (#1285)
        new("narrow", 9.8f, 26, 5, BuildingKind.Commercial, true, Toilets: ToiletSet.Single),
        // a kiosk: no hall fits, the ordinary rules furnish it
        new("too-small", 8, 10, 4, BuildingKind.Commercial, false),
    };

    public override void _Ready()
    {
        foreach (var c in Cases) Plan(c);
        // some halls are mid-film when a cinema is first seen (#1279), from each hall's seed
        int showing = Enumerable.Range(0, 400).Count(i => CinemaBooths.Unpack(CinemaBooths.Default($"{2500 + i}_1117_{i % 37}", i % 7)).Screen);
        Check(showing > 400 * (CinemaBooths.ShowingShare - 0.08) && showing < 400 * (CinemaBooths.ShowingShare + 0.08),
            $"{showing} of 400 halls start mid-film, lights off ({CinemaBooths.ShowingShare:P0} asked)");
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
        // (a door-tall open one is a service doorway, #1279, counted below)
        static bool Port(OpeningPlan o) => o.Kind == OpeningKind.Window && o.Other >= 0 && o.Top - o.Bottom < 1f;
        static bool Service(OpeningPlan o) => o.Kind == OpeningKind.Window && o.Other >= 0 && o.Open && o.Top - o.Bottom >= 1.9f;
        Check(halls.All(h => h.Openings.Count(o => Port(o) && o.Open) == 1
            && h.Openings.Count(o => Port(o) && !o.Open) == 2), $"{what}: every hall has its projection port and viewing windows");
        Check(projRoom != null && projRoom.Openings.Count(Port) == 3 * halls.Count,
            $"{what}: the booth has them all from its side ({projRoom?.Openings.Count(Port)})");
        // a service doorway from each hall's top walkway into the booth, matched on the booth's side, the steps up to it
        // ending at its sill (#1279)
        int services = halls.Count(h => h.Openings.Any(Service));
        Check(services >= c.MinService, $"{what}: {services} of {halls.Count} halls have a service doorway into the booth (at least {c.MinService})");
        Check(projRoom != null && projRoom.Openings.Count(Service) == services, $"{what}: the booth has every service doorway from its side");
        foreach (var steps in l.Furniture.Where(f => f.Type == FurnitureType.ServiceSteps))
            Check(halls.SelectMany(h => h.Openings.Where(Service)).Any(o => Math.Abs(o.Bottom - (steps.Lift + steps.H)) < 0.02f),
                $"{what}: the service steps end at a doorway's sill ({steps.Lift + steps.H:F2} m)");
        var walkBanks = l.Furniture.Where(f => f.Type == FurnitureType.SeatingBank).ToList();
        Check(walkBanks.All(b => halls.Any(h => Math.Abs(Math.Max(Math.Abs(b.X - (h.X1 + h.X0) / 2), Math.Abs(b.Z - (h.Z1 + h.Z0) / 2))) < 50f
            && (Math.Abs(PieceRect(b).X1 - (h.X1 - InteriorGenerator.WallInset)) < 0.05f || Math.Abs(PieceRect(b).Z1 - (h.Z1 - InteriorGenerator.WallInset)) < 0.05f
                || Math.Abs(PieceRect(b).X0 - (h.X0 + InteriorGenerator.WallInset)) < 0.05f || Math.Abs(PieceRect(b).Z0 - (h.Z0 + InteriorGenerator.WallInset)) < 0.05f))),
            $"{what}: the seats' stairs run up to the walls");
        // the beam over the back row's heads: every hall's porthole at least 2 m over its seating's top tread (#1250)
        var banks = l.Furniture.Where(f => f.Type == FurnitureType.SeatingBank).ToList();
        var hallPorts = l.Furniture.Where(f => f.Type == FurnitureType.Porthole && f.Floor == 0).ToList();
        Check(banks.All(b => hallPorts.Any(p => p.Lift + p.H / 2 - b.H >= 1.99f)),
            $"{what}: the projector's lens 2 m over the back row ({string.Join(", ", banks.Select(b => b.H.ToString("F2")))} m treads, {string.Join(", ", hallPorts.Select(p => (p.Lift + p.H / 2).ToString("F2")))} m lenses)");
        // the projectionist sees through the windows standing up (#1250): on the floor upstairs, on a deck at floor level
        var deck = l.Furniture.FirstOrDefault(f => f.Type == FurnitureType.BoothDeck);
        var boothPorts = l.Furniture.Where(f => f.Type == FurnitureType.Porthole && f.Floor == l.Floors.Count - 1 && projRoom != null
            && l.Floors[f.Floor].Rooms.Contains(projRoom)).ToList();
        float eye = (deck?.H ?? 0) + InteriorGenerator.EyeHeight;
        float window = boothPorts.Count > 0 ? boothPorts[0].Lift + (InteriorGenerator.PortFoot + InteriorGenerator.PortHead) / 2 : float.NaN;
        Check(Math.Abs(eye - window) <= 0.35f, $"{what}: the booth's windows at a standing projectionist's eyes ({eye:F2} m eyes{(deck != null ? $" on a {deck.H:F2} m deck" : "")}, {window:F2} m windows)");
        Check(c.Upstairs == (deck == null), $"{what}: a deck in a floor-level booth only");
        if (deck != null)
            Check(l.Furniture.Where(f => f.Type is FurnitureType.Projector or FurnitureType.BoothPanel).All(f => Math.Abs(f.Lift - deck.H) < 0.01f),
                $"{what}: the projectors and the panels stand on the deck");
        Check(Count(FurnitureType.ExitSign) >= halls.Count, $"{what}: exit signs over the doors");
        Check(Count(FurnitureType.Projector) == halls.Count, $"{what}: a projector for every hall ({Count(FurnitureType.Projector)})");
        // a panel per hall, beside its projector (#1279), each its own hall's
        var panels = l.Furniture.Where(f => f.Type == FurnitureType.BoothPanel).ToList();
        Check(panels.Count == halls.Count, $"{what}: a control panel for every hall ({panels.Count})");
        Check(panels.Select(f => CinemaBooths.HallAt(l, f.X, f.Z)).Distinct().Count() == panels.Count && panels.All(f => CinemaBooths.HallAt(l, f.X, f.Z) >= 0),
            $"{what}: each panel works a hall of its own ({string.Join(", ", panels.Select(f => CinemaBooths.HallAt(l, f.X, f.Z)))})");
        Check(Count(FurnitureType.BoxOffice) == 1, $"{what}: a box office");
        Check(Count(FurnitureType.ConcessionStand) == 1, $"{what}: a concession stand");
        // the public toilets the footprint has room for (#1285)
        var loo = ToiletCheck.Problems(l, c.Toilets, c.Accessible);
        Check(loo.Count == 0, $"{what}: toilets {ToiletCheck.Describe(l)}{(loo.Count == 0 ? "" : ": " + string.Join("; ", loo))}");
        bool upstairs = l.Floors.Count > 1 && l.Floors[1].Rooms.Any(r => r.Type == RoomType.ProjectionRoom);
        Check(upstairs == c.Upstairs, $"{what}: the booth {(c.Upstairs ? "upstairs" : "at floor level")} ({l.Floors.Count} floor(s))");
        var booth = l.Floors.SelectMany(f => f.Rooms).FirstOrDefault(r => r.Type == RoomType.ProjectionRoom);
        Check(booth != null, $"{what}: a projection room");
        // the projectionist walks from the booth's door to every panel, a player's width (#1279: a platter and a chair shut them off)
        if (booth != null)
        {
            int bf = l.Floors.FindIndex(fl => fl.Rooms.Contains(booth));
            var pieces = l.Furniture.Where(f => f.Floor == bf && f.Type is not (FurnitureType.BoothDeck or FurnitureType.Porthole)
                && f.X > booth.X0 && f.X < booth.X1 && f.Z > booth.Z0 && f.Z < booth.Z1).Select(PieceRect).ToList();
            int reached = l.Furniture.Count(f => f.Type == FurnitureType.BoothPanel && InteriorGenerator.UseSpot(f, 0.5f) is var spot
                && InteriorGenerator.CanReach(booth, pieces, (spot.X0 + spot.X1) / 2, (spot.Z0 + spot.Z1) / 2, 0.3f));
            Check(reached == Count(FurnitureType.BoothPanel), $"{what}: every panel is reached from the booth's door ({reached} of {Count(FurnitureType.BoothPanel)}, {booth.Width:F1}x{booth.Depth:F1} m booth)");
        }
        // the seats folded at each row's back, a lane a player's width in front of them, every other row staggered (#1279)
        foreach (var b in l.Furniture.Where(f => f.Type == FurnitureType.SeatingBank))
        {
            var rows = InteriorGenerator.RowsOf(b);
            Check(rows.Pitch - InteriorGenerator.SeatDepth >= 0.66f, $"{what}: a {rows.Pitch - InteriorGenerator.SeatDepth:F2} m lane in front of the seats, a player's width");
            Check(rows.Rows < 2 || Math.Abs(rows.SeatX(1, 0) - rows.SeatX(0, 0) - rows.SeatW / 2) < 0.01f, $"{what}: every other row staggered half a seat");
        }
        // one way in (#1279): the extra doors of #498 landed in the halls' side walls
        Check(BuildingFootprint.ComputeDoors(tile, null, null).Count(d => d.Index == 0) == 1, $"{what}: one door, the entrance");
        // every hall's door from the lobby a real leaf, shut until someone opens it (#1279)
        Check(halls.All(h => l.InnerDoors.Any(d => d.Floor == 0 && l.Floors[0].Rooms[d.Room] == h && d.Bottom == 0
                && h.Openings.Any(o => o.Kind == OpeningKind.Door && o.Side == d.Side && Math.Abs(o.Center - d.Center) < 0.01f))),
            $"{what}: every hall's door from the lobby has a leaf");
        // a hall is a hall's height (#1279): in a building of storeys, two or three of them, never one
        if (c.MinClear > 0)
            Check(halls.All(h => l.ClearOf(h) >= c.MinClear), $"{what}: halls {string.Join(", ", halls.Select(h => l.ClearOf(h).ToString("F1")))} m clear, at least {c.MinClear} m");

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
