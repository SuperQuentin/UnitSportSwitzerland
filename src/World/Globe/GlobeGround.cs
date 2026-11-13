using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The globe's ground in the world's own palette, near the ground under <c>--frame earth</c> (#1629): each texel of a
/// pyramid tile's colour image read as cover weights (water, forest, rock, snow; the rest open ground), and each vertex
/// painted from them by <see cref="CoverPalette.ColorFor"/> at its own altitude, as the flat world's horizon blocks
/// are (open ground in altitude bands, the snow line). The photo's own colours (Blue Marble: olive, yellower than the
/// palette; over the region the palette averaged with roofs and roads) drew a pale beige plain where flat draws green.
/// </summary>
public static class GlobeGround
{
    /// <summary>The side of a tile's weight grid, samples.</summary>
    public const int Side = 128;

    /// <summary>Bytes a sample: water, forest, rock, snow.</summary>
    public const int Stride = 4;

    /// <summary>
    /// A colour image (<paramref name="w"/> x <paramref name="h"/> sRGB texels, row 0 the south edge) as cover
    /// weights on a <see cref="Side"/> grid, each sample the mean of its block of texels, read through <see cref="Unstore"/>.
    /// </summary>
    public static byte[] Weigh(int w, int h, Func<int, int, Color> texel)
    {
        var grid = new byte[Side * Side * Stride];
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int x0 = x * w / Side, x1 = Math.Max(x0 + 1, (x + 1) * w / Side);
                int y0 = y * h / Side, y1 = Math.Max(y0 + 1, (y + 1) * h / Side);
                var sum = Vector3.Zero;
                for (int py = y0; py < y1; py++)
                    for (int px = x0; px < x1; px++)
                    {
                        var c = texel(px, py).SrgbToLinear();
                        sum += new Vector3(c.R, c.G, c.B);
                    }
                var weights = Classify(Unstore(sum / ((x1 - x0) * (y1 - y0))));
                int o = (y * Side + x) * Stride;
                for (int c = 0; c < Stride; c++) grid[o + c] = (byte)Math.Clamp(weights[c] * 255f + 0.5f, 0, 255);
            }
        return grid;
    }

    /// <summary>
    /// A stored texel (linear) back as a palette colour (linear): the exact inverse of the bake's encoding over the
    /// region (<see cref="OurColours.ForCartoonShader"/>: a fifth desaturated, then squared); the photo lifted alike
    /// elsewhere (<c>globe.gdshader</c>'s <c>cartoon_earth</c> starts the same way).
    /// </summary>
    public static Vector3 Unstore(Vector3 stored)
    {
        var col = new Vector3(MathF.Sqrt(Math.Max(stored.X, 0)), MathF.Sqrt(Math.Max(stored.Y, 0)), MathF.Sqrt(Math.Max(stored.Z, 0)));
        float l = col.Dot(new Vector3(0.2126f, 0.7152f, 0.0722f));
        return new Vector3(l, l, l).Lerp(col, 1.25f).Max(Vector3.Zero);
    }

    private static float Smooth(float a, float b, float x) => (float)Mathf.SmoothStep(a, b, x);

    /// <summary>
    /// Cover weights of a palette colour (linear), each 0..1, together at most 1: water bluer than green, snow bright,
    /// forest dark and green, rock grey and not dark. Tuned on <see cref="CoverPalette"/>'s own colours (sRGB: water 0.42 blue
    /// against 0.32 green, forest at luma 0.20, open ground 0.34, rock grey 0.43, snow 0.84) and on Blue Marble's.
    /// </summary>
    public static float[] Classify(Vector3 linear)
    {
        var srgb = new Color(Math.Max(linear.X, 0), Math.Max(linear.Y, 0), Math.Max(linear.Z, 0)).LinearToSrgb();
        var s = new Vector3(srgb.R, srgb.G, srgb.B);
        float luma = (s.X + s.Y + s.Z) / 3f;
        float sat = Math.Max(s.X, Math.Max(s.Y, s.Z)) - Math.Min(s.X, Math.Min(s.Y, s.Z));
        // bluer than green, or near black (Blue Marble draws inland lakes black)
        float water = Math.Max(Smooth(0.02f, 0.07f, s.Z - s.Y) * Smooth(0.7f, 0.55f, luma), Smooth(0.12f, 0.06f, luma));
        float snow = Smooth(0.62f, 0.78f, luma) * (1 - water);
        float forest = Smooth(0.30f, 0.24f, luma) * Smooth(0.0f, 0.04f, s.Y - s.Z) * (1 - water - snow);
        float rock = Smooth(0.12f, 0.07f, sat) * Smooth(0.30f, 0.40f, luma) * (1 - water - snow - forest);
        return [water, Math.Max(forest, 0), Math.Max(rock, 0), snow];
    }

    /// <summary>
    /// A leaf vertex this far under the coarser level's surface, metres, lies on a lake's bed (#298) even where that
    /// surface is not level (a shore): the pyramid's levels over the region hold the water's surface on a 2.4 m lattice.
    /// Well over what the region's two builds differ by on land (the lake stands 3.8 m apart in places).
    /// </summary>
    public const float BedM = 20f;

    /// <summary>The colour's water weight a level surface needs to be drawn as water (<see cref="Level"/>).</summary>
    public const float WaterHint = 0.2f;

    /// <summary>
    /// Ground this low, metres, may be the sea's (#1802): the pyramid holds the sea's bed (ETOPO's bathymetry, 20-50 m
    /// down over a shelf sea), so the sea is seldom level and none of the pyramid's lakes. Under it the colour image's
    /// water is kept where the lake list would dry it (no lake of the list lies this low; Lavaux's shore stands 372 m up).
    /// </summary>
    public const float SeaReachM = 10f;

    /// <summary>The water weight above which a coarser level's level vertex is drawn as a lake's surface too (#1669).</summary>
    public const float LakeWeight = 0.5f;

    /// <summary>How level still water is, metres: every neighbour of a vertex within this of it.</summary>
    public const float LevelM = 0.05f;

    /// <summary>
    /// Whether vertex (i, j) of an n x n height grid lies on still water: it and its eight neighbours level within
    /// <see cref="LevelM"/>. Land is never that flat over a drawn cell (10 m at the leaves' level, kilometres higher up).
    /// </summary>
    public static bool Level(float[] hs, int n, int i, int j)
    {
        float h = hs[j * n + i];
        for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int x = Math.Clamp(i + di, 0, n - 1), y = Math.Clamp(j + dj, 0, n - 1);
                if (Math.Abs(hs[y * n + x] - h) > LevelM) return false;
            }
        return true;
    }

    /// <summary>
    /// Whether a height stands at one of the pyramid's lake levels (<see cref="CubeManifest.Lakes"/>), within the tile's
    /// stored quantum: <c>--cube-swiss</c> puts a lake there exactly, so a level vertex at one is that lake (#1669).
    /// </summary>
    public static bool OnLake(float[]? lakes, float h, float quantum)
    {
        if (lakes == null) return false;
        float within = Math.Max(quantum, 0.01f);
        foreach (float l in lakes)
            if (Math.Abs(h - l) <= within) return true;
        return false;
    }

    /// <summary>Weights where a leaf's own water says it is dry: no water, the rest scaled up to fill its share.</summary>
    public static Vector4 Dry(Vector4 w)
    {
        float rest = 1 - w.X;
        return rest > 1e-3f ? new Vector4(0, w.Y, w.Z, w.W) / rest : Vector4.Zero;
    }

    /// <summary>A weight grid sampled at (u, v), bilinear.</summary>
    public static Vector4 Sample(byte[] grid, float u, float v)
    {
        float fx = Math.Clamp(u * Side - 0.5f, 0, Side - 1), fy = Math.Clamp(v * Side - 0.5f, 0, Side - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, Side - 1), y1 = Math.Min(y0 + 1, Side - 1);
        float tx = fx - x0, ty = fy - y0;
        Vector4 At(int x, int y)
        {
            int o = (y * Side + x) * Stride;
            return new Vector4(grid[o], grid[o + 1], grid[o + 2], grid[o + 3]) / 255f;
        }
        return At(x0, y0).Lerp(At(x1, y0), tx).Lerp(At(x0, y1).Lerp(At(x1, y1), tx), ty);
    }

    /// <summary>
    /// A vertex's colour (linear, no surface pattern) from its cover weights (water, forest, rock, snow) at
    /// <paramref name="altitude"/> metres: the palette's colours mixed by weight, open ground for the rest.
    /// </summary>
    public static Color Colour(Vector4 w, float altitude, float hash)
    {
        float open = Math.Max(0, 1 - w.X - w.Y - w.Z - w.W);
        var c = Lin(CoverClass.Open) * open + Lin(CoverClass.Water) * w.X + Lin(CoverClass.Forest) * w.Y
            + Lin(CoverClass.Rock) * w.Z + Lin(CoverClass.Snowfield) * w.W;
        float sum = open + w.X + w.Y + w.Z + w.W;
        if (sum > 0) c /= sum;
        return new Color(c.X, c.Y, c.Z, 0);

        Vector3 Lin(CoverClass cover)
        {
            var col = CoverPalette.ColorFor(cover, altitude, hash).SrgbToLinear();
            return new Vector3(col.R, col.G, col.B);
        }
    }

    /// <summary>The highest ground the far view must show past the horizon, metres: Everest's, with a margin.</summary>
    public const double PeakM = 9_000;

    /// <summary>The split rule's distance is never taken under this, metres (the tile under the camera).</summary>
    public const double NearestM = 200;

    /// <summary>
    /// How far round the Earth a tile can still be seen, radians: the camera's horizon over the ellipsoid
    /// under it, plus the reach of a <see cref="PeakM"/> summit standing past it. On a mean sphere
    /// (6,371 km), a camera at Geneva below ~4 km was under the ground (the ellipsoid is 6,367 km there):
    /// a horizon of 0, and the globe drew only the tiles over the nadir (#1629).
    /// </summary>
    public static double HorizonAngle(EcefPos eye)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(eye);
        var nadir = CubeSphere.OnEllipsoid(CubeSphere.NormalAt(g.LatDeg, g.LonDeg), 0);
        double r = Math.Sqrt(nadir.X * nadir.X + nadir.Y * nadir.Y + nadir.Z * nadir.Z);
        double h = Math.Max(g.H, 1);
        return Math.Acos(r / (r + h)) + Math.Acos(r / (r + PeakM));
    }

    /// <summary>
    /// A tile's missing heights (NaN) taken from its ancestors, nearest first: the vertex (i·stride, j·stride) of
    /// <paramref name="key"/>'s lattice (<paramref name="bits"/>) is a point of each coarser tile's own lattice, read
    /// bilinearly from its four samples when all four are there. The pyramid's Swiss levels were built from the leaves
    /// alone, so past them (the French side of the region) they hold NaN where the global levels above have relief.
    /// </summary>
    public static void FillFromAncestors(float[] hs, int n, CubeKey key, int bits, int stride, Func<CubeKey, CubeTile?> load)
    {
        for (int level = key.Level - 1; level >= 0; level--)
        {
            if (Array.TrueForAll(hs, h => !float.IsNaN(h))) return;
            var up = key.AncestorAt(level);
            if (load(up) is not { } tile) continue;
            int dl = key.Level - level, abits = tile.Bits, side = (1 << abits) + 1;
            // a vertex's place in the ancestor's lattice, in its samples
            double scale = Math.Pow(2, abits - bits - dl);
            double ox = ((key.X - ((long)up.X << dl)) << bits) * scale, oy = ((key.Y - ((long)up.Y << dl)) << bits) * scale;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int k = j * n + i;
                    if (!float.IsNaN(hs[k])) continue;
                    double fx = ox + i * stride * scale, fy = oy + j * stride * scale;
                    int x0 = Math.Clamp((int)fx, 0, side - 2), y0 = Math.Clamp((int)fy, 0, side - 2);
                    float tx = (float)(fx - x0), ty = (float)(fy - y0);
                    float a = tile.HeightAt(x0, y0), b = tile.HeightAt(x0 + 1, y0), c = tile.HeightAt(x0, y0 + 1), d = tile.HeightAt(x0 + 1, y0 + 1);
                    float h = (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
                    if (!float.IsNaN(h)) hs[k] = h;
                }
        }
    }

    /// <summary>
    /// A tile's missing heights (NaN: past the data a level was built from, the French side of the region's leaves)
    /// filled from their neighbours, pass by pass, inward from the known ones. They were drawn at 0 m: pits a kilometre
    /// deep in the Salève, seen as grey blotches from Geneva (#1629). False when the tile has no height at all.
    /// </summary>
    public static bool FillHoles(float[] hs, int n)
    {
        int missing = 0;
        foreach (float h in hs) if (float.IsNaN(h)) missing++;
        if (missing == 0) return true;
        if (missing == hs.Length) return false;
        var next = (float[])hs.Clone();
        while (missing > 0)
        {
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int k = j * n + i;
                    if (!float.IsNaN(hs[k])) continue;
                    float sum = 0;
                    int count = 0;
                    if (i > 0 && !float.IsNaN(hs[k - 1])) { sum += hs[k - 1]; count++; }
                    if (i < n - 1 && !float.IsNaN(hs[k + 1])) { sum += hs[k + 1]; count++; }
                    if (j > 0 && !float.IsNaN(hs[k - n])) { sum += hs[k - n]; count++; }
                    if (j < n - 1 && !float.IsNaN(hs[k + n])) { sum += hs[k + n]; count++; }
                    if (count == 0) continue;
                    next[k] = sum / count;
                    missing--;
                }
            Array.Copy(next, hs, hs.Length);
        }
        return true;
    }
}
