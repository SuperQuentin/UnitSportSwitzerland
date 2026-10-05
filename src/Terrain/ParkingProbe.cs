using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Terrain;

/// <summary>
/// <c>godot --path . -- --chunks fixture:parking --parkingcheck[,SHOT.png]</c> — a laid-out car
/// park (#499) in the running game, on the fixture course so it needs no terrain data.
///
/// <para>
/// What it checks, in order: the tile serves bays, a pad, planters and the props; the bays are
/// <b>not</b> square to the world (the course turns its lot 14°, which is the bug #499 fixes); a
/// body dropped over a bay lands on the pad rather than falling through it or standing on bare
/// ground; a dormant car is solid; and aiming at one wakes it into a real <see cref="VehicleBody"/>.
/// Then a shot from above, which is the only way to judge whether a layout looks like a car park.
/// </para>
/// </summary>
public partial class ParkingProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private double _t;
    private int _phase;
    private readonly List<string> _fail = new();

    private List<ParkingBay> _bays = new();
    private TileId _tile;
    private int _pads, _planters, _walks, _marks;
    private readonly HashSet<PointPropType> _props = new();
    private Vector3 _floorAt;
    private float _floorY, _padY;
    private VehicleSlot? _slot;
    private Camera3D? _camera;
    private bool _shotTaken;
    private Camera3D? _gateCamera;
    private FootPlayer? _player;

    public ParkingProbe(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        Name = "ParkingProbe";
        _chunks = chunks;
        _origin = origin;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--parkingcheck");

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        if (_t > 180) { GD.Print("[parking] RESULT: FAILED (timeout)"); Finish(2); return; }

        switch (_phase)
        {
            case 0: Read(); return;
            case 1: Floor(); return;
            case 2: Solid(); return;
            case 3: Wake(); return;
            case 4: Shoot(); return;
        }
    }

    /// <summary>Phase 0: what the tiles actually serve.</summary>
    private void Read()
    {
        if (_t < 3 || _chunks.Source is not { } source) return;

        var loaded = new List<(TileId Id, int Stride)>();
        _chunks.ListTiles(loaded);
        foreach (var (id, _) in loaded)
        {
            var tile = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
            if (tile is null || tile.Parking.Count == 0) continue;
            _tile = id;
            _bays = tile.Parking;
            _pads = tile.AreaProps.Count(a => a.Type == AreaPropType.ParkingPad);
            _planters = tile.AreaProps.Count(a => a.Type == AreaPropType.ParkingIsland);
            _walks = tile.AreaProps.Count(a => a.Type == AreaPropType.Sidewalk);
            _marks = tile.Paint.Count;
            foreach (var p in tile.PointProps) _props.Add(p.Type);
            break;
        }
        if (_bays.Count == 0) { if (_t > 25) Fail("no tile served any parking bay"); return; }

        GD.Print($"[parking] tile {_tile.E}_{_tile.N}: {_bays.Count} bays, {_pads} pads, "
            + $"{_planters} planters, {_walks} walks, {_marks} markings, props {string.Join('/', _props)}");

        if (_pads == 0) Fail("no pad");
        if (_planters == 0) Fail("no planter");
        if (_marks < _bays.Count) Fail($"only {_marks} markings for {_bays.Count} bays");

        // The course turns its lot 14 deg off east, so a bay must face 14 or 194 deg as a Godot
        // heading — ACROSS the rows. Measuring |angle| off a world axis instead was sign-blind and
        // passed happily while every car sat 28 deg askew in its bay, which is how that shipped.
        foreach (var b in _bays)
        {
            double deg = b.Heading * 180 / Math.PI;
            double off = Math.Min(Math.Abs(deg - 14), Math.Abs(deg - 194));
            if (off > 1.5)
            {
                Fail($"a bay faces {deg:F1} deg, expected 14 or 194 (the lot is turned 14 deg)");
                break;
            }
        }
        Advance();
    }

    /// <summary>
    /// Phase 1: the pad is a floor, at the bay's own height. A body has to stand in the lot first:
    /// collision is queued nearest a body (`perf-collision-commits`), so with nothing there the
    /// ground under the lot is never built and a ray down it hits nothing.
    /// </summary>
    private void Floor()
    {
        // an EMPTY bay: a filled one answers with the roof of the car parked in it, which is a
        // true answer to the wrong question
        var bay = _bays[_bays.Count / 2];
        foreach (var b in _bays)
        {
            var at = _origin.ToWorld(_tile.MinE + b.X, _tile.MaxN - b.Z, b.Y);
            if (DormantVehicles.Instance?.Nearest(at) == null) { bay = b; break; }
        }
        _padY = bay.Y;
        var over = _origin.ToWorld(_tile.MinE + bay.X, _tile.MaxN - bay.Z, bay.Y);
        _floorAt = over + Vector3.Up * 0.2f;

        if (_player == null)
        {
            _player = new FootPlayer { Name = "ParkingProbeBody", Terrain = _chunks };
            AddChild(_player);
            _player.DebugLaunch(over + Vector3.Up * 1.5f, Vector3.Zero);
            _t = 0;
            return;
        }
        // hold it over the bay until the ground there is solid, as the other probes do
        if (!_chunks.HasCollisionAt(_floorAt))
        {
            _player.GlobalPosition = over + Vector3.Up * 1.5f;
            _player.Velocity = Vector3.Zero;
            if (_t > 60) { Fail("no collision over a bay after 60 s"); Advance(); }
            return;
        }
        if (_t < 1.5) return;

        var space = GetViewport().GetWorld3D().DirectSpaceState;
        // exclude our own body, or the ray hits the capsule standing in the bay rather than the pad
        var query = PhysicsRayQueryParameters3D.Create(
            _floorAt + Vector3.Up * 6f, _floorAt + Vector3.Down * 6f, uint.MaxValue,
            new Godot.Collections.Array<Rid> { _player.GetRid() });
        var hit = space.IntersectRay(query);
        if (hit.Count == 0)
        {
            if (_t > 20) { Fail("a ray down a bay hit nothing"); Advance(); }
            return;
        }

        _floorY = hit["position"].AsVector3().Y;
        float off = Mathf.Abs(_floorY - over.Y);
        string what = (hit["collider"].AsGodotObject() as Node)?.Name.ToString() ?? "?";
        GD.Print($"[parking] floor over an empty bay: {off * 100:F1} cm from the bay's own height, "
            + $"hit '{what}' (player on floor: {_player.IsOnFloor()}, "
            + $"terrain height {(_chunks.TryGetHeight(over, out float th) ? (th - over.Y).ToString("F2") : "?")} m off)");
        if (off > 0.35f) Fail($"the floor over a bay is {off:F2} m off its height");
        Advance();
    }

    /// <summary>Phase 2: a dormant car is solid, not a ghost.</summary>
    private void Solid()
    {
        if (DormantVehicles.Instance is not { } dormant)
        {
            Fail("no DormantVehicles (is --systems dormant off?)");
            Advance();
            return;
        }
        if (_t < 2) return;

        // the nearest dormant car to any bay of the lot
        foreach (var b in _bays)
        {
            var at = _origin.ToWorld(_tile.MinE + b.X, _tile.MaxN - b.Z, b.Y);
            if (dormant.Nearest(at) is { } s) { _slot = s; break; }
        }
        if (_slot is not { } slot)
        {
            if (_t > 30) { Fail("no dormant car in the lot"); Advance(); }
            return;
        }

        var centre = _origin.ToWorld(slot.E, slot.N, slot.Height) + Vector3.Up * 0.8f;
        var space = GetViewport().GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(centre + Vector3.Up * 4f, centre + Vector3.Down * 0.3f);
        bool solid = space.IntersectRay(query).Count > 0;
        GD.Print($"[parking] dormant car at bay {slot.Ordinal} ({slot.NodeName}): {(solid ? "solid" : "A GHOST")}");
        if (!solid) Fail("a dormant car has no collision");

        Advance();
    }

    /// <summary>Phase 3: waking one makes it a real vehicle under its deterministic name.</summary>
    private void Wake()
    {
        if (_slot is not { } slot) { Advance(); return; }
        if (_t < 0.5) return;

        if (_t < 1.0) { DormantVehicles.Instance?.Wake(slot); return; }

        var woken = VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(slot.NodeName);
        if (woken == null)
        {
            if (_t > 20) { Fail($"{slot.NodeName} never woke"); Advance(); }
            return;
        }

        float moved = _origin.ToWorld(slot.E, slot.N, slot.Height).DistanceTo(woken.GlobalPosition);
        GD.Print($"[parking] woke {slot.NodeName} as {woken.Kind}, {moved:F2} m from its bay, "
            + $"dormant copy gone: {DormantVehicles.Instance!.IsAwake(slot)}");
        if (moved > 3f) Fail($"the woken car stands {moved:F1} m from its bay");
        if (!DormantVehicles.Instance.IsAwake(slot)) Fail("the dormant copy is still drawn");

        Advance();
    }

    /// <summary>
    /// Phase 4: a shot from above, then a second one low over the entrance — the boom, the kiosk and
    /// the blue P are a metre and a half tall and read as nothing at all from 110 m up.
    ///
    /// <para>
    /// Two separate cameras, each made current in turn, and each grab waits for
    /// <see cref="RenderingServer.SignalName.FramePostDraw"/>. Moving one camera between shots and
    /// grabbing from <c>_PhysicsProcess</c> wrote the <b>same bytes twice</b>: the viewport texture
    /// still held the frame drawn from the first camera, even though the second was already current.
    /// </para>
    /// </summary>
    private void Shoot()
    {
        if (_shot == null) { Verdict(); return; }
        if (_capturing) return;

        if (_camera == null)
        {
            double e = _bays.Average(b => (double)_tile.MinE + b.X);
            double n = _bays.Average(b => (double)_tile.MaxN - b.Z);
            _camera = new Camera3D { Fov = 55, Position = _origin.ToWorld(e, n, _padY + 110) };
            AddChild(_camera);
            _camera.LookAt(_origin.ToWorld(e, n, _padY), Vector3.Forward);
            _camera.MakeCurrent();
            _t = 0;
            return;
        }
        if (_t < 2.5) return;   // let the rings settle under the new view

        if (!_shotTaken)
        {
            _shotTaken = true;
            Capture(_shot, () =>
            {
                if (Gate() is { } at)
                {
                    // low and off to one side, so the boom, its housing and the kiosk stand clear
                    var near = new Camera3D { Fov = 50, Position = at + new Vector3(9f, 4.5f, 9f) };
                    AddChild(near);
                    near.LookAt(at + Vector3.Up * 0.8f, Vector3.Up);
                    near.MakeCurrent();
                    _gateCamera = near;
                    _t = 0;
                }
                else GD.Print("[parking] no gate prop to shoot");
            });
            return;
        }

        if (_gateCamera != null)
        {
            _gateCamera = null;
            Capture(_shot.Replace(".png", "_entrance.png"), Verdict);
            return;
        }
        Verdict();
    }

    private bool _capturing;

    /// <summary>Saves the viewport after the next frame is actually drawn, then runs <paramref name="then"/>.</summary>
    private async void Capture(string file, Action? then)
    {
        _capturing = true;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!IsInsideTree()) return;
        GD.Print(GetViewport().GetTexture().GetImage().SavePng(file) == Error.Ok
            ? $"[parking] wrote {file}" : $"[parking] could not write {file}");
        _capturing = false;
        then?.Invoke();
    }

    /// <summary>Where the way in is: the barrier if the lot is paid, else the give-way line's bay.</summary>
    private Vector3? Gate()
    {
        if (_chunks.Source is not { } source) return null;
        var tile = source.LoadRoadsAsync(_tile).GetAwaiter().GetResult();
        if (tile is null) return null;
        // the barrier first, then the kiosk, then the sign: a trolley shelter is not the way in
        foreach (var want in (PointPropType[])
                 [PointPropType.TicketBarrier, PointPropType.TicketKiosk, PointPropType.ParkingSign])
            foreach (var p in tile.PointProps)
                if (p.Type == want)
                    return _origin.ToWorld(_tile.MinE + p.X, _tile.MaxN - p.Z, p.Y);
        return null;
    }

    private void Advance()
    {
        _phase++;
        _t = 0;
    }

    private void Fail(string why)
    {
        if (_fail.Contains(why)) return;
        _fail.Add(why);
        GD.Print($"[parking] FAIL: {why}");
    }

    private void Verdict()
    {
        GD.Print(_fail.Count == 0
            ? $"[parking] RESULT: ok — {_bays.Count} bays laid out, drivable, a car woken"
            : $"[parking] RESULT: FAILED ({string.Join("; ", _fail)})");
        Finish(_fail.Count == 0 ? 0 : 1);
    }

    private void Finish(int code)
    {
        SetPhysicsProcess(false);
        GetTree().Quit(code);
    }
}
