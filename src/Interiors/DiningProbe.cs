using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --diningcheck --systems ui</c> (#1242): restaurants, cafés, bars and
/// clubs in hand-made buildings: the ground-floor room each venue takes, its kitchen, what furnishes it,
/// the ways through it, the validator, the mesh, and the flats a block keeps above. SVG plans in
/// <c>test_output/dining/</c>.
/// </summary>
public partial class DiningProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--diningcheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[dining] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    /// <summary>A venue at (dx, dz) m from the building's centre, east and south.</summary>
    private sealed record Spot(VenueType Type, string Cuisine, float Dx = 0, float Dz = 0);

    private sealed record Case(string Name, float W, float D, float H, BuildingKind Kind, Spot[] Venues, RoomType[] Rooms,
        FurnitureType[] Pieces, bool FlatsAbove = false, ToiletSet Toilets = ToiletSet.None, bool Accessible = false);

    private static readonly Case[] Cases =
    {
        // a village Beiz: the house's living room is the Gaststube, its kitchen the kitchen, the family upstairs
        new("beiz", 10, 12, 7, BuildingKind.House, new[] { new Spot(VenueType.Restaurant, "regional") },
            new[] { RoomType.Gaststube }, new[] { FurnitureType.BarCounter }, Toilets: ToiletSet.Single),
        new("cafe", 8, 11, 4, BuildingKind.Commercial, new[] { new Spot(VenueType.Cafe, "") },
            new[] { RoomType.CafeRoom }, new[] { FurnitureType.PastryCase }, Toilets: ToiletSet.Single),
        new("pizzeria", 12, 16, 4, BuildingKind.Commercial, new[] { new Spot(VenueType.Restaurant, "pizza") },
            new[] { RoomType.Pizzeria }, new[] { FurnitureType.PizzaOven }, Toilets: ToiletSet.Single),
        new("bar", 14, 18, 4.5f, BuildingKind.Commercial, new[] { new Spot(VenueType.Bar, "") },
            new[] { RoomType.BarRoom }, new[] { FurnitureType.BarCounter, FurnitureType.BarStool }, Toilets: ToiletSet.Pair),
        new("club", 20, 24, 5, BuildingKind.Commercial, new[] { new Spot(VenueType.Nightclub, "") },
            new[] { RoomType.ClubRoom }, new[] { FurnitureType.DanceFloor, FurnitureType.DjBooth, FurnitureType.BarCounter }, Toilets: ToiletSet.Pair),
        new("kebab", 7, 9, 3.5f, BuildingKind.Commercial, new[] { new Spot(VenueType.FastFood, "kebab") },
            new[] { RoomType.KebabRoom }, new[] { FurnitureType.KebabCounter }, Toilets: ToiletSet.Shared),
        // a block of flats with a restaurant and a bar on its ground floor: shop units for them, flats above
        new("block", 26, 14, 16, BuildingKind.Apartment,
            new[] { new Spot(VenueType.Restaurant, "regional", -9, 0), new Spot(VenueType.Bar, "", 9, 0) },
            new[] { RoomType.Gaststube, RoomType.BarRoom }, new[] { FurnitureType.BarCounter }, FlatsAbove: true, Toilets: ToiletSet.Shared),
        // a restaurant in a commercial block's street unit, its front 7 bays (#1292): a women's and a men's WC, the big
        // gendered set needs more wall than its room has (the owner's call: compact washrooms where space is short)
        new("hall", 18, 24, 4.5f, BuildingKind.Commercial, new[] { new Spot(VenueType.Restaurant, "regional") },
            new[] { RoomType.Gaststube, RoomType.GastroKitchen }, new[] { FurnitureType.CookLine }, Toilets: ToiletSet.Pair),
        // #1292: a venue in a school, in an office block, in a block of flats with one door, each by its own street door
        new("school", 30, 14, 10, BuildingKind.Civic, new[] { new Spot(VenueType.Cafe, "") },
            new[] { RoomType.CafeRoom }, new[] { FurnitureType.PastryCase }, Toilets: ToiletSet.Single),
        new("office", 24, 14, 7, BuildingKind.Commercial, new[] { new Spot(VenueType.Restaurant, "regional") },
            new[] { RoomType.Gaststube }, new[] { FurnitureType.BarCounter }, Toilets: ToiletSet.Gendered, Accessible: true),
        new("flats-one-door", 12, 11, 10, BuildingKind.Apartment, new[] { new Spot(VenueType.Bar, "") },
            new[] { RoomType.BarRoom }, new[] { FurnitureType.BarCounter }, FlatsAbove: true, Toilets: ToiletSet.Shared),
        // a kiosk: no room big enough, the ordinary rules furnish it
        new("too-small", 3f, 3.5f, 3, BuildingKind.Commercial, new[] { new Spot(VenueType.Restaurant, "") },
            Array.Empty<RoomType>(), Array.Empty<FurnitureType>()),
    };

    public override void _Ready()
    {
        foreach (var c in Cases) Plan(c);
        Venues.Override = null;
        GD.Print($"[dining] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private void Plan(Case c)
    {
        var id = new TileId(2600, 1200);
        var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, c.W, c.D, c.H, c.Kind) } };
        Venues.Override = c.Venues.Select((v, i) => new VenueRecord($"n{i + 1}", v.Type, id.MinE + 500 + v.Dx, id.MaxN - 500 - v.Dz,
            VenueFix.Inside, VenueFlags.None, 0, v.Cuisine, $"{c.Name} {i}")).ToList();
        string what = $"{c.Name} ({c.W}x{c.D} m, {c.H} m, {c.Kind})";
        Check(BuildingTypes.For(tile).DiningOf(0).Count == c.Venues.Length, $"{what}: the solid holds its {c.Venues.Length} venue(s)");

        InteriorLayout? l;
        try { l = InteriorGenerator.Generate(tile, 0, null, null); }
        catch (Exception e) { Check(false, $"{what}: planning threw {e.GetType().Name}: {e.Message}"); return; }
        if (l == null) { Check(false, $"{what}: an interior was planned"); return; }
        var problems = InteriorValidator.Validate(l);
        Check(problems.Count == 0, $"{what}: the plan validates{(problems.Count == 0 ? "" : ": " + string.Join("; ", problems.Take(6)))}");
        Write(c.Name, l);

        var ground = l.GroundFloor.Rooms;
        var dining = ground.Where(r => InteriorGenerator.DiningRoom(r.Type)).ToList();
        if (c.Rooms.Length == 0)
        {
            Check(dining.Count == 0, $"{what}: too small for a venue, planned by the ordinary rules");
            return;
        }
        foreach (var t in c.Rooms)
            Check(ground.Any(r => r.Type == t), $"{what}: a {t} on the ground floor ({string.Join(", ", ground.Select(r => r.Type))})");
        foreach (var t in c.Pieces)
            Check(l.Furniture.Any(p => p.Type == t), $"{what}: a {t}");
        // the public toilets the venue has room for (#1285)
        var loo = ToiletCheck.Problems(l, c.Toilets, c.Accessible);
        Check(loo.Count == 0, $"{what}: toilets {ToiletCheck.Describe(l)}{(loo.Count == 0 ? "" : ": " + string.Join("; ", loo))}");
        int tables = l.Furniture.Count(p => p.Type is FurnitureType.TableSet or FurnitureType.BanquetteSet or FurnitureType.HighTable);
        Check(tables >= 1 || dining.All(r => r.Type == RoomType.ClubRoom), $"{what}: {tables} table(s)");
        Check(l.Furniture.All(p => p.Type != FurnitureType.ShopCounter || ground.Any(r => r.Type == RoomType.Shop)),
            $"{what}: no shop counter in a restaurant");
        if (ground.Any(r => r.Type == RoomType.GastroKitchen))
            Check(l.Furniture.Any(p => p.Type == FurnitureType.CookLine), $"{what}: the kitchen's cook line");
        // #1250: every venue that cooks has its kitchen, and no guest room is a hall
        if (dining.Any(r => r.Type is not (RoomType.BarRoom or RoomType.ClubRoom) && r.Area >= 24f))
            Check(ground.Any(r => r.Type == RoomType.GastroKitchen), $"{what}: a kitchen for a venue that cooks ({string.Join(", ", ground.Select(r => r.Type))})");
        Check(dining.Where(r => r.Type != RoomType.ClubRoom).All(r => r.Area <= 85.5f),
            $"{what}: no guest room over 85 m2 ({string.Join(", ", dining.Select(r => $"{r.Area:F0}"))})");

        // every doorway of a guest room is reached from its first among what stands there
        foreach (var r in dining)
        {
            var pieces = l.Furniture.Where(p => p.Floor == l.Below && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1
                    && p.Type is not (FurnitureType.DanceFloor or FurnitureType.DartBoard))
                .Select(p =>
                {
                    float ew = p.Turns % 2 == 0 ? p.W : p.D, ed = p.Turns % 2 == 0 ? p.D : p.W;
                    return new RectPlan(p.X - ew / 2, p.Z - ed / 2, p.X + ew / 2, p.Z + ed / 2);
                }).ToList();
            Check(InteriorGenerator.ShutDoorways(r, pieces) == 0, $"{what}: every doorway of the {r.Type} is reachable");
        }
        if (c.FlatsAbove)
            Check(l.Floors.Skip(l.Below + 1).Any(f => f.Rooms.Any(r => r.Type == RoomType.Living)), $"{what}: flats above");

        // #1292: each venue straight in from a street door of its own into its guest room, its windows at the front's bays
        var fp = BuildingFootprint.Compute(tile, 0, null, null)!;
        for (int i = 0; i < c.Venues.Length; i++)
        {
            var door = fp.Doors.FirstOrDefault(d => d.Venue == i + 1);
            if (door.Width <= 0 || !door.Unit) { Check(false, $"{what}: venue {i} has a door of its own"); continue; }
            var e = l.EntranceOf(new DoorKey(fp.Key, door.Slot).ToString());
            int? ri = e == null ? null : InteriorGenerator.EntryRoom(ground, e);
            Check(ri is int k && InteriorGenerator.DiningRoom(ground[k].Type),
                $"{what}: venue {i}'s door (slot {door.Slot}) opens into its guest room ({(ri is int k2 ? ground[k2].Type.ToString() : "nothing")})");
            if (ri is not int gi || !VenueFront.Has(door)) continue;
            var g = ground[gi];
            var entry = g.Openings.First(o => o.Kind == OpeningKind.Entry);
            var windows = g.Openings.Where(o => o.Kind == OpeningKind.Window && o.Side == entry.Side).ToList();
            var o2 = new Vector2(door.Outward.X, door.Outward.Z).Normalized();
            var foot = new Vector2(door.Position.X, door.Position.Z) - fp.Center;
            bool alongX = entry.Side is Side.Front or Side.Back;
            var bays = VenueFront.Bays(door).Select(b => (foot + new Vector2(o2.Y, -o2.X) * b.Center) is var q ? (alongX ? q.Dot(fp.AxisU) : q.Dot(fp.AxisV)) : 0).ToList();
            Check(windows.Count > 0 && windows.All(w => bays.Any(b => Math.Abs(b - w.Center) < 0.05f) && Math.Abs(w.Bottom - VenueFront.Sill) < 0.01f),
                $"{what}: venue {i}'s windows on its front wall stand at the front's bays ({windows.Count} of {bays.Count} bays, front {door.Span:F1} m)");
        }

        var mesh = InteriorMeshBuilder.Build(l);
        Check(mesh.Vertices.Length > 0 && mesh.Collision.Length > 0, $"{what}: meshes ({mesh.Vertices.Length} vertices) with collision");
    }

    private static void Write(string name, InteriorLayout l)
    {
        try
        {
            string dir = ProjectSettings.GlobalizePath("res://test_output/dining");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.svg"), InteriorValidator.ToSvg(l));
        }
        catch (Exception e) { GD.Print($"[dining] could not write the plan: {e.Message}"); }
    }
}
