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
            // both turns, one shaft with a head each end: a flasher's plate where its left and right turns cross walkers and riders
            // (#1109); no lens shows it
            case SignalMoves.Left | SignalMoves.Right:
                into.Add([new(-1f, 0f), new(-0.45f, -0.6f), new(-0.45f, 0.6f)]);
                into.Add([new(1f, 0f), new(0.45f, 0.6f), new(0.45f, -0.6f)]);
                into.Add([new(-0.5f, -0.22f), new(0.5f, -0.22f), new(0.5f, 0.22f), new(-0.5f, 0.22f)]);
                break;
        }
        return into;
    }

    /// <summary>
    /// A walking figure striding to the right, in units of a lens radius: lit in a pedestrian head's green and yellow lens
    /// (#759), black on a flasher's plate (#1109).
    /// </summary>
    public static List<Vector2[]> Walker()
    {
        var into = new List<Vector2[]>();
        into.Add(Disc(new(0.08f, 0.66f), 0.17f, 10));
        Line(into, new(0.04f, 0.44f), new(-0.04f, -0.08f), 0.27f);
        Line(into, new(0.02f, 0.36f), new(0.32f, 0.04f), 0.1f);
        Line(into, new(0.02f, 0.36f), new(-0.3f, 0.1f), 0.1f);
        Line(into, new(-0.04f, -0.1f), new(0.36f, -0.84f), 0.14f);
        Line(into, new(-0.04f, -0.1f), new(-0.12f, -0.44f), 0.14f);
        Line(into, new(-0.12f, -0.44f), new(-0.4f, -0.82f), 0.14f);
        return into;
    }

    /// <summary>
    /// A bicycle side on, facing right, in units of a lens radius: two wheels, the diamond frame, saddle and bars. Lit in a
    /// bike lens (#351), yellow on the right-on-red sign (#711).
    /// </summary>
    public static List<Vector2[]> Bike()
    {
        var into = new List<Vector2[]>();
        Vector2 rear = new(-0.5f, -0.28f), crank = new(-0.02f, -0.28f), seat = new(-0.18f, 0.2f),
            head = new(0.36f, 0.2f), front = new(0.5f, -0.28f);
        Ring(into, rear, 0.34f, 0.1f, 12);
        Ring(into, front, 0.34f, 0.1f, 12);
        const float W = 0.09f;
        Line(into, rear, crank, W); Line(into, rear, seat, W); Line(into, seat, crank, W);
        Line(into, seat, head, W); Line(into, head, crank, W); Line(into, head, front, W);
        Line(into, new(-0.32f, 0.3f), new(-0.06f, 0.3f), 0.08f);
        Line(into, head, new(0.3f, 0.38f), W);
        Line(into, new(0.24f, 0.4f), new(0.44f, 0.36f), 0.08f);
        return into;
    }

    /// <summary>A ring of <paramref name="n"/> quads.</summary>
    public static void Ring(List<Vector2[]> into, Vector2 centre, float r, float width, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var d0 = new Vector2(Mathf.Cos(Mathf.Tau * i / n), Mathf.Sin(Mathf.Tau * i / n));
            var d1 = new Vector2(Mathf.Cos(Mathf.Tau * (i + 1) / n), Mathf.Sin(Mathf.Tau * (i + 1) / n));
            into.Add([centre + d0 * (r - width * 0.5f), centre + d0 * (r + width * 0.5f), centre + d1 * (r + width * 0.5f), centre + d1 * (r - width * 0.5f)]);
        }
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
