using System.Collections.Concurrent;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// <c>godot --headless --path . -- --pitsurvey --chunks D:/.../terrain_chunks</c> (#619): the pits of
/// the real building sites. Plans every site of every building tile in the directory, as a tile build
/// does, and counts how many dig a pit, why the others that would do not (a tile seam, or a road or a
/// neighbour on its ground), the basements, how deep and how steep, and what cutting the ground costs
/// (the collision's holes and faces, and a stride-1 ground mesh cut, on the first tiles with a pit).
/// Real data only: in no tier. Prints a RESULT line.
/// </summary>
public static class PitSurvey
{
    public static bool Requested => CmdArgs.Has("--pitsurvey");

    /// <summary>The stride-1 mesh cut is timed on this many tiles: a million vertices each.</summary>
    private const int MeshTiles = 12;

    public static int Run()
    {
        string dir = CmdArgs.Value(CmdArgs.All, "--chunks") ?? "terrain_chunks";
        var src = new LocalChunkSource(dir);
        var ids = new List<TileId>();
        foreach (var f in System.IO.Directory.GetFiles(dir, "buildings_*.bldg"))
        {
            var parts = System.IO.Path.GetFileNameWithoutExtension(f).Split('_');
            if (parts.Length == 3 && int.TryParse(parts[1], out int e) && int.TryParse(parts[2], out int n)) ids.Add(new TileId(e, n));
        }
        GD.Print($"[pitsurvey] {ids.Count} building tiles in {dir}");

        var rows = new ConcurrentBag<(SitePhase Phase, bool Wanted, string Outcome, int Basements, float Depth, float Grade, float Ramp, float Lane, int Wall)>();
        var costs = new ConcurrentBag<(double Dig, double Holes, double Faces)>();
        var pitTiles = new ConcurrentBag<TileId>();
        var samples = new ConcurrentBag<(string Key, double E, double N, float Depth, int Wall, bool Ramp)>();
        var yards = new ConcurrentBag<(int Rolled, int Dropped, bool Materials, bool WayIn)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id =>
        {
            var tile = src.LoadBuildingsAsync(id).GetAwaiter().GetResult();
            if (tile == null || !SitePlans.HasSite(tile)) return;
            var roads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
            var grid = src.LoadChunkAsync(id).GetAwaiter().GetResult();
            var map = Interiors.BuildingTypes.For(tile);
            bool any = false;
            foreach (var site in SitePlans.For(tile, roads))
            {
                bool wanted = site.Phase == SitePhase.Foundations || site.Phase == SitePhase.Shell && site.BuiltStoreys <= ConstructionSites.PitOpenUpTo;
                if (site.Pit == null)
                {
                    string why = "not wanted";
                    if (wanted)
                    {
                        // planned again with nothing round it: a pit then means a road or a neighbour stopped it
                        int index = int.Parse(site.Key.Split('_')[2]);
                        var free = ConstructionSites.Plan(site.Key, tile.Buildings[index], map.Boxes[index]!.Value,
                            Interiors.BuildingFootprint.StreetNear(roads, site.Box.Center), _ => false);
                        why = free?.Pit != null ? "a road or a neighbour" : "the tile's seam";
                    }
                    rows.Add((site.Phase, wanted, why, 0, 0, 0, 0, 0, -1));
                    continue;
                }
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var (shell, pit) = SiteShellBuilder.Dig(tile, site, grid);
                var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (pit == null) continue;
                var cut = SiteShellBuilder.CutOf(site, pit);
                var holes = PitTerrain.Holes(new[] { cut });
                var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (grid != null) PitTerrain.CollisionFaces(new[] { cut }, holes, (c, r) => (float)grid.HeightMetersAt(c, r));
                var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                costs.Add((Ms(t0, t1), Ms(t1, t2), Ms(t2, t3)));
                rows.Add((site.Phase, wanted, "dug", site.Pit.Basements, pit.TopOuter.Y - pit.Floor, pit.Grade, pit.RampLength, site.Pit.LaneLength,
                    ConstructionSites.LaneWall(site.Pit, site.Box, site.Front)));
                any = true;
                var roles = ConstructionSites.Roles(site.Phase, site.Small);
                int rolled = Enumerable.Range(0, roles.Count).Count(k => roles[k].Chance >= 1 || Fnv.Unit($"{site.Key}|site|machine{k}") < roles[k].Chance);
                yards.Add((rolled, rolled - site.Machines.Count, site.Zones.Any(z => z.Kind == SiteZoneKind.Materials), ConstructionSites.MaterialsWayIn(site)));
                var c = site.Box.Center;
                samples.Add((site.Key, id.MinE + c.X, id.MaxN - c.Y, pit.TopOuter.Y - pit.Floor, ConstructionSites.LaneWall(site.Pit, site.Box, site.Front), pit.HasRamp));
            }
            if (any) pitTiles.Add(id);
        });
        GD.Print($"[pitsurvey] planned in {sw.Elapsed.TotalSeconds:F1} s");

