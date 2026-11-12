using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Fixture;
using UnitSport.Vehicles;

namespace UnitSport.World.Snow;

/// <summary>
/// <c>--chunks fixture:fun --pistecheck[,SHOT.png] [--frame earth|flat]</c> (#1735): the fun course's ski slope and its
/// jetty's moored boats in the running game, on either frame. In order:
/// <list type="number">
/// <item>the slope under a point in its middle is the course's "Neon run" (<see cref="PisteIndex.At"/>: what a skier
/// and the surfaces ask);</item>
/// <item>the ground drawn there carries a layer of the slopes' texture (<see cref="PisteLayer"/>'s <c>piste_layer</c>);</item>
/// <item>the edge poles stand (<see cref="SlopeMarkers"/>), none twice (an earth leaf's stand-in reaches into its
/// neighbours': a pole drawn by two leaves would stand twice), a shot of the slope;</item>
/// <item>the jetty's boats lie moored, afloat on the lake (<see cref="DormantVehicles"/>' marina), a shot of them.</item>
/// </list>
/// <c>--pisteat E,N[,ALT]</c> (LV95 notation) runs the first three on a real region's slope there instead (no name to
/// match, no marina): <c>--frame earth --leaves D:/UnitSportSwitzerland/test_b3/row7/cube --pistecheck,SHOT.png
/// --pisteat 2583954,1109257,1900 --date 2027-01-15</c>, La Tzoumaz above Riddes on the B3 row-7 leaves.
/// </summary>
public partial class PisteCheck : Node
{
    private const string Tag = "[pistecheck]";
    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private readonly List<string> _fail = new();
    private Camera3D? _camera;
    private int _phase;
    private double _t;
    /// <summary>When the phase began, on the wall clock: the leaves load on threads, which a fixed-fps run outpaces.</summary>
    private ulong _since = Time.GetTicksMsec();
    /// <summary>The slope's middle and the jetty's head on the Earth: the world frame shifts under a far camera.</summary>
    private GlobalPos _slopeAt, _jettyAt;
    private Vector3 _slope => _origin.ToWorld(_slopeAt);
    private Vector3 _jetty => _origin.ToWorld(_jettyAt);
    private int _poles, _boats;
    private bool _atJetty;
    /// <summary>A real slope's place (<c>--pisteat</c>): no course, no marina.</summary>
    private bool _real;

    private static readonly StringName LayerParam = PisteLayer.PisteLayerParam;

