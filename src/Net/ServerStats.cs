using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Terrain;

namespace UnitSport.Net;

/// <summary>
/// <c>godot --headless --path . -- --server --serverstats[,label] [--seconds S]</c>: every 5 s prints
/// frame time (p50/p99/max), players, tiles, memory, GC and ENet traffic (total and per peer), and
/// rewrites <c>test_output/loadtest/&lt;label&gt;/server_summary.txt</c>, so a killed server still
/// leaves one. With <c>--seconds S</c> the server quits after S seconds. Load-test tooling: see
/// <c>tools/loadtest.sh</c> and <see cref="Swarm"/>.
///
/// <para>
/// Frame times go into fixed histograms rather than lists: the probe must not allocate in the
/// process whose GC it is measuring.
/// </para>
/// </summary>
public partial class ServerStats : Node
{
    private const double Window = 5;
    private const double BucketMs = 0.05;
    private const int Buckets = 20000;   // 0..1000 ms; anything longer lands in the last one

    private static readonly string[] Args = OS.GetCmdlineUserArgs();
    public static bool Requested => Args.Any(a => a.StartsWith("--serverstats"));

    private readonly string _label = Args.FirstOrDefault(a => a.StartsWith("--serverstats"))?.Split(',') is { Length: > 1 } p
        ? p[1] : "default";
    private double _quitAfter = SecondsArg();

    // frame: wall time between frames (headless idles ~6.9 ms per frame, so this bottoms out
    // there); busy: the work in the frame, process plus physics, which is what load moves first
    private readonly int[] _window = new int[Buckets], _all = new int[Buckets];
    private readonly int[] _busyWindow = new int[Buckets], _busyAll = new int[Buckets];
    private double _windowMax, _allMax, _busyWindowMax, _busyAllMax;
    private double _elapsed, _sinceWindow;
    private ulong _lastTicks;
    private double _lastPauseMs;
    private readonly int[] _lastGc = new int[3];
    private long _peakWs;
    private double _maxPauseMs, _totalPauseMs;
    private double _inSum, _outSum, _inPeak, _outPeak;
    private int _windows, _peakPlayers;
    private double _inPpsPeak;
    private long _lastDrops = -1, _dropsTotal;
    private string _lastLine = "";

    private static double SecondsArg()
    {
        int i = Array.IndexOf(Args, "--seconds");
        return i >= 0 && i + 1 < Args.Length
            && double.TryParse(Args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 0;
    }

    public override void _Ready()
    {
        _lastPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        for (int g = 0; g < 3; g++) _lastGc[g] = GC.CollectionCount(g);
    }

    public override void _Process(double delta)
    {
        // wall clock, not delta: the engine smooths a long frame's delta over the next ones, so
        // a 3 s stall shows up as a run of ~140 ms deltas
        ulong now = Time.GetTicksUsec();
        double ms = _lastTicks == 0 ? delta * 1000 : (now - _lastTicks) / 1000.0;
        _lastTicks = now;
        int b = Math.Min(Buckets - 1, (int)(ms / BucketMs));
        _window[b]++;
        _all[b]++;
        _windowMax = Math.Max(_windowMax, ms);
        _allMax = Math.Max(_allMax, ms);
        // the previous frame's timings: the monitors are filled in at the end of a frame
        double busy = (Performance.GetMonitor(Performance.Monitor.TimeProcess)
            + Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess)) * 1000;
        int bb = Math.Min(Buckets - 1, (int)(busy / BucketMs));
        _busyWindow[bb]++;
        _busyAll[bb]++;
        _busyWindowMax = Math.Max(_busyWindowMax, busy);
        _busyAllMax = Math.Max(_busyAllMax, busy);
        _elapsed += ms / 1000;
        _sinceWindow += ms / 1000;
        if (_sinceWindow >= Window) { Report(_sinceWindow); _sinceWindow = 0; }
        if (_quitAfter > 0 && _elapsed >= _quitAfter)
        {
            _quitAfter = 0;
            GD.Print("[stats] --seconds reached, quitting");
            GetTree().Quit();
        }
    }

