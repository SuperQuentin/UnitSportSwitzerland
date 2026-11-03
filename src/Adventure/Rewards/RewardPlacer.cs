using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Adventure.Places;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>
/// Where a tile's rewards are (#1227, rewards plan 1): <b>deterministic, never stored</b>. A pure function of
/// the tile, its coarse terrain (<c>LoadCoarseChunkAsync</c>, which the server has for every tile on disk), the
/// catalogue's POIs and the tile's roads, so the client and the server compute the same spots and the server can
/// check any claim. Contents are rolled when opened (<see cref="ChestLoot"/>); only what a player took is kept.
/// </summary>
public static class RewardPlacer
{
    /// <summary>Bump when a change moves spots to other cells (new ids); the change note says how many moved (<c>--rewardstats</c>).</summary>
    public const int Version = 1;

    /// <summary>The lottery's cell (four by four per tile).</summary>
    public const double Cell = 250;

    // per 250 m cell, before the terrain's say: tuned with --rewardstats toward the plan's densities
    public const double PCommon = 0.085, PFine = 0.026, PRemote = 0.0035, PEdelweiss = 0.11;
    public const int MaxChests = 8, MaxEdelweiss = 10, MaxCairns = 2;
    public const double EdelweissFloor = 1500;
    /// <summary>A chest keeps this far off a carriageway, a Fine one this far, a remote Precious one this far, an edelweiss this far.</summary>
    public const double RoadClear = 6, FineClear = 80, RemoteClear = 400, EdelweissClear = 150;

    private const ulong SaltCommon = 0xc0aa0UL, SaltFine = 0xf1eeUL, SaltRemote = 0x4e30UL, SaltEdel = 0xede1UL, SaltPoi = 0x9015UL, SaltSeal = 0x5ea1UL;
    private const ulong SaltGate = 0x6a7e1266UL, SaltRoped = 0x40be1266UL;
    /// <summary>#1266: chests above this (glacier country; the placer has no fine cover) may need a rope team.</summary>
    public const float RopedAltitude = 2800f;

