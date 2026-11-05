using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// RoadGen's network stage on work frames (#1507, G2 B3 4b): <see cref="FrameUnits"/> (a leaf as a tile, its square,
/// its own edges, its halo across a face edge) and <see cref="LeafRecords"/> (a tile's records into a leaf's anchor
/// frame and back, headings by the Jacobian). The LV95 instance is held byte-identical to main by a run (PR #1543).
/// </summary>
public class FrameNetworkTests
{
    private const int Level = 14;

    private static readonly CubeKey Geneva = CubeKey.FromLatLon(46.2044, 6.1432, Level);

    /// <summary>The last leaf of face 0 along u: its east neighbour is on the next face.</summary>
    private static readonly CubeKey Edge = new(0, Level, (1 << Level) - 1, 5000);

    private static FrameUnits UnitsOver(CubeKey leaf)
    {
        var driver = new FrameDriver([leaf], 0, SwissSeaLevel.Instance);
        var plan = driver.Frames[0];
        return new FrameUnits(plan.Frame, plan.Transform, Level);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_leaf_is_a_tile_in_its_square_with_its_own_edges(bool faceEdge)
    {
        var leaf = faceEdge ? Edge : Geneva;
        var u = UnitsOver(leaf);
        var id = u.Tile(leaf);
        Assert.Equal(leaf, u.Leaf(id));

        var t = u.TransformOf(id);
        var (cx, cy) = t.LatticeToPlane(t.Cells * 0.5, t.Cells * 0.5);
        Assert.Equal(id, u.TileAt(cx, cy));
        double west = u.West(id), north = u.North(id);
        Assert.Equal(FrameUnits.Box, north - u.South(id));
        Assert.Equal(0, west % FrameUnits.Snap);

        // the centre is inside and off the edges; a corner and an edge's middle are on the edge; past the edge is outside
        Assert.True(u.Inside(id, cx - west, north - cy));
        Assert.False(u.OnEdge(id, cx - west, north - cy, 0.01));
        var (ex, ey) = t.LatticeToPlane(t.Cells, t.Cells * 0.5);
        var (kx, ky) = t.LatticeToPlane(0, 0);
        Assert.True(u.OnEdge(id, ex - west, north - ey, 0.01));
        Assert.True(u.OnEdge(id, kx - west, north - ky, 0.01));
        Assert.True(u.OnAnyEdge(ex, ey, 0.01));
        double ox = ex + (ex - cx) * 0.02, oy = ey + (ey - cy) * 0.02;   // ~6 m out
        Assert.False(u.Inside(id, ox - west, north - oy));
        Assert.False(u.OnEdge(id, ox - west, north - oy, 0.01));

        // the halo: the leaf and its 8 neighbours, the face edge crossed
        var ring = u.Around(id, 1).Select(u.Leaf).ToList();
        Assert.Equal(9, ring.Count);
        Assert.Equal(leaf, ring[0]);
        Assert.Equal(ring.Count, ring.Distinct().Count());
        if (faceEdge) Assert.Contains(ring, k => k.Face != leaf.Face);
        Assert.Equal(25, u.Around(id, 2).Count());
    }

    private static RoadTile Sample(TileId id, float x, float y, float z)
    {
        var seg = new RoadSegment { Class = RoadClass.Major, Width = 7, Points = [x, y, z, x + 30, y + 1, z - 40, x + 80, y + 2, z - 60] };
        var tile = new RoadTile { Id = id, Segments = [seg] };
        tile.Junctions.Add(new RoadJunction { Vertices = [x, y, z, x + 5, y, z, x, y, z + 5], Indices = [0, 1, 2] });
        tile.PointProps.Add(new RoadPointProp(PointPropType.YieldSign, 0, PropFlags.Solid, x + 3, y, z + 2, 0.7f, 2.5f));
        tile.Approaches.Add(new RoadApproach { X = x + 1, Y = y, Z = z + 1, Heading = 2.1f });
        tile.Parking.Add(new ParkingBay(x + 10, y, z + 10, -1.2f, ParkingBayKind.Car, ParkingBayFlags.None));
        tile.JunctionPaths.Add(new RoadJunctionPath
        {
            Junction = tile.Junctions[0], Move = UnitSport.Terrain.Format.SignalMoves.Left,
            From = new JunctionPathEnd(x, y, z, 1.5f), To = new JunctionPathEnd(x + 4, y, z + 4, 1.5f), Points = [x, y, z, x + 4, y, z + 4],
        });
        return tile;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Records_go_into_the_leaf_and_back(bool faceEdge)
    {
        var leaf = faceEdge ? Edge : Geneva;
        var u = UnitsOver(leaf);
        var id = u.Tile(leaf);
        var t = u.TransformOf(id);
        var (cx, cy) = t.LatticeToPlane(t.Cells * 0.5, t.Cells * 0.5);
        float x = (float)(cx - u.West(id)) - 40, z = (float)(u.North(id) - cy) + 20, y = 412.5f;

        static void Near(float a, float b, double tol) => Assert.True(Math.Abs(a - b) < tol, $"{a} vs {b}");
        static void Angle(float a, float b, double tol = 1e-5) => Assert.True(Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)) < tol, $"{a} vs {b}");
        var before = Sample(id, x, y, z);
        var tile = Sample(id, x, y, z);
        LeafRecords.ToLeaf(tile, u);

        // into the anchor frame: the transform's own answer for a point and a heading
        var (lx, ly, lz) = t.PlaneToLocal(u.West(id) + x, u.North(id) - z, y);
        Assert.Equal(lx, tile.Segments[0].Points[0], 3);
        Assert.Equal(ly, tile.Segments[0].Points[1], 3);
        Assert.Equal(lz, tile.Segments[0].Points[2], 3);
        var p = before.PointProps[0];
        double bearing = t.HeadingToLocal(u.West(id) + p.X, u.North(id) - p.Z, -p.Heading * 180 / Math.PI, p.Y);
        Angle((float)(-bearing * Math.PI / 180), tile.PointProps[0].Heading, 1e-4);
        Assert.Equal(tile.JunctionPaths[0].From.X, tile.Junctions[0].Vertices[0]);   // one point, one place

        // and back: every position within a millimetre, every heading within 1e-5 rad
        LeafRecords.FromLeaf(tile, u, id);
        for (int k = 0; k < before.Segments[0].Points.Length; k++) Near(before.Segments[0].Points[k], tile.Segments[0].Points[k], 1e-3);
        for (int k = 0; k < before.Junctions[0].Vertices.Length; k++) Near(before.Junctions[0].Vertices[k], tile.Junctions[0].Vertices[k], 1e-3);
        Near(before.PointProps[0].X, tile.PointProps[0].X, 1e-3);
        Near(before.PointProps[0].Z, tile.PointProps[0].Z, 1e-3);
        Angle(before.PointProps[0].Heading, tile.PointProps[0].Heading);
        Near(before.Approaches[0].X, tile.Approaches[0].X, 1e-3);
        Angle(before.Approaches[0].Heading, tile.Approaches[0].Heading);
        Near(before.Parking[0].Z, tile.Parking[0].Z, 1e-3);
        Angle(before.Parking[0].Heading, tile.Parking[0].Heading);
        Near(before.JunctionPaths[0].To.X, tile.JunctionPaths[0].To.X, 1e-3);
        Near(before.JunctionPaths[0].Points[5], tile.JunctionPaths[0].Points[5], 1e-3);
    }
}
