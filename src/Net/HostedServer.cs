using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// "Host a game" on the Multiplayer screen: starts this same executable as a headless dedicated
/// server in the background, waits until it answers status queries, and stops it again when the
/// player leaves (<c>docs/notes/net/hosting.md</c>).
///
/// <para>
/// The server gets <c>--parent-pid</c> and quits by itself when this client is gone, so a crash
/// or a kill from the task manager does not leave an orphan holding the port. It shares this
/// client's <c>user://</c> folder: placed objects, interiors, loot and bank accounts of a hosted
/// world are kept with the offline ones.
/// </para>
/// </summary>
public sealed class HostedServer
{
    public int Pid { get; private set; } = -1;
    public int Port { get; }
    public string Name { get; }
    public string LogPath { get; }
    public bool Running => Alive(Pid);

    /// <summary>
    /// A secret only this client and its server know (<c>--host-token</c>): sent back after
    /// joining, it makes the host an admin of their own server without a password.
    /// </summary>
    public string HostToken { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public HostedServer(string name, int port)
    {
        Name = name;
        Port = port;
        LogPath = ProjectSettings.GlobalizePath("user://logs/hosted-server.log");
    }

    /// <summary>Launches the process. Returns an error message, or null when it started.</summary>
    public string? Start(bool lanVisible)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
        var args = new List<string> { "--headless", "--log-file", LogPath };
        // an exported game is the executable plus its pack; the editor and `godot --path .` need the project named
        if (!OS.HasFeature("template")) { args.Add("--path"); args.Add(ProjectSettings.GlobalizePath("res://")); }
        args.Add("--");
        args.AddRange(new[] { "--server", "--port", Port.ToString(), "--server-name", Name, "--parent-pid", OS.GetProcessId().ToString(), "--host-token", HostToken });
        // the status port answers the client that waits for the server to be up; hidden from the
        // LAN, it answers only on loopback
        if (!lanVisible) { args.Add("--query-bind"); args.Add("127.0.0.1"); }
        // the same terrain this client reads; a copy without any gets the generated world
        string chunks = TerrainPaths.FindChunkDir();
        args.AddRange(new[] { "--chunks", chunks });
        if (!System.IO.File.Exists(System.IO.Path.Combine(chunks, "manifest.json"))) args.Add("--generated-world");

        Pid = OS.CreateProcess(OS.GetExecutablePath(), args.ToArray(), openConsole: false);
        GD.Print($"[host] started server pid {Pid}: {string.Join(' ', args).Replace(HostToken, "***")}");
        return Pid > 0 ? null : "Could not start the server process.";
    }

    /// <summary>The last useful line of the server's log, to say why it stopped.</summary>
    public string Why()
    {
        try
        {
            if (!System.IO.File.Exists(LogPath)) return "The server stopped before writing a log.";
            var lines = System.IO.File.ReadAllLines(LogPath);
            var bad = lines.AsEnumerable().Reverse().FirstOrDefault(l => l.Contains("ERROR") || l.Contains("failed") || l.Contains("[server]"));
            if (bad != null && bad.Contains("failed to listen")) return $"Port {Port} is already in use. Pick another one.";
            return bad?.Replace("ERROR:", "").Trim() ?? "The server stopped. See logs/hosted-server.log.";
        }
        catch (Exception e) { return e.Message; }
    }

    public void Stop()
    {
        if (Pid <= 0) return;
        if (Alive(Pid))
        {
            var err = OS.Kill(Pid);
            GD.Print($"[host] stopped server pid {Pid} ({err})");
        }
        Pid = -1;
    }

    /// <summary>
    /// Server side: "--parent-pid N" names the client that hosts this server. When it is gone the
    /// server is too, so a crashed client never leaves one behind holding the port.
    /// </summary>
    /// <summary>
    /// Whether a process is alive. Not <c>OS.IsProcessRunning</c>: on Windows that only knows the
    /// processes this one started itself, so a server asking about its parent always got false.
    /// </summary>
    public static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception) { return false; }   // no such process (any more)
    }

    public static int? ParseParentPid()
    {
        var args = CmdArgs.All;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--parent-pid" && int.TryParse(args[i + 1], out int pid)) return pid;
        return null;
    }
}
