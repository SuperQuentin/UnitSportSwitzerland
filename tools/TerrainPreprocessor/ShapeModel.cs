using System.Globalization;

namespace UnitSport.Tools.Preprocessor;

// Plain C# (linked into the unit tests).

/// <summary>
/// A small body's shape model (#1853: Phobos, Deimos) as the whole-body height raster <see cref="CubeBody"/> reads: a
/// triangle mesh round the body's centre, each pixel of a simple cylindrical grid (180° W to 180° E, 90° N to 90° S, the
/// pixel's centre) the farthest hit of the ray from the centre through that latitude and longitude, less the reference
/// sphere's radius. So the body becomes heights over a sphere, which the cube pyramid, the globe and the near ground
/// already draw and stand on: a body as lumpy as Phobos (8 to 14 km from its centre) is still one height a direction
/// (an overhang keeps its outer face).
///
/// <para>
/// The meshes are Celestia's binary <c>.cmod</c> of Ernst et al. (2023, CC-BY-4.0), in km. Their axes are Celestia's: x
/// the prime meridian's opposite, y the north pole, z 90° E; the body's IAU frame is (−x, z, y). Checked on Phobos
/// against DLR's georeferenced HRSC DEM (Willner et al. 2010): 0.21 km rms over a 7.5° grid, the best of six
/// orientations by three times, its prime meridian within 2°.
/// </para>
/// </summary>
public static class ShapeModel
{
    /// <summary>A mesh: vertices (IAU body-fixed axes, metres) and triangles (vertex indices).</summary>
    public sealed record Mesh(double[] X, double[] Y, double[] Z, int[] Triangles)
    {
        public int Vertices => X.Length;
        public int TriangleCount => Triangles.Length / 3;
    }

    /// <summary>A Celestia binary model's first mesh, its positions in the body's IAU axes, scaled to metres.</summary>
    public static Mesh ReadCmod(byte[] b, double metresPerUnit = 1000)
    {
        const string Head = "#celmodel_binary";
        if (b.Length < 16 || System.Text.Encoding.ASCII.GetString(b, 0, 16) != Head) throw new InvalidDataException("not a binary cmod");
        int p = 16;
        short I16() { short v = BitConverter.ToInt16(b, p); p += 2; return v; }
        uint U32() { uint v = BitConverter.ToUInt32(b, p); p += 4; return v; }
        void Property()
        {
            short type = I16();
            switch (type)
            {
                case 1 or 2 or 3 or 4: p += 4 * type; break;     // float1..4
                case 5: p += 2 + BitConverter.ToUInt16(b, p); break;   // string
                case 6: p += 4; break;                            // uint32
                case 7: p += 12; break;                           // colour
                default: throw new InvalidDataException($"cmod: property type {type}");
            }
        }
        while (p < b.Length)
        {
            short token = I16();
            if (token == 1001)   // material
            {
                while (true)
                {
                    short t = I16();
                    if (t == 1002) break;
                    if (t == 1007) I16();   // a texture's semantic
                    Property();
                }
                continue;
            }
            if (token != 1009) throw new InvalidDataException($"cmod: token {token} at {p - 2}");
            if (I16() != 1011) throw new InvalidDataException("cmod: no vertex description");
            int stride = 0, position = -1;
            while (true)
            {
                short semantic = I16();
                if (semantic == 1012) break;
                short format = I16();
                if (semantic == 0) position = stride;
                stride += format switch { 0 => 1, 1 => 2, 2 => 3, 3 => 4, 4 => 1, _ => throw new InvalidDataException($"cmod: vertex format {format}") };
            }
            if (position < 0 || I16() != 1013) throw new InvalidDataException("cmod: no positions");
            int n = (int)U32();
            var x = new double[n]; var y = new double[n]; var z = new double[n];
            for (int v = 0; v < n; v++)
            {
                int at = p + (v * stride + position) * 4;
                double cx = BitConverter.ToSingle(b, at), cy = BitConverter.ToSingle(b, at + 4), cz = BitConverter.ToSingle(b, at + 8);
                // Celestia's axes to the IAU's: X = −x, Y = z, Z = y
                x[v] = -cx * metresPerUnit; y[v] = cz * metresPerUnit; z[v] = cy * metresPerUnit;
            }
            p += n * stride * 4;
            var tris = new List<int>();
            while (true)
            {
                short prim = I16();
                if (prim == 1010) break;
                U32();   // material
                int count = (int)U32();
                if (prim != 0) throw new InvalidDataException($"cmod: primitive {prim} (triangle lists only)");
                for (int k = 0; k < count; k++) tris.Add((int)BitConverter.ToUInt32(b, p + 4 * k));
                p += 4 * count;
            }
            return new Mesh(x, y, z, tris.ToArray());
        }
        throw new InvalidDataException("cmod: no mesh");
    }

