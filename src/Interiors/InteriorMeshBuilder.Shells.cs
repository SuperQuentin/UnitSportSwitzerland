using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// Rooms whose walls and ceiling are one curved shell (#2000, <see cref="ShellKind"/>): a barrel vault, a drum, a dome, or
/// glazed (collision only). Drawn as panels between break lines that fall on every opening's edges, so a doorway's cut is
/// exact and nothing is left hanging across it; every panel is collision too, as the plain walls are. The floor follows
/// the shell's foot.
/// </summary>
public static partial class InteriorMeshBuilder
{
    /// <summary>The shell's colours for the layout being built: a colony theme's (set by <see cref="Build"/>), else plain.</summary>
    [System.ThreadStatic] private static (Color Floor, Color Wall, Color Ceiling)? _shellLook;

    private static (Color Floor, Color Wall, Color Ceiling) ShellLook(RoomPlan r)
    {
        if (_shellLook is { } look) return look;
        return Palette(r.Type);
    }

    /// <summary>A colony theme's shell colours (#2000): its frame grey underfoot, its shell's white round, its band overhead.</summary>
    private static (Color, Color, Color)? ShellLookFor(InteriorLayout l)
    {
        if (l.Colony is not int theme) return null;
        var p = World.Moon.ColonyKit.Of((Terrain.Bodies.ColonyTheme)theme);
        static Color Lin(Color c) => new Color(c.R, c.G, c.B).SrgbToLinear() with { A = 1 };
        return ((Lin(p.Frame) * 0.85f) with { A = 1 }, Lin(p.Shell), Lin(p.Band));
    }

    private static void ShellRoom(Scratch s, RoomPlan r, float y0)
    {
        var (floor, wall, ceiling) = ShellLook(r);
        switch (r.Shell)
        {
            case ShellKind.Barrel: BarrelRoom(s, r, y0, floor, wall); break;
            case ShellKind.Drum: DrumRoom(s, r, y0, floor, wall, ceiling); break;
            case ShellKind.Dome: DomeRoom(s, r, y0, floor, wall); break;
            case ShellKind.Glass: GlassRoom(s, r, y0); break;
        }
    }

    // ---- a barrel vault along Z --------------------------------------------------------------------------------------------

    private static void BarrelRoom(Scratch s, RoomPlan r, float y0, Color floorCol, Color wallCol)
    {
        float R = r.ShellR, Ry = r.ShellRy > 0 ? r.ShellRy : R, axis = r.ShellY;
        // phi from the crown: x = R sin phi, y = axis + Ry cos phi; the shell's foot meets the floor at +-phi0
        float phi0 = Mathf.Acos(Mathf.Clamp(-axis / Ry, -1, 1));
        Vector3 At(float phi, float z) => new(R * Mathf.Sin(phi), y0 + axis + Ry * Mathf.Cos(phi), z);
        float half = R * Mathf.Sin(phi0);
        s.Quad(new(-half, y0, r.Z0), new(half, y0, r.Z0), new(half, y0, r.Z1), new(-half, y0, r.Z1), floorCol);

        // side openings: their angle span (from their bottom and top heights) and their z span
        var cuts = new List<(float P0, float P1, float Z0, float Z1)>();
        foreach (var o in r.Openings)
        {
            if (o.Side is not (Side.Left or Side.Right)) continue;
            float sign = o.Side == Side.Right ? 1 : -1;
            float pTop = Mathf.Acos(Mathf.Clamp((o.Top - axis) / Ry, -1, 1)), pBottom = Mathf.Acos(Mathf.Clamp((o.Bottom - axis) / Ry, -1, 1));
            float a = sign * pTop, b = sign * pBottom;
            cuts.Add((Math.Min(a, b), Math.Max(a, b), o.Center - o.Width / 2, o.Center + o.Width / 2));
        }
        var phis = Breaks(-phi0, phi0, 24, cuts.SelectMany(c => new[] { c.P0, c.P1 }));
        var zs = Breaks(r.Z0, r.Z1, 1, cuts.SelectMany(c => new[] { c.Z0, c.Z1 }));
        for (int i = 0; i + 1 < phis.Count; i++)
            for (int j = 0; j + 1 < zs.Count; j++)
            {
                float pm = (phis[i] + phis[i + 1]) / 2, zm = (zs[j] + zs[j + 1]) / 2;
                if (cuts.Exists(c => pm > c.P0 && pm < c.P1 && zm > c.Z0 && zm < c.Z1)) continue;
                s.WallQuad(At(phis[i], zs[j]), At(phis[i + 1], zs[j]), At(phis[i + 1], zs[j + 1]), At(phis[i], zs[j + 1]), wallCol, y0, axis + Ry);
            }

        // the flat end walls, in strips that break at each opening's sill and lintel
        foreach (var side in new[] { Side.Front, Side.Back })
        {
            float z = side == Side.Front ? r.Z0 : r.Z1;
            var holes = r.Openings.Where(o => o.Side == side).ToList();
            var ys = Breaks(0, axis + Ry, 16, holes.SelectMany(o => new[] { o.Bottom, o.Top }));
            for (int k = 0; k + 1 < ys.Count; k++)
            {
                float ya = ys[k], yb = ys[k + 1], ym = (ya + yb) / 2;
                // the strip as wide as the shell at its wider end, so no chink shows where the two meet
                float wa = Ry * Ry - (ya - axis) * (ya - axis), wb = Ry * Ry - (yb - axis) * (yb - axis);
                float x = R / Ry * Mathf.Sqrt(Math.Max(0, Math.Max(wa, wb)));
                if (x < 0.01f) continue;
                float from = -x;
                foreach (var o in holes.Where(o => ym > o.Bottom && ym < o.Top).OrderBy(o => o.Center))
                {
                    float a = Math.Max(-x, o.Center - o.Width / 2), b = Math.Min(x, o.Center + o.Width / 2);
                    if (a > from) EndStrip(s, from, a, ya, yb, z, y0, wallCol, axis + Ry);
                    from = Math.Max(from, b);
                }
                if (x > from) EndStrip(s, from, x, ya, yb, z, y0, wallCol, axis + Ry);
            }
        }
    }

