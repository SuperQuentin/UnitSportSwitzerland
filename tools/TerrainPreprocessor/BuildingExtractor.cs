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
    private readonly string _gpkgPath;
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

    public BuildingExtractor(string gpkgPath, string? gwrSqlitePath)
    {
        _gpkgPath = gpkgPath;
        if (gwrSqlitePath != null && File.Exists(gwrSqlitePath))
            LoadGwr(gwrSqlitePath);
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

        using var conn = GeoPackageReader.Open(_gpkgPath);
        using var cmd = GeoPackageReader.BboxQuery(conn, "buildings",
            new[] { "OBJEKTART" }, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            string? objektart = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (reader.IsDBNull(1)) continue;
            var rings = GeoPackageReader.ParsePolygons((byte[])reader.GetValue(1));
            if (rings.Count == 0) continue;

            Total++;
            AddBuilding(result, objektart, rings);
        }

        return result;
    }

    private void AddBuilding(Dictionary<TileId, BuildingTile> result, string? objektart,
        List<GeoPackageReader.Ring> rings)
    {
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
