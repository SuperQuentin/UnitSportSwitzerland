using System.Collections.Generic;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>
/// <c>godot --path . -- --frame earth --treeseatcheck[,out.png] [--at E,N] [--treeseatto E,N]</c> (#1776): are the trees
/// standing on the ground? A camera 25 m over the start waits for the leaves and the near trees round it, then measures
/// every tree within <see cref="RadiusM"/>: its base as its tile holds it (the tile's transform on the leaf's tree,
/// <see cref="NearTrees.Registered"/>) and as the renderer draws it (<see cref="NearTrees.DrawnBases"/>), each against
/// <see cref="IWorldGround.TryGetHeight"/> under it. With <c>--treeseatto</c> it then flies there 300 m up (the earth
/// world re-anchoring on the way, turning a little each time) and measures again: the earth playtest's floating trees
/// after a flight over the fill (notes 26 and 30). Fails when a measure's 95th percentile or its worst tree is off by
/// more than <see cref="P95TolM"/> / <see cref="MaxTolM"/>. Windowed, <c>out.png</c> gets a picture of each measure
/// (<c>out_start.png</c>, <c>out_there.png</c>).
/// </summary>
public partial class TreeSeatCheck : Node
{
    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--treeseatcheck");

    /// <summary>Trees this far from the camera (horizontally) are measured.</summary>
    public const float RadiusM = 150f;
    /// <summary>The 95th percentile of the trees' |base - ground| past this fails.</summary>
    public const float P95TolM = 0.3f;
    /// <summary>One tree's |base - ground| past this fails.</summary>
    public const float MaxTolM = 1.5f;
    private const float FlySpeed = 250f, FlyAbove = 300f, LookAbove = 25f;
    // wall clock: the leaves stream on threads
    private const double PhaseTimeoutS = 180;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private readonly (double E, double N)? _to;
    private readonly List<string> _faults = new();
    private readonly List<Vector3> _drawn = new();
    private Camera3D _cam = null!;
    private (double E, double N) _from, _at;
    private double _alt, _phaseT, _stableT, _flown;
    private int _phase, _lastCount = -1, _shifts;

    public TreeSeatCheck(IWorldGround ground, WorldOrigin origin, string? shot)
    {
        _ground = ground;
        _origin = origin;
        _shot = shot;
        _to = SpawnPoint.ParseLv95("--treeseatto");
        Name = "TreeSeatCheck";
        // after the shifter and NearTrees' refill
        ProcessPriority = int.MaxValue;
    }

    public override void _Ready()
    {
        _from = _at = SpawnPoint.ParseTarget();
        AddChild(_cam = new Camera3D { Name = "TreeSeat", Far = 30_000, Current = true });
        _cam.GlobalPosition = _origin.ToWorld(_at.E, _at.N, 0) + Vector3.Up * 2000f;
        _ground.AddAnchor(_cam, collision: true);
        _origin.Shifted += OnShifted;
        Log(Inv($"start LV95 {_at.E:F0}/{_at.N:F0}{(_to is { } t ? Inv($", then to {t.E:F0}/{t.N:F0}") : "")}, origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "flat")}"));
    }

    public override void _ExitTree()
    {
        _origin.Shifted -= OnShifted;
        _ground.RemoveAnchor(_cam);
    }

    private void OnShifted(OriginShift shift) => _shifts++;

    public override void _Process(double delta)
    {
        if (_phase < 0) return;
        _phaseT += delta;
        if (_phaseT > PhaseTimeoutS) { Fault($"timed out in phase {_phase}"); Finish(); return; }
        switch (_phase)
        {
            case 0: if (Settle(delta)) Measure("start", next: _to == null ? 2 : 1); break;
            case 1: Fly(delta); break;
            case 2: Finish(); break;
            case 3: if (Settle(delta)) Measure("there", next: 2); break;
        }
    }

