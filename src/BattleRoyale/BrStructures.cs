using Godot;
using UnitSport.Build;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.BattleRoyale;

/// <summary>
/// Server: where a match's ready-made structures stand (#276), found in the region's own data and
/// seeded by the match, like <see cref="BrSites"/>. Each placement is a <see cref="BrPrefabs"/> prefab
/// at an LV95 origin (cell 0,0,0's corner, on the ground) and a yaw:
/// <list type="bullet">
/// <item>lookout towers on local summits (40 m above the ground 400 m round), a zipline down the fall line;</item>
/// <item>ski jumps on steep open slopes by a wood, a launch pad on top facing downhill;</item>
/// <item>rows of avalanche barriers across slopes over 30° above 1,800 m;</item>
/// <item>army checkpoints astride main roads;</item>
/// <item>scout forts in forest clearings;</item>
/// <item>scaffolding beside every building site (<c>BuildingKind.UnderConstruction</c>);</item>
/// <item>footbridges over streams in a gorge (both banks well above the water).</item>
/// </list>
/// Heights come from the tiles' own height grids (<see cref="ChunkGrid.SampleMeshHeight"/>), the relief
/// from the 100 m horizon lattice. Docs: <c>docs/notes/br/structures.md</c>.
/// </summary>
public static class BrStructures
{
    public const float TowersPerKm2 = 0.25f, FortsPerKm2 = 0.3f, JumpsPerKm2 = 0.15f;
    public const int MaxTowers = 6, MaxJumps = 3, MaxBarrierSites = 8, MaxCheckpoints = 4, MaxForts = 4, MaxScaffolds = 10, MaxBridges = 3;

    /// <summary>One prefab to put up: its origin (LV95 + ground altitude), yaw, and the far end of its zipline if it has one.</summary>
    public sealed record Placement(Prefab Prefab, double E, double N, float Alt, float Yaw, (double E, double N, float Alt)? ZipEnd);

    private sealed class Tiles
    {
        public HorizonIndex? Horizon;
        public readonly Dictionary<TileId, byte[]> Cover = new();
        public readonly Dictionary<TileId, ChunkGrid> Heights = new();
        public readonly List<(TileId Id, BuildingTile Tile)> Buildings = new();
        public readonly List<(TileId Id, RoadTile Tile)> Roads = new();
    }

    public static async Task<List<Placement>> Place(IChunkSource source, BrArea area, int seed, IReadOnlyList<BrLoot.RoadPoint> roads)
    {
        var t = new Tiles { Horizon = await source.LoadHorizonAsync() };
        double half = area.Side * 0.5;
        for (int e = (int)Math.Floor((area.E - half) / 1000); e <= (int)Math.Floor((area.E + half - 1) / 1000); e++)
            for (int n = (int)Math.Floor((area.N - half) / 1000); n <= (int)Math.Floor((area.N + half - 1) / 1000); n++)
            {
                var id = new TileId(e, n);
                try
                {
                    if (await source.LoadChunkAsync(id) is { } g) t.Heights[id] = g;
                    if (await source.LoadCoverAsync(id) is { } c) t.Cover[id] = c;
                    if (await source.LoadBuildingsAsync(id) is { } b) t.Buildings.Add((id, b));
                    if (await source.LoadRoadsAsync(id) is { } r) t.Roads.Add((id, r));
                }
                catch (Exception ex) { GD.PushWarning($"[br] structures {id}: {ex.Message}"); }
            }
        return await Task.Run(() => Find(t, area, seed, roads));
    }

