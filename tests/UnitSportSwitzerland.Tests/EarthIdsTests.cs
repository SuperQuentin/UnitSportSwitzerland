using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnitSport.Terrain.Format;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The Earth ids of what is generated per leaf (#1514, G2 C5, tools/TerrainFormat/EarthIds.cs): a building's id from
/// its footprint's centre, the spots' ~10 m cube cells, the per-leaf lottery cells.
/// </summary>
public class EarthIdsTests
{
    private readonly ITestOutputHelper _out;

    public EarthIdsTests(ITestOutputHelper output) => _out = output;

    /// <summary>A 10 × 6 m box house (roof, floor, two walls) at plan (x0, z0) in its frame, turned by <paramref name="turn"/>.</summary>
    private static Building House(double x0, double z0, double turn, float y0 = 400)
    {
        var pts = new List<float>();
        (float X, float Z) P(double x, double z) => ((float)(x0 + x * Math.Cos(turn) - z * Math.Sin(turn)), (float)(z0 + x * Math.Sin(turn) + z * Math.Cos(turn)));
        void Tri((float X, float Z) a, (float X, float Z) b, (float X, float Z) c, float y) =>
            pts.AddRange(new[] { a.X, y, a.Z, b.X, y, b.Z, c.X, y, c.Z });
        var (a, b, c, d) = (P(-5, -3), P(5, -3), P(5, 3), P(-5, 3));
        foreach (float y in new[] { y0, y0 + 7 }) { Tri(a, b, c, y); Tri(a, c, d, y); }
        // a wall: edge-on from above, left out of the centre
        pts.AddRange(new[] { a.X, y0, a.Z, b.X, y0, b.Z, b.X, y0 + 7, b.Z });
        return new Building { Kind = BuildingKind.House, Triangles = pts.ToArray(), MinY = y0, MaxY = y0 + 7 };
    }

    [Fact]
    public void The_footprint_centre_turns_with_the_building()
    {
        foreach (double turn in new[] { 0, 0.3, 1.1 })
        {
            var c = BuildingId.FootprintCentre(House(120, 340, turn).Triangles)!.Value;
            Assert.Equal(120, c.X, 4);
            Assert.Equal(340, c.Z, 4);
            Assert.Equal(400, c.Y, 4);
        }
    }

    [Fact]
    public void An_lv95_tile_and_a_leaf_give_a_building_the_same_id()
    {
        // a house at LV95 2,600,123 / 1,200,456, 550 m, laid out in its LV95 tile and in its leaf's anchor frame
        var tile = new TileId(2600, 1200);
        var (x, z) = tile.Local(2_600_123, 1_200_456);
        var lv95 = BuildingId.OnLv95Tile(tile, House(x, z, 0, 550))!.Value;
        var p = SwissGeodesy.ToEcef(2_600_123, 1_200_456, 550);
        var leaf = CubeKey.FromEcef(p, 14);
        var (lx, ly, lz) = leaf.Anchor.ToLocal(p);
        // the grid and the leaf's east differ by the convergence: the leaf's house is turned (here by 0.0°-2°, any)
        var onLeaf = BuildingId.OnLeaf(leaf, House(lx, lz, 0.02, (float)ly))!.Value;
        Assert.Equal(lv95, onLeaf);
        Assert.Equal(BuildingId.Of(p), lv95);
    }

    [Fact]
    public void Text_round_trips_and_duplicates_are_numbered()
    {
        var a = new BuildingId(4_620_412, 614_871);
        var b = new BuildingId(-3_390_000, -15_120_000, 2);
        Assert.Equal("b4620412_614871", a.ToString());
        Assert.True(BuildingId.TryParse(a.ToString(), out var a2) && a2 == a);
        Assert.True(BuildingId.TryParse(b.ToString(), out var b2) && b2 == b);
        Assert.False(BuildingId.TryParse("2600_1200_3", out _)); // a BuildingKey's text is not one
        var ids = BuildingId.Assign(new[] { House(0, 0, 0), House(0.1, 0.1, 0), House(50, 0, 0) },
            h => BuildingId.OnLv95Tile(new TileId(2600, 1200), h));
        Assert.Equal(0, ids[0]!.Value.Dup);
        Assert.Equal(1, ids[1]!.Value.Dup);
        Assert.Equal(0, ids[2]!.Value.Dup);
    }

    [Fact]
    public void Spot_ids_name_their_ten_metre_cell()
    {
        var p = SwissGeodesy.ToEcef(2_600_123, 1_200_456, 550);
        string id = EarthCells.SpotId("ch", p);
        Assert.StartsWith("ch:F", id);
        Assert.DoesNotContain("_", id);
        var cell = EarthCells.CellOf(id)!.Value;
        Assert.Equal(CubeKey.FromEcef(p, EarthCells.SpotLevel), cell);
        Assert.InRange(Math.Sqrt(EarthCells.Area(cell)), 5, 12);
        Assert.Null(EarthCells.CellOf("ch:2600120_1200450"));
    }

    [Fact]
    public void A_leaf_holds_sixteen_lottery_cells_drawn_as_densely_as_lv95()
    {
        var leaf = CubeKey.FromEcef(SwissGeodesy.ToEcef(2_600_000, 1_200_000, 500), 14);
        var cells = EarthCells.LotteryCells(leaf).ToList();
        Assert.Equal(16, cells.Count);
        Assert.Equal(16, cells.Distinct().Count());
        Assert.All(cells, c => Assert.Equal(leaf, c.AncestorAt(14)));
        double area = cells.Sum(EarthCells.Area);
        // expected draws over the leaf equal LV95's over the same area, to first order and beyond
        double p = 0.085;
        double leafDraws = cells.Sum(c => EarthCells.Chance(p, EarthCells.Area(c)));
        double lv95Draws = p * area / EarthCells.Lv95CellArea;
        Assert.Equal(lv95Draws, leafDraws, 9);
        _out.WriteLine($"leaf {Math.Sqrt(area):F0} m a side, cells {Math.Sqrt(area / 16):F0} m");
    }

    /// <summary>
    /// On B4's D: region (both builds on D:, skipped elsewhere): every building whose EGID is in both the LV95 build and
    /// the leaf build gets the same id from each, but for the few whose centre sits on a unit's edge.
    /// </summary>
    [Fact]
    public void The_lv95_and_leaf_builds_of_the_d_region_agree_on_building_ids()
    {
        const string root = "D:/UnitSportSwitzerland/test_b3/b4";
        string lv95Dir = Path.Combine(root, "terrain_chunks"), cubeDir = Path.Combine(root, "terrain_chunks_cube");
        if (!Directory.Exists(lv95Dir) || !Directory.Exists(cubeDir)) { _out.WriteLine("no B4 data: skipped"); return; }

        // two bundles of leaves (Geneva's side of the region), and the LV95 tiles under them
        // an EGID can name several solids (a house and its annex): compare the sets
        var leafIds = new Dictionary<uint, HashSet<BuildingId>>();
        var tiles = new HashSet<TileId>();
        foreach (string bundle in Directory.GetFiles(cubeDir, "*.cubl").OrderBy(f => f, StringComparer.Ordinal).Take(2))
            foreach (var (key, bytes) in CubeBundle.ReadAllEntries(bundle))
            {
                var leaf = CubeLeafContent.Decode(bytes);
                if (leaf.Buildings == null) continue;
                foreach (var b in leaf.Buildings.Buildings)
                {
                    if (b.Egid == 0 || BuildingId.OnLeaf(key, b) is not { } id) continue;
                    if (!leafIds.TryGetValue(b.Egid, out var set)) leafIds[b.Egid] = set = new();
                    set.Add(id with { Dup = 0 });
                    var (e, n) = SwissGeodesy.ToLv95(id.LatDeg, id.LonDeg);
                    tiles.Add(TileId.FromLv95(e, n));
                }
            }
        int same = 0, differ = 0, missing = 0;
        foreach (var tile in tiles)
        {
            string path = Path.Combine(lv95Dir, BuildingFormat.FileName(tile));
            if (!File.Exists(path)) continue;
            using var f = File.OpenRead(path);
            foreach (var b in BuildingCodec.Decode(f).Buildings)
            {
                if (b.Egid == 0 || !leafIds.TryGetValue(b.Egid, out var leafSet)) continue;
                if (BuildingId.OnLv95Tile(tile, b) is not { } id) { missing++; continue; }
                if (leafSet.Contains(id with { Dup = 0 })) same++;
                else
                {
                    differ++;
                    if (differ <= 5) _out.WriteLine($"EGID {b.Egid}: {id} vs {string.Join(", ", leafSet)}");
                }
            }
        }
        _out.WriteLine($"{leafIds.Count} EGIDs on the leaves, {tiles.Count} LV95 tiles: {same} same, {differ} differ, {missing} no footprint");
        Assert.True(same > 1000, $"only {same} compared");
        Assert.True(differ <= same / 500 + 2, $"{differ} of {same + differ} differ");
    }
}
