using System.Text.Json;
using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// "--livemap-file &lt;path&gt;" (or <c>UNITSPORT_LIVEMAP_FILE</c>): the server keeps a small JSON file
/// of where every player is, which the live map beside a deployed server reads
/// (<c>tools/deploy/web/map.html</c>, <c>docs/notes/net/live-map.md</c>, #896).
///
/// <para>
/// The same pattern as <see cref="StatusFile"/>: built every <see cref="Period"/> on the main thread
/// (the players live there), written only when it changed or every <see cref="Heartbeat"/> (the page
/// calls a file older than a minute "offline"), always on a worker and through a temp file, so a
/// reader never sees half a file and the frame never waits on the disk
/// (<c>net/perf-no-main-thread-periodic-jobs</c>).
/// </para>
/// </summary>
public partial class LiveMapFile : Node
{
    private const double Period = 2, Heartbeat = 20;

    private readonly string _path;
    private readonly Func<IReadOnlyList<LiveMapPlayer>> _players;
    private double _nextAt, _writtenAt = double.MinValue;
    private string _last = "";
    private int _writing;   // 1 while a worker writes: a slow disk skips a round instead of queueing

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LiveMapFile(string path, Func<IReadOnlyList<LiveMapPlayer>> players)
    {
        _path = path;
        _players = players;
        Name = "LiveMapFile";
    }

    public override void _Ready()
    {
        try { if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir); }
        catch (Exception e) { GD.PushWarning($"[livemap] cannot create the folder of {_path}: {e.Message}"); }
        GD.Print($"[livemap] writing player positions to {_path}");
    }

    public override void _Process(double delta)
    {
        // wall clock: a web page's view of the server is not part of the world (Core.RealClock)
        double now = RealClock.Now;
        if (now < _nextAt) return;
        _nextAt = now + Period;

        var players = _players();
        // "updated" left out of the compare: it changes every round
        string body = JsonSerializer.Serialize(players, Json);
        if (body == _last && now - _writtenAt < Heartbeat) return;
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return;
        _last = body;
        _writtenAt = now;
        string json = JsonSerializer.Serialize(new LiveMapData(true, (long)Time.GetUnixTimeFromSystem(), players), Json);
        Task.Run(() =>
        {
            try { Write(json); }
            finally { Volatile.Write(ref _writing, 0); }
        });
    }

    public override void _ExitTree()
    {
        // a clean stop empties the map at once rather than a minute later
        if (_last.Length == 0) return;
        Write(JsonSerializer.Serialize(new LiveMapData(false, (long)Time.GetUnixTimeFromSystem(), []), Json));
    }

    private void Write(string json)
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) { GD.PushWarning($"[livemap] cannot write {_path}: {e.Message}"); }
    }

    /// <summary>"--livemap-file &lt;path&gt;", else <c>UNITSPORT_LIVEMAP_FILE</c>, else null (no file).</summary>
    public static string? ParsePath() =>
        CmdArgs.Value("--livemap-file", notFlag: true)
        ?? (System.Environment.GetEnvironmentVariable("UNITSPORT_LIVEMAP_FILE") is { Length: > 0 } env ? env : null);
}

/// <summary>One player on the map: LV95 metres, altitude over the sea, what it rides (a <c>RideKind</c> name).</summary>
public sealed record LiveMapPlayer(string Name, double E, double N, double Alt, string Ride, bool Bot);

/// <summary>What <see cref="LiveMapFile"/> writes; the page's contract, so rename nothing lightly.</summary>
public sealed record LiveMapData(bool Online, long Updated, IReadOnlyList<LiveMapPlayer> Players);
