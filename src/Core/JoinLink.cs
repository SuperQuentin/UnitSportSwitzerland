using System;
using System.IO;
using System.Linq;
#if GODOT
using Godot;
#endif

namespace UnitSport.Core;

/// <summary>
/// <c>unitsport://join/host[:port]</c> (#1220): the website's Join button opens the installed game,
/// which joins that server from the title screen (<c>GameShell.Join</c>). An exported build
/// registers the scheme at each launch, like the .gpx "Open with" entry (<c>GpxOpenWith</c>).
/// Note: docs/notes/net/join-link.md.
/// </summary>
public static class JoinLink
{
    public const string Scheme = "unitsport";

    /// <summary>
    /// The endpoint ("host" or "host:port") a <c>unitsport://join/...</c> link names, or null for any
    /// other text. Strict: the link comes from a web page, so only a hostname, an IPv4 or a bracketed
    /// IPv6 address and a port 1-65535 pass; a trailing slash, query or fragment is dropped.
    /// </summary>
    public static string? Parse(string link)
    {
        const string prefix = Scheme + "://join/";
        if (!link.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = link[prefix.Length..];
        int end = rest.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0) rest = rest[..end];

        string host = rest, port = "";
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']');
            if (close < 0) return null;
            host = rest[..(close + 1)];
            string tail = rest[(close + 1)..];
            if (tail.Length > 0)
            {
                if (tail[0] != ':') return null;
                port = tail[1..];
            }
            string inner = host[1..^1];
            if (inner.Length == 0 || !inner.All(c => Uri.IsHexDigit(c) || c is ':' or '.')) return null;
        }
        else
        {
            int colon = rest.IndexOf(':');
            if (colon >= 0) (host, port) = (rest[..colon], rest[(colon + 1)..]);
            if (host.Length is 0 or > 253 || host.StartsWith('-') || host.StartsWith('.')
                || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) return null;
        }
        if (port.Length > 0 && !(port.All(char.IsAsciiDigit) && int.TryParse(port, out int p) && p is > 0 and <= 65535)) return null;
        return port.Length > 0 ? $"{host}:{port}" : host;
    }

#if GODOT
    /// <summary>The endpoint of a link the game was launched with: a bare argument, before any <c>--</c>.</summary>
    public static string? FromArgs()
    {
        foreach (string arg in OS.GetCmdlineArgs())
        {
            if (arg == "--") break;
            if (Parse(arg) is { } endpoint) return endpoint;
        }
        return null;
    }

    /// <summary>
    /// Makes the game the handler of <c>unitsport://</c> (Windows: the current user's registry; Linux: a
    /// hidden .desktop entry set as the scheme's default). Only an exported build, again at each launch so
    /// it follows the executable. Never throws.
    /// </summary>
    public static void Register()
    {
        if (!OS.HasFeature("template") || Platform.IsMobile || DisplayServer.GetName() == "headless") return;
        string exe = OS.GetExecutablePath();
        try
        {
            if (OperatingSystem.IsWindows()) RegisterWindows(exe);
            else if (OperatingSystem.IsLinux()) RegisterLinux(exe);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[join] could not register {Scheme}:// links: {e.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterWindows(string exe)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        key.SetValue("", "URL:UnitSport Switzerland");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
        using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exe}\" \"%1\"");
    }

    private static void RegisterLinux(string exe)
    {
        string data = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share");
        string dir = Path.Combine(data, "applications");
        Directory.CreateDirectory(dir);
        const string name = "unitsport-switzerland-join.desktop";
        string entry = "[Desktop Entry]\nType=Application\n"
            + "Name=UnitSport Switzerland\nComment=Join a UnitSport server\n"
            + $"Exec=\"{exe.Replace("\"", "\\\"")}\" %u\n"
            + $"MimeType=x-scheme-handler/{Scheme};\nNoDisplay=true\nTerminal=false\nCategories=Game;\n";
        string path = Path.Combine(dir, name);
        if (File.Exists(path) && File.ReadAllText(path) == entry) return;
        File.WriteAllText(path, entry);
        // a scheme has one handler: this entry, from now on
        if (Platform.CanSpawnProcesses) OS.CreateProcess("xdg-mime", new[] { "default", name, $"x-scheme-handler/{Scheme}" });
    }
#endif
}
