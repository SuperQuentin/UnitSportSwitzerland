using System.Linq;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Diagnostics;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// <see cref="CapCheck"/> (#1821): a ground cap that paves over a street (a car road's or track's point deep inside it) is
/// found; arms trimmed to its edge, a footpath carried over it, a bridge or tunnel under or over it are not.
/// </summary>
public class CapCheckTests
{
    private static readonly TileId Id = new(2501, 1117);

    /// <summary>A 40 x 20 m ground cap from (10, 10) to (50, 30).</summary>
    private static RoadJunction Cap() => new()
    {
        Class = RoadClass.Road, Layer = 0,
        Vertices = [10, 374, 10, 50, 374, 10, 50, 374, 30, 10, 374, 30],
        Indices = [0, 1, 2, 0, 2, 3],
    };

    private static RoadSegment Line(RoadClass cls, float[] points, RoadFlags flags = RoadFlags.None) =>
        new() { Class = cls, Surface = RoadSurface.Paved, Width = 3, Points = points, Flags = flags };

    private static RoadTile Tile(params RoadSegment[] segments) =>
        new() { Id = Id, Segments = [.. segments], Junctions = [Cap()] };

    [Fact]
    public void ArmsEndingAtTheEdgeAreFine()
    {
        var tile = Tile(Line(RoadClass.Road, [0, 374, 20, 11, 374, 20]), Line(RoadClass.Road, [49, 374, 20, 80, 374, 20]));
        Assert.Empty(CapCheck.Swallowed(tile));
    }

    [Fact]
    public void AStreetRunningThroughIsFound()
    {
        // the quay's case: a track crossing the middle of the paving
        var tile = Tile(Line(RoadClass.Track, [30, 374, 0, 30, 374, 20, 30, 374, 40]));
        var hit = Assert.Single(CapCheck.Swallowed(tile));
        Assert.Equal(RoadClass.Track, hit.Class);
        Assert.Equal(10, hit.Depth, 3);
        Assert.Equal(Id.MinE + 30, hit.E, 3);
        Assert.Equal(Id.MaxN - 20, hit.N, 3);
    }

    [Fact]
    public void PathsBridgesAndTunnelsAreNotCounted()
    {
        var tile = Tile(
            Line(RoadClass.Path, [30, 374, 0, 30, 374, 20, 30, 374, 40]),
            Line(RoadClass.Road, [20, 380, 0, 20, 380, 20, 20, 380, 40], RoadFlags.Bridge),
            Line(RoadClass.Road, [40, 360, 0, 40, 360, 20, 40, 360, 40], RoadFlags.Tunnel));
        Assert.Empty(CapCheck.Swallowed(tile));
    }

    [Fact]
    public void ABridgeDeckCapIsNotAGroundCap()
    {
        var tile = Tile(Line(RoadClass.Track, [30, 374, 0, 30, 374, 20, 30, 374, 40]));
        tile.Junctions[0] = new RoadJunction { Class = RoadClass.Road, Layer = 1, Vertices = Cap().Vertices, Indices = Cap().Indices };
        Assert.Empty(CapCheck.Swallowed(tile).ToList());
    }
}
