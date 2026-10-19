using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// <c>--constructioncheck</c> (#607): the generated world's building sites end to end, without a
/// world. The generator plans the villages round the default spawn and builds the tiles their sites
/// stand in, and each site goes through <see cref="SitePlans.For"/> with the tile's own roads, the
/// way a client and the server will. Asks: is it planned at the phase the generator meant, does it
/// face its street, does nothing of it stand on a road or in a building, does it have its office, a
/// machine and, where it has cranes, cranes that reach every corner. All three phases must be built
/// at least once. Writes the plan views to <c>test_output/construction/generated.svg</c>. Prints a
/// RESULT line, exits 0 or 1. The later issues of #605 extend it with what they draw.
/// </summary>
public static class ConstructionCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--constructioncheck") >= 0;

    /// <summary>How far round the default spawn villages are looked at, m.</summary>
    private const double Radius = 12000;
    /// <summary>At most this many sites of each phase are checked, nearest the spawn first.</summary>
    private const int PerPhase = 3;

    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[constructioncheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        string dir = ProjectSettings.GlobalizePath("res://test_output/construction");
        System.IO.Directory.CreateDirectory(dir);
        double e0 = SpawnPoint.DefaultLv95E, n0 = SpawnPoint.DefaultLv95N;
        var world = new ProceduralWorld(e0, n0);
        var planned = world.SitesNear(e0, n0, Radius).ToList();
        GD.Print($"[constructioncheck] {planned.Count} building sites planned within {Radius / 1000:F0} km: "
            + string.Join(", ", planned.GroupBy(p => (SitePhase)p.Phase).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")));

        var picks = planned.GroupBy(p => p.Phase)
            .SelectMany(g => g.OrderBy(p => Sq(p.E - e0) + Sq(p.N - n0)).Take(PerPhase)).ToList();
        var tiles = new Dictionary<TileId, (BuildingTile? Tile, RoadTile? Roads, List<ConstructionSite> Sites)>();
        var seen = new HashSet<SitePhase>();
        var drawn = new List<SitePlanSvg.Entry>();
        foreach (var p in picks)
        {
            var id = TileId.FromLv95(p.E, p.N);
            if (!tiles.TryGetValue(id, out var t))
            {
                var bt = world.BuildBuildings(id);
                var roads = world.BuildRoads(id);
                t = (bt, roads, bt == null ? new() : SitePlans.For(bt, roads));
                tiles[id] = t;
            }
            var want = (SitePhase)p.Phase;
            string what = $"a {want} site at {p.E:F0},{p.N:F0}";
            // the planned centre in the tile's frame (X east, Z south)
            var at = new Vector2((float)(p.E - id.MinE), (float)(id.MaxN - p.N));
            var site = t.Sites.FirstOrDefault(s => s.Box.Center.DistanceTo(at) < 1f);
            if (site == null || t.Tile == null)
            {
                // its solid turned it down (too steep, on a road): not a failure by itself, but every
                // phase must stand somewhere
                GD.Print($"[constructioncheck] {what}: not built");
                continue;
            }
            seen.Add(site.Phase);
            int index = int.Parse(site.Key.Split('_')[2]);
            var map = BuildingTypes.For(t.Tile);

            // the phase read off the solid is the one it was planned at; the solid starts at its
            // lowest corner, so on a slope a site may read one phase further on
            double low = double.MaxValue, high = double.MinValue;
            foreach (var corner in site.Box.Corners())
            {
                double g = world.Height(id.MinE + corner.X, id.MaxN - corner.Y);
                low = Math.Min(low, g);
                high = Math.Max(high, g);
            }
            double fall = high - low;
            Expect(site.Phase == want || site.Phase == want + 1 && fall > 1.0,
                $"{what}: planned as {site.Phase}, {site.BuiltStoreys}/{site.TargetStoreys} storeys, measured {site.Measured:F1} m on ground at {low:F1}-{high:F1} m");
            Expect(site.TargetStoreys == p.Floors || site.Phase == SitePhase.ToppedOut && site.TargetStoreys >= p.Floors,
                $"{what}: {site.TargetStoreys} storeys to come, GWR-style floors {p.Floors}");

            // it faces its street
            var street = BuildingFootprint.StreetNear(t.Roads, site.Box.Center);
            Expect(street is { } s0 && (s0 - site.Box.Center).Dot(site.Front) > 0,
                $"{what}: faces its street (front {site.Front}, street {street})");

            // nothing on a road or in a building
            var obstacles = new SiteObstacles(map.Boxes, index, t.Roads);
            int on = Points(site).Count(obstacles.Blocked);
            Expect(on == 0, $"{what}: {on} point(s) of the site on a road or in a building");
            Expect(site.Hoarding.Count > 0 && site.Hoarding.Sum(r => r.A.DistanceTo(r.B)) > site.Area.Width,
                $"{what}: hoarding {site.Hoarding.Sum(r => r.A.DistanceTo(r.B)):F0} m in {site.Hoarding.Count} run(s)");
            Expect(site.Zones.Any(z => z.Kind == SiteZoneKind.Office), $"{what}: has its site office");
            Expect(site.Machines.Count > 0, $"{what}: {site.Machines.Count} machine slot(s): {string.Join(", ", site.Machines.Select(m => m.Role))}");
            // the pallets of bricks and cement a machine with tines can lift (#615): not a failure when none fit
            int pallets = SiteShellBuilder.Dressing(t.Tile, site, null).Pallets.Count;
            GD.Print($"[constructioncheck]      {what}: {pallets} pallet(s) of materials, {(ConstructionSites.MaterialsWayIn(site) ? "a way in for a machine" : "no way in for a machine")}");
            Office(t.Tile, t.Roads, t.Sites, site, index, world.BuildGrid(id, 1), what, Expect);
            foreach (var corner in site.Box.Corners())
                if (site.Cranes.Count > 0 && !site.Cranes.Any(k => k.Base.DistanceTo(corner) <= k.JibLength))
                    Expect(false, $"{what}: no crane reaches the corner at {corner}");

            // the pit (#619): dug at the foundations and under a low shell, inside its tile, nothing of
            // the yard in it, a ramp down to its floor in its lane, the ground cut round it
            bool wantsPit = site.Phase == SitePhase.Foundations || site.Phase == SitePhase.Shell && site.BuiltStoreys <= ConstructionSites.PitOpenUpTo;
            if (wantsPit) Expect(site.Pit != null, $"{what}: digs a pit");
            if (site.Pit is { } pit)
            {
                var grid = world.BuildGrid(id, 1);
                var (shell, dig) = SiteShellBuilder.Dig(t.Tile, site, grid);
                var area = ConstructionSites.InPlan(site.Box, pit.Area);
                int inPit = site.Zones.Count(z => z.Rect.Overlaps(area)) + site.Machines.Count(m => area.Contains(m.At))
                    + site.Cranes.Count(k => !k.Inside && area.Contains(k.Base));
                Expect(inPit == 0, $"{what}: {inPit} thing(s) of the yard in the pit");
                Expect(dig != null && dig.Grade <= PitPlans.MaxGrade + 1e-3f && dig.RampLength < pit.LaneLength,
                    $"{what}: pit {pit.Area.Width:F1} x {pit.Area.Depth:F1} m, {pit.Basements} basement(s), floor {dig?.Floor:F1} m, "
                    + $"ramp {dig?.RampLength:F1} m at {100 * dig?.Grade:F0} % in a {pit.LaneLength:F1} m lane, {shell.Levels.Count - 1} storey(s) built");
                if (grid != null && dig != null)
                {
                    // the ground's cut: no ground left in the pit, every quad round it put back
                    var cut = SiteShellBuilder.CutOf(site, dig);
                    var holes = PitTerrain.Holes(new[] { cut });
                    var faces = PitTerrain.CollisionFaces(new[] { cut }, holes, (c, r) => (float)grid.HeightMetersAt(c, r));
                    Expect(holes.Count >= (int)(area.Width * area.Depth) && faces.Count > 0,
                        $"{what}: {holes.Count} quad(s) cut for {area.Width * area.Depth:F0} m², {faces.Count / 3} collision triangles round and in it");
                }
            }

            drawn.Add(new SitePlanSvg.Entry(site, t.Roads, map.Boxes, $"{site.Key}"));
        }

        foreach (var phase in new[] { SitePhase.Foundations, SitePhase.Shell, SitePhase.ToppedOut })
            Expect(seen.Contains(phase), $"a {phase} site is built round the spawn");

        string svg = System.IO.Path.Combine(dir, "generated.svg");
        System.IO.File.WriteAllText(svg, SitePlanSvg.Draw(drawn));
        GD.Print($"[constructioncheck] plan views: {svg}");
        GD.Print($"[constructioncheck] RESULT: {(failures == 0 ? "ok" : $"FAILED ({failures})")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The site office (#822): a door per ground-floor container among the tile's doors, on the
    /// container's door; an interior plan behind them that is valid, has an entrance at every door
    /// lined up with it, holds something to search, and is planned the same twice.
    /// </summary>
    private static void Office(BuildingTile tile, RoadTile? roads, List<ConstructionSite> sites, ConstructionSite site, int index,
        ChunkGrid grid, string what, Action<bool, string> expect)
    {
        var office = SiteShellBuilder.Office(tile, site, grid);
        if (office == null) { expect(false, $"{what}: no office to enter"); return; }
        var doors = BuildingFootprint.ComputeDoors(tile, roads, grid, sites).Where(d => d.Index == index && d.Width > 0).ToList();
        expect(doors.Count == office.Doors.Count && doors.Select(d => d.Slot).SequenceEqual(Enumerable.Range(0, doors.Count)),
            $"{what}: {doors.Count} door(s) among the tile's doors, one per ground-floor container ({office.ContainerDoors}) and toilet cabin ({office.Toilets.Count})");
        expect(doors.All(d => d.Kind == BuildingKind.UnderConstruction && !d.Vehicle
                && d.Hang == (d.Slot < office.ContainerDoors ? DoorHang.Inward : DoorHang.Outward)),
            $"{what}: a building site's plain doors, the containers' swinging in and the cabins' out");
        // the tile's doors without the planned sites handed in: the same doors
        var alone = BuildingFootprint.ComputeDoors(tile, roads, grid).Where(d => d.Index == index && d.Width > 0).ToList();
        expect(alone.SequenceEqual(doors), $"{what}: the doors are the same planned with the tile or on their own");
        for (int i = 0; i < Math.Min(doors.Count, office.Doors.Count); i++)
        {
            var spot = doors[i];
            var sill = SiteShellBuilder.ToTile(site, office.Doors[i].Sill);
            expect(Math.Abs(spot.Position.Y - office.Doors[i].Sill.Y) < 0.001f
                && new Vector2(spot.Position.X - spot.Outward.X * 0.03f - sill.X, spot.Position.Z - spot.Outward.Z * 0.03f - sill.Z).Length() < 0.001f,
                $"{what}: door {i} on its {(i < office.ContainerDoors ? "container" : "cabin")}'s door, its sill at {spot.Position.Y:F2} m (its ground {office.Doors[i].Sill.Y:F2}), "
                + $"--at {tile.Id.MinE + spot.Position.X:F1},{tile.Id.MaxN - spot.Position.Z:F1} facing E {spot.Outward.X:F2} N {-spot.Outward.Z:F2}");
        }

        // a way in: nothing of the yard stands in the 0.6 m in front of a door
        var yard = SiteShellBuilder.Dressing(tile, site, grid);
        for (int i = 0; i < office.Doors.Count; i++)
        {
            var d = office.Doors[i];
            var o = new Vector3(d.Outward.X, 0, d.Outward.Y);
            var across = new Vector3(-d.Outward.Y, 0, d.Outward.X);
            bool clear = true;
            foreach (float step in new[] { 0.1f, 0.35f, 0.6f })
                foreach (float side in new[] { -d.Width / 2 + 0.1f, 0f, d.Width / 2 - 0.1f })
                {
                    var p = d.Sill + o * step + across * side + Vector3.Up * 1f;
                    clear &= !yard.Boxes.Any(b => b.Solid && p.X > b.Min.X && p.X < b.Max.X && p.Y > b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z);
                }
            expect(clear, $"{what}: nothing of the yard stands in front of door {i}");
        }

        var plan = InteriorGenerator.Generate(tile, index, roads, grid);
        if (plan == null) { expect(false, $"{what}: no interior plan behind the office"); return; }
        var problems = InteriorValidator.Validate(plan);
        expect(problems.Count == 0, $"{what}: the office's plan is valid{(problems.Count > 0 ? ": " + string.Join("; ", problems.Take(3)) : "")}");
        expect(plan.Kind == BuildingKind.UnderConstruction && plan.Floors.Count == 1
            && plan.GroundFloor.Rooms.Count == office.Rooms.Count + office.Toilets.Count && plan.GroundFloor.Rooms[0].Type == RoomType.SiteOffice
            && plan.GroundFloor.Rooms.Skip(office.Rooms.Count).All(r => r.Type == RoomType.WC && r.Openings.Count(o => o.Kind == OpeningKind.Entry) == 1 && r.Openings.Count == 1),
            $"{what}: {plan.GroundFloor.Rooms.Count} room(s): {string.Join(", ", plan.GroundFloor.Rooms.Select(r => r.Type))}");
        var placement = new Transform3D(new Basis(Vector3.Up, plan.Yaw), new Vector3(plan.CenterX, 0, plan.CenterZ));
        foreach (var spot in doors)
        {
            var key = spot.KeyIn(tile.Id).ToString();
            var e = plan.EntranceOf(key);
            if (e == null) { expect(false, $"{what}: no entrance behind door {key}"); continue; }
            // the doorway inside is where the door is: the facade plane (3 cm in from the spot) is the
            // trim's face, 2 cm in front of the container's end, which is the plan box's front wall
            var inside = placement * new Vector3(e.X, 0, e.Z);
            var plane = new Vector2(spot.Position.X, spot.Position.Z) - new Vector2(spot.Outward.X, spot.Outward.Z) * (0.03f + SiteOffices.Trim);
            var outward = placement.Basis * new Vector3(-e.InX, 0, -e.InZ);
            expect(new Vector2(inside.X, inside.Z).DistanceTo(plane) < 0.01f && outward.Dot(spot.Outward) > 0.999f
                && Math.Abs(e.DoorX - spot.Position.X) < 0.001f && Math.Abs(e.DoorY - spot.Position.Y) < 0.001f && Math.Abs(e.DoorZ - spot.Position.Z) < 0.001f,
                $"{what}: door {key}'s doorway inside lines up with it ({new Vector2(inside.X, inside.Z).DistanceTo(plane) * 100:F1} cm)");
            var (w, top) = plan.OpeningOf(e);
            expect(Math.Abs(w - spot.Width) < 0.01f && Math.Abs(top - spot.Height) < 0.01f, $"{what}: door {key} opens on a {w:F2} x {top:F2} m doorway ({spot.Width:F2} x {spot.Height:F2} outside)");
        }
        // a cabin holds its seat, urinal, paper, mirror and hook
        foreach (var cabin in plan.GroundFloor.Rooms.Where(r => r.Type == RoomType.WC))
        {
            var inside = plan.Furniture.Where(f => f.X > cabin.X0 && f.X < cabin.X1 && f.Z > cabin.Z0 && f.Z < cabin.Z1).Select(f => f.Type).ToList();
            expect(new[] { FurnitureType.Toilet, FurnitureType.Urinal, FurnitureType.PaperRoll, FurnitureType.Mirror, FurnitureType.CoatHook }.All(inside.Contains),
                $"{what}: a toilet cabin holds {string.Join(", ", inside)}");
        }
        int lootable = plan.Furniture.Count(f => Loot.LootTables.IsLootable(f.Type));
        expect(lootable > 0 && plan.Furniture.All(f => f.Floor == 0),
            $"{what}: {plan.Furniture.Count} piece(s), {lootable} to search: {string.Join(", ", plan.Furniture.Select(f => f.Type))}");
        var again = InteriorGenerator.Generate(tile, index, roads, grid);
        expect(again != null && again.ToJson() == plan.ToJson(), $"{what}: the office is planned the same twice");
    }

    /// <summary>Every point a site puts something on: hoarding ends and middles, zone and machine corners, crane bases outside.</summary>
    private static IEnumerable<Vector2> Points(ConstructionSite s)
    {
        foreach (var r in s.Hoarding) { yield return r.A; yield return r.B; yield return (r.A + r.B) / 2; }
        foreach (var z in s.Zones) foreach (var c in z.Rect.Corners()) yield return c;
        foreach (var m in s.Machines) yield return m.At;
        foreach (var k in s.Cranes) if (!k.Inside) yield return k.Base;
    }

    private static double Sq(double v) => v * v;
}