        var all = rows.ToList();
        var dug = all.Where(r => r.Outcome == "dug").ToList();
        GD.Print($"[pitsurvey] {all.Count} sites: " + string.Join(", ", all.GroupBy(r => r.Phase).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")));
        GD.Print($"[pitsurvey] {all.Count(r => r.Wanted)} want a pit (foundations, shells up to {ConstructionSites.PitOpenUpTo} storeys): "
            + string.Join(", ", all.Where(r => r.Wanted).GroupBy(r => r.Outcome).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
        if (dug.Count > 0)
        {
            GD.Print($"[pitsurvey] dug: {dug.Count(r => r.Phase == SitePhase.Foundations)} at their foundations, {dug.Count(r => r.Phase == SitePhase.Shell)} under a shell; "
                + "basements " + string.Join(", ", dug.GroupBy(r => r.Basements).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}"))
                + $"; the lane along the front {dug.Count(r => r.Wall == 0)}, a side {dug.Count(r => r.Wall == 1)}, the back {dug.Count(r => r.Wall == 2)}");
            GD.Print($"[pitsurvey] depth {dug.Min(r => r.Depth):F1}-{dug.Max(r => r.Depth):F1} m (mean {dug.Average(r => r.Depth):F1}); "
                + $"grade {100 * dug.Where(r => r.Ramp > 0).Min(r => r.Grade):F0}-{100 * dug.Where(r => r.Ramp > 0).Max(r => r.Grade):F0} %, {dug.Count(r => r.Grade > PitPlans.RampGrade + 1e-3f)} steeper than {100 * PitPlans.RampGrade:F0} %, {dug.Count(r => r.Ramp <= 0)} with no ramp (a slope took them too deep for their lane); "
                + $"ramp {dug.Max(r => r.Ramp):F1} m at most, {dug.Count(r => r.Ramp + PitPlans.FootRoom > r.Lane + 0.01f)} with less than {PitPlans.FootRoom:F0} m at its foot");
            var c = costs.ToList();
            GD.Print($"[pitsurvey] per pit: plan and line {c.Average(x => x.Dig):F2} ms (worst {c.Max(x => x.Dig):F1}), holes {c.Average(x => x.Holes):F2} ms, "
                + $"collision faces {c.Average(x => x.Faces):F2} ms (worst {c.Max(x => x.Faces):F1})");
        }

        // the ground mesh's cut at stride 1, where it costs the most, on a few tiles with a pit
        var timed = new List<double>();
        foreach (var id in pitTiles.Take(MeshTiles))
        {
            var tile = src.LoadBuildingsAsync(id).GetAwaiter().GetResult()!;
            var roads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
            var grid = src.LoadChunkAsync(id).GetAwaiter().GetResult();
            if (grid == null) continue;
            var pits = SiteShellBuilder.Pits(tile, SitePlans.For(tile, roads), grid);
            var core = TerrainMeshBuilder.BuildSurfaceCore(grid, 1);
            var t = System.Diagnostics.Stopwatch.StartNew();
            TerrainMeshBuilder.CutPits(core, 1, pits);
            timed.Add(t.Elapsed.TotalMilliseconds);
        }
        if (timed.Count > 0)
            GD.Print($"[pitsurvey] the ground mesh cut at stride 1: {timed.Average():F0} ms a tile (worst {timed.Max():F0}) over {timed.Count} tiles");
        // what the pit costs the yard: machines that found no room, materials with none
        var y = yards.ToList();
        if (y.Count > 0)
            GD.Print($"[pitsurvey] the yards of the {y.Count} pits: {y.Sum(t => t.Dropped)} machine(s) of {y.Sum(t => t.Rolled)} found no room (on {y.Count(t => t.Dropped > 0)} sites), "
                + $"{y.Count(t => !t.Materials)} without their materials, {y.Count(t => t.Materials && !t.WayIn)} with materials no machine can come at");
        // a few to go and look at: the deepest, and one with its lane along each wall
        var seen = samples.ToList();
        foreach (var s in seen.OrderByDescending(s => s.Depth).Take(2).Concat(seen.GroupBy(s => s.Wall).Select(g => g.OrderBy(s => s.Key, StringComparer.Ordinal).First())))
            GD.Print($"[pitsurvey] look at {s.Key}: --at {s.E:F0},{s.N:F0}, {s.Depth:F1} m deep, lane along the {(s.Wall == 0 ? "front" : s.Wall == 1 ? "side" : "back")}, {(s.Ramp ? "a ramp" : "no ramp")}");
        GD.Print("[pitsurvey] RESULT: ok");
        return 0;
    }
}
