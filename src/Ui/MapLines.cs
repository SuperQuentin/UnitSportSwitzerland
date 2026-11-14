using Godot;
using UnitSport.Map;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Ui;

/// <summary>
/// The map's rivers and canton borders as smooth lines (#1826), not cells: the 500 m relief lattice and the 1 km tile borders would
/// otherwise show as staircases and fat blocks when zoomed in. Built once on a worker thread the first time a map asks (like
/// <see cref="MapRelief"/>), then only projected and drawn: no allocation per redraw (the point buffers are pooled by size and the
/// tail of a buffer is parked off screen, as <see cref="EarthMapView.DrawBorders"/> does).
/// <list type="bullet">
/// <item>Rivers follow the terrain's own flow network through cell centres (<c>Relief.Receiver</c>, <c>Relief.Area</c>, above
/// <see cref="RiverKm2"/>), cut at lakes, split into chains between confluences, smoothed with Chaikin passes, and drawn in three
/// widths by how much water they carry.</item>
/// <item>Borders join the tile-edge segments into chains, are smoothed by a moving average (the 1 km staircase) and a Chaikin pass.</item>
/// </list>
/// All points are LV95 metres, as the relief and <see cref="MapView.Borders"/> hold them; the view's projection does the rest.
/// </summary>
public static class MapLines
{
    /// <summary>Drainage above this many km² is a river on the map: the Rhone, Aare, Rhine, Reuss and their big tributaries.</summary>
    public const double RiverKm2 = 120;
    private static readonly Color RiverColour = new(0.36f, 0.62f, 0.92f);
    private static readonly float[] RiverWidth = [1.5f, 2.3f, 3.2f];

    private sealed class Chain
    {
        public Vector2[] Points = [];
        public float MinX, MinY, MaxX, MaxY;
        public int Class;
    }

    private static Chain[] _rivers = [], _borders = [];
    private static Task? _building;
    private static volatile bool _ready;

    /// <summary>The lines are built.</summary>
    public static bool Ready => _ready;

    /// <summary>Starts the build on a worker the first time; true once the lines can be drawn.</summary>
    public static bool TryGet()
    {
        if (_ready) return true;
        _building ??= Task.Run(Build);
        if (_building.IsFaulted)
        {
            GD.PushWarning($"[map] no smooth lines: {_building.Exception?.GetBaseException().Message}");
            _ready = false;
        }
        return _ready;
    }

    private static void Build()
    {
        var rivers = BuildRivers();
        Chain[] borders = [];
        try
        {
            var country = CountryData.LoadEmbedded();
            borders = BuildBorders(MapView.Borders(country));
        }
        catch (Exception e) { GD.PushWarning($"[map] no canton borders: {e.Message}"); }
        _rivers = rivers;
        _borders = borders;
        _ready = true;
    }

    // ---------------------------------------------------------------------------------------- rivers

    private static Chain[] BuildRivers()
    {
        var r = ProceduralWorld.Relief.Instance;
        var lattice = MapRelief.Lattice();   // the lattice's corner and spacing come as one value (the ratchet counts tile-corner reads)
        int count = r.Cols * r.Rows;
        double perNode = r.NodeAreaKm2;
        bool River(int k) => r.LakeOf[k] < 0 && r.Area[k] * perNode >= RiverKm2;
        var inflow = new int[count];
        for (int k = 0; k < count; k++)
        {
            if (!River(k)) continue;
            int j = r.Receiver[k];
            if (j >= 0 && River(j)) inflow[j]++;
        }
        var chains = new List<Chain>();
        var pts = new List<Vector2>();
        for (int k = 0; k < count; k++)
        {
            // a chain starts at a source or a confluence and runs down to the next confluence, a lake or the edge
            if (!River(k) || inflow[k] == 1) continue;
            pts.Clear();
            int cur = k;
            pts.Add(At(lattice, cur));
            while (true)
            {
                int next = r.Receiver[cur];
                if (next < 0 || !River(next)) break;
                pts.Add(At(lattice, next));
                cur = next;
                if (inflow[cur] != 1) break;
            }
            if (pts.Count < 2) continue;
            double km2 = r.Area[cur] * perNode;
            var smooth = Chaikin(Chaikin(pts.ToArray()));
            chains.Add(Make(smooth, km2 < 400 ? 0 : km2 < 2500 ? 1 : 2));
        }
        return chains.ToArray();
    }

    private static Vector2 At((double MinE, double MaxN, double Spacing, int Cols, int Rows) l, int k)
    {
        var (minE, maxN, spacing, cols, _) = l;
        return new((float)(minE + k % cols * spacing), (float)(maxN - k / cols * spacing));
    }

    // ---------------------------------------------------------------------------------------- borders

    private static Chain[] BuildBorders(Vector2[] segments)
    {
        int n = segments.Length / 2;
        var at = new Dictionary<Vector2, List<int>>();
        void Link(Vector2 p, int s)
        {
            if (!at.TryGetValue(p, out var list)) at[p] = list = new List<int>(2);
            list.Add(s);
        }
        for (int s = 0; s < n; s++) { Link(segments[2 * s], s); Link(segments[2 * s + 1], s); }
        var used = new bool[n];
        var chains = new List<Chain>();
        var pts = new List<Vector2>();

        void Walk(Vector2 from, int first)
        {
            pts.Clear();
            pts.Add(from);
            var cur = from;
            int s = first;
            while (s >= 0 && !used[s])
            {
                used[s] = true;
                var next = segments[2 * s] == cur ? segments[2 * s + 1] : segments[2 * s];
                pts.Add(next);
                cur = next;
                // go on only through a point with exactly one other segment (a junction ends the chain)
                var list = at[cur];
                s = -1;
                if (list.Count == 2) s = !used[list[0]] ? list[0] : !used[list[1]] ? list[1] : -1;
            }
            if (pts.Count < 2) return;
            var line = pts.ToArray();
            for (int pass = 0; pass < 2; pass++) line = Average(line);
            chains.Add(Make(Chaikin(line), 0));
        }

        // open chains first (they start at a junction or an end), then whatever loops remain
        foreach (var (p, list) in at)
            if (list.Count != 2)
                foreach (int s in list.ToArray())
                    if (!used[s]) Walk(p, s);
        for (int s = 0; s < n; s++)
            if (!used[s]) Walk(segments[2 * s], s);
        return chains.ToArray();
    }

