using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.RoadGen.Rewrite;

/// <summary>
/// The car park polygons the network stage lays out (#499), kept beside the raw roads in the temp
/// dir exactly as <see cref="RawRoads"/> keeps the raw segments.
///
/// <para>
/// <b>Why they live here.</b> The polygons come from swissTLM3D's <c>Parkplatzareal</c> layers, and
/// only the preprocessor holds the GeoPackage open. <c>CoverStage</c> — which does read those layers
/// — runs <i>after</i> the network stage, and rasterises them to a 2 m world-aligned lattice anyway,
/// which is the very thing #499 exists to stop using. So the <b>road stage</b> dumps the rings here
/// before the network stage runs, and <c>--rewrite</c> alone still rebuilds a region byte-identically
/// from its raw input, which is the property the whole stage rests on (`roadgen`).
/// </para>
///
/// <para>
/// A ring is written to the tile holding its <b>centroid</b>, once, even where it spills into
/// neighbours: the stage sees the whole polygon that way and lays out one layout across the seam.
/// The pieces then go to whichever tile holds each of them.
/// </para>
///
/// <para>
/// Text, not binary: a few rings a tile, read once per build, and a format a human can diff when a
/// lot comes out wrong. Invariant culture throughout (`invariant-culture-floats`).
/// </para>
/// </summary>
public static class RawParking
{
    public const string DirName = "parking_raw";

    public static string DirFor(string tempDir) => Path.Combine(tempDir, DirName);

    public static string PathFor(string rawDir, TileId id) =>
        Path.Combine(rawDir, $"{id.E}_{id.N}.park");

    /// <summary>One car park: its cover class, where it came from, and its LV95 ring.</summary>
    public sealed record Lot(CoverClass Cover, string Source, double[] Ring)
    {
        public int Count => Ring.Length / 2;

        /// <summary>The ring as the planner takes it.</summary>
        public List<(double E, double N)> Points()
        {
            var pts = new List<(double E, double N)>(Count);
            for (int i = 0; i < Count; i++) pts.Add((Ring[i * 2], Ring[i * 2 + 1]));
            return pts;
        }
    }

    public static void Write(string rawDir, TileId id, IReadOnlyList<Lot> lots)
    {
        Directory.CreateDirectory(rawDir);
        string path = PathFor(rawDir, id);
        if (lots.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);   // stale from an earlier run
            return;
        }

        var text = new System.Text.StringBuilder();
        foreach (var lot in lots)
        {
            text.Append(lot.Cover).Append('\t').Append(lot.Source).Append('\t');
            for (int i = 0; i < lot.Count; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(lot.Ring[i * 2].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(lot.Ring[i * 2 + 1].ToString("F3", CultureInfo.InvariantCulture));
            }
            text.Append('\n');
        }
        File.WriteAllText(path + ".part", text.ToString());
        File.Move(path + ".part", path, overwrite: true);
    }

    /// <summary>The lots of one tile; empty when the region was built before #499 or the tile has none.</summary>
    public static List<Lot> Read(string rawDir, TileId id)
    {
        var lots = new List<Lot>();
        string path = PathFor(rawDir, id);
        if (!File.Exists(path)) return lots;

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!Enum.TryParse<CoverClass>(parts[0], out var cover)) continue;

            var pts = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ring = new double[pts.Length * 2];
            bool ok = true;
            for (int i = 0; i < pts.Length && ok; i++)
            {
                var en = pts[i].Split(',');
                ok = en.Length == 2
                    && double.TryParse(en[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ring[i * 2])
                    && double.TryParse(en[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ring[i * 2 + 1]);
            }
            if (ok && pts.Length >= 3) lots.Add(new Lot(cover, parts[1], ring));
        }
        return lots;
    }
}
