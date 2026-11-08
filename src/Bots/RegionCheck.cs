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
/// walker walked, never passed through its blocker (they touched), and each site's bots share one region. A second walker
/// heads north into a car the server parked in its main frame <see cref="CarGap"/> m ahead: in a far region
/// it meets the car's copy there (<c>RegionMirrors</c>), and stops at its side as at home.
/// <c>--noregions</c> runs the same with every body in the server's one frame: the "before". <c>--regionenu</c> makes the
/// far regions ENU frames (#1513): the far site walks in true north and its own up, and the hop is a turning handover that
/// must keep the walker's heading from true north.
/// <c>--regionat E,N --regionfar E,N</c> put the sites elsewhere (a real-terrain run: Geneva and Riddes);
/// <c>--regionwander S</c> then lets every bot stroll S seconds among the trunks and houses (WanderBrain) and
/// reports how far each went, whether it stayed on its ground and how many trunks its region holds.
/// </summary>
public partial class RegionCheck : Node
{
    public static bool Requested => CmdArgs.Has("--regioncheck");

    /// <summary>The far site: this far east of the spawn.</summary>
    public const double FarM = 300_000;
    public const float BlockerGap = 4f;
    /// <summary>The second walker's parked car: this far north of it, its side towards it.</summary>
    public const float CarGap = 6f;
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
        public Bot? Walker, Blocker, Driver;
        public GlobalPos WalkerStart, BlockerStart, DriverStart, CarAt;
        public double MinGap = double.MaxValue, MinCar = double.MaxValue;
        /// <summary>At the end of the walk: how far the walker went, how far the blocker was pushed.</summary>
        public double Walked, Pushed, ToCar;
        /// <summary>At the end of the walk (before a hop moves one): whether its bots shared a region.</summary>
        public bool Shared;
        public readonly StringBuilder Csv = new();
    }

    private sealed class Script(Vector3 wish) : IBotBrain
    {
        public bool Go;
        public BotIntent Walk => wish == Vector3.Zero ? BotIntent.Stand
            : new BotIntent { Wish = wish, FaceYaw = Mathf.Atan2(-wish.X, -wish.Z) };
        public double Think(Bot bot, double now)
        {
            // straight on, at a walk: the same wish every step at both sites
            bot.Intent = Go ? Walk : BotIntent.Stand;
            return 0.05;
        }
    }

    private readonly List<Site> _sites = new();
    /// <summary>The run's name in its files and its cars: the sites, and how it differs from a plain run.</summary>
    private string Tag => _run + (ServerRegions.Off ? "-noregions" : "") + (RegionMirrors.Off ? "-nomirrors" : "") + (ServerRegions.ForceEnu ? "-enu" : "");
    private readonly string _run;
    private string _out = "";
    private int _tick, _settled, _walked = -1, _wander, _wandered = -1;
    private bool _parked;
    /// <summary><c>--regionhop</c> (both sites): after the walk the far walker is put beside the home site.</summary>
    private readonly bool _hop = CmdArgs.Has("--regionhop");
    private int _hopped = -1;
    private const int HopTicks = 300;
    private string _hopLine = "";
    private bool _hopOk = true;
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
        var spawn = SpawnPoint.ParseLv95("--regionat") is var (ae, an) ? GlobalPos.FromLv95(ae, an, 0)
            : GlobalPos.FromLv95(SpawnPoint.DefaultLv95E + 250, SpawnPoint.DefaultLv95N + 250, 0);
        var far = SpawnPoint.ParseLv95("--regionfar") is var (be, bn) ? GlobalPos.FromLv95(be, bn, 0) : spawn.Moved(FarM, 0);
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
            s.Blocker = director.Add(s.At.Moved(BlockerGap, 0), 0f, BotProfile.For(1, 1, BotDifficulty.Normal, new HashSet<string>()));
            s.Driver = director.Add(s.At.Moved(-10, 0), 0f, BotProfile.For(1, 2, BotDifficulty.Normal, new HashSet<string>()));
            if (s.Walker == null || s.Blocker == null || s.Driver == null) { Fail($"site {s.Name}: no bots"); return; }
            s.Walker.Brain = new Script(Vector3.Right);
            s.Blocker.Brain = new Script(Vector3.Zero);
            s.Driver.Brain = new Script(Vector3.Forward);
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
                foreach (var b in new[] { s.Walker!, s.Blocker!, s.Driver! })
                    ready &= b.Body.Placed && b.Body.IsOnFloor() && b.Body.Velocity.Length() < 0.05f;
            _settled = ready ? _settled + 1 : 0;
            if (_settled < SettleTicks) return;
            if (!_parked)
            {
                // a car the server parks in its main frame, across the second walker's way; then a second
                // for its copy to be made in a far region (the regions decide every 0.5 s)
                _parked = true;
                _settled = 0;
                if (!Park()) return;
                return;
            }
            foreach (var s in _sites)
            {
                s.WalkerStart = s.Walker!.Body.Global;
                s.BlockerStart = s.Blocker!.Body.Global;
                s.DriverStart = s.Driver!.Body.Global;
                foreach (var b in new[] { s.Walker, s.Driver })
                {
                    var script = (Script)b.Brain;
                    script.Go = true;
                    // at once, not at its next think (up to 0.5 s away, unwatched): the same step in every run
                    b.Intent = script.Walk;
                }
                s.Csv.Append("tick,we,wn,walt,be,bn,balt,de,dn,dalt\n");
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
            var d = s.Driver!.Body.Global;
            s.MinGap = Math.Min(s.MinGap, w.HorizontalDistanceTo(b));
            s.MinCar = Math.Min(s.MinCar, d.HorizontalDistanceTo(s.CarAt));
            var (we, wn, wa) = s.WalkerStart.OffsetTo(w);
            var (be, bn, ba) = s.BlockerStart.OffsetTo(b);
            var (de, dn, da) = s.DriverStart.OffsetTo(d);
            s.Csv.Append(string.Create(CultureInfo.InvariantCulture,
                $"{_walked},{we:F5},{wn:F5},{wa:F5},{be:F5},{bn:F5},{ba:F5},{de:F5},{dn:F5},{da:F5}\n"));
        }
        if (_walked < WalkTicks) return;
        if (_walked == WalkTicks)
            foreach (var s in _sites)
            {
                s.Walked = s.Walker!.Body.Global.HorizontalDistanceTo(s.WalkerStart);
                s.Pushed = s.Blocker!.Body.Global.HorizontalDistanceTo(s.BlockerStart);
                s.ToCar = s.Driver!.Body.Global.HorizontalDistanceTo(s.DriverStart);
                s.Shared = s.Walker.Body.Region == s.Blocker.Body.Region && s.Driver.Body.Region == s.Walker.Body.Region;
            }
        if (_hop && _sites.Count == 2 && _hopped < HopTicks)
        {
            if (Hop()) return;
        }
        if (_wander <= 0) { Finish(); return; }
        if (_wandered < 0)
        {
            // the walk is done: every bot strolls among the trunks and houses round its site
            foreach (var s in _sites)
                foreach (var b in new[] { s.Walker!, s.Blocker!, s.Driver! })
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

    /// <summary>
    /// The far walker put 6 m south of the home walker (<c>ServerRegions.Put</c>): it must be in the home
    /// site's region at once, then stand on that ground. True while it is still settling.
    /// </summary>
    private bool Hop()
    {
        var home = _sites[0].Walker!.Body;
        var hopper = _sites[1].Walker!.Body;
        if (_hopped < 0)
        {
            var from = hopper.Region;
            var to = home.Global.Moved(0, -6, 1);
            // its heading from true north, before and after: a turning handover (an ENU region into the flat main one,
            // #1513) keeps it; between two flat frames the grid heading is kept, as before G2
            float before = hopper.Origin!.TrueYaw(hopper.Position, hopper.Rotation.Y);
            ServerRegions.Put(hopper, to);
            float after = hopper.Origin!.TrueYaw(hopper.Position, hopper.Rotation.Y);
            float turned = Mathf.RadToDeg(EnuAxes.Wrap(after - before));
            bool turns = from?.IsEnu == true || hopper.Region?.IsEnu == true;
            _hopLine = string.Create(CultureInfo.InvariantCulture,
                $"hop {hopper.Name} from {from} to {hopper.Region}, true heading {Mathf.RadToDeg(before):F3}° -> {Mathf.RadToDeg(after):F3}° ({turned:+0.000;-0.000}°)");
            _hopOk = hopper.Region == home.Region && (!turns || Math.Abs(turned) < 0.01f);
            _hopped = 0;
            return true;
        }
        if (++_hopped < HopTicks) return true;
        double drop = home.Global.OffsetTo(hopper.Global).Up;
        bool stands = hopper.IsOnFloor() && System.Math.Abs(drop) < 3;
        _hopOk &= stands && hopper.Region == home.Region;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"{Log} {_hopLine}: now in {hopper.Region}, on the ground {hopper.IsOnFloor()}, {drop:F2} m from the home walker's height{(_hopOk ? "" : " FAILED")}"));
        return false;
    }

    /// <summary>Parks a car <see cref="CarGap"/> m north of each site's second walker, side on, through the server's own path.</summary>
    private bool Park()
    {
        if (Vehicles.VehicleManager.Instance is not { } vehicles) { Fail("no vehicle manager"); return false; }
        var kind = (RideKind)CarCatalog.First;
        if (Rideable.Create(kind) is not { IsVehicle: true } ride) { Fail("no car to park"); return false; }
        foreach (var s in _sites)
        {
            var at = s.Driver!.Body.Global;
            s.CarAt = at.Moved(0, CarGap);
            var state = new Vehicles.VehicleState(kind, s.CarAt, Mathf.Pi / 2, Vector3.Zero, ride.MaxHealth,
                EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
            // a name of this run's own: the object containers file a parked car, and the runs of one script share user://
            if (vehicles.Place(state, $"regioncheck_car_{Tag}_{s.Name}", settled: true) == null) { Fail($"site {s.Name}: the car was not parked"); return false; }
        }
        GD.Print($"{Log} parked a car at each site; copies in far regions: {ServerRegions.Instance?.Mirrors ?? 0} (made at the next decision)");
        return true;
    }

    private string Status()
    {
        var sb = new StringBuilder();
        foreach (var s in _sites)
            foreach (var b in new[] { s.Walker, s.Blocker, s.Driver })
                if (b != null) sb.Append($"{b.Body.Name} {b.Body.Global} placed {b.Body.Placed} floor {b.Body.IsOnFloor()}; ");
        return sb.ToString();
    }

    private void Finish()
    {
        int failures = 0;
        foreach (var s in _sites)
        {
            string file = Path.Combine(_out, $"{Tag}-{s.Name}.csv");
            File.WriteAllText(file, s.Csv.ToString());
            double walked = s.Walked;
            bool shared = s.Shared;
            // the two capsules (0.32 m each) touched and never went through each other
            bool touched = s.MinGap < 0.8, through = s.MinGap < 0.4;
            // the car's side is ~0.9 m from its centre: the walker stops there, never at the centre (through it)
            bool atCar = s.MinCar > 0.9 && s.ToCar < CarGap;
            bool ok = walked > 3 && touched && !through && shared && atCar;
            if (!ok) failures++;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"{Log} site {s.Name}: walked {walked:F3} m, nearest the blocker {s.MinGap:F3} m, blocker moved {s.Pushed:F3} m, one region {shared} ({s.Walker.Body.Region}); second walker {s.ToCar:F3} m towards the parked car, nearest its centre {s.MinCar:F3} m{(ok ? "" : " FAILED")} -> {file}"));
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
        if (!_hopOk) failures++;
        GD.Print(failures == 0 ? $"{Log} RESULT: ok" : $"{Log} RESULT: FAILED ({failures})");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private void Fail(string why)
    {
        GD.Print($"{Log} RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
