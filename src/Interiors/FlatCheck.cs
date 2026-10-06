using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--flatcheck</c> (#557): apartment blocks end to end through the real code, without a world.
/// Synthetic box solids of the shapes a Swiss town has — a three-family house, a block with one
/// entrance, a long block with three, a tower, a deep block over a car park, a city block with
/// shops under its flats (turned, see <c>DoorCheck</c>) — go through
/// <see cref="BuildingFootprint.ComputeDoors"/> and <see cref="InteriorGenerator"/>, and this asks
/// what a visitor would: is there a stairwell behind every front door, an elevator in a block of
/// three levels or more, a bed, a kitchen and a bathroom in every flat, the same flats on every
/// floor, a shared basement with its laundry, shelter and storage, a car park under a big block?
/// Writes each plan as an SVG to <c>test_output/flats/</c>. Prints a RESULT line and exits 0 or 1.
/// </summary>
public static class FlatCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--flatcheck") >= 0;

    private sealed record Box(string What, BuildingKind Kind, float Width, float Depth, float Height,
        int Wells, bool CarPark = false, bool Shops = false, float Turn = 0f);

    private static readonly Box[] Boxes =
    [
        new("a three-family house", BuildingKind.Apartment, 13, 10, 9.5f, 1),
        new("a block with one entrance", BuildingKind.Apartment, 24, 12, 15, 1),
        new("a long block", BuildingKind.Apartment, 66, 13, 15, 3),
        new("a tower", BuildingKind.Apartment, 24, 22, 42, 1, CarPark: true),
        new("a deep block", BuildingKind.Apartment, 40, 26, 18, 1, CarPark: true),
        new("a narrow block", BuildingKind.Apartment, 9, 11, 12, 1),
        new("a big plain building", BuildingKind.Other, 30, 14, 15, 1),
        // shops under flats is a roll of the key (MixedUse): three, so the case is there
        new("a city block", BuildingKind.Commercial, 48, 16, 18, 1, Shops: true, Turn: 31f),
        new("another city block", BuildingKind.Commercial, 48, 16, 18, 1, Shops: true, Turn: 31f),
        new("a third city block", BuildingKind.Commercial, 48, 16, 18, 1, Shops: true, Turn: 31f),
    ];

    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[flatcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        string dir = ProjectSettings.GlobalizePath("res://test_output/flats");
        System.IO.Directory.CreateDirectory(dir);
        var tile = Tile();
        var doors = BuildingFootprint.ComputeDoors(tile, null, null);
        int flats = 0, locked = 0, lit = 0, livings = 0;

        for (int i = 0; i < Boxes.Length; i++)
        {
            var box = Boxes[i];
            // a commercial block is shops under flats only for some (the MixedUse dice): find one
            // whose key rolls it, so the case is always there
            var l = InteriorGenerator.Generate(tile, i, null, null);
            if (l == null) { Expect(false, $"{box.What}: no plan"); continue; }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{i}_{box.What.Replace(' ', '_')}.svg"), InteriorValidator.ToSvg(l));
            var problems = InteriorValidator.Validate(l);
            Expect(problems.Count == 0, $"{box.What}: the plan validates{(problems.Count > 0 ? " — " + string.Join("; ", problems.Take(4)) : "")}");
            if (box.Shops && l.Type != BuildingType.MixedUse)
            {
                GD.Print($"[flatcheck] {box.What}: not rolled as shops under flats ({l.Type}), skipped");
                continue;
            }
            Expect(l.Type == (box.Shops ? BuildingType.MixedUse : BuildingType.Apartments),
                $"{box.What}: planned as {(box.Shops ? "shops under flats" : "a block of flats")} ({l.Type})");
            if (l.Type is not (BuildingType.Apartments or BuildingType.MixedUse)) continue;

            var ground = l.GroundFloor;
            int above = l.Floors.Count - l.Below;
            // a stairwell is a flight stack: count the flights up from the bottom floor
            int wells = l.Floors.Count > 1 ? l.Floors[0].AllFlights().Count() : 1;
            Expect(wells == box.Wells, $"{box.What}: {wells} stairwell(s), wanted {box.Wells}");
            Expect(ground.Rooms.Count(r => r.Type == RoomType.Lobby && r.Openings.Any(o => o.Kind == OpeningKind.Entry)) >= wells,
                $"{box.What}: a street door into every stairwell's lobby");
            if (l.Floors.Count >= 3)
            {
                Expect(l.Lifts.Count == wells && wells > 0, $"{box.What}: {l.Lifts.Count} elevator(s) for {wells} stairwell(s)");
                foreach (var lift in l.Lifts)
                    Expect(lift.Bottom == 0 && lift.Top == l.Floors.Count - 1, $"{box.What}: an elevator from the bottom floor to the top");
            }
            if (l.Floors.Count > 1)
                Expect(l.Floors.Take(l.Floors.Count - 1).All(f => f.AllFlights().Count() == wells),
                    $"{box.What}: a flight up from every floor in every stairwell");

            // every flat: a bed, a kitchen, a bathroom, a hall at its front door
            for (int f = l.Below; f < l.Floors.Count; f++)
            {
                var floor = l.Floors[f];
                bool shopsHere = f == l.Below && box.Shops;
                foreach (var unit in floor.Rooms.Where(r => r.Unit >= 0).GroupBy(r => r.Unit))
                {
                    var types = unit.Select(r => r.Type).ToList();
                    if (shopsHere && types.Contains(RoomType.Shop)) continue;
                    flats++;
                    Expect(types.Contains(RoomType.Bedroom) && types.Contains(RoomType.Kitchen) && types.Contains(RoomType.Bathroom),
                        $"{box.What} floor {f} flat {unit.Key}: a bed, a kitchen and a bathroom ({string.Join(",", types)})");
                    var door = l.InnerDoors.FirstOrDefault(d => d.Floor == f && d.Unit == unit.Key);
                    Expect(door != null && floor.Rooms[door.Room].Type == RoomType.Hall,
                        $"{box.What} floor {f} flat {unit.Key}: its front door opens into its hall");
                    if (door?.Locked == true) locked++;
                    foreach (var r in unit.Where(r => r.Type == RoomType.Living))
                    {
                        livings++;
                        if (r.Openings.Any(o => o.Kind == OpeningKind.Window)) lit++;
                    }
                    Expect(l.Furniture.Any(p => p.Floor == f && (p.Type is FurnitureType.Bed or FurnitureType.SingleBed)
                            && unit.Any(r => p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1)),
                        $"{box.What} floor {f} flat {unit.Key}: a bed stands in it");
                }
            }
            // the floors of flats are one plan stacked
            int first = l.Below + (box.Shops ? 1 : 0);
            for (int f = first + 1; f < l.Floors.Count; f++)
                Expect(SamePlan(l.Floors[first], l.Floors[f]), $"{box.What}: floor {f} is floor {first}'s plan again");

            if (l.Below > 0)
            {
                var basement = l.Floors[0].Rooms.Select(r => r.Type).ToList();
                foreach (var t in l.Width * l.Depth >= 200 ? new[] { RoomType.Laundry, RoomType.Shelter, RoomType.Cellar } : new[] { RoomType.Laundry })
                    Expect(basement.Contains(t), $"{box.What}: the basement has a {t}");
                if (box.CarPark) Expect(basement.Contains(RoomType.CarPark), $"{box.What}: a car park in the basement");
                if (basement.Contains(RoomType.CarPark))
                    Expect(l.Furniture.Count(p => p.Floor == 0 && p.Type == FurnitureType.Car) > 0, $"{box.What}: cars parked in it");
            }
            else Expect(!box.CarPark, $"{box.What}: has a basement");
            if (box.Shops)
                Expect(ground.Rooms.Any(r => r.Type == RoomType.Shop), $"{box.What}: shops on the ground floor");

            var mine = doors.Where(d => d.Index == i && d.Width > 0).ToList();
            foreach (var d in mine)
                Expect(l.EntranceOf(d.KeyIn(tile.Id).ToString()) != null,
                    $"{box.What} slot {d.Slot}: the door arrives at a doorway inside");
            GD.Print($"[flatcheck] {box.What}: {l.Floors.Count} floor(s) ({l.Below} below), {wells} stairwell(s), "
                + $"{l.Lifts.Count} elevator(s), {l.Floors[^1].Rooms.Where(r => r.Unit >= 0).Select(r => r.Unit).Distinct().Count()} flat(s) on top, "
                + $"{mine.Count} door(s), {l.Furniture.Count} pieces");
        }
        Expect(flats > 0 && locked > 0 && locked < flats, $"{locked} of {flats} flats' front doors locked");
        Expect(livings > 0 && lit >= 0.85f * livings, $"{lit} of {livings} living rooms have a window");
        GD.Print($"[flatcheck] plans in {dir}");
        GD.Print($"[flatcheck] RESULT: {(failures == 0 ? "ok" : $"FAILED ({failures})")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The synthetic tile every box stands on, 150 m apart (also what <c>--flattour</c> walks).</summary>
    internal static BuildingTile Tile() => new()
    {
        Id = new TileId(2583, 1113),
        Buildings = Boxes.Select((b, i) => Solid(b, 150f * i + 60f)).ToList(),
    };

    /// <summary>Index of the box called <paramref name="what"/>.</summary>
    internal static int IndexOf(string what) => Array.FindIndex(Boxes, b => b.What == what);

    /// <summary>Same rooms in the same places, units and all.</summary>
    private static bool SamePlan(FloorPlan a, FloorPlan b)
    {
        var ra = a.Rooms.Where(r => r.Unit >= 0).ToList();
        var rb = b.Rooms.Where(r => r.Unit >= 0).ToList();
        return ra.Count == rb.Count && ra.Zip(rb).All(p => p.First.Type == p.Second.Type
            && Math.Abs(p.First.X0 - p.Second.X0) < 0.01f && Math.Abs(p.First.Z0 - p.Second.Z0) < 0.01f
            && Math.Abs(p.First.X1 - p.Second.X1) < 0.01f && Math.Abs(p.First.Z1 - p.Second.Z1) < 0.01f);
    }

    /// <summary>A plain box solid: four walls and a flat roof, at <paramref name="cz"/> south (as <c>DoorCheck</c>'s).</summary>
    private static Building Solid(Box box, float cz)
    {
        float hw = box.Width / 2, hd = box.Depth / 2, h = box.Height;
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) => tris.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
        void Wall(Vector3 p0, Vector3 p1)
        {
            Tri(p0, p1, p1 + Vector3.Up * h);
            Tri(p0, p1 + Vector3.Up * h, p0 + Vector3.Up * h);
        }
        float turn = Mathf.DegToRad(box.Turn);
        Vector3 Corner(float x, float z) =>
            new(500 + x * Mathf.Cos(turn) - z * Mathf.Sin(turn), 0, cz + x * Mathf.Sin(turn) + z * Mathf.Cos(turn));
        var nw = Corner(-hw, -hd);
        var ne = Corner(hw, -hd);
        var se = Corner(hw, hd);
        var sw = Corner(-hw, hd);
        Wall(nw, ne); Wall(ne, se); Wall(se, sw); Wall(sw, nw);
        Tri(nw + Vector3.Up * h, ne + Vector3.Up * h, se + Vector3.Up * h);
        Tri(nw + Vector3.Up * h, se + Vector3.Up * h, sw + Vector3.Up * h);
        return new Building
        {
            Kind = box.Kind, MinY = 0, MaxY = h,
            Floors = (byte)Math.Max(1, (int)(h / 3f)),
            Triangles = tris.ToArray(),
        };
    }
}
