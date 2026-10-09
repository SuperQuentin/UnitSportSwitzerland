using System.Text.Json;
using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// "--status-file &lt;path&gt;" (or <c>UNITSPORT_STATUS_FILE</c>): the server keeps a small JSON
/// file about itself up to date, which the web page beside a deployed server reads
/// (<c>tools/deploy/web/index.html</c>, <c>docs/notes/net/status-page.md</c>, #740).
///
/// <para>
/// Rebuilt every <see cref="Period"/> on the main thread, written only when it changed or every
/// <see cref="Heartbeat"/> (the page calls a file older than a minute "offline"), always on a
/// worker and through a temp file, so a reader never sees half a file and the frame never waits
/// on the disk (<c>net/perf-no-main-thread-periodic-jobs</c>).
/// </para>
/// </summary>
public partial class StatusFile : Node
{
    private const double Period = 5, Heartbeat = 20;

    private readonly string _path;
    private readonly Func<StatusFileData> _status;
    private readonly long _started = (long)Time.GetUnixTimeFromSystem();
    private double _nextAt, _writtenAt = double.MinValue;
    private string _last = "";
    private int _writing;   // 1 while a worker writes: a slow disk skips a round instead of queueing

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public StatusFile(string path, Func<StatusFileData> status)
    {
        _path = path;
        _status = status;
        Name = "StatusFile";
    }

    public override void _Ready()
    {
        try { if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir); }
        catch (Exception e) { GD.PushWarning($"[status] cannot create the folder of {_path}: {e.Message}"); }
        GD.Print($"[status] writing server status to {_path}");
    }

    public override void _Process(double delta)
    {
        // wall clock: a web page's view of the server is not part of the world (Core.RealClock)
        double now = RealClock.Now;
        if (now < _nextAt) return;
        _nextAt = now + Period;

        var data = _status() with { Online = true, Started = _started };
        // "updated" left out of the compare: it changes every round
        string body = JsonSerializer.Serialize(data, Json);
        if (body == _last && now - _writtenAt < Heartbeat) return;
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return;
        _last = body;
        _writtenAt = now;
        string json = JsonSerializer.Serialize(data with { Updated = (long)Time.GetUnixTimeFromSystem() }, Json);
        Task.Run(() =>
        {
            try { Write(json); }
            finally { Volatile.Write(ref _writing, 0); }
        });
    }

    public override void _ExitTree()
    {
        // a clean stop says so at once rather than a minute later
        if (_last.Length == 0) return;
        var data = _status() with { Online = false, Started = _started, Players = 0, Names = [], Updated = (long)Time.GetUnixTimeFromSystem() };
        Write(JsonSerializer.Serialize(data, Json));
    }

    private void Write(string json)
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) { GD.PushWarning($"[status] cannot write {_path}: {e.Message}"); }
    }

    /// <summary>"--status-file &lt;path&gt;", else <c>UNITSPORT_STATUS_FILE</c>, else null (no file).</summary>
    public static string? ParsePath() =>
        CmdArgs.Value("--status-file", notFlag: true)
        ?? (System.Environment.GetEnvironmentVariable("UNITSPORT_STATUS_FILE") is { Length: > 0 } env ? env : null);
}

/// <summary>What <see cref="StatusFile"/> writes; the page's contract, so rename nothing lightly.</summary>
public sealed record StatusFileData(string Name, string Version, string World, int Port, int Players, int Max, string[] Names)
{
    public bool Online { get; init; }

    /// <summary>Unix seconds the server started.</summary>
    public long Started { get; init; }

    /// <summary>Unix seconds of this write: the page shows "offline" once it is over a minute old.</summary>
    public long Updated { get; init; }
}
