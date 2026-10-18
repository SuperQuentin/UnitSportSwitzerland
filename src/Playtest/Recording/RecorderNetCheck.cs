using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Playtest.TopView;

namespace UnitSport.Playtest.Recording;

/// <summary>
/// <c>--playtest-recordnet A|B</c> (#844, tools/playtestnetcheck.sh): two recorded clients and a
/// recorded dedicated server on loopback. A drives its player round a 15 m circle for 30 s; B
/// only watches. B then reads its own trace and the server's (<c>--playtest-server-trace</c>):
/// A is in both, matched by node name, moved on both, and B drew it within a few metres of where
/// the server had it; and the top view draws the server-vs-client offsets.
/// </summary>
public partial class RecorderNetCheck : Node
{
    public static bool Requested => CmdArgs.Has("--playtest-recordnet");
    private const double Radius = 15, Period = 12, Drive = 30;

    private readonly PlaytestRecorder _recorder;
    private readonly string _role = CmdArgs.Value("--playtest-recordnet") ?? "B";
    private int _failed;
    private Vector3 _centre;
    private double _start = -1;
    private bool _done;

    public RecorderNetCheck(PlaytestRecorder recorder)
    {
        _recorder = recorder;
        Name = "RecorderNetCheck";
    }

    private void Expect(bool ok, string what)
    {
        if (!ok) _failed++;
        GD.Print($"[recordnet {_role}] {(ok ? "ok  " : "FAIL")} {what}");
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds)
    {
        double until = _recorder.Now + seconds;
        while (_recorder.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    public override void _PhysicsProcess(double delta)
    {
        // A walks its body round the circle by hand: client-authoritative, the server takes it as sent
        if (_role != "A" || _start < 0 || _done || _recorder.LocalPlayer is not { } me) return;
        double a = (_recorder.Now - _start) / Period * Math.Tau;
        me.GlobalPosition = _centre + new Vector3((float)(Radius * Math.Cos(a)), 0, (float)(Radius * Math.Sin(a)));
        me.Velocity = new Vector3((float)(-Math.Sin(a)), 0, (float)Math.Cos(a)) * (float)(Radius * Math.Tau / Period);
    }

    private async Task Run()
    {
        MouseCapture.Disabled = true;
        double deadline = _recorder.Now + 120;
        while (_recorder.LocalPlayer == null && _recorder.Now < deadline) await Wait(0.5);
        Expect(_recorder.LocalPlayer != null, "joined the server, the local player exists");
        if (_recorder.LocalPlayer is not { } me) { Done(); return; }
        // the other client a moment to arrive and be seen
        await Wait(8);
        _centre = me.GlobalPosition;
        _start = _recorder.Now;
        GD.Print($"[recordnet {_role}] {(_role == "A" ? "driving the circle" : "watching")} for {Drive:0} s");
        await Wait(Drive);
        _done = true;
        await Wait(2);
        await _recorder.Stop();
        if (_role == "A")
        {
            var mine = new TraceReader(_recorder.Dir);
            var self = mine.Entities.Values.FirstOrDefault(e => e.Local);
            Expect(self != null && Travel(mine.Window(0, 1e9), self.Id) > 2 * Math.PI * Radius, "A's own trace went round the circle");
            Done();
            return;
        }

        var reader = new TraceReader(_recorder.Dir);
        var frames = reader.Window(0, 1e9);
        var remote = reader.Entities.Values.Where(e => e.IsPlayer && !e.Local)
            .OrderByDescending(e => Travel(frames, e.Id)).FirstOrDefault();
        double travel = remote == null ? 0 : Travel(frames, remote.Id);
        Expect(remote != null && travel > 2 * Math.PI * Radius, $"B traced the remote player A ({remote?.Name}, peer {remote?.Peer}) going round: {travel:0} m");
        string? serverDir = CmdArgs.Value("--playtest-server-trace", notFlag: true);
        if (remote == null || serverDir == null || !File.Exists(Path.Combine(serverDir, TraceFormat.TraceFile)))
        {
            Expect(false, $"the server's trace is at {serverDir}");
            Done();
            return;
        }
        var server = new TraceReader(serverDir);
        var twin = server.Entities.Values.FirstOrDefault(e => e.IsPlayer && e.Name == remote.Name);
        Expect(twin != null, $"the server traced the same player by name ({remote.Name}; it has {string.Join(", ", server.Entities.Values.Where(e => e.IsPlayer).Select(e => e.Name))})");
        if (twin == null) { Done(); return; }
        double shift = (reader.Header.StartUnixMs - server.Header.StartUnixMs) / 1000.0;
        var serverFrames = server.Window(0, 1e9);
        Expect(Travel(serverFrames, twin.Id) > 2 * Math.PI * Radius, "the server's trace has A going round too");
        // B's drawing of A against the server's at the same wall-clock time
        var offsets = new List<double>();
        foreach (var f in frames.Where(f => f.T >= _start && f.T <= _start + Drive))
        {
            if (!f.TryGet(remote.Id, out var b)) continue;
            var s = serverFrames.MinBy(x => Math.Abs(x.T - shift - f.T));
            if (s == null || Math.Abs(s.T - shift - f.T) > 0.15 || !s.TryGet(twin.Id, out var a)) continue;
            offsets.Add(Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N)));
        }
        offsets.Sort();
        double median = offsets.Count == 0 ? double.NaN : offsets[offsets.Count / 2];
        // at 7.9 m/s an interpolation delay of 100-300 ms is 1-2.5 m behind: more means the traces do not line up
        Expect(offsets.Count > 100 && median < 4,
            FormattableString.Invariant($"B drew A within a few metres of the server: median {median:F2} m, p90 {(offsets.Count == 0 ? double.NaN : offsets[offsets.Count * 9 / 10]):F2} m over {offsets.Count} samples"));
        string png = Path.Combine(PlaytestDirector.Root, "test_output", "playtest", "recordnet_top.png");
        string drawn = TopViewCli.Draw(_recorder.Dir, _start + 10, _start + 16, _start + 16, null, null, serverDir, png, 900, html: false);
        GD.Print($"[recordnet B] {drawn}");
        Expect(drawn.Contains("server offsets", StringComparison.Ordinal) && !drawn.Contains(" 0 server offsets", StringComparison.Ordinal), "the top view draws the server-vs-client offset");
        Done();
    }

    private static double Travel(List<TraceFrame> frames, int id)
    {
        double sum = 0;
        TraceState? last = null;
        foreach (var f in frames)
        {
            if (!f.TryGet(id, out var s)) continue;
            if (last is { } l)
            {
                double d = Math.Sqrt((s.E - l.E) * (s.E - l.E) + (s.N - l.N) * (s.N - l.N));
                if (d < 20) sum += d;   // a spawn or a teleport is not travel
            }
            last = s;
        }
        return sum;
    }

    private void Done()
    {
        GD.Print(_failed == 0 ? $"[recordnet {_role}] RESULT: ok" : $"[recordnet {_role}] RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
