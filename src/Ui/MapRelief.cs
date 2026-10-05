using Godot;
using UnitSport.Terrain;

namespace UnitSport.Ui;

/// <summary>
/// The shaded-relief picture of Switzerland the map screen is drawn on, built from the 500 m
/// heightmap already embedded in the assembly for the generated terrain
/// (<c>ProceduralWorld.Relief</c>, <c>src/Terrain/swiss_relief.gz</c>). The map therefore costs no
/// new data and looks like the country rather than like a grid of coloured tiles.
///
/// <para>
/// One texture at the relief's own lattice (1101x861 nodes, 500 m apart), built once per process on
/// a worker thread and then only ever sampled: zooming and panning draw a sub-rectangle of it, so
/// neither costs an allocation. That is also why the whole country is baked rather than the visible
/// part — a pan must never wait for a rebuild.
/// </para>
/// </summary>
public static class MapRelief
{
    /// <summary>Lake surfaces. Flat, so they read as water against the shaded land around them.</summary>
    private static readonly Color Water = new(0.16f, 0.29f, 0.44f);

    /// <summary>
    /// Elevation ramp, plateau to summit. The Swiss map convention rather than a rainbow: green
    /// farmland, tan foothills, grey rock, white above the snow line.
    /// </summary>
    private static readonly (float Metres, Color Colour)[] Stops =
    [
        (200, new Color(0.26f, 0.34f, 0.24f)),
        (500, new Color(0.33f, 0.40f, 0.26f)),
        (900, new Color(0.42f, 0.42f, 0.27f)),
        (1400, new Color(0.47f, 0.40f, 0.29f)),
        (2000, new Color(0.46f, 0.41f, 0.37f)),
        (2600, new Color(0.52f, 0.51f, 0.50f)),
        (3200, new Color(0.72f, 0.73f, 0.75f)),
        (4000, new Color(0.92f, 0.94f, 0.96f)),
    ];

    private static ImageTexture? _texture;
    private static Task<Image>? _building;

    /// <summary>
    /// Where the texture sits in LV95 metres, so the canvas can map a position onto its pixels.
    /// Reading this warms the relief, which takes about half a second the first time: call it from
    /// a worker, or after <see cref="TryGet"/> has already returned a texture.
    /// </summary>
    public static (double MinE, double MaxN, double Spacing, int Cols, int Rows) Lattice()
    {
        var r = ProceduralWorld.Relief.Instance;
        return (r.MinE, r.MaxN, r.Spacing, r.Cols, r.Rows);
    }

    /// <summary>
    /// The relief texture, or null while it is still being built. Null is an ordinary first-frame
    /// answer rather than a failure — the canvas draws the tile lattice alone until it arrives,
    /// which is why nothing here blocks the main thread.
    /// </summary>
    public static ImageTexture? TryGet()
    {
        if (_texture != null) return _texture;
        _building ??= Task.Run(Render);
        if (!_building.IsCompleted) return null;

        if (_building.IsFaulted)
        {
            GD.PushWarning($"[map] no relief picture: {_building.Exception?.GetBaseException().Message}");
            // A broken relief must not be retried every frame; the canvas keeps its flat fallback.
            _texture = ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgb8));
            return _texture;
        }
        _texture = ImageTexture.CreateFromImage(_building.Result);
        return _texture;
    }

    /// <summary>
    /// Hillshade plus the elevation ramp, on a worker thread. The sun is in the north-west, which is
    /// the convention every Swiss map is drawn with: lit from the other side, valleys read as ridges
    /// and the country looks inside out.
    ///
    /// <para>
    /// Written into one byte array and handed to <see cref="Image.CreateFromData"/> rather than set
    /// pixel by pixel — that is 948 000 interop calls for a picture built in one pass.
    /// </para>
    /// </summary>
    private static Image Render()
    {
        var relief = ProceduralWorld.Relief.Instance;
        int cols = relief.Cols, rows = relief.Rows;
        var pixels = new byte[cols * rows * 3];

        // The gradient is in metres per metre, so the 500 m spacing divides it; the exaggeration
        // keeps the Plateau from looking dead flat beside the Alps.
        const double Exaggeration = 2.2;
        double scale = Exaggeration / (2 * relief.Spacing);
        double invRoot3 = 1.0 / Math.Sqrt(3.0);

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int k = r * cols + c;
                // Central differences, clamped at the lattice edge. The lattice reaches well past
                // the border, so the clamp never shows inside the country.
                double hx = (Height(relief, c + 1, r) - Height(relief, c - 1, r)) * scale;
                double hy = (Height(relief, c, r + 1) - Height(relief, c, r - 1)) * scale;
                // dot(surface normal, a light at (-1, +1, +1)), normalised.
                double shade = (hx - hy + 1.0) / Math.Sqrt(hx * hx + hy * hy + 1.0) * invRoot3;
                shade = Math.Clamp(0.45 + 0.75 * shade, 0.25, 1.35);

                var colour = relief.LakeOf[k] >= 0 ? Water : Ramp(relief.H[k]);
                pixels[k * 3 + 0] = Byte(colour.R * shade);
                pixels[k * 3 + 1] = Byte(colour.G * shade);
                pixels[k * 3 + 2] = Byte(colour.B * shade);
            }
        }
        return Image.CreateFromData(cols, rows, false, Image.Format.Rgb8, pixels);
    }

    private static byte Byte(double v) => (byte)Math.Clamp(v * 255.0, 0, 255);

    private static float Height(ProceduralWorld.Relief relief, int c, int r) =>
        relief.H[Math.Clamp(r, 0, relief.Rows - 1) * relief.Cols + Math.Clamp(c, 0, relief.Cols - 1)];

    /// <summary>The elevation ramp, linear between its stops.</summary>
    private static Color Ramp(float metres)
    {
        if (metres <= Stops[0].Metres) return Stops[0].Colour;
        for (int i = 1; i < Stops.Length; i++)
        {
            var (hi, hiColour) = Stops[i];
            if (metres > hi) continue;
            var (lo, loColour) = Stops[i - 1];
            return loColour.Lerp(hiColour, (metres - lo) / (hi - lo));
        }
        return Stops[^1].Colour;
    }
}
