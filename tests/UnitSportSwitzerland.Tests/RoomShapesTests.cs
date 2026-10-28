using Godot;
using UnitSport.Interiors;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The polygons rooms that follow a building's outline are made of (#1290): the outline traced off
/// a grid of roof cells with its angled facades straight again, rectangles cut by it, floors
/// triangulated, walls moved in.
/// </summary>
public class RoomShapesTests
{
    private const float Cell = 0.25f;

    /// <summary>A grid of <paramref name="w"/> x <paramref name="d"/> m whose cells (by their centre) <paramref name="covered"/> says.</summary>
    private static bool[,] Grid(float w, float d, Func<float, float, bool> covered)
    {
        int nx = (int)MathF.Ceiling(w / Cell), nz = (int)MathF.Ceiling(d / Cell);
        var g = new bool[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                g[i, j] = covered((i + 0.5f) * Cell - w / 2, (j + 0.5f) * Cell - d / 2);
        return g;
    }

    [Fact]
    public void AnLComesBackAsSixCorners()
    {
        // 20 x 12 m, the back right quarter open
        var rings = RoomShapes.Trace(Grid(20, 12, (x, z) => !(x > 4 && z > 0)), Cell, 20, 12, 0.3f);
        var ring = Assert.Single(rings);
        Assert.Equal(6, ring.Length);
        Assert.InRange(RoomShapes.Area(ring), 240 - 36 - 3, 240 - 36 + 3);
    }

    [Fact]
    public void AnAngledFacadeIsOneStraightEdge()
    {
        // a trapezoid narrowing from 20 m at the front to 12 m at the back, 10 m deep
        var rings = RoomShapes.Trace(Grid(20, 10, (x, z) => Math.Abs(x) < 10 - 4 * (z + 5) / 10), Cell, 20, 10, 0.3f);
        var ring = Assert.Single(rings);
        Assert.Equal(4, ring.Length);
        Assert.True(RoomShapes.Area(ring) > 0, "counter-clockwise");
        Assert.InRange(RoomShapes.Area(ring), 160 - 4, 160 + 4);
        // every corner within a cell of the real one
        foreach (var real in new[] { new Vector2(-10, -5), new Vector2(10, -5), new Vector2(6, 5), new Vector2(-6, 5) })
            Assert.Contains(ring, p => p.DistanceTo(real) < 0.4f);
    }

    [Fact]
    public void ACourtyardIsAHoleWoundTheOtherWay()
    {
        var rings = RoomShapes.Trace(Grid(30, 30, (x, z) => Math.Abs(x) > 6 || Math.Abs(z) > 6), Cell, 30, 30, 0.3f);
        Assert.Equal(2, rings.Count);
        Assert.Single(rings, r => RoomShapes.Area(r) > 0);
        Assert.Single(rings, r => RoomShapes.Area(r) < 0);
        Assert.False(RoomShapes.Contains(rings, Vector2.Zero));
        Assert.True(RoomShapes.Contains(rings, new Vector2(10, 10)));
    }

    [Fact]
    public void ARectangleCutByAnAngledWall()
    {
        // a square whose right side leans in toward the back
        Vector2[] ring = [new(0, 0), new(10, 0), new(6, 10), new(0, 10)];
        var cut = RoomShapes.ClipToRect(ring, 2, 2, 9, 8, out bool split);
        Assert.False(split);
        Assert.True(RoomShapes.Area(cut) > 0);
        // the right part follows the slant: at z = 8 the room ends at x = 6.8
        Assert.Contains(cut, p => Math.Abs(p.X - 6.8f) < 0.01f && Math.Abs(p.Y - 8) < 0.01f);
        Assert.Contains(cut, p => Math.Abs(p.X - 9) < 0.01f && Math.Abs(p.Y - 2.5f) < 0.01f);
    }

    [Fact]
    public void ARectangleAcrossAUsMouthIsSplit()
    {
        // a U open at the back: the rectangle across its arms meets it twice
        Vector2[] u = [new(0, 0), new(10, 0), new(10, 10), new(7, 10), new(7, 4), new(3, 4), new(3, 10), new(0, 10)];
        RoomShapes.ClipToRect(u, 1, 6, 9, 9, out bool split);
        Assert.True(split);
        RoomShapes.ClipToRect(u, 1, 1, 9, 3, out bool whole);
        Assert.False(whole);
    }

    [Fact]
    public void AnLFloorTriangulatesToItsArea()
    {
        Vector2[] l = [new(0, 0), new(8, 0), new(8, 3), new(3, 3), new(3, 7), new(0, 7)];
        var tris = RoomShapes.Triangulate(l);
        Assert.Equal(4 * 3, tris.Count);
        float sum = 0;
        for (int i = 0; i < tris.Count; i += 3)
        {
            float a = RoomShapes.Area(new[] { l[tris[i]], l[tris[i + 1]], l[tris[i + 2]] });
            Assert.True(a > 0, "each triangle counter-clockwise");
            sum += a;
        }
        Assert.Equal(RoomShapes.Area(l), sum, 3);
    }

    [Fact]
    public void AWallMovedInKeepsItsAngle()
    {
        Vector2[] ring = [new(0, 0), new(10, 0), new(6, 10), new(0, 10)];
        var inner = RoomShapes.Inset(ring, 0.1f);
        // each inner edge 0.1 m in from its outer one, and parallel
        for (int i = 0; i < 4; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % 4];
            Assert.Equal(0.1f, RoomShapes.SegmentDistance(a, b, (inner[i] + inner[(i + 1) % 4]) / 2), 3);
        }
    }

    [Fact]
    public void ARayStopsAtTheFirstWall()
    {
        Vector2[] ring = [new(0, 0), new(10, 0), new(6, 10), new(0, 10)];
        float t = RoomShapes.Cast([ring], new Vector2(5, 5), new Vector2(1, 0), 50, out int r, out int e);
        Assert.Equal(0, r);
        Assert.Equal(1, e);
        Assert.Equal(3f, t, 3);
    }
}
