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
    private int _tick, _settled, _walked = -1;

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
        var spawn = new GlobalPos(SpawnPoint.DefaultLv95E + 250, SpawnPoint.DefaultLv95N + 250, 0);
        if (_run is "both" or "a") _sites.Add(new Site("a", spawn));
        if (_run is "both" or "b") _sites.Add(new Site("b", spawn with { E = spawn.E + FarM }));
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
        _walked++;
        foreach (var s in _sites)
        {
            var w = s.Walker!.Body.Global;
            var b = s.Blocker!.Body.Global;
            s.MinGap = Math.Min(s.MinGap, w.HorizontalDistanceTo(b));
            s.Csv.Append(string.Create(CultureInfo.InvariantCulture,
                $"{_walked},{w.E - s.WalkerStart.E:F5},{w.N - s.WalkerStart.N:F5},{w.Alt - s.WalkerStart.Alt:F5},{b.E - s.BlockerStart.E:F5},{b.N - s.BlockerStart.N:F5},{b.Alt - s.BlockerStart.Alt:F5}\n"));
        }
        if (_walked >= WalkTicks) Finish();
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
            var w = s.Walker!.Body.Global;
            double walked = w.HorizontalDistanceTo(s.WalkerStart);
            bool shared = s.Walker.Body.Region == s.Blocker!.Body.Region;
            // the two capsules (0.32 m each) touched and never went through each other
            bool touched = s.MinGap < 0.8, through = s.MinGap < 0.4;
            bool ok = walked > 3 && touched && !through && shared;
            if (!ok) failures++;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"{Log} site {s.Name}: walked {walked:F3} m, nearest the blocker {s.MinGap:F3} m, blocker moved {s.Blocker.Body.Global.HorizontalDistanceTo(s.BlockerStart):F3} m, one region {shared} ({s.Walker.Body.Region}){(ok ? "" : " FAILED")} -> {file}"));
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
