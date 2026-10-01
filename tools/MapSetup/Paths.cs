using System.Text.Json;

namespace UnitSport.Tools.MapSetup;

/// <summary>
/// Where everything lives, resolved from the repository root so the tool works whatever
/// directory it is started from. The layout is the one the README and CLAUDE.md describe.
/// </summary>
public sealed class Paths
{
    public required string Root { get; init; }
    /// <summary>
    /// Somewhere else than the repo's own folders (another drive, a test sandbox): --data / --chunks
    /// for one run, otherwise the location saved in <see cref="LocationFile"/>.
    /// </summary>
    public string? DataOverride { get; init; }
    public string? ChunksOverride { get; init; }

    public string Data => DataOverride ?? DefaultData;
    public string AltiDir => Path.Combine(Data, "swiss_chunks");
    public string TlmDir => Path.Combine(Data, "tlm3d");
    public string BuildingsDir => Path.Combine(Data, "buildings3d");
    public string GwrDir => Path.Combine(Data, "gwr");
    public string RoutesDir => Path.Combine(Data, "routes");

    public string Chunks => ChunksOverride ?? DefaultChunks;
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

    public string GwrSqlite => Path.Combine(GwrDir, "data.sqlite");
    public string RouteKeys => Path.Combine(RoutesDir, "route_keys.sqlite");

    /// <summary>The extracted swissTLM3D GeoPackage, whichever release is on disk (newest name wins).</summary>
    public string? TlmGpkg => Directory.Exists(TlmDir)
        ? Directory.EnumerateFiles(TlmDir, "*.gpkg").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
        : null;

    public string DefaultData => Path.Combine(Root, "ressources", "data");
    public string DefaultChunks => Path.Combine(Root, "terrain_chunks");

    /// <summary>
    /// The saved storage location (gitignored, repo root). The game and the dedicated server read
    /// its "chunks" too (src/Core/TerrainPaths.cs), so tiles built on another drive load with no flag.
    /// </summary>
    public string LocationFile => Path.Combine(Root, DataLocation.FileName);

    /// <summary>The same repo with other folders; null means the repo's own.</summary>
    public Paths With(string? data, string? chunks) => new()
    {
        Root = Root,
        DataOverride = data == null || SamePath(data, DefaultData) ? null : Path.GetFullPath(data),
        ChunksOverride = chunks == null || SamePath(chunks, DefaultChunks) ? null : Path.GetFullPath(chunks),
    };

    /// <summary>Makes this the location later runs (and the game) use; the repo's own folders delete the file.</summary>
    public void SaveLocation()
    {
        if (DataOverride == null && ChunksOverride == null)
            File.Delete(LocationFile);
        else
            new DataLocation { Data = DataOverride, Chunks = ChunksOverride }.Save(LocationFile);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Flags win over the saved location, which wins over the repo's own folders.</summary>
    public static Paths Find(string? data = null, string? chunks = null)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "UnitSportSwitzerland.csproj")))
                {
                    var saved = DataLocation.Load(Path.Combine(dir.FullName, DataLocation.FileName));
                    return new Paths { Root = dir.FullName }.With(data ?? saved.Data, chunks ?? saved.Chunks);
                }
        }
        throw new DirectoryNotFoundException(
            "Could not find the repository root (UnitSportSwitzerland.csproj). Run MapSetup from inside the repo.");
    }
}

/// <summary>terrain_location.json: where the source data and the built tiles live when not in the repo.</summary>
public sealed class DataLocation
{
    public const string FileName = "terrain_location.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string? Data { get; set; }
    public string? Chunks { get; set; }

    /// <summary>Relative entries are relative to the file; a missing or broken file is no location.</summary>
    public static DataLocation Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return new();
            var loc = JsonSerializer.Deserialize<DataLocation>(File.ReadAllText(file), Options) ?? new();
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            return new DataLocation
            {
                Data = string.IsNullOrWhiteSpace(loc.Data) ? null : Path.GetFullPath(loc.Data, dir),
                Chunks = string.IsNullOrWhiteSpace(loc.Chunks) ? null : Path.GetFullPath(loc.Chunks, dir),
            };
        }
        catch (Exception)
        {
            return new();
        }
    }

    public void Save(string file) => File.WriteAllText(file, JsonSerializer.Serialize(this, Options));
}
