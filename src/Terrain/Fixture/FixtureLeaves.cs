using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// A fixture course built as cube leaves in memory (#1509, G2 B5), beside the LV95 tiles its
/// <see cref="FixtureChunkSource"/> serves; the game reads them once C1 draws leaves, behind <c>--frame earth</c>.
/// The course is placed at a latitude and longitude (<see cref="CourseSource"/>: its metres on the plane tangent at the
/// start), not an LV95 point.
///
/// <para>
/// Lattice kinds are computed at every leaf vertex from the course's own functions (the ground every 8 vertices,
/// ~10 m, bilinear between, as the tile source holds it every 10 m; a <see cref="FixtureCourse.Terrain"/> at every
/// vertex). Roads are the course's lines cut at leaf edges (<see cref="LeafSplit"/>); car parks, painted lines and
/// blocks come from the tile source's own planners (the same <c>ParkingPlanner</c>), each record moved into the leaf
/// its first point or middle lies in (<see cref="SourceTileToLeaf"/>). Trees, fields and ski slopes from the course
/// (the slopes as the cube piste stage writes them, #1649). The fleet is no leaf data (vehicle slots are positions):
/// <see cref="FixtureChunkSource.Place"/> puts it where these leaves have its course points.
/// </para>
/// </summary>
public sealed class FixtureLeaves
{
    /// <summary>The ground's sampling step in lattice vertices (~10 m at level 14), as the tile source's 10 m lattice.</summary>
    public const int GroundStride = 8;

    private readonly FixtureChunkSource _tiles;
    private readonly FixtureCourse _course;
    private readonly LeafFrames _frames;
    private readonly HashSet<CubeKey> _leaves = new();
    private readonly Dictionary<CubeKey, List<(RoadClass Class, float Width, List<(double A, double B, double Z)> Piece)>> _roads = new();

    public CourseSource Source { get; }
    public IReadOnlySet<CubeKey> Leaves => _leaves;
    public CubeKey StartLeaf { get; }

    /// <summary>The leaves a course road crosses.</summary>
    public IEnumerable<CubeKey> RoadLeaves => _roads.Keys.Where(_leaves.Contains);
    public FixtureCourse Course => _course;

    /// <summary>The course of <paramref name="tiles"/> placed where its LV95 start is (a notation for that latitude and longitude).</summary>
    public static FixtureLeaves At(FixtureChunkSource tiles, LeafFrames? frames = null)
    {
        // the placement what stands on the course outside its leaves follows (the fleet, #1649): one plane for both
        if (tiles.Place.Earth is { } placed) return new FixtureLeaves(tiles, placed, frames);
        var (e, n) = tiles.Start;
        var (lat, lon) = Lv95Source.Instance.ToLatLon(e, n);
        return new FixtureLeaves(tiles, lat, lon, frames);
    }

    public FixtureLeaves(FixtureChunkSource tiles, double startLatDeg, double startLonDeg, LeafFrames? frames = null)
        : this(tiles, new CourseSource(startLatDeg, startLonDeg, tiles.Course.Ground(0, 0)), frames) { }

    private FixtureLeaves(FixtureChunkSource tiles, CourseSource source, LeafFrames? frames)
    {
        _tiles = tiles;
        _course = tiles.Course;
        _frames = frames ?? LeafFrames.Swiss;
        Source = source;
        StartLeaf = SourceOnLeaf.LeafOf(Source, 0, 0);

        // the leaves under the course's box and its margin, the tile source's (a leaf is ~610 m: a 100 m probe misses none)
        var (minX, minY, maxX, maxY) = _course.Bounds(_course.Roads.Count == 0 ? 100 : 400);
        for (double x = minX; x < maxX + 100; x += 100)
            for (double y = minY; y < maxY + 100; y += 100)
                _leaves.Add(SourceOnLeaf.LeafOf(Source, Math.Min(x, maxX), Math.Min(y, maxY)));

        for (int road = 0; road < _course.Roads.Count; road++)
        {
            var (cls, pts) = _course.Roads[road];
            float width = _course.RoadWidths.TryGetValue(road, out float w) ? w : RoadFormat.DefaultWidth(cls);
            foreach (var (leaf, piece) in LeafSplit.AtLeaves(Source, pts))
            {
                if (!_roads.TryGetValue(leaf, out var list)) _roads[leaf] = list = new();
                list.Add((cls, width, piece));
            }
        }
    }

