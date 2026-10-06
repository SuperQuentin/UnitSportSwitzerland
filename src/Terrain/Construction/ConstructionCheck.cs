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
            foreach (var corner in site.Box.Corners())
                if (site.Cranes.Count > 0 && !site.Cranes.Any(k => k.Base.DistanceTo(corner) <= k.JibLength))
                    Expect(false, $"{what}: no crane reaches the corner at {corner}");

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
