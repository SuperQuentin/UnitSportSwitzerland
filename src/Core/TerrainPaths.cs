using System.Text.Json;
using Godot;

namespace UnitSport.Core;

public static class TerrainPaths
{
    /// <summary>Written by tools/MapSetup when the data lives on another drive: {"chunks": "...", "data": "..."}.</summary>
    public const string LocationFileName = "terrain_location.json";

    /// <summary>Lets a dedicated server (a service, a container) point somewhere else without flags.</summary>
    public const string ChunksEnvVar = "UNITSPORT_CHUNKS";

    private static string? _chunkDir;

    /// <summary>
    /// Locates the terrain_chunks directory, first match wins:
    /// <list type="number">
    /// <item><c>--chunks &lt;dir&gt;</c> on the command line;</item>
    /// <item>the <c>UNITSPORT_CHUNKS</c> environment variable;</item>
    /// <item>the "chunks" of a <c>terrain_location.json</c> next to the executable, then in the
    /// project folder (MapSetup writes it when told to store the data on another drive);</item>
    /// <item><c>terrain_chunks</c> next to the executable in exported builds, otherwise the project folder.</item>
    /// </list>
    /// <para>
    /// The <c>--chunks</c> override also exists so a client can be pointed at a partial copy of the
    /// world while a server on the same machine serves the full one — which is the only way to
    /// exercise terrain streaming without two computers.
    /// </para>
    /// Resolved once per process (the arguments, the variable and the file do not change) and logged.
    /// </summary>
    public static string FindChunkDir()
    {
        if (_chunkDir != null) return _chunkDir;
        var (dir, source) = ResolveChunkDir();
        GD.Print($"[paths] terrain chunks: {dir} ({source})");
        return _chunkDir = dir;
    }

    private static (string Dir, string Source) ResolveChunkDir()
    {
        if (ParseChunkDirArg() is { } explicitDir)
        {
            if (Directory.Exists(explicitDir)) return (explicitDir, "--chunks");
            GD.PushWarning($"[paths] --chunks {explicitDir} does not exist; falling back");
        }

        if (System.Environment.GetEnvironmentVariable(ChunksEnvVar) is { Length: > 0 } envDir)
        {
            if (Directory.Exists(envDir)) return (envDir, ChunksEnvVar);
            GD.PushWarning($"[paths] {ChunksEnvVar}={envDir} does not exist; falling back");
        }

        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
        foreach (string dir in new[] { exeDir, ProjectSettings.GlobalizePath("res://") })
        {
            string file = Path.Combine(dir, LocationFileName);
            if (ReadLocationChunks(file) is not { } saved) continue;
            if (Directory.Exists(saved)) return (saved, file);
            GD.PushWarning($"[paths] {file} points at {saved}, which does not exist; falling back");
        }

        string next = Path.Combine(exeDir, "terrain_chunks");
        if (Directory.Exists(next)) return (next, "next to the executable");
        return (ProjectSettings.GlobalizePath("res://terrain_chunks"), "project folder");
    }

    /// <summary>The "chunks" entry of a terrain_location.json, relative to the file; null if none or unreadable.</summary>
    private static string? ReadLocationChunks(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Name.Equals("chunks", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String
                    && prop.Value.GetString() is { Length: > 0 } chunks)
                    return Path.GetFullPath(chunks, Path.GetDirectoryName(Path.GetFullPath(file))!);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[paths] cannot read {file}: {e.Message}");
        }
        return null;
    }

    /// <summary>Reads an optional "--chunks &lt;dir&gt;" from the command line.</summary>
    public static string? ParseChunkDirArg()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--chunks")
                return args[i + 1];
        return null;
    }

    /// <summary>
    /// Where streamed terrain is cached. Overridable with <c>--cache &lt;dir&gt;</c> so two
    /// clients on one machine do not share a cache during testing.
    /// </summary>
    public static string FindCacheDir()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--cache")
                return args[i + 1];

        return ProjectSettings.GlobalizePath("user://chunk_cache");
    }
}
