using Godot;
using UnitSport.Loot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.BattleRoyale;

/// <summary>
/// Server: the outdoor loot sites of a match (#198), found in the region's own data and seeded by
/// the match. Each kind of site has a rule:
/// <list type="bullet">
/// <item>army bunkers in steep slopes near a road, behind a dial;</item>
/// <item>hunter's high seats at forest edges, facing the open ground;</item>
/// <item>hay stashes by isolated farm buildings, some with a motorbike;</item>
/// <item>SAC emergency boxes by mountain paths above 1,500 m;</item>
/// <item>one crashed helicopter on flat open ground;</item>
/// <item>fishing huts on river banks and lake shores.</item>
/// </list>
/// The data used is the 100 m horizon lattice (relief), the land-cover raster, the building
/// tiles and the roads/paths/rivers. Docs: <c>docs/notes/br/sites.md</c>.
/// </summary>
public static class BrSites
{
    public const float HighSeatsPerKm2 = 1.2f, HayPerKm2 = 1f, FishingPerKm2 = 0.4f;
    public const int MaxFishing = 8, MaxSac = 3;
    /// <summary>Steeper than about 31°: where a fort door is cut into the rock.</summary>
    private const float BunkerSlope = 0.6f;
    private const float SacAltitude = 1500f;

    /// <summary>A node yaw that turns a site's open side (built as −Z) towards <paramref name="dir"/> (east, north).</summary>
    public static float YawFacing(Vector2 dir) => Mathf.Atan2(-dir.X, dir.Y);

    /// <summary>What was found: the sites' crates, and where a motorbike stands by a barn (E, N, altitude, yaw).</summary>
    public sealed record Result(List<Crate> Crates, List<(double E, double N, float Alt, float Yaw)> Bikes);

    private sealed class Tiles
    {
        public HorizonIndex? Horizon;
        public readonly Dictionary<TileId, byte[]> Cover = new();
        public readonly List<(TileId Id, BuildingTile Tile)> Buildings = new();
        public readonly List<(TileId Id, RoadTile Tile)> Roads = new();
    }

    public static async Task<Result> Place(IChunkSource source, BrArea area, int seed, IReadOnlyList<BrLoot.RoadPoint> roads)
    {
        var t = new Tiles { Horizon = await source.LoadHorizonAsync() };
        double half = area.Side * 0.5;
        for (int e = (int)Math.Floor((area.E - half) / 1000); e <= (int)Math.Floor((area.E + half - 1) / 1000); e++)
            for (int n = (int)Math.Floor((area.N - half) / 1000); n <= (int)Math.Floor((area.N + half - 1) / 1000); n++)
            {
                var id = new TileId(e, n);
                try
                {
                    if (await source.LoadCoverAsync(id) is { } c) t.Cover[id] = c;
                    if (await source.LoadBuildingsAsync(id) is { } b) t.Buildings.Add((id, b));
                    if (await source.LoadRoadsAsync(id) is { } r) t.Roads.Add((id, r));
                }
                catch (Exception ex) { GD.PushWarning($"[br] sites {id}: {ex.Message}"); }
            }
        return await Task.Run(() => Find(t, area, seed, roads));
    }