    private static void EndStrip(Scratch s, float x0, float x1, float ya, float yb, float z, float y0, Color col, float height) =>
        s.WallQuad(new(x0, y0 + ya, z), new(x1, y0 + ya, z), new(x1, y0 + yb, z), new(x0, y0 + yb, z), col, y0, height);

    // ---- a drum: an upright cylinder, a flat ceiling -----------------------------------------------------------------------

    private static void DrumRoom(Scratch s, RoomPlan r, float y0, Color floorCol, Color wallCol, Color ceilCol)
    {
        float R = r.ShellR, top = r.Clear;
        var cx = (r.X0 + r.X1) / 2; var cz = (r.Z0 + r.Z1) / 2;
        var cuts = Arcs(r, R);
        var az = Breaks(-Mathf.Pi, Mathf.Pi, 40, cuts.SelectMany(c => new[] { c.A0, c.A1 }));
        Vector3 At(float a, float y) => new(cx + R * Mathf.Sin(a), y0 + y, cz + R * Mathf.Cos(a));
        var centre = new Vector3(cx, y0, cz);
        for (int i = 0; i + 1 < az.Count; i++)
        {
            float a = az[i], b = az[i + 1], m = (a + b) / 2;
            s.Tri(centre, At(a, 0), At(b, 0), floorCol, true);
            s.Tri(centre + Vector3.Up * top, At(b, top), At(a, top), ceilCol, true);
            float from = 0;
            foreach (var c in cuts)
                if (InArc(m, c.A0, c.A1)) from = Math.Max(from, c.Top);
            if (from < top - 0.01f) s.WallQuad(At(a, from), At(b, from), At(b, top), At(a, top), wallCol, y0, top);
        }
    }

    // ---- a dome: an ellipsoid's inside round a centre over (or under) the floor ------------------------------------------

    private static void DomeRoom(Scratch s, RoomPlan r, float y0, Color floorCol, Color wallCol)
    {
        float R = r.ShellR, Ry = r.ShellRy > 0 ? r.ShellRy : R, cy = r.ShellY;
        float cx = (r.X0 + r.X1) / 2, cz = (r.Z0 + r.Z1) / 2;
        float e0 = Mathf.Asin(Mathf.Clamp(-cy / Ry, -1, 1));
        float foot = R * Mathf.Cos(e0);
        var cuts = Arcs(r, foot);
        // elevation of each opening's lintel, so the doorway's top is a ring
        var tops = cuts.Select(c => Mathf.Asin(Mathf.Clamp((c.Top - cy) / Ry, -1, 1)));
        var az = Breaks(-Mathf.Pi, Mathf.Pi, 32, cuts.SelectMany(c => new[] { c.A0, c.A1 }));
        var el = Breaks(e0, Mathf.Pi / 2, 9, tops);
        Vector3 At(float a, float e) => new(cx + R * Mathf.Cos(e) * Mathf.Sin(a), y0 + cy + Ry * Mathf.Sin(e), cz + R * Mathf.Cos(e) * Mathf.Cos(a));
        var centre = new Vector3(cx, y0, cz);
        for (int i = 0; i + 1 < az.Count; i++)
        {
            float a = az[i], b = az[i + 1], m = (a + b) / 2;
            s.Tri(centre, At(a, e0), At(b, e0), floorCol, true);
            for (int k = 0; k + 1 < el.Count; k++)
            {
                float ea = el[k], eb = el[k + 1];
                float ym = cy + Ry * Mathf.Sin((ea + eb) / 2);
                if (cuts.Exists(c => InArc(m, c.A0, c.A1) && ym < c.Top)) continue;
                if (k + 2 == el.Count) s.Tri(At(a, ea), At(b, ea), At(m, eb), wallCol, true);
                else s.WallQuad(At(a, ea), At(b, ea), At(b, eb), At(a, eb), wallCol, y0, cy + Ry);
            }
        }
    }

