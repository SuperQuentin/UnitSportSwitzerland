using System.Runtime.CompilerServices;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>What a group of buildings is, beyond the per-solid <see cref="BuildingKind"/>.</summary>
public enum BuildingType : byte
{
    None = 0, Church = 1, Bank = 2,
    // stored plans hold these as numbers: new types go on the end
    // #497: industrial sites, a pure function of the building (BuildingTypes.SiteFor)
    Warehouse = 3, Factory = 4, Depot = 5, Mechanic = 6, Dealership = 7,
    // #557: a block of flats, and a city block with shops under its flats
    Apartments = 8, MixedUse = 9,
}

/// <summary>The role one solid plays in its group.</summary>
public enum BuildingPart : byte { None = 0, Nave = 1, Tower = 2 }

/// <summary>
/// Solids that are one building to a visitor. <see cref="Members"/>[0] is the primary: the
/// group's interior is stored under its key, whichever member's door was used.
/// </summary>
public sealed record BuildingGroup(BuildingType Type, IReadOnlyList<int> Members, IReadOnlyList<BuildingPart> Parts)
{
    public int Primary => Members[0];

    /// <summary>
    /// Identifies the group's makeup, so a stored interior is regenerated when the grouping or a
    /// member's geometry changes. <paramref name="rules"/> is the generator's rules version.
    /// </summary>
    public string Fingerprint(BuildingTile tile, int rules) =>
        $"{Type}{rules}:" + string.Join(",", Members.Select(m => $"{m}/{tile.Buildings[m].TriangleCount}"));
}

/// <summary>Detection result for one tile: each building's plan box and the groups found.</summary>
public sealed class BuildingTypeMap
{
    private readonly int[] _group;
    private readonly BuildingPart[] _part;

    public PlanBox?[] Boxes { get; }
    public IReadOnlyList<BuildingGroup> Groups { get; }

    internal BuildingTypeMap(PlanBox?[] boxes, List<BuildingGroup> groups)
    {
        Boxes = boxes;
        Groups = groups;
        _group = Enumerable.Repeat(-1, boxes.Length).ToArray();
        _part = new BuildingPart[boxes.Length];
        for (int g = 0; g < groups.Count; g++)
            for (int m = 0; m < groups[g].Members.Count; m++)
            {
                _group[groups[g].Members[m]] = g;
                _part[groups[g].Members[m]] = groups[g].Parts[m];
            }
    }

    public BuildingGroup? GroupOf(int index) =>
        index >= 0 && index < _group.Length && _group[index] >= 0 ? Groups[_group[index]] : null;

    public BuildingPart PartOf(int index) => index >= 0 && index < _part.Length ? _part[index] : BuildingPart.None;

    public BuildingType TypeOf(int index) => GroupOf(index)?.Type ?? BuildingType.None;
}

/// <summary>
/// Recognises building types from what a <c>.bldg</c> tile carries: kind, plan box and height.
/// The data has no notion of "a church" — swissBUILDINGS3D draws the nave and its bell tower as
/// two solids, and the procedural world does the same — so the grouping is inferred here, as a
/// pure function of the tile. Server and clients compute it from the same bytes and agree on it
/// without it ever being sent.
///
/// <para>
/// <b>Church</b>: a hall (Sacral, or Other/Civic when its tower is the Sacral one — the GWR point
/// often lands in only one of the two solids) of at least <see cref="HallMinArea"/>, plus up to two
/// towers touching it: tall, slender, roughly square in plan, rising above the hall's roof, lined
/// up with it, and sharing a wall long enough for a doorway. A Sacral hall with no tower is a
/// church of one member. Towers never pair across tile edges: a building belongs to the tile
/// holding its centre, and the rare church split that way stays two buildings.
/// </para>
/// </summary>
public static class BuildingTypes
{
    // ---- industrial sites (#497) ---------------------------------------------------------------

    /// <summary>A hall below this is a shed or a single-car box, not a site.</summary>
    public const float SiteMinArea = 60f;
    /// <summary>A hall this big is a distribution shed or a works, never a one-man workshop.</summary>
    public const float SiteBigArea = 600f;
    /// <summary>Above this, a hall of any size is a works: the height is machinery, not storage.</summary>
    public const float SiteTallWall = 9f;
    /// <summary>A <see cref="BuildingKind.Garage"/> this big is a trade workshop, not somebody's garage.</summary>
    public const float TradeGarageArea = 120f;
    /// <summary>And this big, often a dealership: a showroom needs frontage.</summary>
    public const float DealerArea = 200f;

