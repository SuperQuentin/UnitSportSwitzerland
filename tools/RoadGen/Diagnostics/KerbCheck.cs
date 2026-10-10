namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// Every pedestrian and bike crossing meets only sloped kerbs (#711, the user's rule): <c>RoadGen --kerb-check --chunks DIR
/// [--at E,N] [--list]</c>. Reads the built tiles and walks each crossing across its ends: a zebra (the yellow bars, SSV 6.17)
/// along three lines through its bars, each 1.5 m on past its outer bars; a bike crossing (its red band) 2 m on past each end.
/// The surface along a line is what the game draws: the raised area props (corners, bands, kerb strips), the street sides
/// (their profile from the attributes) and the carriageway. A rise steeper than <see cref="Steep"/> between two samples
/// <see cref="Step"/> apart is a vertical kerb.
/// </summary>
public static class KerbCheck
{
    /// <summary>Samples this far apart (m) along a walked line.</summary>
    private const double Step = 0.02;

    /// <summary>A rise over one step steeper than this (m per m) is a vertical kerb: a sloped one rises 0.12 over 0.30 (0.4).</summary>
    private const double Steep = 1.25;

    /// <summary>A step lower than this (m) is a seam between two surfaces, not a kerb: counted apart.</summary>
    private const double KerbMin = 0.04;

    /// <summary>What a tile's surface is, for sampling.</summary>
    private sealed class Surface(TileId id, RoadTile tile)
    {
        private readonly List<(double[] X, double[] Z, double[] Y, string What)> _raised = [];
        /// <summary>What the last <see cref="At"/> stood on.</summary>
        public string What = "";
        private readonly List<(RoadSegment Seg, float[] Frac, double X0, double X1, double Z0, double Z1)> _segs = [];
        private readonly List<(double[] X, double[] Z, double[] Y)> _caps = [];
        public TileId Id { get; } = id;
        public RoadTile Tile { get; } = tile;