    // ---- glazed: collision only, the building draws its glass and its floor ----------------------------------------------

    private static void GlassRoom(Scratch s, RoomPlan r, float y0)
    {
        float wallTop = y0 + r.ShellY;
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { s.Col.Add(a); s.Col.Add(b); s.Col.Add(c); s.Col.Add(a); s.Col.Add(c); s.Col.Add(d); }
        void Wall(Vector2 a, Vector2 b) => Face(new(a.X, y0, a.Y), new(b.X, y0, b.Y), new(b.X, wallTop, b.Y), new(a.X, wallTop, a.Y));
        if (r.Shape == null)
        {
            // an alley: its two long sides; its ends open onto the modules
            Wall(new(r.X0, r.Z0), new(r.X0, r.Z1));
            Wall(new(r.X1, r.Z0), new(r.X1, r.Z1));
            Face(new(r.X0, y0, r.Z0), new(r.X1, y0, r.Z0), new(r.X1, y0, r.Z1), new(r.X0, y0, r.Z1));
            if (r.ShellRy > 0)
            {
                float roof = y0 + r.ShellRy;
                Face(new(r.X0, roof, r.Z0), new(r.X1, roof, r.Z0), new(r.X1, roof, r.Z1), new(r.X0, roof, r.Z1));
            }
            return;
        }
        var c = r.Corners();
        float cx = (r.X0 + r.X1) / 2, cz = (r.Z0 + r.Z1) / 2;
        var mid = new Vector2(cx, cz);
        float radius = c[0].DistanceTo(mid);
        var cuts = Arcs(r, radius);
        for (int i = 0; i < c.Length; i++)
        {
            var a = c[i];
            var b = c[(i + 1) % c.Length];
            var m = (a + b) / 2 - mid;
            s.Col.Add(new(cx, y0, cz)); s.Col.Add(new(a.X, y0, a.Y)); s.Col.Add(new(b.X, y0, b.Y));
            if (cuts.Exists(k => InArc(Mathf.Atan2(m.X, m.Y), k.A0, k.A1))) continue;
            Wall(a, b);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    /// <summary>Each opening on a round shell as an arc of azimuth (radians, <c>atan2(x, z)</c>) and its lintel's height.</summary>
    private static List<(float A0, float A1, float Top)> Arcs(RoomPlan r, float radius)
    {
        var list = new List<(float, float, float)>();
        foreach (var o in r.Openings)
        {
            if (o.Angle is not float a) continue;
            float half = Mathf.Asin(Mathf.Clamp(o.Width / 2 / Math.Max(0.1f, radius), 0, 1));
            list.Add((Mathf.Wrap(a - half, -Mathf.Pi, Mathf.Pi), Mathf.Wrap(a + half, -Mathf.Pi, Mathf.Pi), o.Top));
        }
        return list;
    }

    /// <summary>Whether an azimuth is within an arc that may wrap past ±π.</summary>
    private static bool InArc(float a, float a0, float a1)
    {
        a = Mathf.Wrap(a, -Mathf.Pi, Mathf.Pi);
        return a0 <= a1 ? a > a0 && a < a1 : a > a0 || a < a1;
    }

    /// <summary><paramref name="n"/> even steps from <paramref name="from"/> to <paramref name="to"/>, plus every break inside, sorted, no two closer than 1 cm.</summary>
    private static List<float> Breaks(float from, float to, int n, IEnumerable<float> extra)
    {
        var all = new List<float>(n + 8);
        for (int i = 0; i <= n; i++) all.Add(from + (to - from) * i / n);
        foreach (var e in extra)
            if (e > from && e < to) all.Add(e);
        all.Sort();
        var keep = new List<float>(all.Count);
        foreach (var v in all)
            if (keep.Count == 0 || v - keep[^1] > 0.01f) keep.Add(v);
        // the last break is always the end
        keep[^1] = to;
        return keep;
    }
}
