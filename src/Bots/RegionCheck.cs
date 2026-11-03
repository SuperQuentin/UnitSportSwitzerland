using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Net.Regions;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// <c>--regioncheck both|a|b</c> on a dedicated server (<c>tools/regioncheck.sh</c>, #1456): two bots far
/// apart on one server collide and walk as each does alone. At each site (a: the spawn, b: 300 km east of it)
/// a walker walks straight east for <see cref="WalkTicks"/> physics steps into a blocker standing
/// <see cref="BlockerGap"/> m ahead. Both tracks, relative to where each started, go to
/// <c>&lt;out&gt;/&lt;run&gt;.csv</c>; the script compares a run with both sites against each site alone. Here: each
/// walker walked, never passed through its blocker (they touched), and each site's bots share one region.
/// <c>--noregions</c> runs the same with every body in the server's one frame: the "before".
/// <c>--regionat E,N --regionfar E,N</c> put the sites elsewhere (a real-terrain run: Geneva and Val Müstair);
/// <c>--regionwander S</c> then lets every bot stroll S seconds among the trunks and houses (WanderBrain) and
/// reports how far each went, whether it stayed on its ground and how many trunks its region holds.
/// </summary>
public partial class RegionCheck : Node
{
    public static bool Requested => CmdArgs.Has("--regioncheck");

    /// <summary>The far site: this far east of the spawn.</summary>
    public const double FarM = 300_000;
    public const float BlockerGap = 4f;
    /// <summary>12 s at 60 Hz.</summary>
    public const int WalkTicks = 720;
    /// <summary>Standing still this long on the ground before the walk starts.</summary>
    private const int SettleTicks = 60;
    private const int GiveUpTicks = 60 * 120;
    private const string Log = "[regioncheck]";

    private sealed class Site(string name, GlobalPos at)
    {
        public readonly string Name = name;
        public readonly GlobalPos At = at;
        public Bot? Walker, Blocker;
        public GlobalPos WalkerStart, BlockerStart;
        public double MinGap = double.MaxValue;
        /// <summary>At the end of the walk: how far the walker went, how far the blocker was pushed.</summary>
        public double Walked, Pushed;
        public readonly StringBuilder Csv = new();
    }

    private sealed class Script(bool walk) : IBotBrain
    {
        public bool Go;
        public double Think(Bot bot, double now)
        {
            // east, at a walk: the same wish every step at both sites
            bot.Intent = walk && Go ? new BotIntent { Wish = Vector3.Right, FaceYaw = -Mathf.Pi / 2 } : BotIntent.Stand;
            return 0.05;
        }
    }

    private readonly List<Site> _sites = new();
    private readonly string _run;
    private string _out = "";
    private int _tick, _settled, _walked = -1, _wander, _wandered = -1;
    private readonly Dictionary<Bot, (GlobalPos From, int Ticks, int OnFloor, double Path, GlobalPos Last)> _strolls = new();

    public RegionCheck()
    {
        Name = "RegionCheck";
        _run = CmdArgs.Value("--regioncheck") is { } r && !r.StartsWith("--") ? r : "both";
        // after the bodies have stepped: what they did this step
        ProcessPhysicsPriority = int.MaxValue;
    }

    public override void _Ready()
    {
        _out = ProjectSettings.GlobalizePath(CmdArgs.Value("--regionout") ?? "res://test_output/regions");
        Directory.CreateDirectory(_out);
        var spawn = SpawnPoint.ParseLv95("--regionat") is var (ae, an) ? new GlobalPos(ae, an, 0)
            : new GlobalPos(SpawnPoint.DefaultLv95E + 250, SpawnPoint.DefaultLv95N + 250, 0);
        var far = SpawnPoint.ParseLv95("--regionfar") is var (be, bn) ? new GlobalPos(be, bn, 0) : spawn with { E = spawn.E + FarM };
        _wander = CmdArgs.Int("--regionwander") ?? 0;
        if (_run is "both" or "a") _sites.Add(new Site("a", spawn));
        if (_run is "both" or "b") _sites.Add(new Site("b", far));
        GD.Print($"{Log} run {_run}{(ServerRegions.Off ? " (no regions)" : "")}: {string.Join(", ", _sites.ConvertAll(s => $"{s.Name} at {s.At}"))}");
        // the director takes its first frame; the bots come a moment after
        GetTree().CreateTimer(1.0).Timeout += Spawn;
    }