    /// <summary>A leaf of the course, every kind it has; null outside it. Pure, thread-safe, ~0.5 s of one core.</summary>
    public CubeLeafContent? Build(CubeKey leaf)
    {
        if (!_leaves.Contains(leaf)) return null;
        var on = new SourceOnLeaf(_frames, leaf, Source);
        var content = new CubeLeafContent
        {
            Key = leaf,
            Heights = _course.Terrain is { } terrain
                ? LeafLattice.Heights(on, (a, b) => terrain(a, b))
                : LeafLattice.Heights(on, _course.Ground, GroundStride),
            Cover = LeafLattice.Cover(on, _course.Cover ?? ((_, _) => default)),
            Water = _course.Water is { } water ? LeafLattice.Water(on, (a, b) => water(a, b)) : null,
            Trees = Trees(on),
            Fields = Fields(on),
            Pistes = Pistes(on),
        };
        var (roads, buildings) = FromTiles(on);
        content.Roads = roads;
        content.Buildings = buildings;
        return content;
    }

    private List<TreeInstance> Trees(SourceOnLeaf on)
    {
        var trees = new List<TreeInstance>();
        foreach (var (x, y, _, height) in _course.Trees)
        {
            if (!on.Holds(x, y)) continue;
            var (lx, ly, lz) = on.Local(x, y, _course.Ground(x, y));
            trees.Add(new TreeInstance((float)lx, (float)ly, (float)lz, height, 0));
        }
        return trees;
    }

    /// <summary>
    /// The course's ski slopes on the leaf (#1649), as the tile source writes them on a tile and the cube piste stage on
    /// a leaf (#1548): one area record per slope whose box reaches the leaf (its outline in the anchor frame, on the
    /// ground), the slot raster at every lattice vertex inside it (row-major from the south-west), the edge mask
    /// ramping in over <see cref="PisteFormat.EdgeRampM"/>, the groom phase across the slope; null with none.
    /// </summary>
    private PisteTile? Pistes(SourceOnLeaf on)
    {
        if (_course.Slopes.Count == 0) return null;
        var (minA, minB, maxA, maxB) = on.Box();
        int n = on.Side;
        var records = new List<PisteRecord>();
        byte[]? slot = null;
        ushort[]? aux = null;
        foreach (var slope in _course.Slopes)
        {
            double minX = slope.Outline.Min(p => p.X), maxX = slope.Outline.Max(p => p.X);
            double minY = slope.Outline.Min(p => p.Y), maxY = slope.Outline.Max(p => p.Y);
            if (maxX < minA || minX > maxA || maxY < minB || minY > maxB) continue;
            var path = new float[slope.Outline.Length * 2];
            for (int k = 0; k < slope.Outline.Length; k++)
            {
                var (x, _, z) = on.Local(slope.Outline[k].X, slope.Outline[k].Y, _course.Ground(slope.Outline[k].X, slope.Outline[k].Y));
                (path[k * 2], path[k * 2 + 1]) = ((float)x, (float)z);
            }
            var (low, high, sun, steep) = FixtureChunkSource.SlopeTerrain(_course, slope);
            records.Add(new PisteRecord(0xF0F0_0000UL + (ulong)_course.Slopes.IndexOf(slope), PisteType.Downhill, slope.Difficulty,
                PisteGrooming.Classic, true, (float)(maxX - minX), slope.Name, "", path, low, high, sun, steep));
            byte index = (byte)records.Count;
            slot ??= new byte[n * n];
            aux ??= new ushort[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var (x, y) = on.SourceAt(i, j);
                    if (x < minX || x > maxX || y < minY || y > maxY || !FixtureChunkSource.Inside(slope.Outline, x, y)) continue;
                    double edge = Math.Min(Math.Min(x - minX, maxX - x), Math.Min(y - minY, maxY - y));
                    slot[i + j * n] = index;
                    aux[i + j * n] = PisteFormat.PackAux((float)Math.Clamp(edge / PisteFormat.EdgeRampM, 0, 1), x - minX);
                }
        }
        return records.Count == 0 ? null : new PisteTile(default, records, slot!, aux!);
    }

