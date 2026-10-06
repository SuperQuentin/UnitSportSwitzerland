using Microsoft.Data.Sqlite;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Turns the exported swissBUILDINGS3D GeoPackage into per-tile .bldg files, joining the
/// GWR building register on the way.
///
/// The join is spatial, not by key: swissBUILDINGS3D 3.0 Beta declares an EGID field but
/// leaves it entirely null, so we match each solid against GWR building points falling
/// inside its footprint (~96% hit rate in testing).
/// </summary>
public sealed class BuildingExtractor
{
    /// <summary>
    /// Where the solids come from: the GeoPackage GDAL used to export, or the FileGDB itself
    /// (#537), read by <see cref="FileGdb"/>. Both yield the same thing — an OBJEKTART and the
    /// rings of one solid — so everything below this line is unaware of which it is.
    /// </summary>
    private readonly Func<double, double, double, double, IEnumerable<(string? Objektart, List<GeoPackageReader.Ring> Rings)>> _solids;
    private readonly List<GwrPoint> _gwr = new();
    private readonly Dictionary<(int, int), List<GwrPoint>> _gwrGrid = new();
    private const double GwrCellSize = 100.0;

    private sealed record GwrPoint(uint Egid, double E, double N, int? Gklas, int? Year,
        int? Floors, int? Dwellings);

    private Func<double, double, double?>? _heightOf;

    /// <summary>
    /// Depth of the building base below our terrain after normalisation. The source
    /// solids include a foundation block, so a little burial is correct — what is not
    /// correct is letting its depth vary with the mismatch between swisstopo's terrain
    /// and ours, which buried some buildings by over 5 m.
    /// </summary>
    private const double FoundationDepth = 0.8;

    /// <summary>Reads the solids from an exported GeoPackage (what GDAL used to produce).</summary>
    public BuildingExtractor(string gpkgPath, string? gwrSqlitePath)
        : this(gwrSqlitePath) => _solids = (minE, minN, maxE, maxN) => FromGeoPackage(gpkgPath, minE, minN, maxE, maxN);

    /// <summary>
    /// Reads the solids straight out of the swissBUILDINGS3D FileGDB zips (#537), with no GDAL and
    /// no intermediate GeoPackage: one conversion step fewer, and nothing to install.
    /// </summary>
    public BuildingExtractor(IReadOnlyList<string> gdbZips, string workDir, string? gwrSqlitePath)
        : this(gwrSqlitePath) => _solids = (minE, minN, maxE, maxN) => FromFileGdb(gdbZips, workDir, minE, minN, maxE, maxN);

    private BuildingExtractor(string? gwrSqlitePath)
    {
        _solids = null!;
        if (gwrSqlitePath != null && File.Exists(gwrSqlitePath))
            LoadGwr(gwrSqlitePath);
    }

    /// <summary>
    /// Each sheet's extent once it has been looked at, and null for one with no buildings layer.
    /// Lives on the extractor because it is reused across batches — which is the whole point.
    /// </summary>
    private readonly Dictionary<string, (double MinE, double MinN, double MaxE, double MaxN)?> _sheetBounds = new();

    private static bool Overlaps((double MinE, double MinN, double MaxE, double MaxN) b,
        double minE, double minN, double maxE, double maxN) =>
        b.MinE < maxE && b.MaxE > minE && b.MinN < maxN && b.MaxN > minN;

    private static IEnumerable<(string?, List<GeoPackageReader.Ring>)> FromGeoPackage(
        string gpkgPath, double minE, double minN, double maxE, double maxN)
    {
        using var conn = GeoPackageReader.Open(gpkgPath);
        using var cmd = GeoPackageReader.BboxQuery(conn, "buildings",
            new[] { "OBJEKTART" }, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string? objektart = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (reader.IsDBNull(1)) continue;
            var rings = GeoPackageReader.ParsePolygons((byte[])reader.GetValue(1));
            if (rings.Count > 0) yield return (objektart, rings);
        }
    }

