using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>--signalnetcheck [E,N] [--seconds N]</c> (#353, <c>tools/signalnetcheck.sh</c>): the traffic
/// lights over the network. Traffic is local to each client; what every peer shares is the plan
/// and the server clock, so this logs, every <see cref="Step"/> s of <c>ClockSync.ServerNow</c>,
/// the aspect of every group of one junction as the lamps read it (<c>SignalPlan.State(group,
/// ClockSync.ServerNow)</c>), with the system wall clock beside it (one machine: the script turns
/// it into each client's clock error). The junction: the signal record nearest LV95 E,N in that
/// tile of the chunk directory (<c>--chunks</c>; a client may run without terrain, the tile is
/// read from disk), else a crossroads plan built in code (the same on every peer). A client
/// starts once its clock has synced and settled, logs <c>--seconds</c> (20) and quits with
/// <c>RESULT: ok</c>; the server logs until it is stopped.
/// </summary>
public partial class SignalNetProbe : Node
{
    public const double Step = 0.5;

    public static bool Requested => CmdArgs.Has("--signalnetcheck");

    /// <summary>After the first pong, how long the offset settles (the fast pings run 3 s) before sampling.</summary>
    private const double Settle = 3.5;

    private readonly string _role;
    private readonly bool _server;
    private readonly int _want = (int)Math.Round((CmdArgs.Double("--seconds") ?? 20) / Step);
    private SignalPlan? _plan;
    private double _syncedAt = double.NaN, _started;
    private long _lastKey = long.MinValue;
    private int _samples;
    private bool _done;
    private readonly System.Text.StringBuilder _states = new();

    public SignalNetProbe(bool server)
    {
        Name = "SignalNetProbe";
        _server = server;
        _role = server ? "server" : CmdArgs.Value("--name", notFlag: true) ?? "client";
    }

    private void Log(string what) => GD.Print($"[signalnet {_role}] {what}");

    private void Finish(bool ok, string why)
    {
        _done = true;
        Log($"RESULT: {(ok ? "ok" : "FAILED")} ({why})");
        if (!_server) GetTree().Quit(ok ? 0 : 1);
    }

    public override async void _Ready()
    {
        _started = ClockSync.LocalNow;
        var inv = CultureInfo.InvariantCulture;
        string? at = CmdArgs.Value("--signalnetcheck", notFlag: true);
        string where;
        if (at?.Split(',') is [var es, var ns] && double.TryParse(es, NumberStyles.Float, inv, out double e)
            && double.TryParse(ns, NumberStyles.Float, inv, out double n))
        {
            var id = TileId.FromLv95(e, n);
            string dir = TerrainPaths.FindChunkDir();
            var tile = await new LocalChunkSource(dir).LoadRoadsAsync(id);
            if (!IsInsideTree()) return;
            float x = (float)(e - id.MinE), z = (float)(id.MaxN - n);
            var signal = tile?.Signals.MinBy(s => (s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z));
            if (signal == null) { Finish(false, $"no signal record in tile {id} of {dir}"); return; }
            _plan = signal.Plan;
            where = string.Create(inv, $"LV95 {id.MinE + signal.X:F0},{id.MaxN - signal.Z:F0}");
        }
        else
        {
            // a crossroads, main road east-west with left pockets, pedestrians on every arm
            _plan = SignalPlan.Build(
            [
                new SignalArm(0, true, true, LeftPocket: true, Pedestrians: true, Rank: 2),
                new SignalArm(Math.PI / 2, true, true, Pedestrians: true),
                new SignalArm(Math.PI, true, true, LeftPocket: true, Pedestrians: true, Rank: 2),
                new SignalArm(-Math.PI / 2, true, true, Pedestrians: true),
            ], 353);
            where = "a crossroads plan built in code";
        }
        // the same plan on every peer: the script compares this line too
        Log(string.Create(inv, $"plan {where}: {_plan.Groups.Count} groups, cycle {_plan.Cycle:F2} s, offset {_plan.Offset:F2} s, kinds {string.Concat(_plan.Groups.Select(g => (int)g.Kind))}"));
    }

    public override void _Process(double delta)
    {
        if (_plan == null || _done) return;
        double local = ClockSync.LocalNow;
        if (!_server)
        {
            if (!ClockSync.Synced)
            {
                if (local - _started > 90) Finish(false, "the clock never synced with the server");
                return;
            }
            if (double.IsNaN(_syncedAt)) _syncedAt = local;
            if (local - _syncedAt < Settle) return;
        }
        double t = ClockSync.ServerNow;
        long key = (long)Math.Floor(t / Step);
        if (key == _lastKey) return;
        bool first = _lastKey == long.MinValue;
        _lastKey = key;
        if (first) return;   // start on a step boundary, never part-way into one
        double wall = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        _states.Clear();
        double edge = double.PositiveInfinity;
        for (int g = 0; g < _plan.Groups.Count; g++)
        {
            _states.Append(Letter(_plan.State(g, t)));
            edge = Math.Min(edge, Math.Min(_plan.UntilChange(g, t), SinceChange(_plan, g, t)));
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[signalnet {_role}] k={key} t={t:F4} wall={wall:F4} edge={Math.Min(edge, 999):F4} rtt={(_server ? 0 : ClockSync.Rtt) * 1000:F1} states={_states}"));
        if (!_server && ++_samples >= _want)
            Finish(true, string.Create(CultureInfo.InvariantCulture, $"{_samples} samples of {_plan.Groups.Count} groups, rtt {ClockSync.Rtt * 1000:F1} ms"));
    }

    private static char Letter(SignalAspect a) => a switch
    {
        SignalAspect.Red => 'R',
        SignalAspect.RedAmber => 'U',
        SignalAspect.Green => 'G',
        SignalAspect.Amber => 'A',
        SignalAspect.FlashingAmber => 'F',
        _ => 'O',
    };

    /// <summary>Seconds since the group's aspect last changed (its interval's start), as <see cref="SignalPlan.UntilChange"/> looks ahead.</summary>
    private static double SinceChange(SignalPlan plan, int group, double t)
    {
        var list = plan.Groups[group].Intervals;
        if (list.Count <= 1 || plan.Cycle <= 0) return double.PositiveInfinity;
        double c = ((t + plan.Offset) % plan.Cycle + plan.Cycle) % plan.Cycle;
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].From <= c) return c - list[i].From;
        return c + plan.Cycle - list[^1].From;
    }
}
