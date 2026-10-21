using Godot;

namespace UnitSport.Ui;

/// <summary>
/// Line icons drawn at startup from a few strokes each, white so a button's icon colours can
/// tint them. No image assets: they are cached textures built once.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, ImageTexture> Cache = new();

    public static Texture2D Pencil => Get("pencil", 18, new[] { (2.5f, 15.5f, 4f, 11f), (4f, 11f, 12f, 3f), (12f, 3f, 15f, 6f), (15f, 6f, 7f, 14f), (7f, 14f, 2.5f, 15.5f), (10.5f, 4.5f, 13.5f, 7.5f) });
    public static Texture2D Trash => Get("trash", 18, new[] { (3f, 5f, 15f, 5f), (7f, 5f, 7f, 2.5f), (7f, 2.5f, 11f, 2.5f), (11f, 2.5f, 11f, 5f), (4.5f, 5f, 5.5f, 15.5f), (5.5f, 15.5f, 12.5f, 15.5f), (12.5f, 15.5f, 13.5f, 5f), (7.5f, 8f, 7.5f, 13f), (10.5f, 8f, 10.5f, 13f) });
    public static Texture2D Plus => Get("plus", 18, new[] { (9f, 3f, 9f, 15f), (3f, 9f, 15f, 9f) });
    public static Texture2D Back => Get("back", 18, new[] { (15f, 9f, 3.5f, 9f), (3.5f, 9f, 8f, 4.5f), (3.5f, 9f, 8f, 13.5f) });
    public static Texture2D Refresh => Get("refresh", 18, Arc(9, 9, 6, 40, 320).Concat(new[] { (13.6f, 5.1f, 14.5f, 1.8f), (13.6f, 5.1f, 10.5f, 5.0f) }).ToArray());
    public static Texture2D Close => Get("close", 18, new[] { (4f, 4f, 14f, 14f), (14f, 4f, 4f, 14f) });
    public static Texture2D Play => GetFilled("play", 18, new[] { new Vector2(5, 3), new Vector2(15, 9), new Vector2(5, 15) });
    public static Texture2D StarFilled => GetFilled("star_on", 18, StarPoints());
    public static Texture2D Star => Get("star_off", 18, Loop(StarPoints()));
    public static Texture2D Network => Get("net", 18, new[] { (9f, 3f, 9f, 8f), (4f, 8f, 14f, 8f), (4f, 8f, 4f, 11f), (14f, 8f, 14f, 11f), (2f, 11f, 6f, 11f), (2f, 11f, 2f, 15f), (2f, 15f, 6f, 15f), (6f, 15f, 6f, 11f), (12f, 11f, 16f, 11f), (12f, 11f, 12f, 15f), (12f, 15f, 16f, 15f), (16f, 15f, 16f, 11f) });
    public static Texture2D Host => Get("host", 18, new[] { (3f, 3f, 15f, 3f), (15f, 3f, 15f, 8f), (15f, 8f, 3f, 8f), (3f, 8f, 3f, 3f), (3f, 10f, 15f, 10f), (15f, 10f, 15f, 15f), (15f, 15f, 3f, 15f), (3f, 15f, 3f, 10f), (5.5f, 5.5f, 6.5f, 5.5f), (5.5f, 12.5f, 6.5f, 12.5f) });
    public static Texture2D User => Get("user", 18, Arc(9, 6, 3.2f, 0, 360).Concat(Arc(9, 17.5f, 6.5f, 200, 340)).ToArray());

    // the movie editor's buttons (#779): each beside a word, never alone
    public static Texture2D PlayBack => GetFilled("play_back", 18, new[] { new Vector2(13, 3), new Vector2(3, 9), new Vector2(13, 15) });
    public static Texture2D Pause => Get("pause", 18, new[] { (6f, 3.5f, 6f, 14.5f), (12f, 3.5f, 12f, 14.5f) }, 2.6f);
    public static Texture2D SkipBack => Get("skip_back", 18, new[] { (4f, 3.5f, 4f, 14.5f), (14f, 4f, 7f, 9f), (7f, 9f, 14f, 14f) });
    public static Texture2D SkipForward => Get("skip_fwd", 18, new[] { (14f, 3.5f, 14f, 14.5f), (4f, 4f, 11f, 9f), (11f, 9f, 4f, 14f) });
    public static Texture2D StepBack => Get("step_back", 18, new[] { (11f, 4f, 6f, 9f), (6f, 9f, 11f, 14f) });
    public static Texture2D StepForward => Get("step_fwd", 18, new[] { (7f, 4f, 12f, 9f), (12f, 9f, 7f, 14f) });
    public static Texture2D Scissors => Get("scissors", 18, Arc(5, 13.5f, 2.4f, 0, 360).Concat(Arc(13, 13.5f, 2.4f, 0, 360))
        .Concat(new[] { (6.5f, 11.5f, 12.5f, 2.5f), (11.5f, 11.5f, 5.5f, 2.5f) }).ToArray());
    public static Texture2D Key => Get("key", 18, new[] { (9f, 3f, 15f, 9f), (15f, 9f, 9f, 15f), (9f, 15f, 3f, 9f), (3f, 9f, 9f, 3f) });
    public static Texture2D Camera => Get("camera", 18, new[] { (2.5f, 6f, 12f, 6f), (12f, 6f, 12f, 13f), (12f, 13f, 2.5f, 13f), (2.5f, 13f, 2.5f, 6f), (12f, 8.5f, 15.5f, 6.5f), (15.5f, 6.5f, 15.5f, 12.5f), (15.5f, 12.5f, 12f, 10.5f) });
    public static Texture2D Fly => Get("fly", 18, new[] { (2.5f, 9f, 15.5f, 3f), (15.5f, 3f, 11f, 15.5f), (11f, 15.5f, 8.5f, 10f), (8.5f, 10f, 2.5f, 9f), (8.5f, 10f, 15.5f, 3f) });
    public static Texture2D Eye => Get("eye", 18, Arc(9, 13, 7.5f, 205, 335).Concat(Arc(9, 5, 7.5f, 25, 155)).Concat(Arc(9, 9, 2.2f, 0, 360)).ToArray());
    public static Texture2D Music => Get("music", 18, Arc(5.5f, 13.5f, 2.2f, 0, 360).Concat(Arc(12.5f, 12f, 2.2f, 0, 360))
        .Concat(new[] { (7.7f, 13.5f, 7.7f, 4f), (14.7f, 12f, 14.7f, 2.5f), (7.7f, 4f, 14.7f, 2.5f) }).ToArray());
    public static Texture2D Wand => Get("wand", 18, new[] { (3f, 15f, 11f, 7f), (12.5f, 2f, 12.5f, 5f), (11f, 3.5f, 14f, 3.5f), (15f, 7f, 15f, 9f), (14f, 8f, 16f, 8f) });
    public static Texture2D ZoomIn => Get("zoom_in", 18, Arc(7.5f, 7.5f, 5f, 0, 360).Concat(new[] { (11f, 11f, 15.5f, 15.5f), (5f, 7.5f, 10f, 7.5f), (7.5f, 5f, 7.5f, 10f) }).ToArray());
    public static Texture2D ZoomOut => Get("zoom_out", 18, Arc(7.5f, 7.5f, 5f, 0, 360).Concat(new[] { (11f, 11f, 15.5f, 15.5f), (5f, 7.5f, 10f, 7.5f) }).ToArray());
    public static Texture2D Fit => Get("fit", 18, new[] { (2.5f, 6f, 2.5f, 2.5f), (2.5f, 2.5f, 6f, 2.5f), (12f, 2.5f, 15.5f, 2.5f), (15.5f, 2.5f, 15.5f, 6f), (15.5f, 12f, 15.5f, 15.5f), (15.5f, 15.5f, 12f, 15.5f), (6f, 15.5f, 2.5f, 15.5f), (2.5f, 15.5f, 2.5f, 12f) });
    public static Texture2D Folder => Get("folder", 18, new[] { (2.5f, 5f, 2.5f, 14.5f), (2.5f, 14.5f, 15.5f, 14.5f), (15.5f, 14.5f, 15.5f, 6.5f), (15.5f, 6.5f, 8.5f, 6.5f), (8.5f, 6.5f, 7f, 4f), (7f, 4f, 2.5f, 4f), (2.5f, 4f, 2.5f, 5f) });
    public static Texture2D Save => Get("save", 18, new[] { (3f, 3f, 13f, 3f), (13f, 3f, 15f, 5f), (15f, 5f, 15f, 15f), (15f, 15f, 3f, 15f), (3f, 15f, 3f, 3f), (6f, 3f, 6f, 7f), (6f, 7f, 12f, 7f), (12f, 7f, 12f, 3f), (6f, 15f, 6f, 10.5f), (6f, 10.5f, 12f, 10.5f), (12f, 10.5f, 12f, 15f) });
    public static Texture2D Copy => Get("copy", 18, new[] { (6f, 6f, 15f, 6f), (15f, 6f, 15f, 15f), (15f, 15f, 6f, 15f), (6f, 15f, 6f, 6f), (3f, 12f, 3f, 3f), (3f, 3f, 12f, 3f) });
    public static Texture2D Enlarge => Get("enlarge", 18, new[] { (10.5f, 2.5f, 15.5f, 2.5f), (15.5f, 2.5f, 15.5f, 7.5f), (15.5f, 2.5f, 10f, 8f), (7.5f, 15.5f, 2.5f, 15.5f), (2.5f, 15.5f, 2.5f, 10.5f), (2.5f, 15.5f, 8f, 10f) });
    public static Texture2D Terminal => Get("terminal", 18, new[] { (2.5f, 3.5f, 15.5f, 3.5f), (15.5f, 3.5f, 15.5f, 14.5f), (15.5f, 14.5f, 2.5f, 14.5f), (2.5f, 14.5f, 2.5f, 3.5f), (5f, 7f, 7.5f, 9f), (7.5f, 9f, 5f, 11f), (9f, 11.5f, 13f, 11.5f) });
    public static Texture2D Help => Get("help", 18, Arc(9, 6.5f, 3.5f, 180, 400).Concat(new[] { (9f, 10f, 9f, 11.5f), (9f, 14.5f, 9f, 15f) }).ToArray());

    private static (float, float, float, float)[] Arc(float cx, float cy, float r, float fromDeg, float toDeg)
    {
        var segs = new List<(float, float, float, float)>();
        int n = Math.Max(6, (int)((toDeg - fromDeg) / 15));
        for (int i = 0; i < n; i++)
        {
            float a0 = Mathf.DegToRad(fromDeg + (toDeg - fromDeg) * i / n);
            float a1 = Mathf.DegToRad(fromDeg + (toDeg - fromDeg) * (i + 1) / n);
            segs.Add((cx + r * Mathf.Cos(a0), cy + r * Mathf.Sin(a0), cx + r * Mathf.Cos(a1), cy + r * Mathf.Sin(a1)));
        }
        return segs.ToArray();
    }

    private static Vector2[] StarPoints()
    {
        var pts = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float a = Mathf.DegToRad(-90 + i * 36);
            float r = i % 2 == 0 ? 7.5f : 3.2f;
            pts[i] = new Vector2(9 + r * Mathf.Cos(a), 9.6f + r * Mathf.Sin(a));
        }
        return pts;
    }

    private static (float, float, float, float)[] Loop(Vector2[] pts) =>
        pts.Select((p, i) => (p.X, p.Y, pts[(i + 1) % pts.Length].X, pts[(i + 1) % pts.Length].Y)).ToArray();

    private static ImageTexture Get(string key, int size, (float X0, float Y0, float X1, float Y1)[] strokes, float width = 1.6f)
    {
        if (Cache.TryGetValue(key, out var t)) return t;
        // drawn at 2x and filtered down by the GPU: crisp at the size it is shown
        const int k = 2;
        int s = size * k;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                var p = new Vector2((x + 0.5f) / k, (y + 0.5f) / k);
                float d = float.MaxValue;
                foreach (var (x0, y0, x1, y1) in strokes)
                    d = Math.Min(d, UiTheme.DistSeg(p, new Vector2(x0, y0), new Vector2(x1, y1)));
                float a = Mathf.Clamp((width / 2 - d) * k + 0.5f, 0, 1);
                img.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        img.Resize(size, size, Image.Interpolation.Lanczos);
        return Cache[key] = ImageTexture.CreateFromImage(img);
    }

    private static ImageTexture GetFilled(string key, int size, Vector2[] polygon)
    {
        if (Cache.TryGetValue(key, out var t)) return t;
        const int k = 4;
        int s = size * k;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                var p = new Vector2((x + 0.5f) / k, (y + 0.5f) / k);
                img.SetPixel(x, y, new Color(1, 1, 1, Geometry2D.IsPointInPolygon(p, polygon) ? 1 : 0));
            }
        img.Resize(size, size, Image.Interpolation.Lanczos);
        return Cache[key] = ImageTexture.CreateFromImage(img);
    }
}