    private void Spawn()
    {
        if (BotDirector.Instance is not { } director) { Fail("no bot director"); return; }
        foreach (var s in _sites)
        {
            s.Walker = director.Add(s.At, 0f, BotProfile.For(1, 0, BotDifficulty.Normal, new HashSet<string>()));
            s.Blocker = director.Add(s.At with { E = s.At.E + BlockerGap }, 0f, BotProfile.For(1, 1, BotDifficulty.Normal, new HashSet<string>()));
            if (s.Walker == null || s.Blocker == null) { Fail($"site {s.Name}: no bots"); return; }
            s.Walker.Brain = new Script(walk: true);
            s.Blocker.Brain = new Script(walk: false);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_sites.Count == 0 || _sites[0].Walker == null) return;
        if (++_tick > GiveUpTicks) { Fail($"never ready: {Status()}"); return; }
        if (_walked < 0)
        {
            // every body on its ground, still, for a second
            bool ready = true;
            foreach (var s in _sites)
                foreach (var b in new[] { s.Walker!, s.Blocker! })
                    ready &= b.Body.Placed && b.Body.IsOnFloor() && b.Body.Velocity.Length() < 0.05f;
            _settled = ready ? _settled + 1 : 0;
            if (_settled < SettleTicks) return;
            foreach (var s in _sites)
            {
                s.WalkerStart = s.Walker!.Body.Global;
                s.BlockerStart = s.Blocker!.Body.Global;
                ((Script)s.Walker.Brain).Go = true;
                // at once, not at its next think (up to 0.5 s away, unwatched): the same step in every run
                s.Walker.Intent = new BotIntent { Wish = Vector3.Right, FaceYaw = -Mathf.Pi / 2 };
                s.Csv.Append("tick,we,wn,walt,be,bn,balt\n");
                GD.Print($"{Log} site {s.Name}: walker {s.Walker.Body.Name} in {s.Walker.Body.Region}, blocker in {s.Blocker.Body.Region}, start {s.WalkerStart}");
            }
            _walked = 0;
            return;
        }
        // the walk's steps; the stroll after it is not recorded
        if (++_walked <= WalkTicks)
        foreach (var s in _sites)
        {
            var w = s.Walker!.Body.Global;
            var b = s.Blocker!.Body.Global;
            s.MinGap = Math.Min(s.MinGap, w.HorizontalDistanceTo(b));
            s.Csv.Append(string.Create(CultureInfo.InvariantCulture,
                $"{_walked},{w.E - s.WalkerStart.E:F5},{w.N - s.WalkerStart.N:F5},{w.Alt - s.WalkerStart.Alt:F5},{b.E - s.BlockerStart.E:F5},{b.N - s.BlockerStart.N:F5},{b.Alt - s.BlockerStart.Alt:F5}\n"));
        }
        if (_walked < WalkTicks) return;
        if (_walked == WalkTicks)
            foreach (var s in _sites)
            {
                s.Walked = s.Walker!.Body.Global.HorizontalDistanceTo(s.WalkerStart);
                s.Pushed = s.Blocker!.Body.Global.HorizontalDistanceTo(s.BlockerStart);
            }
        if (_wander <= 0) { Finish(); return; }
        if (_wandered < 0)
        {
            // the walk is done: every bot strolls among the trunks and houses round its site
            foreach (var s in _sites)
                foreach (var b in new[] { s.Walker!, s.Blocker! })
                {
                    b.Brain = new WanderBrain();
                    _strolls[b] = (b.Body.Global, 0, 0, 0, b.Body.Global);
                }
            _wandered = 0;
        }
        foreach (var (b, st) in _strolls)
        {
            var g = b.Body.Global;
            _strolls[b] = (st.From, st.Ticks + 1, st.OnFloor + (b.Body.IsOnFloor() ? 1 : 0), st.Path + g.HorizontalDistanceTo(st.Last), g);
        }
        if (++_wandered >= _wander * 60) Finish();
    }

    private string Status()
    {
        var sb = new StringBuilder();
        foreach (var s in _sites)
            foreach (var b in new[] { s.Walker, s.Blocker })
                if (b != null) sb.Append($"{b.Body.Name} {b.Body.Global} placed {b.Body.Placed} floor {b.Body.IsOnFloor()}; ");
        return sb.ToString();
    }

    private void Finish()
    {
        int failures = 0;
        foreach (var s in _sites)
        {
            string file = Path.Combine(_out, $"{_run}{(ServerRegions.Off ? "-noregions" : "")}-{s.Name}.csv");
            File.WriteAllText(file, s.Csv.ToString());
            double walked = s.Walked;
            bool shared = s.Walker.Body.Region == s.Blocker!.Body.Region;
            // the two capsules (0.32 m each) touched and never went through each other
            bool touched = s.MinGap < 0.8, through = s.MinGap < 0.4;
            bool ok = walked > 3 && touched && !through && shared;
            if (!ok) failures++;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"{Log} site {s.Name}: walked {walked:F3} m, nearest the blocker {s.MinGap:F3} m, blocker moved {s.Pushed:F3} m, one region {shared} ({s.Walker.Body.Region}){(ok ? "" : " FAILED")} -> {file}"));
        }
        foreach (var (b, st) in _strolls)
        {
            int trunks = 0;
            if (b.Body.Region?.Root is { } root)
                foreach (var n in root.GetChildren()) if (n is World.TreeColliders t) trunks = t.GetChildCount();
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"{Log} stroll {b.Body.Name} in {b.Body.Region}: {st.Path:F1} m walked, {b.Body.Global.HorizontalDistanceTo(st.From):F1} m from where it began, on the ground {100.0 * st.OnFloor / Math.Max(1, st.Ticks):F0} % of the time, alt {b.Body.Global.Alt:F1}{(trunks > 0 ? $", {trunks} trunk bodies in its region" : "")}"));
            if (st.Path < 5 || b.Body.Global.Alt < st.From.Alt - 30) failures++;
        }
        GD.Print(failures == 0 ? $"{Log} RESULT: ok" : $"{Log} RESULT: FAILED ({failures})");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private void Fail(string why)
    {
        GD.Print($"{Log} RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