    /// <summary>
    /// The mesh's heights over a sphere of <paramref name="radius"/> on a <paramref name="width"/> × width/2 simple
    /// cylindrical grid, metres, row 0 the northmost: each pixel's ray from the centre, its farthest hit. Triangles are
    /// binned by 1° of latitude and longitude round their directions, so a ray meets only its own few.
    /// </summary>
    public static float[] Heights(Mesh m, double radius, int width, out int missed)
    {
        int height = width / 2;
        const int BinsLat = 180, BinsLon = 360;
        var bins = new List<int>[BinsLat * BinsLon];
        for (int t = 0; t < m.TriangleCount; t++)
        {
            int a = m.Triangles[3 * t], b = m.Triangles[3 * t + 1], c = m.Triangles[3 * t + 2];
            // the triangle's mean direction and how far its corners stray from it
            double cx = m.X[a] + m.X[b] + m.X[c], cy = m.Y[a] + m.Y[b] + m.Y[c], cz = m.Z[a] + m.Z[b] + m.Z[c];
            double cl = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            cx /= cl; cy /= cl; cz /= cl;
            double spread = 0;
            foreach (int v in (ReadOnlySpan<int>)[a, b, c])
            {
                double l = Math.Sqrt(m.X[v] * m.X[v] + m.Y[v] * m.Y[v] + m.Z[v] * m.Z[v]);
                double dot = (m.X[v] * cx + m.Y[v] * cy + m.Z[v] * cz) / l;
                spread = Math.Max(spread, Math.Acos(Math.Clamp(dot, -1, 1)));
            }
            spread = spread * 180 / Math.PI + 0.6;
            double lat = Math.Asin(Math.Clamp(cz, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(cy, cx) * 180 / Math.PI;
            int r0 = Math.Max(0, (int)Math.Floor(90 - lat - spread)), r1 = Math.Min(BinsLat - 1, (int)Math.Floor(90 - lat + spread));
            // a cap of radius ρ round latitude φ spans asin(sin ρ / cos φ) of longitude either way, or all of it past a pole
            int k0 = 0, k1 = BinsLon - 1;
            if (Math.Abs(lat) + spread < 89)
            {
                double dLon = Math.Asin(Math.Min(1, Math.Sin(spread * Math.PI / 180) / Math.Cos(lat * Math.PI / 180))) * 180 / Math.PI + 1;
                if (dLon < 180) { k0 = (int)Math.Floor(lon + 180 - dLon); k1 = (int)Math.Floor(lon + 180 + dLon); }
            }
            for (int r = r0; r <= r1; r++)
                for (int k = k0; k <= k1; k++)
                {
                    int kk = ((k % BinsLon) + BinsLon) % BinsLon;
                    (bins[r * BinsLon + kk] ??= new List<int>()).Add(t);
                }
        }

        var heights = new float[width * height];
        int miss = 0;
        Parallel.For(0, height, row =>
        {
            double lat = 90 - (row + 0.5) * 180.0 / height;
            int binRow = Math.Clamp((int)Math.Floor(90 - lat), 0, BinsLat - 1);
            double sl = Math.Sin(lat * Math.PI / 180), cl = Math.Cos(lat * Math.PI / 180);
            for (int col = 0; col < width; col++)
            {
                double lon = -180 + (col + 0.5) * 360.0 / width;
                int binCol = Math.Clamp((int)Math.Floor(lon + 180), 0, BinsLon - 1);
                double dx = cl * Math.Cos(lon * Math.PI / 180), dy = cl * Math.Sin(lon * Math.PI / 180), dz = sl;
                double best = double.NaN;
                if (bins[binRow * BinsLon + binCol] is { } list)
                    foreach (int t in list)
                    {
                        double hit = Hit(m, t, dx, dy, dz);
                        if (hit > 0 && !(hit <= best)) best = hit;
                    }
                if (double.IsNaN(best)) Interlocked.Increment(ref miss);
                heights[row * width + col] = (float)(best - radius);
            }
        });
        missed = miss;
        // a ray that slipped between two triangles' edges takes its row neighbours' mean
        for (int i = 0; i < heights.Length; i++)
            if (float.IsNaN(heights[i]))
            {
                int row = i / width, col = i % width;
                float l = heights[row * width + (col + width - 1) % width], r = heights[row * width + (col + 1) % width];
                heights[i] = float.IsNaN(l) ? r : float.IsNaN(r) ? l : (l + r) / 2;
            }
        return heights;
    }

    /// <summary>Möller-Trumbore from the centre along a unit direction: the distance to triangle <paramref name="t"/>, or NaN.</summary>
    private static double Hit(Mesh m, int t, double dx, double dy, double dz)
    {
        int a = m.Triangles[3 * t], b = m.Triangles[3 * t + 1], c = m.Triangles[3 * t + 2];
        double e1x = m.X[b] - m.X[a], e1y = m.Y[b] - m.Y[a], e1z = m.Z[b] - m.Z[a];
        double e2x = m.X[c] - m.X[a], e2y = m.Y[c] - m.Y[a], e2z = m.Z[c] - m.Z[a];
        double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
        double det = e1x * px + e1y * py + e1z * pz;
        if (Math.Abs(det) < 1e-12) return double.NaN;
        double inv = 1 / det;
        double tx = -m.X[a], ty = -m.Y[a], tz = -m.Z[a];
        double u = (tx * px + ty * py + tz * pz) * inv;
        if (u < -1e-9 || u > 1 + 1e-9) return double.NaN;
        double qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
        double v = (dx * qx + dy * qy + dz * qz) * inv;
        if (v < -1e-9 || u + v > 1 + 1e-9) return double.NaN;
        return (e2x * qx + e2y * qy + e2z * qz) * inv;
    }

    /// <summary>
    /// The raster as <see cref="CylRaster"/> reads it: a classic little-endian TIFF, one uncompressed strip of int16
    /// metres (rounded), width × width/2.
    /// </summary>
    public static byte[] Tiff(float[] heights, int width)
    {
        int height = width / 2;
        const int Entries = 10;
        int ifd = 8, dataAt = ifd + 2 + Entries * 12 + 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write(ifd);
        w.Write((ushort)Entries);
        void Entry(ushort tag, ushort type, int value) { w.Write(tag); w.Write(type); w.Write(1); if (type == 3) { w.Write((ushort)value); w.Write((ushort)0); } else w.Write(value); }
        Entry(256, 4, width);
        Entry(257, 4, height);
        Entry(258, 3, 16);
        Entry(259, 3, 1);
        Entry(262, 3, 1);
        Entry(273, 4, dataAt);
        Entry(277, 3, 1);
        Entry(278, 4, height);
        Entry(279, 4, width * height * 2);
        Entry(339, 3, 2);
        w.Write(0);
        foreach (float h in heights) w.Write((short)Math.Clamp(Math.Round(h), -32767, 32767));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary><c>--shape-dem MODEL.cmod --shape-radius M --shape-out FILE.tif [--shape-width 1440]</c>.</summary>
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--shape-dem");

    public static int Run(IReadOnlyList<string> args)
    {
        string? model = null, outPath = null;
        double radius = 0;
        int width = 1440;
        for (int i = 0; i < args.Count; i++)
            switch (args[i])
            {
                case "--shape-dem": model = args[++i]; break;
                case "--shape-radius": radius = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--shape-out": outPath = args[++i]; break;
                case "--shape-width": width = int.Parse(args[++i], CultureInfo.InvariantCulture) / 2 * 2; break;
                default: Console.Error.WriteLine($"--shape-dem: unknown option {args[i]}"); return 2;
            }
        if (model == null || outPath == null || radius <= 0) { Console.Error.WriteLine("--shape-dem needs a model, --shape-radius and --shape-out"); return 2; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mesh = ReadCmod(File.ReadAllBytes(model));
        var h = Heights(mesh, radius, width, out int missed);
        File.WriteAllBytes(outPath, Tiff(h, width));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"shape-dem: {Path.GetFileName(model)} {mesh.Vertices} vertices, {mesh.TriangleCount} triangles -> {outPath} {width} x {width / 2}, {h.Min():F0} to {h.Max():F0} m over {radius:F0} m, {missed} rays between triangles, {sw.Elapsed.TotalSeconds:F1} s"));
        return 0;
    }
}
