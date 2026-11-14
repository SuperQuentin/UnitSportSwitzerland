using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --doorwardencheck --at E,N --chunks …</c> (#1766, #1767): a facade door is drawn only
/// if it opens. A camera stands at the start while <see cref="DoorWarden"/> plans the buildings round it with extra
/// doors and the tiles that had doors with no doorway are built again. Then every drawn door within
/// <see cref="Reach"/> m (the door index is the drawn list) is asked of the real <see cref="InteriorManager"/>: its
/// building's plan must have a doorway for it, so E opens it. It also needs the warden to have found and left out
/// at least one door when <c>--doorwardenlocked</c> asks it to (central Geneva has hundreds). RESULT ok or FAILED.
/// </summary>
public partial class DoorWardenCheck : Node3D
{
    public static bool Requested() => CmdArgs.Has("--doorwardencheck");

    /// <summary>The doors checked: within this of the start, well inside the warden's radius.</summary>
    private const float Reach = 90f;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private double _t, _settledAt = -1;
    private bool _asking, _grounded;

    public DoorWardenCheck(IWorldGround ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
        Name = "DoorWardenCheck";
    }

    public override void _Ready()
    {
        var (e, n) = SpawnPoint.ParseTarget();
        Position = _origin.ToWorld(e, n, 0);
        _ground.AddAnchor(this, collision: false);
        var cam = new Camera3D { Position = new Vector3(0, 1.7f, 0), Far = 3000 };
        AddChild(cam);
        cam.MakeCurrent();
        GD.Print($"[doorwarden] start LV95 {e:F0}/{n:F0}");
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_asking) return;
        // on the ground once it is in: the warden plans round the camera, measured in 3D
        if (!_grounded)
        {
            if (!_ground.TryGetHeight(GlobalPosition, out float h)) return;
            GlobalPosition = GlobalPosition with { Y = h };
            _grounded = true;
            _t = 0;
        }
        var warden = DoorWarden.Instance;
        if (_t > 900) { Fault($"not settled after 15 minutes ({warden?.Counts})"); Finish(); return; }
        // the tiles round the start in, and the warden through them
        // an extra door drawn near the start (its tile in), and the warden settled for 5 s since: a scan that ran
        // before the tile came in found nothing to plan and called itself settled
        if (_t < 10 || warden == null || !warden.Settled || !DoorsNear().Any(d => d.Key.Slot > 0)) { _settledAt = -1; return; }
        if (_settledAt < 0) _settledAt = _t;
        if (_t - _settledAt < 5) return;
        // then the tiles it asked to build again committed: no drawn door left that is on record as locked
        if (StillDrawn() > 0 && _t - _settledAt < 95) return;
        GD.Print($"[doorwarden] the tiles built again {_t - _settledAt:F1} s after the warden settled");
        _asking = true;
        _ = Ask(warden);
    }

    /// <summary>Drawn doors on record as having no doorway, anywhere.</summary>
    private static int StillDrawn()
    {
        int n = 0;
        foreach (var tile in DoorIndex.ByTile)
            foreach (var d in tile)
                if (DoorLocks.Shared!.Recorded(d.Key)) n++;
        return n;
    }

    private List<DoorIndex.Entry> DoorsNear()
    {
        var at = GlobalPosition;
        var near = new List<DoorIndex.Entry>();
        foreach (var doors in DoorIndex.ByTile)
            foreach (var d in doors)
                if (d.World.DistanceTo(at) < Reach && d.Width > 0) near.Add(d);
        return near;
    }

    private async System.Threading.Tasks.Task Ask(DoorWarden warden)
    {
        var interiors = InteriorManager.Instance!;
        var doors = DoorsNear();
        var (planned, locked) = warden.Counts;
        GD.Print($"[doorwarden] settled after {_t:F0} s: {planned} buildings planned, {locked} doors left out; {doors.Count} drawn doors within {Reach} m"
            + $" ({doors.Count(d => d.Key.Slot > 0)} extra)");
        int opened = 0;
        foreach (var d in doors)
        {
            string key = d.Key.ToString();
            var layout = await interiors.GetOrCreate(key);
            if (layout?.EntranceOf(key) == null) Fault($"door {key} ({d.Kind}) is drawn, and its plan has no doorway for it");
            else opened++;
        }
        GD.Print($"[doorwarden] {opened} of {doors.Count} drawn doors have a doorway inside");
        if (doors.Count(d => d.Key.Slot > 0) == 0) Fault("no extra door drawn near the start: nothing was checked");
        // the tiles that had doors left out were built again without them, everywhere the camera reached
        int still = 0;
        foreach (var tile in DoorIndex.ByTile)
            foreach (var d in tile)
                if (DoorLocks.Shared!.Recorded(d.Key) && ++still <= 5) Fault($"door {d.Key} is on record with no doorway, and still drawn 90 s on");
        GD.Print($"[doorwarden] {still} drawn doors on record with no doorway");
        if (CmdArgs.Has("--doorwardenlocked") && locked == 0) Fault("the warden left out no door here");
        Finish();
    }

    private void Fault(string what)
    {
        _faults.Add(what);
        GD.Print($"[doorwarden] FAIL {what}");
    }

    private void Finish()
    {
        GD.Print(_faults.Count == 0 ? "[doorwarden] RESULT: ok" : $"[doorwarden] RESULT: FAILED ({_faults.Count})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }
}