    /// <summary>
    /// The spots of a tile. <paramref name="coarse"/> null (no terrain there): nothing. <paramref name="pois"/>
    /// are the catalogue's of this tile; <paramref name="lakes"/> the lakes whose outline may cross it;
    /// <paramref name="cairns"/> says which POIs carry their canton's cairn (<see cref="Cairns.Of"/>).
    /// </summary>
    public static RewardSpot[] Place(TileId tile, ChunkGrid? coarse, IReadOnlyList<Poi> pois, IReadOnlyList<Poi> lakes,
        RoadTile? roads, Func<Poi, bool>? cairns = null)
    {
        if (coarse == null) return Array.Empty<RewardSpot>();
        var t = new Terrain(tile, coarse, lakes, roads);
        string canton = MainCanton(pois);
        var chests = new List<RewardSpot>();
        var flowers = new List<RewardSpot>();
        var stones = new List<RewardSpot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        // ---- the cell lotteries ----
        for (int cx = 0; cx < 4; cx++)
            for (int cy = 0; cy < 4; cy++)
            {
                long ke = tile.E * 4L + cx, kn = tile.N * 4L + cy;
                double e0 = tile.MinE + cx * Cell, n0 = tile.MinN + cy * Cell;
                var common = new SpotRng(AdventureHash.Of(ke, kn, SaltCommon));
                if (common.Next() < PCommon && Candidate(ref common, t, e0, n0, 30, RoadClear) is { } c)
                    Add(chests, ids, Chest(c.E, c.N, c.Alt, ChestTier.Common, null, ke, kn, SaltCommon, t, canton, remote: false));
                var fine = new SpotRng(AdventureHash.Of(ke, kn, SaltFine));
                if (fine.Next() < PFine && Candidate(ref fine, t, e0, n0, 40, FineClear) is { } f)
                    Add(chests, ids, Chest(f.E, f.N, f.Alt, ChestTier.Fine, null, ke, kn, SaltFine, t, canton, remote: t.RoadDistance(f.E, f.N) > RemoteClear));
                var remote = new SpotRng(AdventureHash.Of(ke, kn, SaltRemote));
                if (remote.Next() < PRemote && Candidate(ref remote, t, e0, n0, 40, RemoteClear) is { } r)
                    Add(chests, ids, Chest(r.E, r.N, r.Alt, ChestTier.Precious, null, ke, kn, SaltRemote, t, canton, remote: true));
                var edel = new SpotRng(AdventureHash.Of(ke, kn, SaltEdel));
                if (edel.Next() < PEdelweiss && t.Height(e0 + Cell / 2, n0 + Cell / 2) >= EdelweissFloor - 150)
                    for (int tries = 0; tries < 6; tries++)
                    {
                        double e = e0 + edel.Range(0, Cell), n = n0 + edel.Range(0, Cell);
                        float alt = t.Height(e, n);
                        // a ledge on a steep face, well off any road
                        if (alt < EdelweissFloor || t.Slope(e, n) < 35 || t.Water(e, n) || t.RoadDistance(e, n) < EdelweissClear) continue;
                        Add(flowers, ids, new RewardSpot(RewardRules.CellId(RewardRules.EdelweissPrefix, e, n), SpotKind.Edelweiss, e, n, alt,
                            Hash: AdventureHash.Of(ke, kn, SaltEdel + (ulong)tries), Canton: canton));
                        break;
                    }
            }

        // ---- at the places ----
        foreach (var poi in pois)
        {
            if (poi.Kind == PoiKind.Lake) continue;
            bool viewpoint = poi.Is(PoiFlags.Viewpoint);
            bool precious = poi.Kind is PoiKind.Castle or PoiKind.Pass || viewpoint
                || poi.Kind == PoiKind.Peak && poi.Elevation > 2000 && poi.Hash % 4 == 0;
            bool luxurious = poi.Kind is PoiKind.Peak or PoiKind.Castle && poi.Tier == 1
                || poi.Kind == PoiKind.Pass && poi.Elevation > 2000 && poi.Tier <= 2;
            if (precious) Add(chests, ids, PoiChest(poi, 0, ChestTier.Precious, t));
            if (luxurious) Add(chests, ids, PoiChest(poi, 1, ChestTier.Luxurious, t));
            // a summit's edelweiss, within 30 m of it
            if (poi.Kind is PoiKind.Peak or PoiKind.SummitCross && poi.Elevation >= EdelweissFloor)
            {
                var rng = new SpotRng(AdventureHash.Of(poi.Id, SaltEdel));
                int count = 1 + rng.Below(2);
                for (int i = 0; i < count; i++)
                {
                    double a = rng.Range(0, Math.PI * 2), d = rng.Range(8, 30);
                    var (e, n) = t.Inside(poi.E + Math.Sin(a) * d, poi.N + Math.Cos(a) * d);
                    Add(flowers, ids, new RewardSpot(RewardRules.CellId(RewardRules.EdelweissPrefix, e, n), SpotKind.Edelweiss, e, n,
                        t.Height(e, n), PoiId: poi.Id, Hash: rng.NextULong(), Canton: poi.Canton));
                }
            }
            if (cairns?.Invoke(poi) == true)
            {
                var rng = new SpotRng(AdventureHash.Of(poi.Id, 0xca1eUL));
                double a = rng.Range(0, Math.PI * 2);
                var (e, n) = t.Inside(poi.E + Math.Sin(a) * 12, poi.N + Math.Cos(a) * 12);
                Add(stones, ids, new RewardSpot(Cairns.IdOf(poi), SpotKind.Cairn, e, n, t.Height(e, n), PoiId: poi.Id, Hash: rng.NextULong(), Canton: poi.Canton));
            }
        }

        // ---- the budget: the highest tiers first ----
        chests.Sort((a, b) => b.Tier != a.Tier ? b.Tier.CompareTo(a.Tier) : string.CompareOrdinal(a.Id, b.Id));
        flowers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        stones.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var all = new List<RewardSpot>(MaxChests + MaxEdelweiss + MaxCairns);
        all.AddRange(chests.Take(MaxChests));
        all.AddRange(flowers.Take(MaxEdelweiss));
        all.AddRange(stones.Take(MaxCairns));
        return all.ToArray();
    }