    private static List<Placement> Find(Tiles t, BrArea area, int seed, IReadOnlyList<BrLoot.RoadPoint> roads)
    {
        var rng = new Random(seed ^ 0x2763_1f0b);
        var result = new List<Placement>();
        var taken = new List<(double E, double N, float R)>();   // no two structures on top of each other
        float km2 = area.Side * area.Side / 1e6f;
        double west = area.E - area.Side * 0.5, south = area.N - area.Side * 0.5;
        const float S = BuildGrid.Cell;

        double Relief(double e, double n) => BrMapImage.Height(t.Horizon, e, n);
        float? Ground(double e, double n) =>
            t.Heights.TryGetValue(TileId.FromLv95(e, n), out var g) ? (float)g.SampleMeshHeight(e, n) : null;
        Vector2 Gradient(double e, double n, double step) => new(
            (float)((Relief(e + step, n) - Relief(e - step, n)) / (2 * step)), (float)((Relief(e, n + step) - Relief(e, n - step)) / (2 * step)));
        CoverClass Cover(double e, double n)
        {
            var id = TileId.FromLv95(e, n);
            return t.Cover.TryGetValue(id, out var c)
                ? (CoverClass)c[Math.Clamp((int)(id.MaxN - n), 0, CoverFormat.Size - 1) * CoverFormat.Size + Math.Clamp((int)(e - id.MinE), 0, CoverFormat.Size - 1)]
                : CoverClass.Open;
        }
        bool Open(CoverClass c) => c is CoverClass.Open or CoverClass.Vineyard;
        bool Wooded(CoverClass c) => c is CoverClass.Forest or CoverClass.OpenForest;
        bool Free(double e, double n, float r) => area.Contains(e, n)
            && !taken.Any(q => (q.E - e) * (q.E - e) + (q.N - n) * (q.N - n) < (q.R + r) * (q.R + r));

        // the origin corner of a prefab whose local point (lx, lz) should sit on (e, n), turned by yaw
        (double E, double N) Corner(double e, double n, float yaw, float lx, float lz)
        {
            // node yaw: local +X is (cos, -sin) in (east, north), local +Z is (-sin... ) — via Godot's basis
            var b = new Basis(Vector3.Up, yaw);
            var off = b * new Vector3(lx, 0, lz);
            return (e - off.X, n + off.Z);   // world Z points south
        }

        void Add(Prefab p, double e, double n, float yaw, float lx, float lz, float radius, (double, double, float)? zip = null)
        {
            var (oe, on) = Corner(e, n, yaw, lx, lz);
            if (Ground(oe, on) is not { } alt) return;
            taken.Add((e, n, radius));
            result.Add(new Placement(p, oe, on, alt, yaw, zip));
        }

        void Take<T>(List<T> candidates, int count, float spacing, Func<T, (double E, double N)> at, Func<T, bool> add)
        {
            int placed = 0;
            var mine = new List<(double E, double N)>();
            for (int tries = 0; tries < count * 40 && placed < count && candidates.Count > 0; tries++)
            {
                var c = candidates[rng.Next(candidates.Count)];
                var p = at(c);
                if (mine.Any(q => (q.E - p.E) * (q.E - p.E) + (q.N - p.N) * (q.N - p.N) < spacing * spacing)) continue;
                if (!add(c)) continue;
                mine.Add(p);
                placed++;
            }
        }

        // ---- lookout towers: local summits, a zipline down the fall line ----------------------------
        var summits = new List<(double E, double N)>();
        for (double e = west + 50; e < west + area.Side; e += 100)
            for (double n = south + 50; n < south + area.Side; n += 100)
            {
                double h = Relief(e, n), ring = double.MaxValue;
                bool top = true;
                for (int k = 0; k < 8 && top; k++)
                {
                    double a = k * Math.PI / 4;
                    if (Relief(e + Math.Cos(a) * 200, n + Math.Sin(a) * 200) > h) top = false;
                    ring = Math.Min(ring, Relief(e + Math.Cos(a) * 400, n + Math.Sin(a) * 400));
                }
                if (top && h - ring >= 40 && Cover(e, n) is not (CoverClass.Water or CoverClass.Glacier)) summits.Add((e, n));
            }
        var tower = BrPrefabs.LookoutTower;
        var zipSpot = tower.Gadgets[0];
        Take(summits, Math.Min(MaxTowers, Math.Max(1, (int)(km2 * TowersPerKm2))), 600f, s => s, s =>
        {
            if (!Free(s.E, s.N, 8) || Ground(s.E, s.N) is not { } g0) return false;
            var down = -Gradient(s.E, s.N, 60);
            if (down.LengthSquared() < 1e-6f) down = new Vector2(1, 0);
            down = down.Normalized();
            float yaw = BrSites.YawFacing(down);   // the tower's −Z faces downhill
            var (oe, on) = Corner(s.E, s.N, yaw, S * 0.5f, 0);
            // the cable leaves from the platform's middle and lands on the ground down the slope
            var start = new Vector3((float)(s.E - area.E), g0 + zipSpot.Y, (float)(area.N - s.N));
            foreach (float d in new[] { 130f, 110f, 90f, 70f })
            {
                double ee = s.E + down.X * d, en = s.N + down.Y * d;
                if (!area.Contains(ee, en) || Ground(ee, en) is not { } ge) continue;
                if (Gadgets.ZipProblem(start, new Vector3((float)(ee - area.E), ge, (float)(area.N - en))) != null) continue;
                Add(tower, s.E, s.N, yaw, S * 0.5f, 0, 8, (ee, en, ge));
                return true;
            }
            Add(tower, s.E, s.N, yaw, S * 0.5f, 0, 8);   // no good landing: a tower without its zipline
            return true;
        });

        // ---- ski jumps: steep open slopes beside a wood ---------------------------------------------
        var slopes = new List<(double E, double N, Vector2 Down)>();
        for (double e = west + 40; e < west + area.Side; e += 80)
            for (double n = south + 40; n < south + area.Side; n += 80)
            {
                var g = Gradient(e, n, 40);
                if (g.Length() is < 0.45f or > 0.85f || !Open(Cover(e, n))) continue;
                bool wood = false;
                for (int k = 0; k < 8 && !wood; k++)
                    wood = Wooded(Cover(e + Math.Cos(k * Math.PI / 4) * 60, n + Math.Sin(k * Math.PI / 4) * 60));
                if (wood) slopes.Add((e, n, -g.Normalized()));
            }
        Take(slopes, Math.Min(MaxJumps, (int)Math.Ceiling(km2 * JumpsPerKm2)), 800f, s => (s.E, s.N), s =>
        {
            if (!Free(s.E, s.N, 6)) return false;
            Add(BrPrefabs.SkiJump, s.E, s.N, BrSites.YawFacing(s.Down), S * 0.5f, 0, 6);
            return true;
        });

        // ---- avalanche barriers: rows across steep slopes high up ------------------------------------
        var faces = new List<(double E, double N, Vector2 Down)>();
        for (double e = west + 30; e < west + area.Side; e += 60)
            for (double n = south + 30; n < south + area.Side; n += 60)
            {
                var g = Gradient(e, n, 30);
                if (g.Length() < 0.58f || Relief(e, n) < 1800 || Cover(e, n) is CoverClass.Glacier or CoverClass.Water) continue;
                faces.Add((e, n, -g.Normalized()));
            }
        var barrier = BrPrefabs.AvalancheBarrier;
        Take(faces, MaxBarrierSites, 300f, f => (f.E, f.N), f =>
        {
            if (!Free(f.E, f.N, 25)) return false;
            float yaw = BrSites.YawFacing(f.Down);   // the walls run along the contour (local X)
            for (int row = -1; row <= 1; row++)
            {
                double e = f.E + f.Down.X * row * 15 + f.Down.Y * row * 4, n = f.N + f.Down.Y * row * 15 - f.Down.X * row * 4;
                var (oe, on) = Corner(e, n, yaw, barrier.Pieces.Length * S * 0.5f, 0);
                if (Ground(oe, on) is { } alt) result.Add(new Placement(barrier, oe, on, alt, yaw, null));
            }
            taken.Add((f.E, f.N, 25));
            return true;
        });

        // ---- army checkpoints: astride a main road -------------------------------------------------
        var mains = roads.Where(r => r.Class is RoadClass.Major or RoadClass.Road).ToList();
        Take(mains, MaxCheckpoints, 800f, r => (r.E, r.N), r =>
        {
            if (!Free(r.E, r.N, 6)) return false;
            var along = new Vector2(Mathf.Sin(r.Heading), Mathf.Cos(r.Heading));
            // the road runs along the ring's local Z, through the open middle cell
            Add(BrPrefabs.Checkpoint, r.E, r.N, BrSites.YawFacing(along), S * 0.5f, S * 0.5f, 6);
            return true;
        });

        // ---- scout forts: forest clearings -----------------------------------------------------------
        var clearings = new List<(double E, double N)>();
        for (double e = west + 15; e < west + area.Side; e += 30)
            for (double n = south + 15; n < south + area.Side; n += 30)
            {
                if (!Open(Cover(e, n)) || Gradient(e, n, 30).Length() > 0.25f) continue;
                int trees = 0;
                for (int k = 0; k < 8; k++)
                    if (Wooded(Cover(e + Math.Cos(k * Math.PI / 4) * 35, n + Math.Sin(k * Math.PI / 4) * 35))) trees++;
                if (trees >= 6) clearings.Add((e, n));
            }
        Take(clearings, Math.Min(MaxForts, Math.Max(1, (int)(km2 * FortsPerKm2))), 700f, c => c, c =>
        {
            if (!Free(c.E, c.N, 8)) return false;
            Add(BrPrefabs.ScoutFort, c.E, c.N, (float)(rng.Next(4) * Math.PI / 2), S, S, 8);
            return true;
        });

        // ---- scaffolding: every building site --------------------------------------------------------
        var sites = new List<(double E, double N, float Radius)>();
        foreach (var (id, tile) in t.Buildings)
            foreach (var b in tile.Buildings)
                if (b.Kind == BuildingKind.UnderConstruction)
                {
                    var f = BrSites.Footprint(id, b);
                    if (area.Contains(f.E, f.N)) sites.Add(f);
                }
        var scaffold = BrPrefabs.Scaffolding;
        foreach (var f in sites.OrderBy(_ => rng.Next()).Take(MaxScaffolds))
        {
            var dir = new Vector2(Mathf.Cos(rng.Next(4) * Mathf.Pi / 2), Mathf.Sin(rng.Next(4) * Mathf.Pi / 2));
            double e = f.E + dir.X * (f.Radius + 1.5), n = f.N + dir.Y * (f.Radius + 1.5);
            if (!Free(e, n, 4)) continue;
            // the strip's back (local −Z) toward the building
            Add(scaffold, e, n, BrSites.YawFacing(-dir), 5 * S * 0.5f, S * 0.5f, 7);
        }

        // ---- footbridges: streams in a gorge ------------------------------------------------------------
        var bridge = BrPrefabs.SuspensionBridge;
        float span = (bridge.Pieces.Max(p => p.Slot.X) + 1) * S;
        var crossings = new List<(double E, double N, Vector2 Across, float Deck)>();
        foreach (var (id, tile) in t.Roads)
            foreach (var s in tile.Segments)
            {
                if (s.Class != RoadClass.Watercourse) continue;
                for (int i = 0; i + 1 < s.PointCount; i += 4)
                {
                    double e = id.MinE + s.Points[i * 3], n = id.MaxN - s.Points[i * 3 + 2];
                    if (!area.Contains(e, n)) continue;
                    var along = new Vector2(s.Points[i * 3 + 3] - s.Points[i * 3], -(s.Points[i * 3 + 5] - s.Points[i * 3 + 2]));
                    if (along.LengthSquared() < 1e-4f) continue;
                    var across = new Vector2(along.Y, -along.X).Normalized();
                    double ae = e - across.X * span / 2, an = n - across.Y * span / 2, be = e + across.X * span / 2, bn = n + across.Y * span / 2;
                    if (Ground(ae, an) is not { } ga || Ground(be, bn) is not { } gb || Ground(e, n) is not { } gw) continue;
                    // a gorge: both banks level with each other and at least 5 m over the water
                    if (Mathf.Abs(ga - gb) < 2f && Mathf.Min(ga, gb) - gw > 5f) crossings.Add((e, n, across, Mathf.Max(ga, gb)));
                }
            }
        Take(crossings, MaxBridges, 600f, c => (c.E, c.N), c =>
        {
            if (!Free(c.E, c.N, span / 2)) return false;
            float yaw = BrSites.YawFacing(new Vector2(-c.Across.Y, c.Across.X));   // local X across the water
            var (oe, on) = Corner(c.E, c.N, yaw, span / 2, S * 0.5f);
            taken.Add((c.E, c.N, span / 2));
            result.Add(new Placement(bridge, oe, on, c.Deck, yaw, null));
            return true;
        });

        return result;
    }
}