    public PisteCheck(IWorldGround ground, WorldOrigin origin, string? shot)
    {
        Name = "PisteCheck";
        _ground = ground;
        _origin = origin;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--pistecheck");

    public static bool Requested() => ParseArgs().Requested;

    public override void _Ready()
    {
        if (CmdArgs.Value("--pisteat")?.Split(',') is { Length: >= 2 } at)
        {
            // a person's LV95 notation: where the slope is
            double Num(int i, double or) => i < at.Length && double.TryParse(at[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : or;
            _real = true;
            _slopeAt = GlobalPos.FromLv95(Num(0, 0), Num(1, 0), Num(2, 1500));
        }
        else if (CoursePlace.Active is { } place)
        {
            // the slope's middle, and the water off the jetty's head (FunCourse's own numbers)
            _slopeAt = At(place, (FunCourse.HillX0 + FunCourse.LiftX) / 2, (FunCourse.SlopeY0 + FunCourse.SlopeY1) / 2 + 20, FunCourse.Flat + FunCourse.HillH / 2);
            _jettyAt = At(place, FunCourse.LakeX + FunCourse.LakeR - 25, FunCourse.LakeY + 60, FunCourse.Level);
        }
        else { Fail("no fixture course (run with --chunks fixture:fun) and no --pisteat"); Verdict(); return; }
        _camera = new Camera3D { Name = "PisteCheckCamera", Far = 6000 };
        AddChild(_camera);
        _camera.MakeCurrent();
        Look(_slope + new Vector3(0, 90, 160), _slope);
        _ground.AddAnchor(_camera, collision: false);
        GD.Print($"{Tag} slope at {_slope}" + (_real ? "" : $", jetty at {_jetty}") + $", frame {GameSettings.Current.Frame}");
    }

    public override void _ExitTree()
    {
        if (_camera != null) _ground.RemoveAnchor(_camera);
    }

    private static GlobalPos At(CoursePlace place, double x, double y, double alt)
    {
        var (e, n) = place.Exact(x, y);
        return GlobalPos.FromLv95(e, n, alt);
    }

    private void Look(Vector3 from, Vector3 at)
    {
        _camera!.GlobalPosition = from;
        _camera.LookAt(at, _origin.UpAt(at));
    }

    public override void _Process(double delta)
    {
        _t += delta;
        switch (_phase)
        {
            case 0: Slope(); break;
            case 1: Drawn(); break;
            case 2: Poles(); break;
            case 3: Boats(); break;
            case 4: Map(); break;
            case 5: Race(); break;
        }
    }

    /// <summary>Phase 0: the slope under the point, once its leaf or tile has loaded.</summary>
    private void Slope()
    {
        if (!_ground.TryGetHeight(_slope, out float h))
        {
            // kept over the slope while the frame shifts toward it
            Look(_slope + new Vector3(0, 90, 160), _slope);
            Wait("the ground under the slope", 120);
            return;
        }
        _slopeAt = _origin.ToGlobal(_slope with { Y = h });
        if (SnowService.Instance?.Index is not { } index) { Fail("no snow service (--systems without snow?)"); Verdict(); return; }
        if (index.At(_slope + Vector3.Up * 0.1f, out float mask) is not { } r) { Wait("the slope under its middle", 120); return; }
        GD.Print($"{Tag} under the slope's middle: '{r.Name}' ({r.Type}, {r.Difficulty}), mask {mask:F2}, altitudes {r.AltLow:F0}-{r.AltHigh:F0} m");
        if (SnowService.Instance.TryAt(_slope + Vector3.Up * 0.1f, out _, out var snow, out _))
            GD.Print($"{Tag} its snow at {_origin.AltitudeOf(_slope):F0} m, {SnowService.Instance.Hour:F1} h: {snow.Condition} "
                + $"(fresh {snow.Fresh:F2}, loose {snow.Loose:F2}, ice {snow.Ice:F2}, wet {snow.Wet:F2})");
        if (!_real && r.Name != "Neon run") Fail($"the slope under its middle is '{r.Name}', not the course's 'Neon run'");
        if (mask < 0.5f) Fail($"the slope's middle has an edge mask of {mask:F2}");
        Next();
    }

    /// <summary>Phase 1: the ground mesh there carries a layer of the slopes' texture.</summary>
    private void Drawn()
    {
        int layer = _ground.GroundAt(_ground.TileAt(_slope)) is { } mesh ? mesh.GetInstanceShaderParameter(LayerParam).AsInt32() : -1;
        if (layer < 0) { Wait("the slope's layer on its ground mesh", 60); return; }
        GD.Print($"{Tag} the ground under the slope draws layer {layer} of the slopes' texture");
        Next();
    }

    /// <summary>Phase 2: the edge poles, none standing twice; then a shot from the foot of the slope.</summary>
    private void Poles()
    {
        var at = new List<Vector3>();
        foreach (var node in GetTree().Root.FindChildren("SlopeMarkers", "Node3D", true, false))
            if (node is Node3D poles && poles.HasMeta(SlopeMarkers.PolesMeta))
                foreach (var p in poles.GetMeta(SlopeMarkers.PolesMeta).AsVector3Array()) at.Add(poles.GlobalTransform * p);
        if (at.Count == 0 || _t < 3) { Wait("the slope's poles", 90); return; }
        _poles = at.Count;
        int twice = 0, near = 0;
        for (int i = 0; i < at.Count; i++)
        {
            if (at[i].DistanceTo(_slope) < 400) near++;
            for (int j = i + 1; j < at.Count; j++)
                if (at[i].DistanceTo(at[j]) < 0.3f) { twice++; break; }
        }
        // a pole stands on the ground drawn under it
        int floating = 0;
        foreach (var p in at)
            if (_ground.TryGetHeight(p, out float g) && Mathf.Abs(p.Y - g) > 1.5f) floating++;
        GD.Print($"{Tag} {at.Count} poles ({near} within 400 m of the slope), {twice} standing twice, {floating} off the ground by 1.5 m");
        if (near == 0) Fail("no pole near the slope");
        if (twice > 0) Fail($"{twice} poles stand twice");
        if (floating > at.Count / 20) Fail($"{floating} of {at.Count} poles are off the ground by 1.5 m");
        // a real region's runs from higher up and further back: several of them in the picture
        Look(_slope + new Vector3(0, _real ? 110 : 35, 0) + Toward(_slope, -1) * (_real ? 320f : 140f), _slope);
        // parked while the shot is taken: Next comes after it
        _phase = -1;
        Shot("slope", () => _phase = 4);
    }

    private SlopeMap? _map;

    /// <summary>Phase 4 (#1737): the ski map painted round the slope's middle shows it there, its leaves or tiles all in.</summary>
    private void Map()
    {
        if (_map == null) AddChild(_map = new SlopeMap(() => null, _ground));
        if (!_map.PaintAt(_slope)) { Wait("the ski map's tiles", 60); return; }
        int pixels = _map.SlopePixels();
        GD.Print($"{Tag} the ski map round the slope: {pixels} of {200 * 200} pixels a slope, the middle {(_map.MiddleIsSlope ? "on" : "off")} it");
        if (!_map.MiddleIsSlope) Fail("the ski map does not show the slope under the skier");
        if (pixels < 200) Fail($"the ski map shows only {pixels} slope pixels");
        Next();
    }

    private System.Threading.Tasks.Task<Player.RaceRoute?>? _race;

    /// <summary>Phase 5 (#1737): a ski race from the slope's top builds down the slopes, its points on them.</summary>
    private void Race()
    {
        if (_ground.Content is not { } source) { Fail("no tile files"); Verdict(); return; }
        if (_race == null)
        {
            // from near the top of the slope, as a race is opened where the skier stands
            var top = _real ? _slope : _origin.ToWorld(At(CoursePlace.Active!, (FunCourse.HillX0 + FunCourse.LiftX) / 2, FunCourse.SlopeY1 + 10, FunCourse.Flat + FunCourse.HillH));
            var frame = _origin.Frame;
            _race = System.Threading.Tasks.Task.Run(() => Player.RaceRoute.BuildPisteAsync(source, frame, top, 300f));
            return;
        }
        if (!_race.IsCompleted) { Wait("the ski race's route", 60); return; }
        if (_race.Result is not { Centre.Count: > 10 } route) { Fail("no ski race down the slope"); Finish(); return; }
        int on = 0;
        float first = 0, last = 0;
        for (int i = 0; i < route.Centre.Count; i++)
        {
            var p = route.Centre[i];
            if (SnowService.Instance?.Index?.At(p + Vector3.Up * 0.2f, out _) != null) on++;
            float alt = (float)_origin.AltitudeOf(p);
            if (i == 0) first = alt;
            last = alt;
        }
        float length = route.Arc[^1];
        GD.Print($"{Tag} a ski race: {route.Centre.Count} points over {length:F0} m, {on} on a slope, from {first:F0} m down to {last:F0} m");
        if (on < route.Centre.Count * 0.9f) Fail($"only {on} of {route.Centre.Count} race points lie on a slope");
        if (last >= first - 10) Fail($"the race does not go down ({first:F0} m to {last:F0} m)");
        if (length < 100) Fail($"the race is {length:F0} m long");
        Finish();
    }

    /// <summary>The slope's phases done: the marina's next on the course, the verdict on a real region.</summary>
    private void Finish()
    {
        if (_real) Verdict();
        else Next3();
    }

    private void Next3()
    {
        _phase = 3;
        _t = 0;
        _since = Time.GetTicksMsec();
    }

    /// <summary>A horizontal direction down the slope (toward the course's south) at a point.</summary>
    private Vector3 Toward(Vector3 at, float sign)
    {
        var north = TrueNorth.WorldNorth(_origin.Frame, at);
        return north * sign;
    }

    /// <summary>Phase 3: the jetty's moored boats, afloat; then a shot of them.</summary>
    private void Boats()
    {
        if (!_atJetty)
        {
            _atJetty = true;
            Look(_jetty + new Vector3(0, 40, 0) + Toward(_jetty, 1) * 70f, _jetty);
        }
        if (DormantVehicles.Instance is not { } dormant) { Fail("no dormant fleets"); Verdict(); return; }
        int boats = 0, dry = 0;
        string owner = "";
        foreach (var s in dormant.Slots())
        {
            if (s.KindId is not ((int)RideKind.Speedboat or (int)RideKind.Jetski)) continue;
            var p = dormant.WorldOf(s);
            if (p.DistanceTo(_jetty) > 200) continue;
            boats++;
            owner = s.Owner;
            float draught = s.KindId == (int)RideKind.Speedboat ? DormantSlots.SpeedboatDraught : DormantSlots.JetskiDraught;
            if (!_ground.TryGetWaterLevel(p, out float level) || Mathf.Abs(p.Y + draught - level) > 0.2f) dry++;
        }
        if (boats == 0)
        {
            if ((Time.GetTicksMsec() - _since) / 1000.0 >= 120)
            {
                float nearest = float.MaxValue;
                int all = 0;
                foreach (var s in dormant.Slots())
                    if (s.KindId is (int)RideKind.Speedboat or (int)RideKind.Jetski)
                    {
                        all++;
                        nearest = Mathf.Min(nearest, dormant.WorldOf(s).DistanceTo(_jetty));
                    }
                GD.Print($"{Tag} {Landings.Current.Jetties.Count} jetties, {all} boats in the fleets, the nearest {nearest:F0} m from the jetty");
            }
            Wait("the jetty's boats", 120);
            return;
        }
        if (_t < 2) return;
        _boats = boats;
        GD.Print($"{Tag} {boats} boats moored at the jetty (slots of {owner}), {dry} not afloat at their still water");
        if (dry > 0) Fail($"{dry} of {boats} moored boats are not afloat");
        _phase = -1;
        Shot("marina", Verdict);
    }

    private void Next()
    {
        _phase++;
        _t = 0;
        _since = Time.GetTicksMsec();
    }

    private void Wait(string what, double limit)
    {
        if ((Time.GetTicksMsec() - _since) / 1000.0 < limit) return;
        Fail($"timed out waiting for {what} after {limit:F0} s");
        Verdict();
    }

    private async void Shot(string name, System.Action then)
    {
        if (_shot != null)
        {
            // a few frames for the layer, the poles and the boats to be drawn
            for (int i = 0; i < 20; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (!IsInsideTree()) return;
            string file = _shot.Replace(".png", $"_{name}.png");
            GD.Print(GetViewport().GetTexture().GetImage().SavePng(file) == Error.Ok ? $"{Tag} wrote {file}" : $"{Tag} could not write {file}");
        }
        _t = 0;
        _since = Time.GetTicksMsec();
        then();
    }

    private void Fail(string why)
    {
        if (_fail.Contains(why)) return;
        _fail.Add(why);
        GD.Print($"{Tag} FAIL: {why}");
    }

    private bool _done;

    private void Verdict()
    {
        if (_done) return;
        _done = true;
        GD.Print(_fail.Count == 0
            ? $"{Tag} RESULT: ok — the slope found, drawn and poled ({_poles} poles)" + (_real ? "" : $", {_boats} boats moored afloat")
            : $"{Tag} RESULT: FAILED ({string.Join("; ", _fail)})");
        GetTree().Quit(_fail.Count == 0 ? 0 : 1);
    }
}
