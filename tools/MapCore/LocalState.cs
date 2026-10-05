using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Map;

/// <summary>
/// What is already on this machine: which terrain tiles are downloaded, which are built, and
/// which of the one-off files (TLM, GWR, routes, buildings) are present. Read from the files
/// themselves rather than any MapSetup record, so work done by hand before this tool counts.
/// </summary>
public sealed partial class LocalState
{
    public HashSet<TileId> Downloaded { get; } = new();
    public HashSet<TileId> Built { get; } = new();

    public bool TlmZip { get; private set; }
    public bool TlmExtracted { get; private set; }
    public bool RouteKeys { get; private set; }
    public bool RoutesZips { get; private set; }
    public bool GwrSqlite { get; private set; }
    /// <summary>Lower-case canton codes whose GWR zip is on disk ("ch" for the national one).</summary>
    public HashSet<string> GwrZips { get; } = new();
    /// <summary>Buildings sheet keys (e.g. "1305-24") whose gdb zip is on disk.</summary>
    public HashSet<string> BuildingSheets { get; } = new();
    public bool BuildingsNationwide { get; private set; }

    [GeneratedRegex(@"^swissalti3d_\d{4}_(\d{4})-(\d{4})_0\.5_.*\.xyz(\.zip)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AltiName();

    [GeneratedRegex(@"^swissbuildings3d_3_0_\d{4}_(\d+-\d+)_2056_5728\.gdb\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetName();

    public static LocalState Scan(Paths paths)
    {
        var s = new LocalState();

        if (Directory.Exists(paths.AltiDir))
            foreach (var f in Directory.EnumerateFiles(paths.AltiDir, "swissalti3d_*", SearchOption.AllDirectories))
                if (AltiName().Match(Path.GetFileName(f)) is { Success: true } m)
                    s.Downloaded.Add(new TileId(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));

        var manifest = Path.Combine(paths.Chunks, "manifest.json");
        if (File.Exists(manifest))
        {
            try
            {
                foreach (var t in TerrainManifest.FromJson(File.ReadAllText(manifest)).Tiles)
                    s.Built.Add(t.Id);
            }
            catch (Exception) { /* a half-written manifest just means nothing counts as built */ }
        }

        s.TlmZip = Directory.Exists(paths.TlmDir) && Directory.EnumerateFiles(paths.TlmDir, "*.gpkg.zip").Any();
        s.TlmExtracted = paths.TlmGpkg != null;
        s.RouteKeys = File.Exists(paths.RouteKeys);
        s.RoutesZips = File.Exists(Path.Combine(paths.RoutesDir, "veloland_2056.gdb.zip"))
                       && File.Exists(Path.Combine(paths.RoutesDir, "mountainbikeland_2056.gdb.zip"));
        s.GwrSqlite = File.Exists(paths.GwrSqlite);
        if (Directory.Exists(paths.GwrDir))
            foreach (var f in Directory.EnumerateFiles(paths.GwrDir, "gwr_*.zip"))
                s.GwrZips.Add(Path.GetFileNameWithoutExtension(f)[4..].ToLowerInvariant());

        if (Directory.Exists(paths.BuildingsDir))
            foreach (var f in Directory.EnumerateFiles(paths.BuildingsDir, "swissbuildings3d_3_0_*.gdb.zip"))
            {
                var name = Path.GetFileName(f);
                if (SheetName().Match(name) is { Success: true } m) s.BuildingSheets.Add(m.Groups[1].Value);
                else s.BuildingsNationwide = true;
            }
        return s;
    }
}
