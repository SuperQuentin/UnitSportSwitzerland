using Godot;

namespace UnitSport.Core;

public static class TerrainPaths
{
    /// <summary>
    /// Locates the terrain_chunks directory: an explicit <c>--chunks &lt;dir&gt;</c> if given,
    /// otherwise next to the executable in exported builds, otherwise the project folder.
    /// <para>
    /// The override exists so a client can be pointed at a partial copy of the world while a
    /// server on the same machine serves the full one — which is the only way to exercise
    /// terrain streaming without two computers.
    /// </para>
    /// </summary>
    public static string FindChunkDir()
    {
        if (ParseChunkDirArg() is { } explicitDir)
        {
            if (Directory.Exists(explicitDir)) return explicitDir;
            GD.PushWarning($"[paths] --chunks {explicitDir} does not exist; falling back");
        }

        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
        string next = Path.Combine(exeDir, "terrain_chunks");
        if (Directory.Exists(next)) return next;
        return ProjectSettings.GlobalizePath("res://terrain_chunks");
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