    // ---------------------------------------------------------------------------------------- smoothing

    /// <summary>One corner-cutting pass, the ends kept where they are.</summary>
    private static Vector2[] Chaikin(Vector2[] p)
    {
        if (p.Length < 3) return p;
        var o = new List<Vector2>(p.Length * 2) { p[0] };
        for (int i = 0; i + 1 < p.Length; i++)
        {
            if (i > 0) o.Add(p[i] * 0.75f + p[i + 1] * 0.25f);
            if (i + 1 < p.Length - 1) o.Add(p[i] * 0.25f + p[i + 1] * 0.75f);
        }
        o.Add(p[^1]);
        return o.ToArray();
    }

    /// <summary>A 1-2-1 moving average of the inner points: a 1 km staircase becomes a gentle diagonal.</summary>
    private static Vector2[] Average(Vector2[] p)
    {
        if (p.Length < 3) return p;
        var o = new Vector2[p.Length];
        o[0] = p[0];
        o[^1] = p[^1];
        for (int i = 1; i < p.Length - 1; i++) o[i] = (p[i - 1] + p[i] * 2f + p[i + 1]) * 0.25f;
        return o;
    }

    private static Chain Make(Vector2[] pts, int cls)
    {
        var c = new Chain { Points = pts, Class = cls, MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
        foreach (var p in pts)
        {
            c.MinX = Math.Min(c.MinX, p.X); c.MaxX = Math.Max(c.MaxX, p.X);
            c.MinY = Math.Min(c.MinY, p.Y); c.MaxY = Math.Max(c.MaxY, p.Y);
        }
        return c;
    }

    // ---------------------------------------------------------------------------------------- drawing

    private static readonly int[] Sizes = [256, 1024, 4096, 16384, 65536, 262144];
    private static readonly Vector2[][] Pool = new Vector2[Sizes.Length][];
    private static readonly Vector2 Parked = new(-1e4f, -1e4f);

    private static Vector2[] Rent(int count)
    {
        for (int i = 0; i < Sizes.Length; i++)
            if (count <= Sizes[i]) return Pool[i] ??= new Vector2[Sizes[i]];
        return Pool[^1] ??= new Vector2[Sizes[^1]];
    }

    /// <summary>The segments of every chain of <paramref name="chains"/> in <paramref name="cls"/> (-1: all) that touch the view, as screen pairs.</summary>
    private static int Project(Chain[] chains, int cls, EarthMapView view, out Vector2[] buffer)
    {
        var (w, s, e, n) = view.Bounds();
        // a margin so a line that leaves by a corner is not cut short
        float pad = 3000f;
        int need = 0;
        foreach (var c in chains)
            if ((cls < 0 || c.Class == cls) && c.MaxX >= w - pad && c.MinX <= e + pad && c.MaxY >= s - pad && c.MinY <= n + pad) need += (c.Points.Length - 1) * 2;
        buffer = Rent(need);
        int kept = 0;
        foreach (var c in chains)
        {
            if ((cls >= 0 && c.Class != cls) || c.MaxX < w - pad || c.MinX > e + pad || c.MaxY < s - pad || c.MinY > n + pad) continue;
            var p = c.Points;
            var prev = view.ToScreen(p[0].X, p[0].Y);
            for (int i = 1; i < p.Length && kept + 2 <= buffer.Length; i++)
            {
                var next = view.ToScreen(p[i].X, p[i].Y);
                buffer[kept++] = prev;
                buffer[kept++] = next;
                prev = next;
            }
        }
        for (int i = kept; i < buffer.Length; i++) buffer[i] = Parked;
        return kept;
    }

    /// <summary>The rivers, anti-aliased, three widths by flow (a few pixels at every zoom, a little wider as the view closes in).</summary>
    public static void DrawRivers(EarthMapView view, CanvasItem on, float alpha = 1f)
    {
        if (!_ready || _rivers.Length == 0) return;
        float zoomK = 0.85f + 0.08f * view.Zoom;
        for (int cls = 0; cls < RiverWidth.Length; cls++)
        {
            if (Project(_rivers, cls, view, out var buffer) == 0) continue;
            on.DrawMultiline(buffer, new Color(RiverColour, alpha), RiverWidth[cls] * zoomK, true);
        }
    }

    /// <summary>The canton and country borders: a dark halo under a light line when <paramref name="halo"/>, anti-aliased.</summary>
    public static void DrawBorders(EarthMapView view, CanvasItem on, Color line, float width, bool halo)
    {
        if (!_ready || _borders.Length == 0) return;
        if (Project(_borders, -1, view, out var buffer) == 0) return;
        if (halo) on.DrawMultiline(buffer, new Color(0, 0, 0, 0.5f), width + 2.4f, true);
        on.DrawMultiline(buffer, line, width, true);
    }
}
