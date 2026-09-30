namespace UnitSport.Tools.MapSetup;

/// <summary>
/// Where everything lives, resolved from the repository root so the tool works whatever
/// directory it is started from. The layout is the one the README and CLAUDE.md describe.
/// </summary>
public sealed class Paths
{
    public required string Root { get; init; }
    /// <summary>--data / --chunks: somewhere else than the repo's own folders (another drive, a test sandbox).</summary>
    public string? DataOverride { get; init; }
    public string? ChunksOverride { get; init; }

    public string Data => DataOverride ?? Path.Combine(Root, "ressources", "data");
    public string AltiDir => Path.Combine(Data, "swiss_chunks");
    public string TlmDir => Path.Combine(Data, "tlm3d");
    public string BuildingsDir => Path.Combine(Data, "buildings3d");
    public string GwrDir => Path.Combine(Data, "gwr");
    public string RoutesDir => Path.Combine(Data, "routes");

    public string Chunks => ChunksOverride ?? Path.Combine(Root, "terrain_chunks");
    /// <summary>
    /// The preprocessor's own default temp dir (&lt;out&gt;_temp), gitignored; all MapSetup state
    /// (selection, logs, measured rates) goes here too.
    /// </summary>
    public string Temp => Chunks.TrimEnd('/', '\\') + "_temp";
    public string StateFile => Path.Combine(Temp, "mapsetup.json");
    public string StatsFile => Path.Combine(Temp, "mapsetup_stats.json");
    public string LogsDir => Path.Combine(Temp, "mapsetup_logs");

    public string Tools => Path.Combine(Root, "tools");
    public string SwissData => Path.Combine(Tools, "swiss_data.py");

    /// <summary>The committed country map; next to the binary when built, in the source folder otherwise.</summary>
    public string CountryFile
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, CountryData.FileName);
            return File.Exists(beside) ? beside : CountrySourceFile;
        }
    }

    public string CountrySourceFile => Path.Combine(Tools, "MapSetup", CountryData.FileName);

    /// <summary>The regional buildings export the feature pass reads (see tools/export_buildings.py).</summary>
    public string BuildingsGpkg => Path.Combine(BuildingsDir, "buildings.gpkg");
    public string GwrSqlite => Path.Combine(GwrDir, "data.sqlite");
    public string RouteKeys => Path.Combine(RoutesDir, "route_keys.sqlite");

    /// <summary>The extracted swissTLM3D GeoPackage, whichever release is on disk (newest name wins).</summary>
    public string? TlmGpkg => Directory.Exists(TlmDir)
        ? Directory.EnumerateFiles(TlmDir, "*.gpkg").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
        : null;

    public static Paths Find(string? data = null, string? chunks = null)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "UnitSportSwitzerland.csproj")))
                    return new Paths
                    {
                        Root = dir.FullName,
                        DataOverride = data == null ? null : Path.GetFullPath(data),
                        ChunksOverride = chunks == null ? null : Path.GetFullPath(chunks),
                    };
        }
        throw new DirectoryNotFoundException(
            "Could not find the repository root (UnitSportSwitzerland.csproj). Run MapSetup from inside the repo.");
    }
}
