using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// <c>godot --path . -- --frame earth --farfillcheck --at E,N [--farfillshot DIR]</c> (#1800): the generated fill far from
/// Switzerland (another cube face, Asia, the Americas, the sea) against the globe round it. The camera waits 60 m over
/// the start until the near leaves and the globe are idle, then over a 41 × 41 grid 15 m apart round it reads the near
/// leaves' ground and water (<see cref="IWorldGround"/>) and the highest surface the globe draws there
/// (<see cref="World.Globe.SurfaceAt"/>). It fails where the globe's ground or lakes stand over the leaves' ground (the
/// user's flight to Ladakh: the leaves' trees and a house through a pale globe surface),
/// or where the world's up at the camera is
/// not the ellipsoid's within 0.05° (a rolled view). It prints the pyramid's datum read-out (the ground's ellipsoidal
/// height, LHN95 against the leaves' sea level). Windowed, <c>--farfillshot</c> writes two pictures. RESULT ok or FAILED.
/// </summary>
public partial class FarFillCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--farfillcheck");

    private const int Half = 20;
    private const float Step = 15f, CameraM = 60f, OverM = 0.5f, FarM = 2500f;
    private const double TimeoutS = 300, UpTolDeg = 0.05;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly Func<World.Globe?> _globe;
    private readonly Func<bool> _idle;
    private readonly Func<string?> _status;
    private readonly List<string> _faults = new();
    private readonly string? _shots = CmdArgs.Value("--farfillshot");
    private double _t, _idleFor;
    private int _stage;
    private Vector3 _at;

    public FarFillCheck(IWorldGround ground, WorldOrigin origin, Func<World.Globe?> globe, Func<bool> idle, Func<string?> status)
    {
        _ground = ground;
        _origin = origin;
        _globe = globe;
        _idle = idle;
        _status = status;
        Name = "FarFillCheck";
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (GetViewport()?.GetCamera3D() is not { } camera) return;
        if (_t > TimeoutS && _stage < 3) { Finish($"FAILED timeout after {TimeoutS} s ({_status()})"); return; }
        switch (_stage)
        {
            case 0:
                // over the start, looking down the slope's way later; hold it there each frame
                _at = new Vector3(camera.GlobalPosition.X, 0, camera.GlobalPosition.Z);
                if (!_ground.TryGetHeight(_at, out float h0)) { Hold(camera, 3000); return; }
                Hold(camera, h0 + CameraM);
                _idleFor = _idle() ? _idleFor + delta : 0;
                if (_idleFor > 2) _stage = 1;
                return;
            case 1:
                Measure(camera);
                _stage = 2;
                _shotAt = 0;
                return;
            case 2:
                Shots(camera, delta);
                return;
        }
    }

    private void Hold(Camera3D camera, float y)
    {
        camera.GlobalPosition = new Vector3(_at.X, y, _at.Z);
        camera.Rotation = new Vector3(Mathf.DegToRad(-15), 0, 0);
    }

    private void Measure(Camera3D camera)
    {
        var globe = _globe();
        if (globe == null) { _faults.Add("no globe in this world"); return; }
        int points = 0, over = 0, wetOver = 0, leafWater = 0, noGlobe = 0;
        float worst = 0, worstWet = 0, worstLeafWater = 0;
        Vector3 worstAt = default;
        var levels = new Dictionary<int, int>();
        for (int j = -Half; j <= Half; j++)
            for (int i = -Half; i <= Half; i++)
            {
                var at = _at + new Vector3(i * Step, 0, j * Step);
                if (!_ground.TryGetHeight(at, out float ground)) continue;
                points++;
                var p = new Vector3(at.X, ground, at.Z);
                bool hasLeafWater = _ground.TryGetWaterLevel(p, out float level) && level > ground + 1f;
                if (!globe.SurfaceAt(p, _origin.ToEcef(p), out float gy, out float wy, out int lvl)) { noGlobe++; }
                else
                {
                    levels[lvl] = levels.GetValueOrDefault(lvl) + 1;
                    if (gy - ground > OverM) { over++; if (gy - ground > worst) { worst = gy - ground; worstAt = at; } }
                    if (wy - ground > OverM && !hasLeafWater) { wetOver++; worstWet = Math.Max(worstWet, wy - ground); }
                }
                if (hasLeafWater && !(wy > ground))
                {
                    leafWater++;
                    worstLeafWater = Math.Max(worstLeafWater, level - ground);
                }
            }

        var g = _origin.ToGlobal(new Vector3(_at.X, 0, _at.Z));
        var geo = g.Geodetic;
        _ground.TryGetHeight(_at, out float hc);
        var ground0 = _origin.ToGlobal(new Vector3(_at.X, hc, _at.Z));
        Print($"at {geo.LatDeg:F4} N {geo.LonDeg:F4} E (LV95 {g.Lv95.E:F0},{g.Lv95.N:F0}), face F{CubeKey.FromEcef(g.Ecef, 14).Face}: "
            + $"ground {ground0.Geodetic.H:F1} m ellipsoidal, {ground0.Alt:F1} m LHN95 (the leaves' sea level {ground0.Geodetic.H - ground0.Alt:F1} m over the ellipsoid)");
        var lv = string.Join(", ", levels.OrderBy(kv => kv.Key).Select(kv => $"L{kv.Key}: {kv.Value}"));
        Print($"{points} points: the globe's ground over the leaves' at {over} (worst {worst:F1} m at {worstAt}), its lakes at {wetOver} "
            + $"(worst {worstWet:F1} m), the leaves' own water over their ground at {leafWater} (a lake or river, worst {worstLeafWater:F1} m), "
            + $"no globe tile at {noGlobe}; globe levels drawn {lv}");
        if (points < 100) _faults.Add($"only {points} points on the leaves' ground");
        if (over > 0) _faults.Add($"the globe's ground stands over the leaves' at {over} of {points} points (worst {worst:F1} m)");
        if (wetOver > 0) _faults.Add($"the globe's lakes stand over the leaves' ground at {wetOver} of {points} points (worst {worstWet:F1} m)");

        // the read-out sees the globe at all: past the near leaves (900 m, dropped 300 m further) it draws the ground
        int seen = 0;
        for (int k = 0; k < 8; k++)
        {
            var far = _at + new Vector3(Mathf.Cos(k * Mathf.Pi / 4), 0, Mathf.Sin(k * Mathf.Pi / 4)) * FarM;
            if (globe.SurfaceAt(far, _origin.ToEcef(far), out _, out _, out _)) seen++;
        }
        Print($"the globe drawn at {seen} of 8 points {FarM / 1000:F1} km out");
        if (seen < 6) _faults.Add($"the globe read at only {seen} of 8 points {FarM / 1000:F1} km out: the read-out cannot see it");

        // the world's up at the camera against the ellipsoid's normal there: a rolled view is a frame off its up
        var eye = _origin.ToEcef(camera.GlobalPosition);
        var up = _origin.ToEcef(camera.GlobalPosition + Vector3.Up) - eye;
        var gd = Ellipsoid.Wgs84.ToGeodetic(eye);
        double la = gd.LatDeg * Math.PI / 180, lo = gd.LonDeg * Math.PI / 180;
        double nx = Math.Cos(la) * Math.Cos(lo), ny = Math.Cos(la) * Math.Sin(lo), nz = Math.Sin(la);
        double len = Math.Sqrt(up.X * up.X + up.Y * up.Y + up.Z * up.Z);
        double off = Math.Acos(Math.Clamp((up.X * nx + up.Y * ny + up.Z * nz) / len, -1, 1)) * 180 / Math.PI;
        // the ground's slope under the camera, from the heights 15 m round it
        _ground.TryGetHeight(_at + new Vector3(Step, 0, 0), out float he);
        _ground.TryGetHeight(_at + new Vector3(0, 0, -Step), out float hn);
        double slope = Math.Atan(Math.Sqrt((he - hc) * (he - hc) + (hn - hc) * (hn - hc)) / Step) * 180 / Math.PI;
        Print($"the world's up at the camera is {off:F4}° off the ellipsoid's; the ground slopes {slope:F1}° there");
        if (off > UpTolDeg) _faults.Add($"the world's up is {off:F3}° off the ellipsoid's at the camera");
    }

    private double _shotAt;
    private int _shot;

    private void Shots(Camera3D camera, double delta)
    {
        if (_shots == null || DisplayServer.GetName() == "headless") { Done(); return; }
        _shotAt += delta;
        if (_shotAt < 1.5) return;
        _shotAt = 0;
        _ground.TryGetHeight(_at, out float h);
        string name = _shot == 0 ? "farfill_low.png" : "farfill_high.png";
        string path = System.IO.Path.Combine(_shots, name);
        GetViewport().GetTexture().GetImage().SavePng(path);
        Print($"wrote {path}");
        _shot++;
        if (_shot >= 2) { Done(); return; }
        // the second picture: 300 m up, looking out
        camera.GlobalPosition = new Vector3(_at.X, h + 300, _at.Z);
        camera.Rotation = new Vector3(Mathf.DegToRad(-20), Mathf.DegToRad(90), 0);
    }

    private void Done()
    {
        foreach (var f in _faults) Print($"  {f}");
        Finish(_faults.Count == 0 ? "ok" : $"FAILED {_faults.Count} fault(s)");
    }

    private static void Print(string s) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[farfillcheck] {s}"));

    private void Finish(string result)
    {
        Print($"RESULT: {result}");
        SetProcess(false);
        GetTree().Quit(result == "ok" ? 0 : 1);
    }
}