    private static void Add(List<RewardSpot> into, HashSet<string> ids, RewardSpot? spot)
    {
        if (spot != null && ids.Add(spot.Id)) into.Add(spot);
    }

    private static string MainCanton(IReadOnlyList<Poi> pois)
    {
        string best = "";
        int n = 0;
        foreach (var p in pois)
        {
            if (p.Canton.Length != 2) continue;
            int c = 0;
            foreach (var q in pois) if (q.Canton == p.Canton) c++;
            if (c > n || c == n && string.CompareOrdinal(p.Canton, best) < 0) (best, n) = (p.Canton, c);
        }
        return best;
    }

    /// <summary>A walkable point of a cell: up to four tries for one under <paramref name="maxSlope"/>, dry, that far off the roads.</summary>
    private static (double E, double N, float Alt)? Candidate(ref SpotRng rng, Terrain t, double e0, double n0, double maxSlope, double roadClear)
    {
        for (int tries = 0; tries < 4; tries++)
        {
            double e = e0 + rng.Range(5, Cell - 5), n = n0 + rng.Range(5, Cell - 5);
            if (t.Slope(e, n) > maxSlope || t.Water(e, n) || t.RoadDistance(e, n) < roadClear) continue;
            return (e, n, t.Height(e, n));
        }
        return null;
    }

    private static RewardSpot Chest(double e, double n, float alt, ChestTier tier, Poi? poi, long ke, long kn, ulong salt, Terrain t,
        string canton, bool remote)
    {
        ulong hash = AdventureHash.Of(ke, kn, salt ^ SaltSeal);
        var seal = SealFor(tier, hash, poi, alt, t.Slope(e, n), remote);
        return new RewardSpot(RewardRules.CellId(RewardRules.ChestPrefix, e, n), SpotKind.Chest, e, n, alt, tier, seal, null, hash, canton);
    }

    private static RewardSpot PoiChest(Poi poi, int slot, ChestTier tier, Terrain t)
    {
        ulong hash = AdventureHash.Of(poi.Id, SaltPoi + (ulong)slot);
        var rng = new SpotRng(hash);
        // beside the place, not on its cross: 9-14 m off, a different side for each slot
        double a = rng.Range(0, Math.PI * 2) + slot * Math.PI, d = rng.Range(9, 14);
        var (e, n) = t.Inside(poi.E + Math.Sin(a) * d, poi.N + Math.Cos(a) * d);
        float alt = t.Height(e, n);
        var seal = SealFor(tier, hash, poi, alt, t.Slope(e, n), remote: false);
        return new RewardSpot(RewardRules.PoiChestId(poi.Id, slot), SpotKind.Chest, e, n, alt, tier, seal, poi.Id, hash, poi.Canton);
    }

    /// <summary>
    /// Whether a chest is sealed (0 % of Common, 20 % of Fine, 60 % of Precious, all Luxurious) and by what: plates
    /// at castles, bonfires on peaks and high ridges, cowbells on alpine pastures, rings on open slopes, a guide to a
    /// remote chest; the hash picks among what the terrain allows.
    /// </summary>
    public static SealKind SealFor(ChestTier tier, ulong hash, Poi? poi, float alt, float slope, bool remote)
    {
        var rng = new SpotRng(hash ^ SaltSeal);
        double sealed_ = tier switch { ChestTier.Common => 0, ChestTier.Fine => 0.2, ChestTier.Precious => 0.6, _ => 1 };
        if (rng.Next() >= sealed_) return SealKind.None;
        // #1266: one castle or hut spot in six is behind a two-handle gate; the high glacier chests need a rope team
        if (poi?.Kind is PoiKind.Castle or PoiKind.Hut && new SpotRng(hash ^ SaltGate).Below(6) == 0) return SealKind.Gate;
        if (alt >= RopedAltitude && (tier == ChestTier.Luxurious || tier == ChestTier.Precious && new SpotRng(hash ^ SaltRoped).Next() < 0.3))
            return SealKind.Roped;
        if (poi?.Kind == PoiKind.Castle) return SealKind.Plates;
        Span<SealKind> options = stackalloc SealKind[5];
        int n = 0;
        if (poi?.Kind is PoiKind.Peak or PoiKind.SummitCross || alt >= 1800 && slope < 25) options[n++] = SealKind.Bonfire;
        if (alt is >= 900 and <= 2300 && slope < 18) options[n++] = SealKind.Bells;
        if (slope < 35) options[n++] = SealKind.Rings;
        if (remote || poi == null) options[n++] = SealKind.Guide;
        if (n == 0) options[n++] = SealKind.Bonfire;
        return options[rng.Below(n)];
    }

