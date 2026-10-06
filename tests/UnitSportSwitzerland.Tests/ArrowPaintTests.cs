using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Meshing;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Lane arrows climb with the road (#639): one height for the whole arrow put the head of one on a
/// climbing approach under the road, where it vanished from close up.
/// </summary>
public class ArrowPaintTests
{
    public static IEnumerable<object[]> Kinds =>
    [
        [PaintArrow.Straight], [PaintArrow.Left], [PaintArrow.Right],
        [PaintArrow.Straight | PaintArrow.Right], [PaintArrow.Straight | PaintArrow.Left],
    ];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_vertex_lies_on_a_climbing_road(PaintArrow kind)
    {
        // pointing north-east up a 10 % grade: the road's height at a point is the tail's plus a
        // tenth of how far ahead of the tail it lies
        double fx = Math.Sqrt(0.5), fz = -Math.Sqrt(0.5), tailX = 100, tailZ = 200, tailY = 400;
        double tipY = tailY + 0.1 * PaintEmitter.ArrowLength;
        var p = PaintEmitter.Arrow(tailX, tailY, tailZ, fx, fz, kind, tipY);
        for (int i = 0; i < p.Vertices.Length / 3; i++)
        {
            double ahead = (p.Vertices[i * 3] - tailX) * fx + (p.Vertices[i * 3 + 2] - tailZ) * fz;
            Assert.InRange(p.Vertices[i * 3 + 1] - (tailY + 0.1 * ahead), -1e-3, 1e-3);
        }
    }

    [Fact]
    public void A_level_arrow_stays_level()
    {
        var p = PaintEmitter.Arrow(10, 500, 10, 1, 0, PaintArrow.Left, 500);
        for (int i = 0; i < p.Vertices.Length / 3; i++)
            Assert.Equal(500f, p.Vertices[i * 3 + 1]);
    }
}