    private static (double P50, double P99, int N) Pct(int[] h)
    {
        int n = h.Sum();
        double p50 = 0, p99 = 0;
        int seen = 0;
        for (int i = 0; i < h.Length; i++)
        {
            if (h[i] == 0) continue;
            int before = seen;
            seen += h[i];
            double ms = (i + 0.5) * BucketMs;
            if (before < n * 0.5 && seen >= n * 0.5) p50 = ms;
            if (before < n * 0.99 && seen >= n * 0.99) p99 = ms;
        }
        return (p50, p99, n);
    }

    private void Report(double seconds)
    {
        var world = GetParent();
        int players = world.GetNodeOrNull("Players")?.GetChildCount() ?? 0;
        _peakPlayers = Math.Max(_peakPlayers, players);
        var chunks = world.GetNodeOrNull<ChunkManager>("Terrain");
        long ws = System.Environment.WorkingSet;
        _peakWs = Math.Max(_peakWs, ws);
        long heap = GC.GetTotalMemory(false);

        double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        double pauseDelta = pause - _lastPauseMs;
        _lastPauseMs = pause;
        _totalPauseMs += pauseDelta;
        _maxPauseMs = Math.Max(_maxPauseMs, pauseDelta);
        Span<int> gc = stackalloc int[3];
        for (int g = 0; g < 3; g++) { gc[g] = GC.CollectionCount(g) - _lastGc[g]; _lastGc[g] += gc[g]; }

        // PopStatistic reads and resets the host's counters, so each call covers one window
        double inBps = 0, outBps = 0, inPps = 0, outPps = 0, rttAvg = 0, rttMax = 0, lossAvg = 0;
        int peerCount = 0;
        if ((Multiplayer.MultiplayerPeer as ENetMultiplayerPeer)?.Host is { } host)
        {
            inBps = host.PopStatistic(ENetConnection.HostStatistic.ReceivedData) / seconds;
            outBps = host.PopStatistic(ENetConnection.HostStatistic.SentData) / seconds;
            inPps = host.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets) / seconds;
            outPps = host.PopStatistic(ENetConnection.HostStatistic.SentPackets) / seconds;
            var peers = host.GetPeers();
            peerCount = peers.Count;
            foreach (var pr in peers)
            {
                double rtt = pr.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
                rttAvg += rtt;
                rttMax = Math.Max(rttMax, rtt);
                // ENet scales packet loss by ENET_PEER_PACKET_LOSS_SCALE (65536)
                lossAvg += pr.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss) / 65536.0;
            }
            if (peerCount > 0) { rttAvg /= peerCount; lossAvg /= peerCount; }
        }
        _inSum += inBps; _outSum += outBps;
        _inPpsPeak = Math.Max(_inPpsPeak, inPps);
        long drops = UdpDrops();
        long dropDelta = _lastDrops >= 0 && drops >= 0 ? drops - _lastDrops : 0;
        _lastDrops = drops;
        _dropsTotal += dropDelta;
        _inPeak = Math.Max(_inPeak, inBps); _outPeak = Math.Max(_outPeak, outBps);
        _windows++;

        var (p50, p99, n) = Pct(_window);
        var (b50, b99, _) = Pct(_busyWindow);
        _lastLine = string.Format(CultureInfo.InvariantCulture,
            "[stats] t={0:F0}s players={1} tiles={2} frame ms p50={3:F2} p99={4:F2} max={5:F2} (n={6}) "
            + "busy ms p50={20:F2} p99={21:F2} max={22:F2} "
            + "ws={7:F0}MB heap={8:F0}MB gc={9}/{10}/{11} pause={12:F1}ms "
            + "net in={13:F1} out={14:F1} KB/s, per peer in={15:F1} out={16:F1} KB/s, rtt={17:F1}/{18:F0}ms loss={19:P1} "
            + "packets in={23:F0}/s out={24:F0}/s udp drops={25}",
            _elapsed, players, chunks?.ActiveChunkCount ?? -1, p50, p99, _windowMax, n,
            ws / 1048576.0, heap / 1048576.0, gc[0], gc[1], gc[2], pauseDelta,
            inBps / 1024, outBps / 1024, peerCount > 0 ? inBps / 1024 / peerCount : 0, peerCount > 0 ? outBps / 1024 / peerCount : 0,
            rttAvg, rttMax, lossAvg, b50, b99, _busyWindowMax, inPps, outPps, dropDelta);
        GD.Print(_lastLine);
        Array.Clear(_window);
        Array.Clear(_busyWindow);
        _windowMax = 0;
        _busyWindowMax = 0;
        WriteSummary();
    }

    /// <summary>
    /// Datagrams the kernel dropped because this process's UDP receive buffer was full (Linux:
    /// the <c>drops</c> column of /proc/net/udp[6] for this process's sockets), or -1 elsewhere.
    /// ENet never sees these, so they only show up as packet loss and, past a point, timeouts.
    /// </summary>
    private static long UdpDrops()
    {
        try
        {
            var inodes = new HashSet<string>();
            foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
                if (new FileInfo(fd).LinkTarget is { } t && t.StartsWith("socket:["))
                    inodes.Add(t[8..^1]);
            long drops = 0;
            foreach (var table in new[] { "/proc/net/udp", "/proc/net/udp6" })
                foreach (var line in File.ReadLines(table).Skip(1))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length > 12 && inodes.Contains(f[9])) drops += long.Parse(f[12], CultureInfo.InvariantCulture);
                }
            return drops;
        }
        catch (Exception) { return -1; }
    }

    private void WriteSummary()
    {
        var (p50, p99, n) = Pct(_all);
        var (b50, b99, _) = Pct(_busyAll);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Line(string key, string format, params object[] v) => sb.AppendLine(key.PadRight(24) + string.Format(inv, format, v));
        Line("label", "{0}", _label);
        Line("duration_s", "{0:F0}", _elapsed);
        Line("peak_players", "{0}", _peakPlayers);
        Line("frames", "{0}", n);
        Line("frame_ms_p50", "{0:F2}", p50);
        Line("frame_ms_p99", "{0:F2}", p99);
        Line("frame_ms_max", "{0:F2}", _allMax);
        Line("busy_ms_p50", "{0:F2}", b50);
        Line("busy_ms_p99", "{0:F2}", b99);
        Line("busy_ms_max", "{0:F2}", _busyAllMax);
        Line("ws_peak_mb", "{0:F0}", _peakWs / 1048576.0);
        Line("ws_final_mb", "{0:F0}", System.Environment.WorkingSet / 1048576.0);
        Line("heap_final_mb", "{0:F0}", GC.GetTotalMemory(false) / 1048576.0);
        Line("gc_pause_total_ms", "{0:F1}", _totalPauseMs);
        Line("gc_pause_max_window_ms", "{0:F1}", _maxPauseMs);
        Line("gc_gen0/1/2", "{0}/{1}/{2}", GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        Line("net_in_avg_kbs", "{0:F1}", _windows > 0 ? _inSum / _windows / 1024 : 0);
        Line("net_in_peak_kbs", "{0:F1}", _inPeak / 1024);
        Line("net_out_avg_kbs", "{0:F1}", _windows > 0 ? _outSum / _windows / 1024 : 0);
        Line("net_out_peak_kbs", "{0:F1}", _outPeak / 1024);
        Line("packets_in_peak_per_s", "{0:F0}", _inPpsPeak);
        Line("udp_rcvbuf_drops", "{0}", _dropsTotal);
        sb.AppendLine("last_window             " + _lastLine);
        try
        {
            string dir = ProjectSettings.GlobalizePath($"res://test_output/loadtest/{_label}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "server_summary.txt"), sb.ToString());
        }
        catch (Exception e) { GD.PushWarning($"[stats] cannot write summary: {e.Message}"); }
    }
}
