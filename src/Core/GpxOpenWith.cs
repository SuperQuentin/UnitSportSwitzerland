using System.IO;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// A .gpx opened with the game from outside (#1029): its replay starts at once
/// (<see cref="GameShell.OpenGpx"/>). On Android the file comes in an intent
/// (<see cref="AndroidBridge.OpenedFile"/>, the manifest entry is <c>addons/unitsport_android</c>); on
/// desktop it is a launch argument, and the game puts itself in the system's "Open with" list at each
/// launch (<see cref="Register"/>), never as the default app. Note: docs/notes/gpx/open-with.md.
/// </summary>
public static class GpxOpenWith
{
    private const string ProgId = "UnitSportSwitzerland.gpx";
    private const string AppName = "UnitSport Switzerland";

    /// <summary>
    /// The .gpx the game was launched with: a file manager passes it as a bare argument, before any
    /// <c>--</c> (Godot takes a positional argument for a scene only when it ends in .tscn and the like).
    /// </summary>
    public static string? FromArgs()
    {
        foreach (string arg in OS.GetCmdlineArgs())
        {
            if (arg == "--") break;
            if (arg.EndsWith(".gpx", System.StringComparison.OrdinalIgnoreCase) && File.Exists(arg)) return Path.GetFullPath(arg);
        }
        return null;
    }

    /// <summary>
    /// Adds the game to the "Open with" choices for .gpx (Windows: the current user's registry;
    /// Linux: a hidden .desktop entry with the GPX MIME type). Only an exported build, and again at
    /// each launch, so the entry follows the executable when the game moves or updates. Never throws.
    /// </summary>
    public static void Register()
    {
        if (!OS.HasFeature("template") || Platform.IsMobile || DisplayServer.GetName() == "headless") return;
        string exe = OS.GetExecutablePath();
        try
        {
            if (System.OperatingSystem.IsWindows()) RegisterWindows(exe);
            else if (System.OperatingSystem.IsLinux()) RegisterLinux(exe);
        }
        catch (System.Exception e)
        {
            GD.PushWarning($"[gpx] could not register for Open with: {e.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterWindows(string exe)
    {
        using var classes = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        using (var prog = classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", "GPX track");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var command = prog.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        // listed under Open with, never the default: that stays the user's choice
        using (var with = classes.CreateSubKey(@".gpx\OpenWithProgids"))
            with.SetValue(ProgId, System.Array.Empty<byte>(), Microsoft.Win32.RegistryValueKind.None);
        using (var app = classes.CreateSubKey($@"Applications\{Path.GetFileName(exe)}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using var types = app.CreateSubKey("SupportedTypes");
            types.SetValue(".gpx", "");
        }
    }

    private static void RegisterLinux(string exe)
    {
        string data = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share");
        string dir = Path.Combine(data, "applications");
        Directory.CreateDirectory(dir);
        string entry = "[Desktop Entry]\nType=Application\n"
            + $"Name={AppName}\nComment=Replay a GPX track over Switzerland\n"
            + $"Exec=\"{exe.Replace("\"", "\\\"")}\" %f\n"
            + "MimeType=application/gpx+xml;\nNoDisplay=true\nTerminal=false\nCategories=Game;\n";
        string path = Path.Combine(dir, "unitsport-switzerland-gpx.desktop");
        if (File.Exists(path) && File.ReadAllText(path) == entry) return;
        File.WriteAllText(path, entry);
        // the desktop's MIME cache, where the tool exists; the entry works without it on most desktops
        if (Platform.CanSpawnProcesses) OS.CreateProcess("update-desktop-database", new[] { dir });
    }
}
