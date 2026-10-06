using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Minimal GeoPackage access: a GeoPackage is a plain SQLite database whose geometry
/// columns hold a small GPKG header followed by standard WKB, and which ships an R-tree
/// index per spatial layer. That is everything we need, so no GDAL dependency.
/// </summary>
public static class GeoPackageReader
{
    /// <summary>A polyline in map coordinates (LV95 east, north, altitude).</summary>
    public sealed record Polyline(double[] E, double[] N, double[] Z)
    {
        public int Count => E.Length;
    }

    public static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Geometry column name for a layer. GeoPackages converted from other formats keep
    /// the original name (swissBUILDINGS3D arrives as "SHAPE", swissTLM3D as "geom").
    /// </summary>
    public static string GeometryColumn(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select column_name from gpkg_geometry_columns where table_name = $t";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() as string
               ?? throw new InvalidDataException($"No geometry column registered for {table}");
    }

    /// <summary>Bbox query that resolves the geometry column itself.</summary>
    public static SqliteCommand BboxQuery(SqliteConnection conn, string table,
        IReadOnlyList<string> columns, double minE, double minN, double maxE, double maxN)
        => BboxQuery(conn, table, GeometryColumn(conn, table), columns, minE, minN, maxE, maxN);

    /// <summary>
    /// Builds a bbox-filtered query against a layer using its R-tree index.
    /// Returns rows of the requested columns plus the geometry blob as the last column.
    /// </summary>
    public static SqliteCommand BboxQuery(SqliteConnection conn, string table, string geomColumn,
        IReadOnlyList<string> columns, double minE, double minN, double maxE, double maxN)
    {
        string cols = string.Join(", ", columns.Select(c => $"t.\"{c}\""));
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            select {cols}, t."{geomColumn}"
            from "{table}" t
            join "rtree_{table}_{geomColumn}" r on t.rowid = r.id
            where r.maxx >= $minE and r.minx <= $maxE
              and r.maxy >= $minN and r.miny <= $maxN
            """;
        cmd.Parameters.AddWithValue("$minE", minE);
        cmd.Parameters.AddWithValue("$maxE", maxE);
        cmd.Parameters.AddWithValue("$minN", minN);
        cmd.Parameters.AddWithValue("$maxN", maxN);
        return cmd;
    }

    /// <summary>
    /// One row of <see cref="TileRows"/>: the requested columns, then the geometry blob, read by
    /// index the way a <see cref="SqliteDataReader"/> row is.
    /// </summary>
    public readonly struct Row
    {
        private readonly object?[] _values;

        public Row(object?[] values) => _values = values;

        public int FieldCount => _values.Length;
        public bool IsDBNull(int i) => _values[i] is null;
        public object GetValue(int i) => _values[i]!;

        public string GetString(int i) => _values[i] as string
            ?? Convert.ToString(_values[i], System.Globalization.CultureInfo.InvariantCulture)!;

        public int GetInt32(int i) => _values[i] switch
        {
            long l => (int)l,
            double d => (int)d,
            var v => int.Parse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!,
                System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    // Rows fetched per sweep of the file. Bounds what is held at once: road lines are a few hundred
    // bytes each, land cover polygons about 2 KB.
    private const int SweepRows = 50_000;

    /// <summary>
    /// The rows of a layer that a tile region needs: the same rows, in the same order, as
    /// <see cref="BboxQuery(SqliteConnection, string, string, IReadOnlyList{string}, double, double, double, double)"/>
    /// over the region's box, minus those whose box does not come within the region's margin of
    /// a wanted tile (#678).
    ///
    /// <para>
    /// It also reads them faster. The R-tree hands rows back in its own order, which is random
    /// access into a 4.8 GB file: 0.81 ms a row cold on a hard disk, against 0.15 ms in rowid
    /// order. So the ids come from the index alone, are fetched in rowid order one sweep at a time,
    /// and are handed back in the order the index gave them, which is the order every extractor's
    /// output was built in.
    /// </para>
    /// </summary>
    public static IEnumerable<Row> TileRows(SqliteConnection conn, string table, string geomColumn,
        IReadOnlyList<string> columns, TileRegion region)
    {
        var ids = new List<long>();
        using (var index = conn.CreateCommand())
        {
            index.CommandText = $"""
                select r.id, r.minx, r.maxx, r.miny, r.maxy
                from "rtree_{table}_{geomColumn}" r
                where r.maxx >= $minE and r.minx <= $maxE
                  and r.maxy >= $minN and r.miny <= $maxN
                """;
            index.Parameters.AddWithValue("$minE", region.MinE);
            index.Parameters.AddWithValue("$maxE", region.MaxE);
            index.Parameters.AddWithValue("$minN", region.MinN);
            index.Parameters.AddWithValue("$maxN", region.MaxN);
            using var r = index.ExecuteReader();
            while (r.Read())
                if (region.Touches(r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)))
                    ids.Add(r.GetInt64(0));
        }

        string cols = string.Join(", ", columns.Select(c => $"t.\"{c}\""));
        int width = columns.Count + 1;
        var sweep = new Dictionary<long, object?[]>();
        for (int start = 0; start < ids.Count; start += SweepRows)
        {
            int count = Math.Min(SweepRows, ids.Count - start);
            var sorted = ids.GetRange(start, count);
            sorted.Sort();

            sweep.Clear();
            using (var fetch = conn.CreateCommand())
            {
                // an id list in the statement: SQLite walks it in ascending rowid order
                fetch.CommandText = $"select t.rowid, {cols}, t.\"{geomColumn}\" from \"{table}\" t where t.rowid in ("
                    + string.Join(",", sorted) + ")";
                using var r = fetch.ExecuteReader();
                while (r.Read())
                {
                    var values = new object?[width];
                    for (int i = 0; i < width; i++)
                        values[i] = r.IsDBNull(i + 1) ? null : r.GetValue(i + 1);
                    sweep[r.GetInt64(0)] = values;
                }
            }
            // an index entry with no row is skipped, as the join skipped it
            for (int i = start; i < start + count; i++)
                if (sweep.TryGetValue(ids[i], out var values))
                    yield return new Row(values);
        }
    }

    /// <summary><see cref="TileRows(SqliteConnection, string, string, IReadOnlyList{string}, TileRegion)"/> that resolves the geometry column itself.</summary>
    public static IEnumerable<Row> TileRows(SqliteConnection conn, string table,
        IReadOnlyList<string> columns, TileRegion region)
        => TileRows(conn, table, GeometryColumn(conn, table), columns, region);

    /// <summary>A closed ring as flat x,y,z triples in map coordinates.</summary>
    public sealed record Ring(double[] Xyz)
    {
        public int Count => Xyz.Length / 3;
    }

    /// <summary>
    /// Parses a GeoPackage geometry blob into polylines. Handles LineString and
    /// MultiLineString, with or without Z/M. Non-linear geometry types are skipped.
    /// </summary>
    public static List<Polyline> ParseLines(byte[] blob)
    {
        var result = new List<Polyline>();
        if (!TryGetWkbOffset(blob, out int offset)) return result;
        ReadGeometry(blob, ref offset, result);
        return result;
    }

    /// <summary>
    /// Parses polygonal geometry into exterior rings. Handles Polygon, MultiPolygon,
    /// PolyhedralSurface, TIN and Triangle — swissBUILDINGS3D solids arrive as a
    /// MultiPolygonZ of triangular faces. Interior rings are ignored: building faces
    /// do not have holes, and treating one as an exterior ring would be worse.
    /// </summary>
    public static List<Ring> ParsePolygons(byte[] blob)
    {
        var result = new List<Ring>();
        if (!TryGetWkbOffset(blob, out int offset)) return result;
        ReadPolygonal(blob, ref offset, result);
        return result;
    }

    /// <summary>
    /// Parses point geometry into map coordinates. Handles Point and MultiPoint, with or
    /// without Z/M — <c>tlm_bb_einzelbaum</c> is a PointZ layer of 11.5 M surveyed trees.
    /// </summary>
    public static List<(double E, double N, double Z)> ParsePoints(byte[] blob)
    {
        var result = new List<(double, double, double)>();
        if (!TryGetWkbOffset(blob, out int offset)) return result;
        ReadPointal(blob, ref offset, result);
        return result;
    }

    private static void ReadPointal(byte[] b, ref int offset, List<(double, double, double)> into)
    {
        bool little = b[offset] == 1;
        offset += 1;
        uint type = ReadU32(b, ref offset, little);

        bool hasZ = (type / 1000) % 2 == 1 || (type & 0x80000000) != 0;
        bool hasM = (type / 1000) >= 2 || (type & 0x40000000) != 0;
        uint baseType = type % 1000;
        if ((type & 0x80000000) != 0 || (type & 0x40000000) != 0)
            baseType = type & 0xFF;

        switch (baseType)
        {
            case 1: // Point
            {
                double e = ReadF64(b, ref offset, little);
                double n = ReadF64(b, ref offset, little);
                double z = hasZ ? ReadF64(b, ref offset, little) : 0;
                if (hasM) offset += 8;
                into.Add((e, n, z));
                break;
            }

            case 4: // MultiPoint
            case 7: // GeometryCollection
            {
                uint n = ReadU32(b, ref offset, little);
                for (uint i = 0; i < n; i++)
                    ReadPointal(b, ref offset, into); // each part carries its own header
                break;
            }
        }
    }

    private static bool TryGetWkbOffset(byte[] blob, out int offset)
    {
        offset = 0;
        if (blob.Length < 8 || blob[0] != 'G' || blob[1] != 'P') return false;

        byte flags = blob[3];
        int envelopeIndicator = (flags >> 1) & 0x07;
        int envelopeBytes = envelopeIndicator switch
        {
            0 => 0, 1 => 32, 2 => 48, 3 => 48, 4 => 64,
            _ => throw new InvalidDataException($"Bad GPKG envelope indicator {envelopeIndicator}"),
        };
        offset = 8 + envelopeBytes;
        return true;
    }

    private static void ReadPolygonal(byte[] b, ref int offset, List<Ring> into)
    {
        bool little = b[offset] == 1;
        offset += 1;
        uint type = ReadU32(b, ref offset, little);

        bool hasZ = (type / 1000) % 2 == 1 || (type & 0x80000000) != 0;
        bool hasM = (type / 1000) >= 2 || (type & 0x40000000) != 0;
        uint baseType = type % 1000;
        if ((type & 0x80000000) != 0 || (type & 0x40000000) != 0)
            baseType = type & 0xFF;

        switch (baseType)
        {
            case 3:  // Polygon
            case 17: // Triangle — same layout
            {
                uint rings = ReadU32(b, ref offset, little);
                for (uint i = 0; i < rings; i++)
                {
                    var ring = ReadRing(b, ref offset, little, hasZ, hasM);
                    if (i == 0) into.Add(ring); // exterior only
                }
                break;
            }

            case 6:  // MultiPolygon
            case 15: // PolyhedralSurface
            case 16: // TIN
            case 7:  // GeometryCollection
            {
                uint n = ReadU32(b, ref offset, little);
                for (uint i = 0; i < n; i++)
                    ReadPolygonal(b, ref offset, into); // each part carries its own header
                break;
            }
        }
    }

    private static Ring ReadRing(byte[] b, ref int offset, bool little, bool hasZ, bool hasM)
    {
        uint n = ReadU32(b, ref offset, little);
        var xyz = new double[n * 3];
        for (uint i = 0; i < n; i++)
        {
            xyz[i * 3 + 0] = ReadF64(b, ref offset, little);
            xyz[i * 3 + 1] = ReadF64(b, ref offset, little);
            xyz[i * 3 + 2] = hasZ ? ReadF64(b, ref offset, little) : 0;
            if (hasM) offset += 8;
        }
        return new Ring(xyz);
    }

    private static void ReadGeometry(byte[] b, ref int offset, List<Polyline> into)
    {
        bool little = b[offset] == 1;
        offset += 1;
        uint type = ReadU32(b, ref offset, little);

        // ISO WKB encodes Z as +1000 / M as +2000; EWKB uses high bits.
        bool hasZ = (type / 1000) % 2 == 1 || (type & 0x80000000) != 0;
        bool hasM = (type / 1000) >= 2 || (type & 0x40000000) != 0;
        uint baseType = type % 1000;
        if ((type & 0x80000000) != 0 || (type & 0x40000000) != 0)
            baseType = type & 0xFF;

        switch (baseType)
        {
            case 2: // LineString
                into.Add(ReadLineString(b, ref offset, little, hasZ, hasM));
                break;

            case 5: // MultiLineString
            {
                uint n = ReadU32(b, ref offset, little);
                for (uint i = 0; i < n; i++)
                    ReadGeometry(b, ref offset, into); // each part carries its own header
                break;
            }

            case 7: // GeometryCollection
            {
                uint n = ReadU32(b, ref offset, little);
                for (uint i = 0; i < n; i++)
                    ReadGeometry(b, ref offset, into);
                break;
            }

            default:
                // points/polygons are not roads — ignore
                break;
        }
    }

    private static Polyline ReadLineString(byte[] b, ref int offset, bool little, bool hasZ, bool hasM)
    {
        uint n = ReadU32(b, ref offset, little);
        var e = new double[n];
        var nn = new double[n];
        var z = new double[n];
        for (uint i = 0; i < n; i++)
        {
            e[i] = ReadF64(b, ref offset, little);
            nn[i] = ReadF64(b, ref offset, little);
            if (hasZ) z[i] = ReadF64(b, ref offset, little);
            if (hasM) offset += 8;
        }
        return new Polyline(e, nn, z);
    }

    private static uint ReadU32(byte[] b, ref int o, bool little)
    {
        uint v = little
            ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o))
            : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        o += 4;
        return v;
    }

    private static double ReadF64(byte[] b, ref int o, bool little)
    {
        double v = little
            ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o))
            : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o));
        o += 8;
        return v;
    }
}