    /// <summary>
    /// The same solids out of the FileGDB. Each multipatch part is one face, which is exactly the
    /// ring shape the rest of this class already works on, so the geometry needs no conversion
    /// beyond flattening. Filtering is by the solid's own vertices rather than an index: a sheet
    /// holds a few thousand buildings, and skipping the spatial index keeps the reader small.
    /// </summary>
    private IEnumerable<(string?, List<GeoPackageReader.Ring>)> FromFileGdb(
        IReadOnlyList<string> gdbZips, string workDir, double minE, double minN, double maxE, double maxN)
    {
        foreach (string zip in gdbZips)
        {
            // A sheet covers about 4.4 x 3 km and a batch of tiles about 400 km², so nearly every
            // sheet is irrelevant to nearly every batch. Its extent is in the geometry field
            // descriptor, so this costs one header read the first time and nothing afterwards —
            // without it a nationwide build decodes all 14.4 GB of sheets once per batch (#570).
            if (_sheetBounds.TryGetValue(zip, out var known))
            {
                if (known is not { } cached || !Overlaps(cached, minE, minN, maxE, maxN)) continue;
            }

            using var gdb = FileGdb.OpenZip(zip, workDir);
            if (!gdb.Has("Building_solid")) { _sheetBounds[zip] = null; continue; }
            using var table = gdb.OpenTable("Building_solid");
            int shape = table.FieldIndex("SHAPE"), kind = table.FieldIndex("OBJEKTART");
            if (shape < 0 || table.Grid is not { } grid) { _sheetBounds[zip] = null; continue; }

            _sheetBounds[zip] = grid.Bounds;
            if (!Overlaps(grid.Bounds, minE, minN, maxE, maxN)) continue;

            foreach (var row in table.Rows())
            {
                if (row.Blob(shape) is not { } blob) continue;
                var decoded = FileGdbGeometry.DecodeMultiPatch(blob, grid);
                if (decoded.Vertices.Count == 0) continue;

                bool touches = false;
                foreach (var v in decoded.Vertices)
                    if (v.X >= minE && v.X <= maxE && v.Y >= minN && v.Y <= maxN) { touches = true; break; }
                if (!touches) continue;

                var rings = new List<GeoPackageReader.Ring>(decoded.Parts.Count);
                foreach (var part in decoded.Parts)
                {
                    var xyz = new double[part.Count * 3];
                    for (int i = 0; i < part.Count; i++)
                    {
                        var v = decoded.Vertices[part.Start + i];
                        xyz[i * 3] = v.X; xyz[i * 3 + 1] = v.Y; xyz[i * 3 + 2] = v.Z;
                    }
                    rings.Add(new GeoPackageReader.Ring(xyz));
                }
                yield return (kind >= 0 ? row.Text(kind) : null, rings);
            }
        }
    }

    public int CadastreCount => _gwr.Count;
    public int Matched { get; private set; }
    public int Total { get; private set; }