    /// <summary>Holds the camera over <see cref="_at"/>; true once the leaves' trees round it are in and steady.</summary>
    private bool Settle(double delta)
    {
        var at = _origin.ToWorld(_at.E, _at.N, 0);
        if (!_ground.HasCollisionAt(at) || !_ground.TryGetHeight(at, out float h)) return false;
        // looking north and down a little: the trees ahead in the picture
        _cam.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(-20f), 0, 0)), at with { Y = h + LookAbove });
        if (NearTrees.Instance is not { } near) { Fault("no near trees (the style draws its trees per tile: Cartoon wanted)"); Finish(); return false; }
        _drawn.Clear();
        near.DrawnBases(_drawn);
        int count = _drawn.Count + Registered(at).Count;
        if (count != _lastCount || count == 0) { _lastCount = count; _stableT = 0; return false; }
        _stableT += delta;
        // three seconds with the same trees, and the shot's frame drawn
        return _stableT > 3.0 && _phaseT > 5.0;
    }

    private void Fly(double delta)
    {
        var to = _to!.Value;
        double de = to.E - _from.E, dn = to.N - _from.N, len = System.Math.Sqrt(de * de + dn * dn);
        _flown = System.Math.Min(len, _flown + FlySpeed * delta);
        double f = len > 0 ? _flown / len : 1;
        _at = (_from.E + de * f, _from.N + dn * f);
        var at = _origin.ToWorld(_at.E, _at.N, 0);
        if (_ground.TryGetHeight(at, out float h)) _alt = System.Math.Max(_alt, h + FlyAbove);
        _alt = System.Math.Max(_alt, at.Y + FlyAbove);
        _cam.GlobalPosition = at with { Y = (float)_alt };
        if (_flown < len) return;
        Log(Inv($"flown {len:F0} m to {_at.E:F0}/{_at.N:F0} across {_shifts} re-anchors"));
        Next(3);
    }

    /// <summary>The leaves' trees within the radius, in the world, as their tiles place them.</summary>
    private List<Vector3> Registered(Vector3 at)
    {
        var list = new List<Vector3>();
        foreach (var (tile, b) in NearTrees.Registered)
        {
            if (!IsInstanceValid(tile) || !tile.IsInsideTree() || !tile.IsVisibleInTree()) continue;
            var g = tile.GlobalTransform;
            foreach (var (buf, n) in new[] { (b.Conifers, b.ConiferCount), (b.Broadleaves, b.BroadleafCount) })
                for (int i = 0; i < n; i++)
                {
                    int s = i * 16;
                    var p = g * new Vector3(buf[s + 3], buf[s + 7], buf[s + 11]);
                    if (Flat(p - at) <= RadiusM) list.Add(p);
                }
        }
        return list;
    }

    private static float Flat(Vector3 v) => Mathf.Sqrt(v.X * v.X + v.Z * v.Z);

    private void Measure(string label, int next)
    {
        var at = _cam.GlobalPosition;
        var drawn = new List<Vector3>();
        foreach (var p in _drawn) if (Flat(p - at) <= RadiusM) drawn.Add(p);
        Judge(label, "tiles", Registered(at));
        Judge(label, "drawn", drawn);
        if (_shot != null && DisplayServer.GetName() != "headless")
        {
            string path = _shot.EndsWith(".png") ? _shot[..^4] + $"_{label}.png" : $"{_shot}_{label}.png";
            if (GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok) Log($"wrote {path}");
        }
        Next(next);
    }

    /// <summary>Every tree's base against the ground under it: the spread, the worst, the verdict.</summary>
    private void Judge(string label, string what, List<Vector3> trees)
    {
        var off = new List<float>(trees.Count);
        float worst = 0;
        Vector3 worstAt = default;
        foreach (var p in trees)
        {
            if (!_ground.TryGetHeight(p, out float h)) continue;
            float d = p.Y - h;
            off.Add(Mathf.Abs(d));
            if (Mathf.Abs(d) > Mathf.Abs(worst)) { worst = d; worstAt = p; }
        }
        if (off.Count == 0) { Fault($"{label}: no {what} trees within {RadiusM:F0} m"); return; }
        off.Sort();
        float p50 = off[off.Count / 2], p95 = off[(int)(off.Count * 0.95)], max = off[^1];
        var (we, wn) = _origin.ToLv95(worstAt);
        Log(Inv($"{label} {what}: {off.Count} trees within {RadiusM:F0} m, |base - ground| median {p50:F2} m, p95 {p95:F2}, max {max:F2} (worst {worst:+0.00;-0.00} m at LV95 {we:F0}/{wn:F0}), {_shifts} re-anchors so far"));
        if (p95 > P95TolM || max > MaxTolM)
            Fault(Inv($"{label} {what}: trees off the ground, p95 {p95:F2} m (tol {P95TolM}), max {max:F2} m (tol {MaxTolM})"));
    }

    private void Next(int phase)
    {
        _phase = phase;
        _phaseT = 0;
        _stableT = 0;
        _lastCount = -1;
    }

    private void Fault(string what)
    {
        _faults.Add(what);
        Log($"FAULT: {what}");
    }

    private void Finish()
    {
        _phase = -1;
        Log(_faults.Count == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_faults.Count} faults)");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }

    private static void Log(string s) => GD.Print($"[treeseat] {s}");

    private static string Inv(System.FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
