using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Pulls the car park polygons out of swissTLM3D (and the hand-traced overrides) so the network
/// stage can lay them out (#499). Runs in the road stage, which already holds the GeoPackage open,
/// and writes <see cref="RawParking"/> files beside the raw roads — the reasons are on that class.
///
/// <para>
/// Only the rings, not a raster: <see cref="CoverExtractor"/> still rasterises the same layers for
/// the ground colour and the fallback surface pattern. The two are deliberately independent — a lot
/// too small or too steep for a layout keeps the pattern.
/// </para>
/// </summary>
public sealed class ParkingAreaExtractor(string gpkgPath)
{
    /// <summary>Lots by the tile holding their centroid, which is the tile that lays them out.</summary>
    public Dictionary<TileId, List<RawParking.Lot>> Lots { get; } = new();

    public int RingCount { get; private set; }
    public int Skipped { get; private set; }

    /// <summary>
    /// A ring this small is not a car park worth laying out: TLM maps forecourts and lay-bys the
    /// same way. The planner rejects them again on its own rules; this just keeps the temp files small.
    /// </summary>
    private const double MinAreaM2 = 120;

    public void Extract(IReadOnlyCollection<TileId> tiles, string? overridesPath)
    {
        if (tiles.Count == 0) return;
        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MinN), maxN = tiles.Max(t => t.MinN) + ChunkFormat.TileSizeM;

        using var conn = GeoPackageReader.Open(gpkgPath);
        using var cmd = GeoPackageReader.BboxQuery(conn, "tlm_areale_verkehrsareal",
            new[] { "objektart" }, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            string? kind = reader.IsDBNull(0) ? null : reader.GetString(0);
            var cover = CoverFormat.ParseTrafficArea(kind);
            if (!CoverFormat.IsParking(cover) || reader.IsDBNull(1)) continue;

            foreach (var ring in GeoPackageReader.ParsePolygons((byte[])reader.GetValue(1)))
                Add(ring.Xyz, ring.Count, cover, "tlm");
        }

        // the hand-traced rings (#84: the rows at Riddes house 15, which TLM does not map at all)
        // come through the same path, so they get real bays like any car park
        if (overridesPath != null && File.Exists(overridesPath)) AddOverrides(overridesPath);
    }

    private void AddOverrides(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("polygons", out var polygons)) return;

        foreach (var poly in polygons.EnumerateArray())
        {
            if (!poly.TryGetProperty("cover", out var coverText)) continue;
            if (!Enum.TryParse<CoverClass>(coverText.GetString(), out var cover)) continue;
            if (!CoverFormat.IsParking(cover)) continue;

            var pts = new List<double>();
            foreach (var p in poly.GetProperty("ring").EnumerateArray())
            {
                pts.Add(p[0].GetDouble());
                pts.Add(p[1].GetDouble());
                pts.Add(0);
            }
            Add(pts.ToArray(), pts.Count / 3, cover, "override");
        }
    }

    /// <summary>
    /// Keeps one ring, flattened to E,N, on the tile holding its centroid — once, even where it
    /// spills into neighbours, so the stage lays out the whole polygon across a seam.
    /// </summary>
    private void Add(double[] xyz, int count, CoverClass cover, string source)
    {
        // drop a duplicated closing point: the planner closes the ring itself
        if (count >= 2
            && Math.Abs(xyz[0] - xyz[(count - 1) * 3]) < 1e-9
            && Math.Abs(xyz[1] - xyz[(count - 1) * 3 + 1]) < 1e-9) count--;
        if (count < 3) { Skipped++; return; }

        var ring = new double[count * 2];
        for (int i = 0; i < count; i++) { ring[i * 2] = xyz[i * 3]; ring[i * 2 + 1] = xyz[i * 3 + 1]; }

        double area = 0, ce = 0, cn = 0;
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            double cross = ring[i * 2] * ring[j * 2 + 1] - ring[j * 2] * ring[i * 2 + 1];
            area += cross;
            ce += (ring[i * 2] + ring[j * 2]) * cross;
            cn += (ring[i * 2 + 1] + ring[j * 2 + 1]) * cross;
        }
        area *= 0.5;
        if (Math.Abs(area) < MinAreaM2) { Skipped++; return; }

        var centroid = (E: ce / (6 * area), N: cn / (6 * area));
        var home = TileId.FromLv95(centroid.E, centroid.N);
        if (!Lots.TryGetValue(home, out var list)) Lots[home] = list = new List<RawParking.Lot>();
        list.Add(new RawParking.Lot(cover, source, ring));
        RingCount++;
    }

    /// <summary>Writes every tile's lots, and clears the file of a tile that no longer has any.</summary>
    public void WriteAll(string rawDir, IReadOnlyCollection<TileId> tiles)
    {
        foreach (var id in tiles)
            RawParking.Write(rawDir, id, Lots.GetValueOrDefault(id) ?? []);
    }
}