    private void LoadGwr(string path)
    {
        using var conn = GeoPackageReader.Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select EGID, GKODE, GKODN, GKLAS, GBAUJ, GASTW, GANZWHG
            from building where GKODE is not null and GKODN is not null
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var p = new GwrPoint(
                (uint)r.GetInt64(0), r.GetDouble(1), r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetInt32(6));
            _gwr.Add(p);
            var key = ((int)(p.E / GwrCellSize), (int)(p.N / GwrCellSize));
            if (!_gwrGrid.TryGetValue(key, out var list))
                _gwrGrid[key] = list = new List<GwrPoint>();
            list.Add(p);
        }
    }

    public int Reseated { get; private set; }
    public double MaxLift { get; private set; }

    public Dictionary<TileId, BuildingTile> Extract(IReadOnlyCollection<TileId> tiles,
        Func<double, double, double?>? heightOf = null)
    {
        _heightOf = heightOf;
        var result = tiles.ToDictionary(t => t, t => new BuildingTile { Id = t, Buildings = new() });
        if (tiles.Count == 0) return result;

        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MinN), maxN = tiles.Max(t => t.MinN) + ChunkFormat.TileSizeM;

        foreach (var (objektart, rings) in _solids(minE, minN, maxE, maxN))
            if (rings.Count > 0)
                AddBuilding(result, objektart, rings);

        return result;
    }

    public int StrayFaces { get; private set; }
    public int StrayBuildings { get; private set; }

    /// <summary>
    /// swissBUILDINGS3D 3.0 has solids carrying faces kilometres from the building itself —
    /// nationwide, 798 "single houses" span more than 200 m, the worst 4.3 km. Left in, they
    /// inflate the bounding box, so the 3x3 ground sample lands on a distant hillside (re-seat
    /// shifts of 700 m) and the stray triangles render as slivers across the landscape. A face
    /// is dropped when any vertex lies further from the median face centre than
    /// max(<see cref="StrayMinM"/>, <see cref="StrayFactor"/> x the median face distance) —
    /// generous enough that a genuine 600 m hall keeps its far walls. The same happens
    /// vertically (a face 320 m under a house in flat Geneva), so a vertex more than
    /// <see cref="StrayVerticalM"/> from the median face height goes too — the tallest Swiss
    /// tower is 205 m, so its roof sits about 100 m from its median face.
    /// </summary>
    private const double StrayMinM = 150, StrayFactor = 4, StrayVerticalM = 180;

    private List<GeoPackageReader.Ring> DropStrayFaces(List<GeoPackageReader.Ring> rings)
    {
        if (rings.Count < 3) return rings;
        var cx = new double[rings.Count];
        var cy = new double[rings.Count];
        var cz = new double[rings.Count];
        for (int r = 0; r < rings.Count; r++)
        {
            var ring = rings[r];
            for (int i = 0; i < ring.Count; i++)
            {
                cx[r] += ring.Xyz[i * 3]; cy[r] += ring.Xyz[i * 3 + 1]; cz[r] += ring.Xyz[i * 3 + 2];
            }
            cx[r] /= Math.Max(1, ring.Count);
            cy[r] /= Math.Max(1, ring.Count);
            cz[r] /= Math.Max(1, ring.Count);
        }
        double mx = Median(cx), my = Median(cy), mz = Median(cz);
        var dist = new double[rings.Count];
        for (int r = 0; r < rings.Count; r++) dist[r] = Math.Sqrt((cx[r] - mx) * (cx[r] - mx) + (cy[r] - my) * (cy[r] - my));
        double limit = Math.Max(StrayMinM, StrayFactor * Median(dist));

        List<GeoPackageReader.Ring>? kept = null;
        for (int r = 0; r < rings.Count; r++)
        {
            var ring = rings[r];
            bool stray = false;
            for (int i = 0; i < ring.Count && !stray; i++)
            {
                double dx = ring.Xyz[i * 3] - mx, dy = ring.Xyz[i * 3 + 1] - my;
                stray = dx * dx + dy * dy > limit * limit
                    || Math.Abs(ring.Xyz[i * 3 + 2] - mz) > StrayVerticalM;
            }
            if (stray)
            {
                kept ??= rings.Take(r).ToList();
                StrayFaces++;
            }
            else kept?.Add(ring);
        }
        if (kept == null) return rings;
        StrayBuildings++;
        return kept;
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private void AddBuilding(Dictionary<TileId, BuildingTile> result, string? objektart,
        List<GeoPackageReader.Ring> rings)
    {
        rings = DropStrayFaces(rings);
        double minE = double.MaxValue, maxE = double.MinValue;
        double minN = double.MaxValue, maxN = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        int triCount = 0;

        foreach (var ring in rings)
        {
            // a closed ring of n points describes n-1 distinct corners
            int corners = ring.Count - 1;
            if (corners < 3) continue;
            triCount += corners - 2;
            for (int i = 0; i < ring.Count; i++)
            {
                double x = ring.Xyz[i * 3], y = ring.Xyz[i * 3 + 1], z = ring.Xyz[i * 3 + 2];
                if (x < minE) minE = x;
                if (x > maxE) maxE = x;
                if (y < minN) minN = y;
                if (y > maxN) maxN = y;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
        }
        if (triCount == 0) return;

        // a building belongs to the tile containing its centre; it is never split, so it
        // stays whole when neighbouring tiles stream in and out
        double cE = (minE + maxE) * 0.5, cN = (minN + maxN) * 0.5;
        var tile = TileId.FromLv95(cE, cN);
        if (!result.TryGetValue(tile, out var bucket)) return;

        var match = FindCadastre(minE, minN, maxE, maxN, cE, cN);
        // counted here, not per query row: the batch bbox also returns buildings owned by
        // tiles in other batches, which are skipped above and must not dilute the match rate
        Total++;
        if (match != null) Matched++;

        // Re-seat the solid on our own heightfield. Sampling a 3x3 grid over the footprint
        // and taking the median keeps a building on a slope from being driven by one
        // outlying corner.
        double lift = 0;
        if (_heightOf != null)
        {
            var samples = new List<double>(9);
            for (int gy = 0; gy <= 2; gy++)
                for (int gx = 0; gx <= 2; gx++)
                {
                    double? h = _heightOf(minE + (maxE - minE) * gx / 2.0,
                                          minN + (maxN - minN) * gy / 2.0);
                    if (h.HasValue) samples.Add(h.Value);
                }
            if (samples.Count > 0)
            {
                samples.Sort();
                double ground = samples[samples.Count / 2];
                lift = (ground - FoundationDepth) - minZ;
                if (Math.Abs(lift) > 0.05)
                {
                    Reseated++;
                    MaxLift = Math.Max(MaxLift, Math.Abs(lift));
                }
                minZ += lift;
                maxZ += lift;
            }
        }

        var tris = new float[triCount * 9];
        int w = 0;
        foreach (var ring in rings)
        {
            int corners = ring.Count - 1;
            if (corners < 3) continue;
            // fan-triangulate; source faces are already triangles so this is usually a no-op
            for (int i = 1; i < corners - 1; i++)
            {
                WriteVertex(tris, ref w, ring, 0, tile, lift);
                WriteVertex(tris, ref w, ring, i, tile, lift);
                WriteVertex(tris, ref w, ring, i + 1, tile, lift);
            }
        }

        bucket.Buildings.Add(new Building
        {
            Kind = BuildingFormat.Classify(objektart, match?.Gklas, match?.Dwellings),
            Egid = match?.Egid ?? 0,
            YearBuilt = (ushort)Math.Clamp(match?.Year ?? 0, 0, ushort.MaxValue),
            Floors = (byte)Math.Clamp(match?.Floors ?? 0, 0, 255),
            MinY = (float)minZ,
            MaxY = (float)maxZ,
            Triangles = tris,
        });
    }

    private static void WriteVertex(float[] tris, ref int w, GeoPackageReader.Ring ring,
        int index, TileId tile, double lift)
    {
        tris[w++] = (float)(ring.Xyz[index * 3] - tile.MinE);
        tris[w++] = (float)(ring.Xyz[index * 3 + 2] + lift);
        tris[w++] = (float)(tile.MaxN - ring.Xyz[index * 3 + 1]);
    }

    /// <summary>Nearest GWR point whose coordinates fall inside the building footprint.</summary>
    private GwrPoint? FindCadastre(double minE, double minN, double maxE, double maxN,
        double cE, double cN)
    {
        GwrPoint? best = null;
        double bestD2 = double.MaxValue;
        int e0 = (int)(minE / GwrCellSize), e1 = (int)(maxE / GwrCellSize);
        int n0 = (int)(minN / GwrCellSize), n1 = (int)(maxN / GwrCellSize);

        for (int e = e0; e <= e1; e++)
            for (int n = n0; n <= n1; n++)
            {
                if (!_gwrGrid.TryGetValue((e, n), out var list)) continue;
                foreach (var p in list)
                {
                    if (p.E < minE - 1 || p.E > maxE + 1 || p.N < minN - 1 || p.N > maxN + 1) continue;
                    double d2 = (p.E - cE) * (p.E - cE) + (p.N - cN) * (p.N - cN);
                    if (d2 < bestD2) { bestD2 = d2; best = p; }
                }
            }
        return best;
    }
}