        public Surface Build()
        {
            foreach (var a in Tile.AreaProps)
            {
                if (!StreetAreas.Is(a.Type) || a.Vertices.Length < 9) continue;
                for (int k = 0; k + 2 < a.Indices.Length; k += 3)
                {
                    var x = new double[3]; var z = new double[3]; var y = new double[3];
                    for (int m = 0; m < 3; m++)
                    {
                        int v = a.Indices[k + m] * 3;
                        (x[m], y[m], z[m]) = (a.Vertices[v], a.Vertices[v + 1] + a.Height, a.Vertices[v + 2]);
                    }
                    _raised.Add((x, z, y, "area " + a.Type));
                }
            }
            foreach (var j in Tile.Junctions)
                for (int k = 0; k + 2 < j.Indices.Length; k += 3)
                {
                    var x = new double[3]; var z = new double[3]; var y = new double[3];
                    for (int m = 0; m < 3; m++)
                    {
                        int v = j.Indices[k + m] * 3;
                        (x[m], y[m], z[m]) = (j.Vertices[v], j.Vertices[v + 1], j.Vertices[v + 2]);
                    }
                    _caps.Add((x, z, y));
                }
            foreach (var s in Tile.Segments)
                if (s.PointCount >= 2 && s.Class <= RoadClass.Square && s.Class != RoadClass.Railway && (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0)
                {
                    // its box, widened by everything beside the carriageway
                    double pad = s.Width * 0.5 + Math.Max(s.Attributes.Left.Reach, s.Attributes.Right.Reach) + 0.5;
                    double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                    for (int i = 0; i < s.PointCount; i++)
                    {
                        x0 = Math.Min(x0, s.Points[i * 3]); x1 = Math.Max(x1, s.Points[i * 3]);
                        z0 = Math.Min(z0, s.Points[i * 3 + 2]); z1 = Math.Max(z1, s.Points[i * 3 + 2]);
                    }
                    _segs.Add((s, RoadStreetSection.Fractions(s), x0 - pad, x1 + pad, z0 - pad, z1 + pad));
                }
            return this;
        }

        private static double? InTri(double[] x, double[] z, double[] y, double px, double pz)
        {
            if (px < Math.Min(x[0], Math.Min(x[1], x[2])) || px > Math.Max(x[0], Math.Max(x[1], x[2]))
                || pz < Math.Min(z[0], Math.Min(z[1], z[2])) || pz > Math.Max(z[0], Math.Max(z[1], z[2]))) return null;
            double d = (z[1] - z[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (z[0] - z[2]);
            if (Math.Abs(d) < 1e-12) return null;
            double a = ((z[1] - z[2]) * (px - x[2]) + (x[2] - x[1]) * (pz - z[2])) / d;
            double b = ((z[2] - z[0]) * (px - x[2]) + (x[0] - x[2]) * (pz - z[2])) / d;
            double c = 1 - a - b;
            const double eps = -1e-6;
            return a < eps || b < eps || c < eps ? null : a * y[0] + b * y[1] + c * y[2];
        }

        /// <summary>The surface height at a tile-local point (x east, z south), NaN off every road surface.</summary>
        public double At(double px, double pz)
        {
            double best = double.NaN;
            What = "";
            void Take(double h, string what) { if (double.IsNaN(best) || h > best) { best = h; What = what; } }
            foreach (var (x, z, y, what) in _raised)
                if (InTri(x, z, y, px, pz) is { } h) Take(h, what);
            foreach (var (seg, frac, sx0, sx1, sz0, sz1) in _segs)
            {
                if (px < sx0 || px > sx1 || pz < sz0 || pz > sz1) continue;
                // nearest point of the centreline: lateral distance (right of travel positive), height, fraction along
                var p = seg.Points;
                double bestD = double.MaxValue, lat = 0, cy = 0, f = 0;
                bool inside = false;
                for (int i = 0; i + 1 < seg.PointCount; i++)
                {
                    double ax = p[i * 3], az = p[i * 3 + 2], bx = p[i * 3 + 3], bz = p[i * 3 + 5];
                    double dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
                    if (len2 < 1e-12) continue;
                    double t = ((px - ax) * dx + (pz - az) * dz) / len2;
                    double tc = Math.Clamp(t, 0, 1);
                    double qx = ax + dx * tc, qz = az + dz * tc, dd = Math.Sqrt((px - qx) * (px - qx) + (pz - qz) * (pz - qz));
                    if (dd >= bestD) continue;
                    bestD = dd;
                    inside = t >= -1e-6 && t <= 1 + 1e-6;
                    lat = (-dz * (px - ax) + dx * (pz - az)) / Math.Sqrt(len2);   // right of travel (tile-local right is (-dz, dx))
                    cy = p[i * 3 + 1] + (p[i * 3 + 4] - p[i * 3 + 1]) * tc;
                    f = frac[i] + (frac[i + 1] - frac[i]) * tc;
                }
                if (!inside) continue;
                double half = seg.Width * 0.5, off = Math.Abs(lat);
                if (off <= half) { Take(cy, "carriageway"); continue; }
                var side = lat > 0 ? seg.Attributes.Right : seg.Attributes.Left;
                if (RoadStreetSection.For(side, seg.Attributes.Has(RoadAttrFlags.LoweredKerbs)) is not { } prof) continue;
                double d = off - half - side.ShiftAt(f);
                if (d < 0 || d > prof.Width) continue;
                int k = 1;
                while (k < prof.Count - 1 && prof.D[k] < d) k++;
                double d0 = prof.D[k - 1], d1 = prof.D[k];
                double h = d1 - d0 < 1e-6 ? prof.H[k] : prof.H[k - 1] + (prof.H[k] - prof.H[k - 1]) * (d - d0) / (d1 - d0);
                Take(cy + h, $"side {(prof.Surface[Math.Min(k - 1, prof.Surface.Length - 1)])}{(seg.Attributes.Has(RoadAttrFlags.LoweredKerbs) ? " lowered" : "")}");
            }
            if (double.IsNaN(best))
                foreach (var (x, z, y) in _caps)
                    if (InTri(x, z, y, px, pz) is { } h) { best = h; What = "cap"; break; }
            return best;
        }
    }

    public static int Run(string chunks, string? at, bool list, Action<string> log)
    {
        var c = CultureInfo.InvariantCulture;
        (double E, double N)? centre = null;
        if (at?.Split(',') is [var se, var sn]) centre = (double.Parse(se, c), double.Parse(sn, c));
        int zebras = 0, bikes = 0, steepZebras = 0, steepBikes = 0, seams = 0;
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(chunks, "roads_*.road"))
        {
            var name = Path.GetFileNameWithoutExtension(file).Split('_');
            if (name.Length != 3 || !int.TryParse(name[1], out int te) || !int.TryParse(name[2], out int tn)) continue;
            var id = new TileId(te, tn);
            RoadTile tile;
            using (var fs = File.OpenRead(file)) tile = RoadCodec.Decode(fs);
            var surface = new Surface(id, tile).Build();
            foreach (var paint in tile.Paint)
            {
                List<(double X, double Z, double DX, double DZ, double Length)> lines;
                bool zebra = paint.Type == PaintType.YellowSolid && paint.Shape == PaintShape.Triangles && Bars(paint) is { Count: >= 2 };
                bool bike = paint.Type == PaintType.BikeCrossing && paint.Shape == PaintShape.Polyline && paint.Vertices.Length >= 6;
                if (!zebra && !bike) continue;
                lines = zebra ? ZebraLines(Bars(paint)!) : BikeLines(paint);
                var (ce, cn) = (id.MinE + lines[0].X, id.MaxN - lines[0].Z);
                if (centre is { } ct && Math.Abs(ce - ct.E) > 60 || centre is { } ct2 && Math.Abs(cn - ct2.N) > 60) continue;
                if (zebra) zebras++; else bikes++;
                double worst = 0;
                (double X, double Z) worstAt = (0, 0);
                string worstWhat = "";
                foreach (var (x0, z0, dx, dz, length) in lines)
                {
                    double prev = double.NaN;
                    string prevWhat = "";
                    for (double s = 0; s <= length; s += Step)
                    {
                        double h = surface.At(x0 + dx * s, z0 + dz * s);
                        // carriageway against junction cap is no kerb (the ribbon is sampled at its centreline's height here)
                        bool road = prevWhat is "carriageway" or "cap" && surface.What is "carriageway" or "cap";
                        if (!double.IsNaN(prev) && !double.IsNaN(h) && !road)
                        {
                            double rise = Math.Abs(h - prev) / Step;
                            if (rise > worst) { worst = rise; worstAt = (x0 + dx * s, z0 + dz * s); worstWhat = h > prev ? $"{prevWhat} -> {surface.What}" : $"{surface.What} -> {prevWhat}"; }
                        }
                        prev = h;
                        prevWhat = surface.What;
                    }
                }
                if (Environment.GetEnvironmentVariable("KERBDBG") is { } kd && kd.Split(',') is [var de, var dn]
                    && Math.Abs(id.MinE + worstAt.X - double.Parse(de, c)) < 1.5 && Math.Abs(id.MaxN - worstAt.Z - double.Parse(dn, c)) < 1.5)
                    foreach (var (x0, z0, dx, dz, length) in lines)
                    {
                        var row = new System.Text.StringBuilder();
                        for (double s = 0; s <= length; s += 0.1)
                        {
                            double h = surface.At(x0 + dx * s, z0 + dz * s);
                            row.Append(double.IsNaN(h) ? "  -   " : (h - surface.At(x0, z0)).ToString("+0.00;-0.00", c) + " ");
                        }
                        log($"  line from LV95 {id.MinE + x0:F1},{id.MaxN - z0:F1} dir ({dx:F2},{-dz:F2}): {row}");
                    }
                if (worst <= Steep) continue;
                if (worst * Step < KerbMin) { seams++; continue; }
                if (zebra) steepZebras++; else steepBikes++;
                bad.Add(string.Create(c, $"{(zebra ? "zebra" : "bike ")} at LV95 {id.MinE + worstAt.X:F1},{id.MaxN - worstAt.Z:F1}: a {worst * Step * 100:F0} cm step, {worstWhat}"));
            }
        }
        log(string.Create(c, $"[kerbcheck] zebras {zebras}, {steepZebras} meeting a vertical kerb; bike crossings {bikes}, {steepBikes} meeting a vertical kerb; {seams} with only a seam under {KerbMin * 100:F0} cm"));
        foreach (var b in list ? bad : bad.Take(20)) log("  " + b);
        bool ok = steepZebras + steepBikes == 0;
        log(ok ? "[kerbcheck] RESULT: ok" : "[kerbcheck] RESULT: FAILED");
        return ok ? 0 : 2;
    }

    /// <summary>A zebra's bars (quads, tile-local x,z): centre, long axis (unit) and half its length; null when it is no zebra.</summary>
    private static List<(double X, double Z, double AX, double AZ, double Half)>? Bars(RoadPaint p)
    {
        var v = p.Vertices;
        var bars = new List<(double, double, double, double, double)>();
        for (int q = 0; (q + 4) * 3 <= v.Length; q += 4)
        {
            double X(int i) => v[(q + i) * 3];
            double Z(int i) => v[(q + i) * 3 + 2];
            double l01 = Math.Sqrt((X(1) - X(0)) * (X(1) - X(0)) + (Z(1) - Z(0)) * (Z(1) - Z(0)));
            double l12 = Math.Sqrt((X(2) - X(1)) * (X(2) - X(1)) + (Z(2) - Z(1)) * (Z(2) - Z(1)));
            double longL = Math.Max(l01, l12), shortL = Math.Min(l01, l12);
            if (Math.Abs(shortL - 0.5) > 0.1 || longL < 1.5 || longL > 4) return null;
            var (ax, az) = l01 > l12 ? (X(1) - X(0), Z(1) - Z(0)) : (X(2) - X(1), Z(2) - Z(1));
            double n = Math.Sqrt(ax * ax + az * az);
            bars.Add(((X(0) + X(1) + X(2) + X(3)) / 4, (Z(0) + Z(1) + Z(2) + Z(3)) / 4, ax / n, az / n, longL / 2));
        }
        return bars.Count >= 2 ? bars : null;
    }

    /// <summary>Three lines across a zebra (through its bars, at -1, 0, +1 m along them), each 1.5 m on past its outer bars.</summary>
    private static List<(double X, double Z, double DX, double DZ, double Length)> ZebraLines(List<(double X, double Z, double AX, double AZ, double Half)> bars)
    {
        // across: from the bar furthest one way to the one furthest the other, square to the bars
        var (ax, az) = (bars[0].AX, bars[0].AZ);
        var (cx, cz) = (-az, ax);
        double Across((double X, double Z, double AX, double AZ, double Half) b) => b.X * cx + b.Z * cz;
        var first = bars.MinBy(Across);
        var last = bars.MaxBy(Across);
        double span = Across(last) - Across(first);
        var lines = new List<(double, double, double, double, double)>();
        foreach (double along in (ReadOnlySpan<double>)[-1.0, 0.0, 1.0])
        {
            double sx = first.X + ax * along - cx * 1.5, sz = first.Z + az * along - cz * 1.5;
            lines.Add((sx, sz, cx, cz, span + 3.0));
        }
        return lines;
    }

    /// <summary>A bike crossing's red band, 3 m on past each end along its end's direction.</summary>
    private static List<(double X, double Z, double DX, double DZ, double Length)> BikeLines(RoadPaint p)
    {
        var v = p.Vertices;
        int n = v.Length / 3;
        var lines = new List<(double, double, double, double, double)>();
        foreach (var (a, b) in new[] { (1, 0), (n - 2, n - 1) })
        {
            double dx = v[b * 3] - v[a * 3], dz = v[b * 3 + 2] - v[a * 3 + 2], len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6) continue;
            dx /= len; dz /= len;
            // from 0.5 m back inside the band, on over its end
            lines.Add((v[b * 3] - dx * 0.5, v[b * 3 + 2] - dz * 0.5, dx, dz, 2.5));
        }
        return lines;
    }
}