    /// <summary>The tile's coarse terrain, roads and lakes, as the placer asks them.</summary>
    private sealed class Terrain
    {
        private readonly TileId _tile;
        private readonly ChunkGrid _grid;
        private readonly IReadOnlyList<Poi> _lakes;
        private readonly List<double[]> _roads = new();

        public Terrain(TileId tile, ChunkGrid grid, IReadOnlyList<Poi> lakes, RoadTile? roads)
        {
            _tile = tile;
            _grid = grid;
            _lakes = lakes;
            if (roads == null) return;
            foreach (var s in roads.Segments)
            {
                // carriageways and rails; tracks and footpaths are where a chest may well be
                if (s.Class > RoadClass.Lane && s.Class != RoadClass.Railway) continue;
                var line = new double[s.PointCount * 2];
                for (int i = 0; i < s.PointCount; i++) (line[i * 2], line[i * 2 + 1]) = s.Lv95(roads.Id, i);
                _roads.Add(line);
            }
        }

        public (double E, double N) Inside(double e, double n) =>
            (Math.Clamp(e, _tile.MinE + 2, _tile.MinE + 998), Math.Clamp(n, _tile.MinN + 2, _tile.MinN + 998));

        public float Height(double e, double n) => (float)_grid.SampleHeight(e, n);

