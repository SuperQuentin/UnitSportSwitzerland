namespace UnitSport.Core;

/// <summary>
/// Finds flickering pixels in a short run of frames taken while the camera creeps a few
/// millimetres (#788). A pixel that crosses an edge changes once; one where two faces fight for the
/// depth buffer, or a texture shimmers (a roof's tiles too fine for the screen), or the PS1 vertex
/// snap trades two faces, keeps turning back: bright, dark, bright. <see cref="Reversals"/> counts
/// those turns. Pure (tier-0 tests in <c>FlickerTests</c>); <c>FlickerShot</c> takes the frames.
/// See docs/notes/general/zfight.md.
/// </summary>
public static class Flicker
{
    /// <summary>A change in brightness (0-255) smaller than this is noise, not a new colour.</summary>
    public const int Threshold = 24;

    /// <summary>Turns a pixel needs to count as flickering: A, B, A, B.</summary>
    public const int MinReversals = 2;

    /// <summary>
    /// For each pixel of same-sized RGBA8 frames, how many times its brightness changed direction
    /// by more than <paramref name="threshold"/>, each change measured from the level after the last
    /// one (so a slow drift is one change, not many).
    /// </summary>
    public static byte[] Reversals(IReadOnlyList<byte[]> frames, int width, int height, int threshold = Threshold)
    {
        int n = width * height;
        var turns = new byte[n];
        if (frames.Count == 0) return turns;
        var anchor = new short[n];
        var direction = new sbyte[n];
        var first = frames[0];
        for (int p = 0; p < n; p++) anchor[p] = Luma(first, p);
        for (int f = 1; f < frames.Count; f++)
        {
            var frame = frames[f];
            for (int p = 0; p < n; p++)
            {
                short l = Luma(frame, p);
                int change = l - anchor[p];
                if (change <= threshold && change >= -threshold) continue;
                sbyte dir = change > 0 ? (sbyte)1 : (sbyte)-1;
                if (direction[p] != 0 && dir != direction[p] && turns[p] < byte.MaxValue) turns[p]++;
                direction[p] = dir;
                anchor[p] = l;
            }
        }
        return turns;
    }

    /// <summary>The pixels with at least <paramref name="min"/> turns.</summary>
    public static int Count(byte[] turns, int min = MinReversals)
    {
        int count = 0;
        foreach (byte t in turns)
            if (t >= min) count++;
        return count;
    }

    private static short Luma(byte[] rgba, int p) =>
        (short)((rgba[p * 4] * 2 + rgba[p * 4 + 1] * 5 + rgba[p * 4 + 2]) >> 3);
}
