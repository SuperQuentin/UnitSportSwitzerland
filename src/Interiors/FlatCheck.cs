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

    /// <param name="Parts">
    /// A shaped building (#577): the rectangles of its outline, (x0, z0, x1, z1) round its centre;
    /// null for a plain box of <see cref="Width"/> by <see cref="Depth"/>.
    /// </param>
    private sealed record Box(string What, BuildingKind Kind, float Width, float Depth, float Height,
        int Wells, bool CarPark = false, bool Shops = false, float Turn = 0f, float[][]? Parts = null);

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
        // shaped blocks (#577): the inside follows the outline, wing by wing
        new("an L-shaped block", BuildingKind.Apartment, 30, 26, 15, -1,
            Parts: [[-15, -13, 15, -1], [3, -1, 15, 13]]),
        new("a U-shaped block", BuildingKind.Apartment, 36, 28, 15, -1,
            Parts: [[-18, -14, 18, -2], [-18, -2, -6, 14], [6, -2, 18, 14]]),
        new("a courtyard block", BuildingKind.Apartment, 40, 40, 18, -1, Turn: 12f,
            Parts: [[-20, -20, 20, -8], [-20, 8, 20, 20], [-20, -8, -8, 8], [8, -8, 20, 8]]),
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
        int flats = 0, locked = 0, lit = 0, livings = 0, wetLit = 0, wet = 0, deadEnds = 0;
        var walkedThrough = new List<string>();
        var dark = new List<string>();
        var blockedRooms = new List<string>();
        var turnedAway = new List<string>();
        int tvs = 0, tvsFacing = 0;

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
            // a stairwell is a flight stack: count the flights up from the bottom floor's own level
            int wells = l.Floors.Count > 1 ? l.Floors[0].AllFlights().Count(x => x.From <= 0.001f) : 1;
            Expect(box.Wells < 0 || wells == box.Wells, $"{box.What}: {wells} stairwell(s), wanted {box.Wells}");
            if (box.Parts != null)
            {
                // the inside follows the outline: every room within one of its parts, and the
                // courtyard or the yard empty (#577)
                // a plan point to the world (the plan is turned to face its main door) and into the
                // parts' own frame (Shaped)
                float yaw = l.Yaw, turn = Mathf.DegToRad(box.Turn), cz = 150f * i + 60f;
                bool Inside(float x, float z)
                {
                    float wx = l.CenterX + Mathf.Cos(yaw) * x + Mathf.Sin(yaw) * z - 500;
                    float wz = l.CenterZ - Mathf.Sin(yaw) * x + Mathf.Cos(yaw) * z - cz;
                    float px = wx * Mathf.Cos(turn) + wz * Mathf.Sin(turn), pz = -wx * Mathf.Sin(turn) + wz * Mathf.Cos(turn);
                    return box.Parts.Any(q => px >= q[0] - 0.6f && px <= q[2] + 0.6f && pz >= q[1] - 0.6f && pz <= q[3] + 0.6f);
                }
                var plan = l.Floors.SelectMany(f => f.Rooms).ToList();
                int outside = plan.Count(r => !Inside((r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2));
                Expect(outside == 0, $"{box.What}: every room inside the outline ({outside} outside)");
                float roomArea = l.GroundFloor.Rooms.Sum(r => r.Area), outline = box.Parts.Sum(q => (q[2] - q[0]) * (q[3] - q[1]));
                Expect(roomArea <= outline * 1.02f && roomArea >= outline * 0.6f,
                    $"{box.What}: the ground floor is {roomArea:F0} m² of a {outline:F0} m² outline (the box is {box.Width * box.Depth:F0} m²)");
            }
            Expect(ground.Rooms.Count(r => r.Type == RoomType.Lobby && r.Openings.Any(o => o.Kind == OpeningKind.Entry)) >= wells,
                $"{box.What}: a street door into every stairwell's lobby");
            if (l.Floors.Count >= 3)
            {
                Expect(l.Lifts.Count == wells && wells > 0, $"{box.What}: {l.Lifts.Count} elevator(s) for {wells} stairwell(s)");
                foreach (var lift in l.Lifts)
                    Expect(lift.Bottom == 0 && lift.Top == l.Floors.Count - 1, $"{box.What}: an elevator from the bottom floor to the top");
            }
            if (l.Floors.Count > 1)
                Expect(l.Floors.Take(l.Floors.Count - 1).All(f => f.AllFlights().Count(x => x.From <= 0.001f) == wells
                        && f.AllFlights().Count(x => x.Half) == 2 * wells && f.Landings.Count == wells),
                    $"{box.What}: two flights round a half landing up from every floor in every stairwell");

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
                    // daylight (#571): every room people live in has a window, a wet room only if it is on a facade
                    foreach (var r in unit)
                    {
                        // and a bedroom, a bathroom or a WC is a dead end: one door (#576)
                        if (r.Type is RoomType.Bedroom or RoomType.Bathroom or RoomType.WC)
                        {
                            deadEnds++;
                            int ways = r.Openings.Count(o => o.Kind is OpeningKind.Door or OpeningKind.Arch);
                            if (ways != 1) walkedThrough.Add($"{box.What} floor {f} flat {unit.Key} {r.Type} ({ways} doors)");
                        }
                        bool window = r.Openings.Any(o => o.Kind == OpeningKind.Window);
                        if (r.Type is RoomType.Living or RoomType.Bedroom)
                        {
                            livings++;
                            if (window) lit++;
                            else dark.Add($"{box.What} floor {f} flat {unit.Key} {r.Type}");
                        }
                        else if (r.Type is RoomType.Kitchen or RoomType.Bathroom or RoomType.WC)
                        {
                            wet++;
                            if (window) wetLit++;
                        }
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

            // furniture leaves a way through (#680): from a room's first doorway, every other is reachable
            for (int f = 0; f < l.Floors.Count; f++)
                foreach (var r in l.Floors[f].Rooms)
                {
                    var pieces = l.Furniture.Where(p => p.Floor == f && p.H > 0.05f && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1)
                        .Select(p => p.Turns % 2 == 0
                            ? new RectPlan(p.X - p.W / 2, p.Z - p.D / 2, p.X + p.W / 2, p.Z + p.D / 2)
                            : new RectPlan(p.X - p.D / 2, p.Z - p.W / 2, p.X + p.D / 2, p.Z + p.W / 2)).ToList();
                    if (InteriorGenerator.ShutDoorways(r, pieces) > 0) blockedRooms.Add($"{box.What} floor {f} {r.Type} {r.X0:F1},{r.Z0:F1}");
                    // and no corner of it is walled off by a cage or a cupboard (a 1.5 m² pocket at most)
                    if (InteriorGenerator.SealedFloor(r, pieces) > 1.5f) blockedRooms.Add($"{box.What} floor {f} {r.Type} {r.X0:F1},{r.Z0:F1}: {InteriorGenerator.SealedFloor(r, pieces):F1} m² sealed off");
                    // a TV looks at its sofa
                    if (r.Type != RoomType.Living) continue;
                    foreach (var tv in l.Furniture.Where(p => p.Floor == f && p.Type == FurnitureType.Tv && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1))
                    {
                        var sofa = l.Furniture.FirstOrDefault(p => p.Floor == f && p.Type == FurnitureType.Sofa && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1);
                        if (sofa == null) continue;
                        tvs++;
                        var (fx, fz) = (tv.Turns & 3) switch { 0 => (0f, 1f), 1 => (1f, 0f), 2 => (0f, -1f), _ => (-1f, 0f) };
                        if (fx * (sofa.X - tv.X) + fz * (sofa.Z - tv.Z) > -0.5f) tvsFacing++;
                        else turnedAway.Add($"{box.What} floor {f} room {r.X0:F1},{r.Z0:F1}-{r.X1:F1},{r.Z1:F1} sofa {sofa.X:F1},{sofa.Z:F1} t{sofa.Turns} tv {tv.X:F1},{tv.Z:F1} t{tv.Turns}");
                    }
                }

            var mine = doors.Where(d => d.Index == i && d.Width > 0).ToList();
            foreach (var d in mine)
                Expect(l.EntranceOf(d.KeyIn(tile.Id).ToString()) != null,
                    $"{box.What} slot {d.Slot}: the door arrives at a doorway inside");
            GD.Print($"[flatcheck] {box.What}: {l.Floors.Count} floor(s) ({l.Below} below), {wells} stairwell(s), "
                + $"{l.Lifts.Count} elevator(s), {l.Floors[^1].Rooms.Where(r => r.Unit >= 0).Select(r => r.Unit).Distinct().Count()} flat(s) on top, "
                + $"{mine.Count} door(s), {l.Furniture.Count} pieces");
        }
        Expect(flats > 0 && locked > 0 && locked < flats, $"{locked} of {flats} flats' front doors locked");
        Expect(livings > 0 && lit == livings, $"{lit} of {livings} living rooms and bedrooms have a window"
            + (dark.Count > 0 ? ": dark " + string.Join("; ", dark.Distinct().Take(6)) : ""));
        GD.Print($"[flatcheck] {wetLit} of {wet} kitchens, bathrooms and WCs have one (those on a facade)");
        Expect(walkedThrough.Count == 0, $"{deadEnds - walkedThrough.Count} of {deadEnds} bedrooms, bathrooms and WCs have one door"
            + (walkedThrough.Count > 0 ? ": " + string.Join("; ", walkedThrough.Distinct().Take(6)) : ""));
        Expect(blockedRooms.Count == 0, "no room has a doorway or a corner shut off by its furniture" + (blockedRooms.Count > 0 ? ": " + string.Join("; ", blockedRooms.Take(6)) : ""));
        Expect(tvs == 0 || tvsFacing == tvs, $"{tvsFacing} of {tvs} TVs face their sofa" + (turnedAway.Count > 0 ? ": " + string.Join("; ", turnedAway.Distinct().Take(8)) : ""));
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

    /// <summary>
    /// A building of several rectangles (#577): walls only where the outline runs (an edge between
    /// a covered and an open 0.5 m cell, run together), a flat roof over each part, nothing over a
    /// courtyard. Centred on the parts' own origin, turned like <see cref="Solid"/>.
    /// </summary>
    private static Building Shaped(Box box, float cz)
    {
        const float cell = 0.5f;
        float h = box.Height;
        float minX = box.Parts!.Min(q => q[0]), minZ = box.Parts.Min(q => q[1]);
        float maxX = box.Parts.Max(q => q[2]), maxZ = box.Parts.Max(q => q[3]);
        int nx = (int)MathF.Round((maxX - minX) / cell), nz = (int)MathF.Round((maxZ - minZ) / cell);
        bool In(int i, int j) => i >= 0 && j >= 0 && i < nx && j < nz
            && box.Parts.Any(q => minX + (i + 0.5f) * cell > q[0] && minX + (i + 0.5f) * cell < q[2]
                && minZ + (j + 0.5f) * cell > q[1] && minZ + (j + 0.5f) * cell < q[3]);
        var tris = new List<float>();
        float turn = Mathf.DegToRad(box.Turn);
        Vector3 P(float x, float z) => new(500 + x * Mathf.Cos(turn) - z * Mathf.Sin(turn), 0, cz + x * Mathf.Sin(turn) + z * Mathf.Cos(turn));
        void Tri(Vector3 a, Vector3 b, Vector3 c) => tris.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
        void Wall(Vector3 p0, Vector3 p1)
        {
            Tri(p0, p1, p1 + Vector3.Up * h);
            Tri(p0, p1 + Vector3.Up * h, p0 + Vector3.Up * h);
        }
        // runs of outline along z (walls facing x), then along x (walls facing z)
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j < nz;)
            {
                if (In(i - 1, j) == In(i, j)) { j++; continue; }
                bool side = In(i - 1, j);
                int j0 = j;
                while (j < nz && In(i - 1, j) != In(i, j) && In(i - 1, j) == side) j++;
                float x = minX + i * cell;
                Wall(P(x, minZ + j0 * cell), P(x, minZ + j * cell));
            }
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i < nx;)
            {
                if (In(i, j - 1) == In(i, j)) { i++; continue; }
                bool side = In(i, j - 1);
                int i0 = i;
                while (i < nx && In(i, j - 1) != In(i, j) && In(i, j - 1) == side) i++;
                float z = minZ + j * cell;
                Wall(P(minX + i0 * cell, z), P(minX + i * cell, z));
            }
        foreach (var q in box.Parts)
        {
            Vector3 R(float x, float z) => P(x, z) + Vector3.Up * h;
            Tri(R(q[0], q[1]), R(q[2], q[1]), R(q[2], q[3]));
            Tri(R(q[0], q[1]), R(q[2], q[3]), R(q[0], q[3]));
        }
        return new Building
        {
            Kind = box.Kind, MinY = 0, MaxY = h,
            Floors = (byte)Math.Max(1, (int)(h / 3f)),
            Triangles = tris.ToArray(),
        };
    }

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
        if (box.Parts != null) return Shaped(box, cz);
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
