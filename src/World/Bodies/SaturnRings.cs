using Godot;

namespace UnitSport.World.Bodies;

/// <summary>
/// Saturn's rings (#1854): their radii (the D ring's inner edge to the A ring's outer one, the sky's own), their game
/// thickness, and their opacity by radius read once from the ring map's alpha (the dense B ring, the near-empty Cassini
/// division): what the slab draws (<c>ring_slab.gdshader</c>) and how many ice chunks a place holds (<see cref="RingChunks"/>).
/// </summary>
public static class SaturnRings
{
    public const double Inner = 74_500_000, Outer = 140_220_000;

    /// <summary>
    /// Half the slab's thickness, metres. The real main rings are ~10 m to 1 km thick; 200 m reads as a slab from a few
    /// km and keeps the chunks' field (and the shrunk system's floats) comfortable.
    /// </summary>
    public const double HalfThickness = 200;

    public const string MapPath = "res://assets/space/saturn_ring_2k.png";

    private static float[]? _opacity;

    /// <summary>The ring's opacity at a radius, 0 to 1: the map's alpha along its strip (inner edge left), any thread once loaded.</summary>
    public static double Opacity(double r)
    {
        var o = _opacity ??= Load();
        double u = (r - Inner) / (Outer - Inner);
        if (u < 0 || u > 1 || o.Length == 0) return 0;
        double x = u * (o.Length - 1);
        int i = (int)x;
        int j = System.Math.Min(i + 1, o.Length - 1);
        return o[i] + (o[j] - o[i]) * (x - i);
    }

    /// <summary>The alpha strip, averaged over the map's height: loaded on the main thread at the first use.</summary>
    public static void Preload() => _opacity ??= Load();

    private static float[] Load()
    {
        var image = GD.Load<Texture2D>(MapPath)?.GetImage();
        if (image == null) return [];
        if (image.IsCompressed()) image.Decompress();
        int w = image.GetWidth(), h = image.GetHeight();
        var o = new float[w];
        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int y = 0; y < h; y++) sum += image.GetPixel(x, y).A;
            o[x] = sum / h;
        }
        return o;
    }
}