        /// <summary>Degrees, from the coarse grid over ±5 m.</summary>
        public float Slope(double e, double n)
        {
            double dx = (_grid.SampleHeight(e + 5, n) - _grid.SampleHeight(e - 5, n)) / 10;
            double dy = (_grid.SampleHeight(e, n + 5) - _grid.SampleHeight(e, n - 5)) / 10;
            return (float)(Math.Atan(Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI);
        }

        /// <summary>A lake from the catalogue, or ground dead flat over 20 m (a water surface in the elevation model).</summary>
        public bool Water(double e, double n)
        {
            foreach (var lake in _lakes)
                if (PoiCatalogue.InLake(lake, e, n, 8)) return true;
            double h = _grid.SampleHeight(e, n);
            return Math.Abs(_grid.SampleHeight(e + 10, n) - h) < 0.02 && Math.Abs(_grid.SampleHeight(e - 10, n) - h) < 0.02
                && Math.Abs(_grid.SampleHeight(e, n + 10) - h) < 0.02 && Math.Abs(_grid.SampleHeight(e, n - 10) - h) < 0.02;
        }

        public double RoadDistance(double e, double n)
        {
            double best = double.MaxValue;
            foreach (var line in _roads)
                for (int i = 0; i + 3 < line.Length; i += 2)
                {
                    double ax = line[i], ay = line[i + 1], bx = line[i + 2], by = line[i + 3];
                    double vx = bx - ax, vy = by - ay, len = vx * vx + vy * vy;
                    double u = len > 1e-9 ? Math.Clamp(((e - ax) * vx + (n - ay) * vy) / len, 0, 1) : 0;
                    double dx = ax + vx * u - e, dy = ay + vy * u - n, d = dx * dx + dy * dy;
                    if (d < best) best = d;
                }
            return best == double.MaxValue ? double.MaxValue : Math.Sqrt(best);
        }
    }
}

/// <summary>
/// Hand-placed spots (#1227): <c>--rewardspots fixture</c> sets these round the fixture start, so the checks have
/// one of every kind within a walk on the flat course (a fixture course places nothing by itself), or
/// <c>--rewardspots &lt;file&gt;</c> reads lines <c>kind tier seal dE dN</c> relative to the start.
/// </summary>
public static class RewardSpots
{
    /// <summary>The fixture set. Every kind, seal and guide; ids are cells, as the placer's.</summary>
    public static List<RewardSpot> Fixture(double startE, double startN, float ground, PoiCatalogue? catalogue)
    {
        var list = new List<RewardSpot>();
        RewardSpot Chest(double x, double y, ChestTier tier, SealKind seal)
        {
            double e = startE + x, n = startN + y;
            var s = new RewardSpot(RewardRules.CellId(RewardRules.ChestPrefix, e, n), SpotKind.Chest, e, n, ground, tier, seal,
                null, AdventureHash.Of((long)e, (long)n, 0xf1c5UL), "VS");
            list.Add(s);
            return s;
        }
        Chest(25, 0, ChestTier.Common, SealKind.None);
        Chest(0, 90, ChestTier.Fine, SealKind.Bonfire);
        Chest(-90, 10, ChestTier.Precious, SealKind.Bells);
        Chest(110, -110, ChestTier.Precious, SealKind.Rings);
        Chest(-90, 110, ChestTier.Fine, SealKind.Plates);
        Chest(160, 160, ChestTier.Precious, SealKind.Guide);
        Chest(-30, -60, ChestTier.Common, SealKind.None);
        // the fixture peak's Luxurious chest, by edition
        if (catalogue?.OfKind(PoiKind.Peak) is { Count: > 0 } peaks)
        {
            var peak = peaks[0];
            list.Add(new RewardSpot(RewardRules.PoiChestId(peak.Id, 1), SpotKind.Chest, peak.E + 8, peak.N - 6, ground,
                ChestTier.Luxurious, SealKind.Plates, peak.Id, AdventureHash.Of(peak.Id, 1), "VS"));
        }
        // edelweiss on the walk to the chapel (offer them there), and two more apart
        for (int i = 0; i < 7; i++)
        {
            double e = startE - 20 - i * 14, n = startN - 20 - i * 12;
            list.Add(new RewardSpot(RewardRules.CellId(RewardRules.EdelweissPrefix, e, n), SpotKind.Edelweiss, e, n, ground,
                Hash: AdventureHash.Of((long)e, (long)n, 0xedeUL), Canton: "VS"));
        }
        // a cairn at the fixture pass
        if (catalogue?.OfKind(PoiKind.Pass) is { Count: > 0 } passes)
        {
            var pass = passes[0];
            list.Add(new RewardSpot(Cairns.IdOf(pass), SpotKind.Cairn, pass.E + 10, pass.N, ground, PoiId: pass.Id,
                Hash: AdventureHash.Of(pass.Id, 3), Canton: "VS"));
        }
        return list;
    }

    /// <summary>A spots file: <c>kind tier seal dE dN</c> per line (<c>chest fine bonfire 40 -10</c>, <c>edelweiss - - 3 4</c>), # comments.</summary>
    public static List<RewardSpot> Parse(IEnumerable<string> lines, double startE, double startN, float ground)
    {
        var list = new List<RewardSpot>();
        foreach (string raw in lines)
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 5) continue;
            double e = startE + double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
            double n = startN + double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture);
            var kind = p[0] switch { "edelweiss" => SpotKind.Edelweiss, "cairn" => SpotKind.Cairn, _ => SpotKind.Chest };
            Enum.TryParse<ChestTier>(p[1], true, out var tier);
            Enum.TryParse<SealKind>(p[2], true, out var seal);
            string prefix = kind switch { SpotKind.Edelweiss => RewardRules.EdelweissPrefix, SpotKind.Cairn => RewardRules.CairnPrefix, _ => RewardRules.ChestPrefix };
            list.Add(new RewardSpot(RewardRules.CellId(prefix, e, n), kind, e, n, ground, tier, kind == SpotKind.Chest ? seal : SealKind.None,
                Hash: AdventureHash.Of((long)e, (long)n, 0xf11eUL)));
        }
        return list;
    }
}