    /// <summary>The course's fields, each whole in every leaf its box reaches (as the tile source writes them per tile).</summary>
    private List<FieldPolygon> Fields(SourceOnLeaf on)
    {
        var fields = new List<FieldPolygon>();
        var (minA, minB, maxA, maxB) = on.Box();
        foreach (var (fid, crop, outline) in _course.Fields)
        {
            if (outline.Max(p => p.X) < minA || outline.Min(p => p.X) > maxA || outline.Max(p => p.Y) < minB || outline.Min(p => p.Y) > maxB)
                continue;
            var ring = new float[outline.Length * 2];
            for (int i = 0; i < outline.Length; i++)
            {
                var (x, _, z) = on.Local(outline[i].X, outline[i].Y, _course.Ground(outline[i].X, outline[i].Y));
                (ring[i * 2], ring[i * 2 + 1]) = ((float)x, (float)z);
            }
            fields.Add(new FieldPolygon(fid, crop, FieldSource.Osm, 0, [ring]));
        }
        return fields;
    }

    /// <summary>
    /// The leaf's roads (the course's lines cut at its edges) and what the tile source planned on its tiles near the
    /// leaf: car parks, painted lines, blocks, each filed by the leaf its first point or middle lies in.
    /// </summary>
    private (RoadTile Roads, BuildingTile Buildings) FromTiles(SourceOnLeaf on)
    {
        var segments = new List<RoadSegment>();
        if (_roads.TryGetValue(on.Leaf, out var pieces))
            foreach (var (cls, width, piece) in pieces)
            {
                var xyz = new float[piece.Count * 3];
                for (int i = 0; i < piece.Count; i++)
                {
                    var (x, y, z) = on.Local(piece[i].A, piece[i].B, piece[i].Z);
                    (xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]) = ((float)x, (float)y, (float)z);
                }
                segments.Add(new RoadSegment { Class = cls, Surface = RoadSurface.Paved, Width = width, Points = xyz });
            }

        var roads = new RoadTile { Segments = segments };
        var buildings = new BuildingTile { Buildings = new() };
        var (minA, minB, maxA, maxB) = on.Box();
        foreach (var id in _tiles.TilesUnder(minA, minB, maxA, maxB))
            {
                var map = new SourceTileToLeaf(on, _tiles.CourseOf(id));
                if (_tiles.LoadRoadsAsync(id).Result is { } tile)
                {
                    // the leaf's records, a copy (the move is in place, and the tile source keeps its own)
                    var kept = new RoadTile
                    {
                        Segments = new(),
                        AreaProps = tile.AreaProps.Where(a => map.Holds(a.Vertices[0], a.Vertices[2])).ToList(),
                        Paint = tile.Paint.Where(p => map.Holds(p.Vertices[0], p.Vertices[2])).ToList(),
                        PointProps = tile.PointProps.Where(p => map.Holds(p.X, p.Z)).ToList(),
                        Parking = tile.Parking.Where(p => map.Holds(p.X, p.Z)).ToList(),
                    };
                    var copy = LeafRoads.Decode(LeafRoads.Encode(kept));
                    LeafRecords.Map(copy, map);
                    roads.AreaProps.AddRange(copy.AreaProps);
                    roads.Paint.AddRange(copy.Paint);
                    roads.PointProps.AddRange(copy.PointProps);
                    roads.Parking.AddRange(copy.Parking);
                }
                if (_tiles.LoadBuildingsAsync(id).Result is { } bt)
                    foreach (var b in bt.Buildings)
                    {
                        var (mx, mz) = SourceTileToLeaf.Middle(b);
                        if (map.Holds(mx, mz)) buildings.Buildings.Add(map.Move(b));
                    }
            }
        return (roads, buildings);
    }
}