    /// <summary>
    /// Which kind of industrial site a building is (#497), or <see cref="BuildingType.None"/> for
    /// anything that is not one. A pure function of the building — footprint box, wall height, kind
    /// and a stable hash of its key — in the same spirit as <see cref="BuildingFootprint.IsBank"/>
    /// and <c>ShopTables.TypeFor</c>: the data says "industrial building" and nothing more, so which
    /// industry it is has to be invented, and invented identically on the server and on every
    /// client without a byte being sent.
    ///
    /// <para>
    /// Geometry decides what it can be, the hash decides between what is left. A big low hall is a
    /// warehouse or a works; a big tall one is always a works (that height is a crane, a silo run or
    /// a press, not pallet racking); a medium hall is a works, a haulier's depot or a body shop; a
    /// trade-sized garage is a body shop, a depot or — with the frontage for a showroom — a
    /// dealership.
    /// </para>
    /// </summary>
    public static BuildingType SiteFor(string key, BuildingKind kind, float width, float depth, float wallHeight)
    {
        float area = width * depth, longSide = Math.Max(width, depth);
        // a tall slender solid is a silo or a tank: it has no floors to walk, so it is no site
        if (longSide <= 14f && wallHeight >= Math.Max(10f, 1.6f * longSide)) return BuildingType.None;

        if (kind == BuildingKind.Industrial)
        {
            if (area < SiteMinArea) return BuildingType.None;
            double roll = Core.Fnv.Unit(key + "|site");
            if (area >= SiteBigArea)
                return wallHeight >= SiteTallWall ? BuildingType.Factory
                    : roll < 0.55 ? BuildingType.Warehouse : BuildingType.Factory;
            if (wallHeight >= SiteTallWall) return BuildingType.Factory;
            if (area >= DealerArea)
                return roll < 0.40 ? BuildingType.Warehouse
                    : roll < 0.75 ? BuildingType.Factory : BuildingType.Depot;
            return roll < 0.5 ? BuildingType.Depot : BuildingType.Mechanic;
        }

        if (kind == BuildingKind.Garage && area >= TradeGarageArea)
        {
            double roll = Core.Fnv.Unit(key + "|trade");
            if (area >= DealerArea)
                return roll < 0.35 ? BuildingType.Dealership
                    : roll < 0.65 ? BuildingType.Depot : BuildingType.Mechanic;
            return roll < 0.75 ? BuildingType.Mechanic : BuildingType.Depot;
        }

        return BuildingType.None;
    }

    /// <summary>Whether a type is one of the industrial sites, rather than a church or a bank.</summary>
    public static bool IsSite(BuildingType type) =>
        type is BuildingType.Warehouse or BuildingType.Factory or BuildingType.Depot
            or BuildingType.Mechanic or BuildingType.Dealership;

    /// <summary>Whether a site's main hall is driven into — every one of them but a dealership's showroom.</summary>
    public static bool DrivenInto(BuildingType type) =>
        type is BuildingType.Warehouse or BuildingType.Factory or BuildingType.Depot or BuildingType.Mechanic;

    // ---- churches ------------------------------------------------------------------------------

    public const float HallMinArea = 40f;
    private const float HallMinLong = 8f;
    private const float TowerMinSide = 2.5f, TowerMaxSide = 12f, TowerMaxAspect = 1.6f;
    private const float TowerMinHeight = 12f, TowerSlenderness = 2.2f;
    private const float PairMaxGap = 1.5f, PairMinShared = 1.5f, PairMaxSkewDeg = 15f;
    private const int MaxTowers = 2;

    private static readonly ConditionalWeakTable<BuildingTile, BuildingTypeMap> Cache = new();

    /// <summary>The tile's detection, computed once per tile object. Thread safe.</summary>
    public static BuildingTypeMap For(BuildingTile tile) => Cache.GetValue(tile, Detect);

