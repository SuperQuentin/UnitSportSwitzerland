using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Real farm fields (#494): the federal land-use polygons (LWB Nutzungsflächen, MGDM 153.1) of every
/// freely published canton, read straight from their zips (<see cref="LwbReader"/>), plus an
/// OpenStreetMap fallback for the gated cantons (<see cref="OsmFields"/>). Each field is mapped to a
/// <see cref="CropKind"/> (<see cref="FieldCodes"/>), simplified (0.5 m), dropped under 200 m², and
/// written whole into every manifest tile its bounds touch as <c>fields_E_N.fld</c> (<see cref="FieldFormat"/>),
/// plus <c>fields_report.txt</c>. Only those files are written. See docs/notes/tools/farm-fields.md.
/// </summary>
public static class FieldStage
{
    private sealed class Stats
    {
        public long Objects, Parts, Small, Outside, Placed, TileCopies;
        public readonly Dictionary<CropKind, double> AreaByCrop = new();
        public readonly Dictionary<CropKind, long> CountByCrop = new();
        public readonly Dictionary<int, double> Dropped = new();   // known code, CropKind.None: ha
        public readonly Dictionary<int, double> Unmapped = new();  // code not in the table: ha

        public void Merge(Stats o)
        {
            Objects += o.Objects; Parts += o.Parts; Small += o.Small; Outside += o.Outside; Placed += o.Placed; TileCopies += o.TileCopies;
            foreach (var (k, v) in o.AreaByCrop) AreaByCrop[k] = AreaByCrop.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.CountByCrop) CountByCrop[k] = CountByCrop.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.Dropped) Dropped[k] = Dropped.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.Unmapped) Unmapped[k] = Unmapped.GetValueOrDefault(k) + v;
        }
    }

    private static readonly Regex ZipName = new(@"_(?<c>[A-Z]{2})_lv95\.zip$", RegexOptions.IgnoreCase);

    public static int Run(string lwbDir, string? osmPbf, string? gwrPath, string outDir, IReadOnlySet<TileId> region, int jobs)
    {
        var clock = Stopwatch.StartNew();
        var sink = new FieldSink(region);
        var report = new StringBuilder();
        report.AppendLine($"# fields report: {region.Count} manifest tiles, lwb={lwbDir} osm={osmPbf ?? "-"} gwr={gwrPath ?? "-"}");

        // ---- federal data, one task per canton, biggest first ----
        var zips = Directory.Exists(lwbDir)
            ? Directory.GetFiles(lwbDir, "*.zip").Where(z => ZipName.IsMatch(z)).OrderByDescending(z => new FileInfo(z).Length).ToList()
            : [];
        if (zips.Count == 0) Console.Error.WriteLine($"warning: no lwb_nutzungsflaechen_*_XX_lv95.zip in {lwbDir} (swiss_data.py lwb)");
        var total = new Stats();
        var perCanton = new SortedDictionary<string, Stats>();
        Parallel.ForEach(zips, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(jobs, zips.Count)) }, zip =>
        {
            string canton = ZipName.Match(zip).Groups["c"].Value.ToUpperInvariant();
            var st = new Stats();
            var sw = Stopwatch.StartNew();
            try { LwbReader.ReadZip(zip, item => Consume(canton, item, sink, st)); }
            catch (Exception ex) { Console.Error.WriteLine($"{canton}: {ex.GetType().Name}: {ex.Message}"); throw; }
            lock (perCanton) { perCanton[canton] = st; total.Merge(st); }
            Console.WriteLine($"  {canton}: {st.Objects} objects, {st.Placed} fields, {st.AreaByCrop.Values.Sum():F0} ha in {sw.Elapsed.TotalSeconds:F1}s");
        });
        var lwbTiles = new HashSet<TileId>(sink.Tiles.Keys);
        Console.WriteLine($"LWB: {zips.Count} cantons, {total.Placed} fields in {lwbTiles.Count} tiles, {clock.Elapsed.TotalSeconds:F1}s");
        report.AppendLine($"cantons ({zips.Count}): {string.Join(" ", perCanton.Keys)}");
        foreach (var (canton, st) in perCanton)
            report.AppendLine($"  {canton}: objects {st.Objects}, parts {st.Parts}, fields {st.Placed}, under {FieldGeometry.MinAreaM2:F0} m2 {st.Small}, outside manifest {st.Outside}, area {st.AreaByCrop.Values.Sum():F0} ha");
        AppendStats(report, "LWB", total);

        // ---- OpenStreetMap fallback for the gated cantons ----
        if (osmPbf != null)
        {
            var osmClock = Stopwatch.StartNew();
            Func<double, double, bool> gated;
            string how;
            if (gwrPath != null && File.Exists(gwrPath))
            {
                var mask = OsmFields.GatedMask.FromGwr(gwrPath);
                gated = mask.Contains;
                how = $"gated cantons {string.Join(",", OsmFields.GatedCantons.Order())} by GWR building canton (100 m cells)";
            }
            else
            {
                gated = (e, n) => !lwbTiles.Contains(TileId.FromLv95(e, n));
                how = "tiles with no LWB field at all (no --gwr)";
            }
            var polys = OsmFields.Load(osmPbf, jobs);
            var os = new Stats();
            foreach (var p in polys)
            {
                var outer = p.Rings[0];
                double ce = 0, cn = 0;
                for (int i = 0; i < outer.Length; i += 2) { ce += outer[i]; cn += outer[i + 1]; }
                ce /= outer.Length / 2; cn /= outer.Length / 2;
                os.Objects++;
                if (!gated(ce, cn)) { os.Outside++; continue; }
                os.Parts++;
                var rings = FieldGeometry.Prepare(p.Rings);
                if (rings == null) { os.Small++; continue; }
                uint id = FieldGeometry.OsmId(p.Relation, p.Id, p.Part);
                var crop = OsmFields.CropOf(p.Landuse, p.Crop, id);
                int tiles = sink.Add(id, crop, FieldSource.Osm, 0, rings);
                if (tiles == 0) { os.Outside++; continue; }
                os.Placed++; os.TileCopies += tiles;
                os.AreaByCrop[crop] = os.AreaByCrop.GetValueOrDefault(crop) + FieldGeometry.Area(rings) / 10000;
                os.CountByCrop[crop] = os.CountByCrop.GetValueOrDefault(crop) + 1;
            }
            Console.WriteLine($"OSM: {polys.Count} farmland/meadow polygons read, {os.Placed} kept ({how}) in {osmClock.Elapsed.TotalSeconds:F1}s");
            report.AppendLine($"OSM fallback: {how}; polygons {os.Objects}, in gated area {os.Parts}, fields {os.Placed}, under {FieldGeometry.MinAreaM2:F0} m2 {os.Small}, not kept (outside) {os.Outside}");
            AppendStats(report, "OSM", os);
            total.Merge(os);
        }

        // ---- write ----
        Directory.CreateDirectory(outDir);
        var written = 0;
        long bytes = 0;
        var tilesWritten = new HashSet<TileId>();
        Parallel.ForEach(sink.Tiles, new ParallelOptions { MaxDegreeOfParallelism = jobs }, kv =>
        {
            var list = kv.Value.OrderBy(f => f.Id).ThenBy(f => f.Source).ToList();
            using var ms = new MemoryStream();
            FieldFormat.Encode(kv.Key, list, ms);
            string path = Path.Combine(outDir, FieldFormat.FileName(kv.Key));
            File.WriteAllBytes(path, ms.ToArray());
            Interlocked.Add(ref bytes, ms.Length);
            Interlocked.Increment(ref written);
            lock (tilesWritten) tilesWritten.Add(kv.Key);
        });
        // a tile that no longer has fields loses its stale file (only this stage's own files are touched)
        int stale = 0;
        foreach (var tile in region)
        {
            if (tilesWritten.Contains(tile)) continue;
            string path = Path.Combine(outDir, FieldFormat.FileName(tile));
            if (File.Exists(path)) { File.Delete(path); stale++; }
        }
        double secs = clock.Elapsed.TotalSeconds;
        double peakGb = Process.GetCurrentProcess().PeakWorkingSet64 / 1073741824.0;
        string summary = $"{total.Placed} fields ({total.TileCopies} tile copies) in {written} of {region.Count} tiles, {bytes / 1048576.0:F1} MB, {stale} stale files removed, {secs:F0}s, peak {peakGb:F1} GB";
        report.AppendLine(summary);
        File.WriteAllText(Path.Combine(outDir, "fields_report.txt"), report.ToString());
        Console.WriteLine("Fields: " + summary);
        return 0;
    }

    private static void Consume(string canton, LwbReader.Item item, FieldSink sink, Stats st)
    {
        st.Objects++;
        var crop = FieldCodes.Of(item.Code);
        if (crop is null or CropKind.None)
        {
            double ha = 0;
            foreach (var poly in item.Polygons) ha += FieldGeometry.Area(poly) / 10000;
            var into = crop is null ? st.Unmapped : st.Dropped;
            into[item.Code] = into.GetValueOrDefault(item.Code) + ha;
            return;
        }
        for (int part = 0; part < item.Polygons.Count; part++)
        {
            st.Parts++;
            var rings = FieldGeometry.Prepare(item.Polygons[part]);
            if (rings == null) { st.Small++; continue; }
            uint id = FieldGeometry.LwbId(canton, item.Identifier, part);
            int tiles = sink.Add(id, crop.Value, FieldSource.Lwb, (ushort)item.Code, rings);
            if (tiles == 0) { st.Outside++; continue; }
            st.Placed++; st.TileCopies += tiles;
            st.AreaByCrop[crop.Value] = st.AreaByCrop.GetValueOrDefault(crop.Value) + FieldGeometry.Area(rings) / 10000;
            st.CountByCrop[crop.Value] = st.CountByCrop.GetValueOrDefault(crop.Value) + 1;
        }
    }

    private static void AppendStats(StringBuilder sb, string label, Stats st)
    {
        sb.AppendLine($"{label} area per crop (ha, fields):");
        foreach (var (k, v) in st.AreaByCrop.OrderByDescending(p => p.Value))
            sb.AppendLine($"  {k,-12} {v,10:F0} {st.CountByCrop.GetValueOrDefault(k),9}");
        if (st.Dropped.Count > 0)
            sb.AppendLine($"{label} dropped codes (not farm fields here), ha: " + string.Join(", ", st.Dropped.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value:F0}")));
        if (st.Unmapped.Count > 0)
            sb.AppendLine($"{label} UNMAPPED codes (not in FieldCodes), ha: " + string.Join(", ", st.Unmapped.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value:F0}")));
    }

    // ---- self-check -----------------------------------------------------------------------------

    /// <summary>--fields-check: synthetic XTF, simplification, tile placement, encode/decode/rasterise, OSM helpers; 1 on failure.</summary>
    public static int SelfCheck()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }

        // 1. XTF: v3 root names; a wheat field with a hole and a second part, a vineyard, a v2-style tiny one
        const string xtf = """
            <?xml version="1.0" encoding="UTF-8"?><TRANSFER xmlns="http://www.interlis.ch/INTERLIS2.3"><DATASECTION>
            <LWB_Nutzungsflaechen_V3_0.Nutzung BID="b">
            <LWB_Nutzungsflaechen_V3_0.Nutzung.Bezugsjahr TID="y"><Bezugsjahr>2025</Bezugsjahr></LWB_Nutzungsflaechen_V3_0.Nutzung.Bezugsjahr>
            <LWB_Nutzungsflaechen_V3_0.Nutzung.LNF_Nutzung TID="t1"><Flaeche><LWB_Nutzungsflaechen_V3_0.Nutzung.MultiPolygon><Polygons>
              <LWB_Nutzungsflaechen_V3_0.Nutzung.PolygonStructure><Polygon><SURFACE>
                <BOUNDARY><POLYLINE><COORD><C1>2600990</C1><C2>1200990</C2></COORD><COORD><C1>2601050</C1><C2>1200990</C2></COORD><COORD><C1>2601050</C1><C2>1201050</C2></COORD><COORD><C1>2600990</C1><C2>1201050</C2></COORD><COORD><C1>2600990</C1><C2>1200990</C2></COORD></POLYLINE></BOUNDARY>
                <BOUNDARY><POLYLINE><COORD><C1>2601010</C1><C2>1201010</C2></COORD><COORD><C1>2601030</C1><C2>1201010</C2></COORD><COORD><C1>2601030</C1><C2>1201030</C2></COORD><COORD><C1>2601010</C1><C2>1201030</C2></COORD><COORD><C1>2601010</C1><C2>1201010</C2></COORD></POLYLINE></BOUNDARY>
              </SURFACE></Polygon></LWB_Nutzungsflaechen_V3_0.Nutzung.PolygonStructure>
              <LWB_Nutzungsflaechen_V3_0.Nutzung.PolygonStructure><Polygon><SURFACE>
                <BOUNDARY><POLYLINE><COORD><C1>2600000</C1><C2>1200000</C2></COORD><COORD><C1>2600030</C1><C2>1200000</C2></COORD><COORD><C1>2600030</C1><C2>1200030</C2></COORD><COORD><C1>2600000</C1><C2>1200030</C2></COORD><COORD><C1>2600000</C1><C2>1200000</C2></COORD></POLYLINE></BOUNDARY>
              </SURFACE></Polygon></LWB_Nutzungsflaechen_V3_0.Nutzung.PolygonStructure>
            </Polygons></LWB_Nutzungsflaechen_V3_0.Nutzung.MultiPolygon></Flaeche>
              <Identifikator>VS-0001</Identifikator><Ist_Definitiv>true</Ist_Definitiv>
              <Nutzungsart><LWB_Nutzungsflaechen_V3_0.LNF_Kataloge.LNF_Katalog_NutzungsartRef><Reference REF="513"></Reference></LWB_Nutzungsflaechen_V3_0.LNF_Kataloge.LNF_Katalog_NutzungsartRef></Nutzungsart>
              <Programm><LWB_Nutzungsflaechen_V3_0.LNF_Kataloge.LNF_Katalog_ProgrammRef><Reference REF="999"></Reference></LWB_Nutzungsflaechen_V3_0.LNF_Kataloge.LNF_Katalog_ProgrammRef></Programm>
            </LWB_Nutzungsflaechen_V3_0.Nutzung.LNF_Nutzung>
            <LWB_Nutzungsflaechen_V3_0.Nutzung.LNF_Nutzung TID="t2"><Flaeche><LWB_Nutzungsflaechen_V3_0.Nutzung.MultiPolygon><Polygons>
              <LWB_Nutzungsflaechen_V3_0.Nutzung.PolygonStructure><Polygon><SURFACE>
                <BOUNDARY><POLYLINE><COORD><C1>2600100</C1><C2>1200100</C2></COORD><COORD><C1>2600200</C1><C2>1200100</C2></COORD><COORD><C1>2600200</C1><C2>1200200</C2></COORD><COORD><C1>2600100</C1><C2>1200100</C2></COORD></POLYLINE></BOUNDARY>
              </SURFACE></Polygon></LWB_Nutzungsflaechen_V3_0.Nutzung.PolygonStructure>
            </Polygons></LWB_Nutzungsflaechen_V3_0.Nutzung.MultiPolygon></Flaeche>
              <Identifikator>VS-0002</Identifikator>
              <Nutzungsart><LWB_Nutzungsflaechen_V3_0.LNF_Kataloge.LNF_Katalog_NutzungsartRef><Reference REF="701"></Reference></LWB_Nutzungsflaechen_V3_0.LNF_Kataloge.LNF_Katalog_NutzungsartRef></Nutzungsart>
            </LWB_Nutzungsflaechen_V3_0.Nutzung.LNF_Nutzung>
            </LWB_Nutzungsflaechen_V3_0.Nutzung></DATASECTION></TRANSFER>
            """;
        var items = new List<LwbReader.Item>();
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(xtf))) LwbReader.Read(ms, items.Add);
        Check(items.Count == 2, $"xtf: {items.Count} objects, want 2");
        if (items.Count == 2)
        {
            Check(items[0].Code == 513 && items[0].Identifier == "VS-0001", "xtf: code or identifier (the Programm reference must not win)");
            Check(items[0].Polygons.Count == 2 && items[0].Polygons[0].Count == 2 && items[0].Polygons[1].Count == 1, "xtf: polygon and ring structure");
            Check(items[0].Polygons[0][0].Length == 8, "xtf: closing vertex dropped");
            Check(Math.Abs(FieldGeometry.Area(items[0].Polygons[0]) - (3600 - 400)) < 1e-6, "xtf: outer minus hole area");
            Check(items[1].Code == 701, "xtf: second object code");
        }
        Check(FieldCodes.Of(513) == CropKind.Wheat && FieldCodes.Of(701) == CropKind.None && FieldCodes.Of(611) == CropKind.Meadow
              && FieldCodes.Of(616) == CropKind.Pasture && FieldCodes.Of(12345) == null, "codes: table lookups");

        // 2. simplification keeps shape, drops collinear points; small fields and holes go
        var sq = new double[] { 0, 0, 5, 0.1, 10, 0, 10, 10, 5, 10.2, 0, 10 };
        var simple = FieldGeometry.SimplifyRing(sq, 0.5);
        Check(simple.Length == 8 && Math.Abs(Math.Abs(FieldGeometry.SignedArea(simple)) - 100) < 1, $"simplify: {simple.Length / 2} points");
        Check(FieldGeometry.Prepare([new double[] { 0, 0, 10, 0, 10, 10, 0, 10 }]) == null, "prepare: 100 m2 field kept");
        Check(FieldGeometry.Prepare([new double[] { 0, 0, 20, 0, 20, 20, 0, 20 }]) != null, "prepare: 400 m2 field dropped");
        Check(FieldGeometry.LwbId("VS", "a", 0) == FieldGeometry.LwbId("VS", "a", 0) && FieldGeometry.LwbId("VS", "a", 0) != FieldGeometry.LwbId("VS", "a", 1)
              && (FieldGeometry.LwbId("VS", "a", 0) & 0x80000000) == 0 && (FieldGeometry.OsmId(false, 5, 0) & 0x80000000) != 0, "ids: stable, LWB top bit clear, OSM set");

        // 3. tile placement + encode/decode/rasterise: a 60 m field over a tile edge, with a 20 m hole
        var region = new HashSet<TileId> { new(2600, 1200), new(2601, 1200), new(2601, 1201) };
        var sink = new FieldSink(region);
        var prepared = FieldGeometry.Prepare(items.Count == 2 ? items[0].Polygons[0] : [new double[] { 2600990, 1200990, 2601050, 1200990, 2601050, 1201050, 2600990, 1201050 }])!;
        int placed = sink.Add(42, CropKind.Wheat, FieldSource.Lwb, 513, prepared);
        Check(placed == 3 && sink.Tiles.Count == 3, $"place: {placed} tiles, want 3 (the field touches four, one is outside the region)");
        if (sink.Tiles.TryGetValue(new TileId(2601, 1201), out var top))
        {
            using var ms = new MemoryStream();
            FieldFormat.Encode(new TileId(2601, 1201), top, ms);
            ms.Position = 0;
            var back = FieldFormat.Decode(ms);
            Check(back.Count == 1 && back[0].Id == 42 && back[0].Crop == CropKind.Wheat && back[0].LnfCode == 513 && back[0].Rings.Count == 2, "codec: round trip");
            if (back.Count == 1)
            {
                // the part of the field in this tile: E 2601000..2601050 (50 m), N 1201000..1201050 (50 m), hole 10..30 x 10..30
                var owner = FieldFormat.Rasterise(back);
                int cells = owner.Count(o => o == 1);
                // 50 x 50 m minus the 20 x 20 hole = 2100 m2 = 131 cells of 16 m2 (+- the boundary)
                Check(Math.Abs(cells - 131) <= 14, $"rasterise: {cells} cells, want ~131");
                var (hx, hy) = (20 / FieldFormat.CellSize, 20 / FieldFormat.CellSize);
                Check(owner[FieldFormat.CellIndex((int)hx, (int)hy)] == 0 && owner[FieldFormat.CellIndex(1, 1)] == 1, "rasterise: hole is empty, ground owned");
            }
        }
        else fails.Add("place: no top tile");

        // 4. OSM: ring joining, holes, crop choice
        var joined = OsmFields.JoinRings([[1, 2, 3], [3, 4, 5], [1, 6, 5]]);
        Check(joined.Count == 1 && joined[0].Length == 7 && joined[0][0] == joined[0][^1], $"osm: ring join gave {joined.Count} rings");
        var polys = OsmFields.Assemble([new double[] { 0, 0, 100, 0, 100, 100, 0, 100 }], [new double[] { 10, 10, 20, 10, 20, 20 }, new double[] { 500, 500, 510, 500, 510, 510 }]);
        Check(polys.Count == 1 && polys[0].Count == 2, "osm: hole goes to the outer holding it, a stray one is dropped");
        Check(OsmFields.CropOf("meadow", null, 7) == CropKind.Meadow && OsmFields.CropOf("farmland", "barley", 7) == CropKind.Barley
              && OsmFields.CropOf("farmland", null, 29) == CropKind.Wheat && OsmFields.CropOf("farmland", null, 99) == CropKind.OtherArable
              && OsmFields.CropOf("farmland", "weird", 29) == CropKind.Wheat, "osm: crop choice");
        var mask = new OsmFields.GatedMask();
        mask.Add(2500000, 1150000, true); mask.Add(2600000, 1200000, false);
        Check(mask.Contains(2500200, 1150100) && !mask.Contains(2600300, 1200100) && !mask.Contains(2550000, 1170000), "osm: gated mask");

        foreach (var f in fails) Console.Error.WriteLine("FAIL " + f);
        Console.WriteLine(fails.Count == 0 ? "fields-check: OK" : $"fields-check: {fails.Count} failures");
        return fails.Count == 0 ? 0 : 1;
    }
}
