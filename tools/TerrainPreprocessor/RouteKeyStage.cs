using Microsoft.Data.Sqlite;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Which swissTLM3D road segments carry a signed cycling or mountain-biking route, read straight
/// from the ASTRA Veloland / Mountainbikeland FileGDBs (#537).
///
/// <para>
/// This replaces <c>tools/export_route_keys.py</c>, which needed GDAL's Python bindings to do the
/// same thing. There was never any spatial work in it: both networks carry a <c>TLM_ID</c>
/// referencing a TLM road segment, so this is a plain key export, and with
/// <see cref="FileGdb"/> reading the format it needs nothing installed.
/// </para>
///
/// <para>
/// The output is the same <c>route_keys.sqlite</c> the road stage already reads
/// (<see cref="RoadExtractor.LoadRouteKeys"/>): one <c>route_key(uuid, kind)</c> row per distinct
/// segment, sorted, with the index on <c>uuid</c>. Written to the same place and shape so a
/// database produced by either tool is interchangeable.
/// </para>
/// </summary>
public static class RouteKeyStage
{
    /// <summary>The published zips, the layer in each, and the kind of route it records.</summary>
    private static readonly (string Zip, string Layer, string Kind)[] Sources =
    [
        ("veloland_2056.gdb.zip", "VeloWeg", "cycle"),
        ("mountainbikeland_2056.gdb.zip", "MTBWeg", "mtb"),
    ];

    public const string FileName = "route_keys.sqlite";

    /// <summary>
    /// Reads whichever of the two networks are downloaded in <paramref name="routesDir"/> and writes
    /// <c>route_keys.sqlite</c> there. A missing zip is skipped with a line, not an error: a region
    /// built without the routes layer simply has no signed routes on its roads.
    /// </summary>
    public static int Run(string routesDir, string tempDir, Action<string>? log = null)
    {
        void Say(string line) => (log ?? Console.WriteLine)(line);

        string output = Path.Combine(routesDir, FileName);
        string work = Path.Combine(tempDir, "routegdb");
        Directory.CreateDirectory(work);

        var keys = new List<(string Uuid, string Kind)>();
        foreach (var (zipName, layer, kind) in Sources)
        {
            string zip = Path.Combine(routesDir, zipName);
            if (!File.Exists(zip))
            {
                Say($"  skip {zipName} (not downloaded)");
                continue;
            }

            using var gdb = FileGdb.OpenZip(zip, work);
            if (!gdb.Has(layer))
            {
                Say($"  skip {zipName}: no {layer} layer in it");
                continue;
            }

            using var table = gdb.OpenTable(layer);
            int id = table.FieldIndex("TLM_ID");
            if (id < 0)
            {
                Say($"  skip {zipName}: {layer} has no TLM_ID column");
                continue;
            }

            // Ordinal, and distinct: a route runs over the same road segment in several stages, and
            // the braced upper-case UUID is what the road stage matches TLM's own ids against.
            var distinct = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in table.Rows())
                if (row.Text(id) is { Length: > 0 } uuid) distinct.Add(uuid);

            foreach (var uuid in distinct) keys.Add((uuid, kind));
            Say($"  {kind}: {distinct.Count:N0} distinct TLM road segments");
        }

        if (keys.Count == 0)
        {
            Say("no route networks downloaded; no route keys written");
            return 0;
        }

        Write(output, keys);
        Say($"wrote {output} ({keys.Count:N0} keys)");
        return 0;
    }

    private static void Write(string path, List<(string Uuid, string Kind)> keys)
    {
        if (File.Exists(path)) File.Delete(path);
        using var db = new SqliteConnection($"Data Source={path}");
        db.Open();

        using (var create = db.CreateCommand())
        {
            create.CommandText = "create table route_key (uuid text not null, kind text not null)";
            create.ExecuteNonQuery();
        }

        // One transaction: a quarter of a million separate inserts would take minutes.
        using (var tx = db.BeginTransaction())
        {
            using var insert = db.CreateCommand();
            insert.CommandText = "insert into route_key (uuid, kind) values ($uuid, $kind)";
            var uuid = insert.CreateParameter(); uuid.ParameterName = "$uuid"; insert.Parameters.Add(uuid);
            var kind = insert.CreateParameter(); kind.ParameterName = "$kind"; insert.Parameters.Add(kind);
            foreach (var (u, k) in keys)
            {
                uuid.Value = u;
                kind.Value = k;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }

        using var index = db.CreateCommand();
        index.CommandText = "create index idx_route_key_uuid on route_key (uuid)";
        index.ExecuteNonQuery();
    }
}