    public static BuildingTypeMap Detect(BuildingTile tile)
    {
        int n = tile.Buildings.Count;
        var boxes = new PlanBox?[n];
        for (int i = 0; i < n; i++) boxes[i] = PlanBox.Of(tile.Buildings[i]);

        var towers = new List<int>();
        var halls = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (boxes[i] is not { } box) continue;
            var b = tile.Buildings[i];
            if (IsTower(b, box)) towers.Add(i);
            else if (b.Kind is BuildingKind.Sacral or BuildingKind.Other or BuildingKind.Civic
                     && box.Area >= HallMinArea && box.Long >= HallMinLong)
                halls.Add(i);
        }

        // each tower joins the hall it fits best: nearest, then largest
        var towersOf = new Dictionary<int, List<int>>();
        foreach (int t in towers)
        {
            int best = -1;
            float bestGap = float.MaxValue, bestArea = 0;
            foreach (int h in halls)
            {
                if (!Pairs(tile.Buildings[h], boxes[h]!.Value, tile.Buildings[t], boxes[t]!.Value, out float gap)) continue;
                float area = boxes[h]!.Value.Area;
                if (gap < bestGap - 0.05f || Math.Abs(gap - bestGap) <= 0.05f && area > bestArea)
                {
                    best = h;
                    bestGap = gap;
                    bestArea = area;
                }
            }
            if (best < 0) continue;
            if (!towersOf.TryGetValue(best, out var list)) towersOf[best] = list = new List<int>();
            list.Add(t);
        }

        var groups = new List<BuildingGroup>();
        foreach (int h in halls)
        {
            towersOf.TryGetValue(h, out var mine);
            if (mine == null && tile.Buildings[h].Kind != BuildingKind.Sacral) continue;
            var members = new List<int> { h };
            var parts = new List<BuildingPart> { BuildingPart.Nave };
            // the tallest towers, if a hall somehow has more than a church would
            foreach (int t in (mine ?? new List<int>()).OrderByDescending(t => tile.Buildings[t].MaxY).ThenBy(t => t).Take(MaxTowers))
            {
                members.Add(t);
                parts.Add(BuildingPart.Tower);
            }
            groups.Add(new BuildingGroup(BuildingType.Church, members, parts));
        }
        return new BuildingTypeMap(boxes, groups);
    }

    private static bool IsTower(Building b, PlanBox box)
    {
        if (b.Kind is BuildingKind.House or BuildingKind.Apartment or BuildingKind.Commercial
            or BuildingKind.Industrial or BuildingKind.Agricultural or BuildingKind.UnderConstruction) return false;
        float height = b.MaxY - b.MinY;
        return box.Short >= TowerMinSide && box.Long <= TowerMaxSide && box.Long / box.Short <= TowerMaxAspect
            && height >= Math.Max(TowerMinHeight, TowerSlenderness * box.Long);
    }

    private static bool Pairs(Building hall, PlanBox hb, Building tower, PlanBox tb, out float gap)
    {
        gap = hb.GapTo(tb);
        if (gap > PairMaxGap) return false;
        if (hall.Kind != BuildingKind.Sacral && tower.Kind != BuildingKind.Sacral) return false;
        if (tower.MaxY < hall.MaxY + 2f) return false;
        if (tb.Area > 0.6f * hb.Area) return false;

        // lined up: towers are built square to their church
        float skew = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(Math.Abs(hb.AxisU.Dot(tb.AxisU)), 0f, 1f)));
        skew = Math.Min(skew, 90f - skew);
        if (skew > PairMaxSkewDeg) return false;

        // sharing a wall long enough to walk through: overlap across the side the tower is on
        var (hu0, hu1) = hb.Along(hb.AxisU, hb.Center);
        var (hv0, hv1) = hb.Along(hb.AxisV, hb.Center);
        var (tu0, tu1) = tb.Along(hb.AxisU, hb.Center);
        var (tv0, tv1) = tb.Along(hb.AxisV, hb.Center);
        float outU = Math.Max(tu0 - hu1, hu0 - tu1), outV = Math.Max(tv0 - hv1, hv0 - tv1);
        float shared = outU >= outV
            ? Math.Min(hv1, tv1) - Math.Max(hv0, tv0)
            : Math.Min(hu1, tu1) - Math.Max(hu0, tu0);
        return shared >= PairMinShared;
    }
}
