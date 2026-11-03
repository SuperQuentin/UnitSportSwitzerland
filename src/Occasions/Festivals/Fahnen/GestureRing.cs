// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fahnenschwingen.md).

namespace UnitSport.Occasions.Festivals.Fahnen;

/// <summary>What a swing of the stick or the mouse drew.</summary>
public enum Stroke : byte { None, Up, Down, Left, Right, CircleCw, CircleCcw, Eight }

/// <summary>
/// The flag swinger's gesture reader (#1269): a stick or mouse path sampled at 30 Hz becomes a list of 8 sectors
/// (0 = up, then clockwise on the screen), and the list is told apart by how far it turned each way. The client records
/// the sectors and the server reads them again with this same code, so a figure is judged where the score is paid.
/// </summary>
public static class GestureRing
{
    public const int Sectors = 8;
    public const double SampleHz = 30;
    /// <summary>A path longer than this is not a figure (a 3 s swing is 90 samples).</summary>
    public const int MaxSamples = 240;
    /// <summary>Sectors passed in one direction that make a loop, and the other direction's wobble it may carry.</summary>
    private const int LoopSectors = 7, Wobble = 2;

    /// <summary>The sector of a move (x right, y down, as the screen); 0 = up, 2 = right, 4 = down, 6 = left.</summary>
    public static byte SectorOf(float x, float y)
    {
        double a = Math.Atan2(x, -y);   // 0 up, +pi/2 right
        int s = (int)Math.Round(a / (Math.Tau / Sectors));
        return (byte)((s % Sectors + Sectors) % Sectors);
    }

    /// <summary>The shortest signed turn from sector <paramref name="a"/> to <paramref name="b"/>: -3..+3, or 4 for a half turn (not a turn).</summary>
    public static int Step(int a, int b)
    {
        int d = ((b - a + 12) % Sectors) - 4;
        return d == 4 || d == -4 ? 4 : d;
    }

    public static Stroke Recognise(ReadOnlySpan<byte> path)
    {
        if (path.Length < 4 || path.Length > MaxSamples) return Stroke.None;
        int pos = 0, neg = 0;
        double nx = 0, ny = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] >= Sectors) return Stroke.None;
            double a = path[i] * Math.Tau / Sectors;
            nx += Math.Sin(a);
            ny -= Math.Cos(a);
            if (i == 0) continue;
            int d = Step(path[i - 1], path[i]);
            if (d == 4) continue;
            if (d > 0) pos += d; else neg -= d;
        }
        if (pos >= LoopSectors && neg <= Wobble) return Stroke.CircleCw;
        if (neg >= LoopSectors && pos <= Wobble) return Stroke.CircleCcw;
        if (pos >= 3 && neg >= 3 && pos + neg >= 10) return Stroke.Eight;
        if (Math.Sqrt(nx * nx + ny * ny) < path.Length * 0.5) return Stroke.None;
        if (Math.Abs(nx) > Math.Abs(ny)) return nx > 0 ? Stroke.Right : Stroke.Left;
        return ny < 0 ? Stroke.Up : Stroke.Down;
    }

    /// <summary>A clean path for a stroke: what a bot draws, and what the tests feed the reader.</summary>
    public static byte[] Ideal(Stroke s) => s switch
    {
        Stroke.Up => [0, 0, 0, 0, 0, 0],
        Stroke.Down => [4, 4, 4, 4, 4, 4],
        Stroke.Left => [6, 6, 6, 6, 6, 6],
        Stroke.Right => [2, 2, 2, 2, 2, 2],
        Stroke.CircleCw => [0, 1, 2, 3, 4, 5, 6, 7, 0, 1],
        Stroke.CircleCcw => [0, 7, 6, 5, 4, 3, 2, 1, 0, 7],
        Stroke.Eight => [0, 1, 2, 3, 4, 3, 2, 1, 0, 7, 6, 5, 4],
        _ => [],
    };
}
