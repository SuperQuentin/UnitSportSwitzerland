using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The arrows of a traffic light (#759) as convex polygons in units of a lens radius, straight on
/// pointing up and right as the viewer sees it: drawn lit in a green lens, dark in a red or yellow
/// one's mask (<see cref="SignalLamps"/>), and black on the white plate under a head
/// (<see cref="SignalBuilder"/>).
/// </summary>
public static class SignalGlyphs
{
    /// <summary>The arrow of <paramref name="moves"/>: straight on, straight on and a turn, or the turn; none for anything else.</summary>
    public static List<Vector2[]> Arrow(SignalMoves moves)
    {
        var into = new List<Vector2[]>();
        switch (moves)
        {
            case SignalMoves.Through:
                into.Add([new(0f, 0.95f), new(-0.62f, 0.22f), new(0.62f, 0.22f)]);
                Line(into, new(0f, 0.3f), new(0f, -0.85f), 0.4f);
                break;
            case SignalMoves.Through | SignalMoves.Left: StraightAndTurn(into, -1); break;
            case SignalMoves.Through | SignalMoves.Right: StraightAndTurn(into, 1); break;
            case SignalMoves.Left: Turn(into, -1); break;
            case SignalMoves.Right: Turn(into, 1); break;
        }
        return into;
    }

    /// <summary>A turn to <paramref name="dir"/> (-1 left, +1 right): a head, then a shaft.</summary>
    private static void Turn(List<Vector2[]> into, float dir)
    {
        into.Add([new(dir, 0f), new(0f, 0.75f), new(0f, -0.75f)]);
        into.Add([new(0f, -0.28f), new(0f, 0.28f), new(-dir, 0.28f), new(-dir, -0.28f)]);
    }

    /// <summary>Straight on and a turn to <paramref name="dir"/>: a stem up with its head, and a branch off its middle with its own.</summary>
    private static void StraightAndTurn(List<Vector2[]> into, float dir)
    {
        float x = -0.3f * dir;
        into.Add([new(x, 0.95f), new(x - 0.5f, 0.4f), new(x + 0.5f, 0.4f)]);
        Line(into, new(x, 0.45f), new(x, -0.85f), 0.32f);
        Line(into, new(x, -0.25f), new(x + 0.7f * dir, -0.25f), 0.32f);
        into.Add([new(x + 1.22f * dir, -0.25f), new(x + 0.7f * dir, 0.25f), new(x + 0.7f * dir, -0.75f)]);
    }

    /// <summary>A stroke with round ends, so strokes that meet join cleanly.</summary>
    public static void Line(List<Vector2[]> into, Vector2 a, Vector2 b, float width)
    {
        var n = (b - a).Normalized().Orthogonal() * (width * 0.5f);
        into.Add([a - n, b - n, b + n, a + n]);
        into.Add(Disc(a, width * 0.5f, 6));
        into.Add(Disc(b, width * 0.5f, 6));
    }

    public static Vector2[] Disc(Vector2 centre, float r, int n)
    {
        var ring = new Vector2[n];
        for (int i = 0; i < n; i++) ring[i] = centre + r * new Vector2(Mathf.Cos(Mathf.Tau * i / n), Mathf.Sin(Mathf.Tau * i / n));
        return ring;
    }
}