    private static Result Find(Tiles t, BrArea area, int seed, IReadOnlyList<BrLoot.RoadPoint> roads)
    {
        var rng = new Random(seed ^ 0x7f4a7c15);
        var crates = new List<Crate>();
        var bikes = new List<(double, double, float, float)>();
        float km2 = area.Side * area.Side / 1e6f;
        double west = area.E - area.Side * 0.5, south = area.N - area.Side * 0.5;
        double H(double e, double n) => BrMapImage.Height(t.Horizon, e, n);
        Vector2 Gradient(double e, double n) => new(
            (float)((H(e + 30, n) - H(e - 30, n)) / 60), (float)((H(e, n + 30) - H(e, n - 30)) / 60));
        CoverClass Cover(double e, double n)
        {
            var id = TileId.FromLv95(e, n);
            return t.Cover.TryGetValue(id, out var c)
                ? (CoverClass)c[Math.Clamp((int)(id.MaxN - n), 0, CoverFormat.Size - 1) * CoverFormat.Size + Math.Clamp((int)(e - id.MinE), 0, CoverFormat.Size - 1)]
                : CoverClass.Open;
        }
        bool Open(CoverClass c) => c is CoverClass.Open or CoverClass.Vineyard;
        bool Wooded(CoverClass c) => c is CoverClass.Forest or CoverClass.OpenForest;

        // roads near a point: a 150 m hash
        var roadCells = new HashSet<(int, int)>();
        foreach (var r in roads) roadCells.Add(((int)Math.Floor(r.E / 150), (int)Math.Floor(r.N / 150)));
        bool NearRoad(double e, double n)
        {
            int ce = (int)Math.Floor(e / 150), cn = (int)Math.Floor(n / 150);
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                    if (roadCells.Contains((ce + de, cn + dn))) return true;
            return false;
        }

        void Take<T>(List<T> candidates, int count, float spacing, Func<T, (double E, double N)> at, Action<T> add)
        {
            var placed = new List<(double E, double N)>();
            for (int tries = 0; tries < count * 30 && placed.Count < count && candidates.Count > 0; tries++)
            {
                var c = candidates[rng.Next(candidates.Count)];
                var p = at(c);
                if (placed.Any(q => (q.E - p.E) * (q.E - p.E) + (q.N - p.N) * (q.N - p.N) < spacing * spacing)) continue;
                placed.Add(p);
                add(c);
            }
        }

        Crate Site(CrateStyle style, MatchTable table, double e, double n, float yaw, string label, float alt = BrCrates.Ground)
        {
            var c = new Crate { Style = style, E = e, N = n, Yaw = yaw, Alt = alt, Label = label };
            c.SetStacks(MatchLoot.Roll(table, new Random(seed * 31 + crates.Count * 7919 + (int)style)));
            crates.Add(c);
            return c;
        }

        // ---- army bunkers: steep slopes, near a road, door facing downhill ---------------------
        var steep = new List<(double E, double N, Vector2 Down)>();
        for (double e = west + 30; e < west + area.Side; e += 60)
            for (double n = south + 30; n < south + area.Side; n += 60)
            {
                var g = Gradient(e, n);
                if (g.Length() < BunkerSlope || !NearRoad(e, n) || Cover(e, n) is CoverClass.Water or CoverClass.Glacier) continue;
                steep.Add((e, n, -g.Normalized()));
            }
        Take(steep, area.Side >= 6000f ? 2 : 1, 1500f, s => (s.E, s.N),
            s => Site(CrateStyle.Bunker, MatchTable.Bunker, s.E, s.N, YawFacing(s.Down), "the army bunker").Locked = true);

        // ---- high seats: a forest cell with open ground beside it --------------------------------
        var edges = new List<(double E, double N, Vector2 Out)>();
        var dirs = new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) };
        for (double e = west + 10; e < west + area.Side; e += 20)
            for (double n = south + 10; n < south + area.Side; n += 20)
            {
                if (!Wooded(Cover(e, n))) continue;
                foreach (var d in dirs)
                    if (Open(Cover(e + d.X * 25, n + d.Y * 25)) && Open(Cover(e + d.X * 45, n + d.Y * 45)))
                    {
                        // just inside the trees, looking out over the field
                        edges.Add((e + d.X * 6, n + d.Y * 6, d));
                        break;
                    }
            }
        Take(edges, (int)(km2 * HighSeatsPerKm2), 300f, s => (s.E, s.N),
            s => Site(CrateStyle.HighSeat, MatchTable.HighSeat, s.E, s.N, YawFacing(s.Out), "the high seat"));

        // ---- hay stashes: isolated farm buildings ------------------------------------------------
        var farms = new List<(double E, double N, float Radius, float Alt)>();
        var centres = new List<(double E, double N)>();
        foreach (var (id, tile) in t.Buildings)
            foreach (var b in tile.Buildings)
            {
                var (ce, cn, r) = Footprint(id, b);
                centres.Add((ce, cn));
                if (b.Kind == BuildingKind.Agricultural && area.Contains(ce, cn)) farms.Add((ce, cn, r, b.MinY));
            }
        var counted = new Dictionary<(int, int), int>();
        foreach (var (e, n) in centres)
        {
            var k = ((int)Math.Floor(e / 80), (int)Math.Floor(n / 80));
            counted[k] = counted.GetValueOrDefault(k) + 1;
        }
        bool Isolated(double e, double n)
        {
            int others = 0;
            int ce = (int)Math.Floor(e / 80), cn = (int)Math.Floor(n / 80);
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                    others += counted.GetValueOrDefault((ce + de, cn + dn));
            return others <= 2;   // the barn itself and at most one shed
        }
        Take(farms.Where(f => Isolated(f.E, f.N)).ToList(), (int)(km2 * HayPerKm2), 250f, f => (f.E, f.N), f =>
        {
            float a = (float)(rng.NextDouble() * Math.Tau);
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            double e = f.E + dir.X * (f.Radius + 3), n = f.N + dir.Y * (f.Radius + 3);
            Site(CrateStyle.HayStash, MatchTable.HayStash, e, n, YawFacing(dir), "the hay bales", f.Alt);
            // some farmers left their motorbike by the bales
            if (rng.NextDouble() < 0.35)
                bikes.Add((f.E + dir.X * (f.Radius + 6) - dir.Y * 3, f.N + dir.Y * (f.Radius + 6) + dir.X * 3, f.Alt, (float)(rng.NextDouble() * Math.Tau)));
        });

        // ---- SAC boxes: high mountain paths ------------------------------------------------------
        var paths = new List<(double E, double N, float Alt)>();
        var rivers = new List<(double E, double N, Vector2 Along)>();
        foreach (var (id, tile) in t.Roads)
            foreach (var s in tile.Segments)
            {
                bool path = s.Class is RoadClass.Path or RoadClass.Track && (s.Flags & (RoadFlags.Hiking | RoadFlags.MountainHiking)) != 0
                            || s.Class == RoadClass.Path;
                bool river = s.Class == RoadClass.Watercourse;
                if (!path && !river) continue;
                for (int i = 0; i + 1 < s.PointCount; i++)
                {
                    double e = id.MinE + s.Points[i * 3], n = id.MaxN - s.Points[i * 3 + 2];
                    if (!area.Contains(e, n)) continue;
                    if (path && s.Points[i * 3 + 1] > SacAltitude) paths.Add((e, n, s.Points[i * 3 + 1]));
                    if (river)
                        rivers.Add((e, n, new Vector2(s.Points[i * 3 + 3] - s.Points[i * 3], -(s.Points[i * 3 + 5] - s.Points[i * 3 + 2])).Normalized()));
                }
            }
        Take(paths, MaxSac, 800f, p => (p.E, p.N), p =>
        {
            float a = (float)(rng.NextDouble() * Math.Tau);
            Site(CrateStyle.SacBox, MatchTable.SacBox, p.E + Math.Cos(a) * 2, p.N + Math.Sin(a) * 2, YawFacing(new Vector2(-Mathf.Cos(a), -Mathf.Sin(a))),
                "the SAC emergency box");
        });

        // ---- the helicopter wreck: flat open ground inside the first circle ----------------------
        float r0 = Math.Min(area.Side * 0.57f, area.Side * 0.5f) * 0.6f;
        var flats = new List<(double E, double N)>();
        for (double e = area.E - r0; e < area.E + r0; e += 25)
            for (double n = area.N - r0; n < area.N + r0; n += 25)
                if ((e - area.E) * (e - area.E) + (n - area.N) * (n - area.N) < r0 * r0 && Open(Cover(e, n)) && Gradient(e, n).Length() < 0.25f)
                    flats.Add((e, n));
        Take(flats, 1, 0f, f => f, f => Site(CrateStyle.Wreck, MatchTable.Wreck, f.E, f.N, (float)(rng.NextDouble() * Math.Tau), "the helicopter wreck"));

        // ---- fishing huts: river banks and lake shores --------------------------------------------
        var banks = new List<(double E, double N, Vector2 ToWater)>();
        foreach (var (e, n, along) in rivers)
        {
            var side = new Vector2(along.Y, -along.X) * (rng.Next(2) == 0 ? 1 : -1);
            double he = e + side.X * 8, hn = n + side.Y * 8;
            if (Open(Cover(he, hn)) || Wooded(Cover(he, hn))) banks.Add((he, hn, -side));
        }
        for (double e = west + 10; e < west + area.Side; e += 20)
            for (double n = south + 10; n < south + area.Side; n += 20)
            {
                if (Cover(e, n) is not (CoverClass.Open or CoverClass.Woodland)) continue;
                foreach (var d in dirs)
                    if (Cover(e + d.X * 15, n + d.Y * 15) == CoverClass.Water)
                    {
                        banks.Add((e, n, d));
                        break;
                    }
            }
        Take(banks, Math.Min(MaxFishing, (int)(km2 * FishingPerKm2)), 500f, b => (b.E, b.N),
            b => Site(CrateStyle.FishingHut, MatchTable.FishingHut, b.E, b.N, YawFacing(b.ToWater), "the fishing hut"));

        return new Result(crates, bikes);
    }

    /// <summary>A building's centre (LV95) and the radius of its plan, from its triangles.</summary>
    internal static (double E, double N, float Radius) Footprint(TileId id, Building b)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        var tri = b.Triangles;
        for (int i = 0; i + 2 < tri.Length; i += 3)
        {
            minX = Math.Min(minX, tri[i]); maxX = Math.Max(maxX, tri[i]);
            minZ = Math.Min(minZ, tri[i + 2]); maxZ = Math.Max(maxZ, tri[i + 2]);
        }
        float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
        return (id.MinE + cx, id.MaxN - cz, Math.Max(maxX - minX, maxZ - minZ) * 0.5f);
    }
}
