using Godot;

namespace UnitSport.Movie;

/// <summary>
/// The movie being made in this run (#638), kept across studio visits and world loads, and its
/// files in <c>user://movies/</c>.
/// </summary>
public static class MovieSession
{
    public const string Folder = "user://movies";
    public const string Extension = ".usmovie";

    private static MovieProject? _project;

    /// <summary>Something worth a toast happened (a clip saved, nothing to save).</summary>
    public static event Action<string>? Said;

    public static MovieProject Project => _project ??= new MovieProject(ActorIo.Names);

    public static bool HasClips => _project is { Clips.Count: > 0 };

    public static void NewProject()
    {
        _project = new MovieProject(ActorIo.Names);
        ReplayRecorder.Instance?.ForgetGrabs();
    }

    /// <summary>
    /// The replay buffer's last minutes into the movie, one clip per player: F5 in game, or the pause
    /// menu. Says how much went in, unless <paramref name="quiet"/> (opening the studio does it too).
    /// </summary>
    public static int SaveClip(bool quiet = false)
    {
        if (ReplayRecorder.Instance is not { } recorder)
        {
            Said?.Invoke("Nothing is being recorded here");
            return 0;
        }
        if (Core.GameSettings.Current.ReplayMinutes <= 0)
        {
            Said?.Invoke("The replay buffer is off (Settings > Gameplay)");
            return 0;
        }
        double held = recorder.Held;
        int clips = recorder.Grab(Project);
        if (!quiet) Said?.Invoke(clips == 0 ? "Nothing new to save since the last clip"
            : $"Saved the last {Clock(held)} to the movie studio ({clips} {(clips == 1 ? "player" : "players")})");
        return clips;
    }

    public static string Clock(double seconds)
    {
        int s = (int)Math.Round(Math.Max(0, seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    public static string PathOf(string name) => $"{Folder}/{Safe(name)}{Extension}";

    /// <summary>A file name made of what a file name can hold.</summary>
    public static string Safe(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray();
        string safe = new string(chars).Trim();
        return safe.Length == 0 ? "Untitled" : safe;
    }

    /// <summary>Saves the project as <paramref name="name"/>; an error message, or null.</summary>
    public static string? Save(string name)
    {
        try
        {
            var p = Project;
            p.Name = name.Trim().Length > 0 ? name.Trim() : "Untitled";
            p.Compact();
            DirAccess.MakeDirRecursiveAbsolute(Folder);
            string path = ProjectSettings.GlobalizePath(PathOf(p.Name));
            using var file = File.Create(path);
            MovieFile.Write(p, file);
            GD.Print($"[movie] saved {path}: {p.Clips.Count} clips, {p.Tracks.Count} tracks, {file.Length / 1024} KB");
            return null;
        }
        catch (Exception e)
        {
            GD.PushError($"[movie] save failed: {e.Message}");
            return e.Message;
        }
    }

    /// <summary>Opens a saved project in place of the current one; an error message, or null.</summary>
    public static string? Load(string name)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(PathOf(name));
            using var file = File.OpenRead(path);
            _project = MovieFile.Read(file, ActorIo.Names);
            ReplayRecorder.Instance?.ForgetGrabs();
            GD.Print($"[movie] loaded {path}: {_project.Clips.Count} clips");
            return null;
        }
        catch (Exception e)
        {
            GD.PushError($"[movie] load failed: {e.Message}");
            return e.Message;
        }
    }

    /// <summary>The saved projects, newest first.</summary>
    public static List<string> Saved()
    {
        string dir = ProjectSettings.GlobalizePath(Folder);
        if (!Directory.Exists(dir)) return new();
        return new DirectoryInfo(dir).GetFiles("*" + Extension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .ToList();
    }
}
