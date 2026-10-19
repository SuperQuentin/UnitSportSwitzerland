using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --slopewalk [--at E,N]</c> (#883): how walking up real mountain ground
/// feels, measured. Finds slopes of rising steepness within <see cref="SearchM"/> of the target on
/// the drawn ground, stands a player 4 m below each and runs it straight uphill for
/// <see cref="RunSeconds"/> with the real input actions. Per slope it prints how far up it got, how
/// often it lost and found the floor (each flip is a fall pose, a landing dip, a thump), the
/// landings raised, and how far the feet were above or below the drawn ground (floating, sinking).
/// RESULT ok when the floor is never lost on ground a player can stand on (under 45° over ±2 m). The
/// feet's gap to the drawn ground is reported, not judged: above it they stand by the capsule's rounded
/// bottom on a slope, below it on a road the blend cut into the raw heights read here.
/// </summary>
public partial class SlopeWalkProbe : Node
{
    private const float SearchM = 150f, StepM = 3f, RunSeconds = 8f;
    private static readonly float[] Bands = { 25f, 40f, 48f, 56f, 65f, 78f };

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private readonly List<(float Angle, Vector3 Start, Vector3 Up)> _cases = new();
    private int _case = -1, _flips, _standFlips, _landings;
    private double _t, _wait;
    private bool _wasFloor, _ok = true, _searched;
    private float _gapMin, _gapMax, _airTime;
    private Vector3 _from;
    private float _lastY;

    public SlopeWalkProbe(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public static bool Requested() => CmdArgs.Has("--slopewalk");

    public override void _PhysicsProcess(double delta)
    {
        _wait += delta;
        if (_wait > 240) { GD.Print("[slopewalk] TIMEOUT"); Finish(2); return; }
        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);

        if (_player == null)
        {
            // the player is what the collision is built round
            if (!_chunks.TryGetHeight(at, out float ground)) return;
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            _player.Landed += _ => { if (_t > 0.3) _landings++; };
            AddChild(_player);
            _player.GlobalPosition = at with { Y = ground + 1f };
            return;
        }
        if (!_searched)
        {
            // every height read from the full-resolution grid, not a coarse ring's still in place
            if (!_chunks.HasCollisionAt(at) || !_chunks.SettledNear(at, 1)) return;
            Search(at);
            _searched = true;
            if (_cases.Count == 0) { GD.Print("[slopewalk] no slopes found"); Finish(2); return; }
            Next();
            return;
        }

        var c = _cases[_case];
        if (_t == 0 && !(_chunks.HasCollisionAt(c.Start) && _player.IsOnFloor()))
        {
            // settle on the ground first; the walk starts from standing
            _player.LookYaw = Mathf.Atan2(-c.Up.X, -c.Up.Z);
            return;
        }
        if (_t == 0)
        {
            _from = _player.GlobalPosition;
            if (_from.DistanceTo(c.Start) > 1.5f)
            {
                // it fell or slid off before the walk: not a slope to judge the walk on
                GD.Print($"[slopewalk] {c.Angle,2:0}° slope: skipped, the body did not stay at its start ({_from.DistanceTo(c.Start):0.0} m off)");
                Next();
                return;
            }
        }
        _t += delta;
        _player.LookYaw = Mathf.Atan2(-c.Up.X, -c.Up.Z);
        Input.ActionPress(PlayerInput.MoveForward);
        Input.ActionPress(PlayerInput.Sprint);

        if (Mathf.Abs(_player.GlobalPosition.Y - _lastY) > 2f && _t > delta)
            GD.Print($"[slopewalk]   JUMP at t {_t:0.00}: y {_lastY:0.00} -> {_player.GlobalPosition.Y:0.00}");
        _lastY = _player.GlobalPosition.Y;
        bool floor = _player.IsOnFloor();
        if (_t > 0.3 && floor != _wasFloor)
        {
            _flips++;
            // a floor lost on ground you can stand on is the stutter; off steep ground it is a fall
            if (!floor && SlopeAt(_player.GlobalPosition) < 45f) _standFlips++;
        }
        _wasFloor = floor;
        if (!floor) _airTime += (float)delta;
        if (floor && _chunks.TryGetHeight(_player.GlobalPosition, out float g))
        {
            float gap = _player.GlobalPosition.Y - g;
            _gapMin = Mathf.Min(_gapMin, gap);
            _gapMax = Mathf.Max(_gapMax, gap);
        }
        if (CmdArgs.Has("--trace") && (int)(_t * 60) % 6 == 0)
        {
            var p = _player.GlobalPosition;
            string fl = floor ? $"floor {Mathf.RadToDeg(_player.GetFloorNormal().AngleTo(Vector3.Up)),2:0}°" : "air     ";
            string wall = _player.IsOnWall() ? $" wall {Mathf.RadToDeg(_player.GetWallNormal().AngleTo(Vector3.Up)):0}°" : "";
            float gnd = _chunks.TryGetHeight(p, out float gg) ? gg : float.NaN;
            GD.Print($"[slopewalk]   t {_t:0.0} y {p.Y:0.00} ground {gnd:0.00} v {_player.Velocity.X:0.00},{_player.Velocity.Y:0.00},{_player.Velocity.Z:0.00} {fl}{wall} slides {_player.GetSlideCollisionCount()}");
        }
        if (_t < RunSeconds) return;

        Input.ActionRelease(PlayerInput.MoveForward);
        Input.ActionRelease(PlayerInput.Sprint);
        var moved = _player.GlobalPosition - _from;
        float up = moved.X * c.Up.X + moved.Z * c.Up.Z;
        bool bad = _standFlips > 0;
        _ok &= !bad;
        GD.Print($"[slopewalk] {c.Angle,2:0}° slope: {up,5:0.0} m along, climbed {moved.Y,5:+0.0;-0.0} m, "
            + $"floor lost/found {_flips}x ({_standFlips} lost on standable ground), air {_airTime:0.0} s, landings {_landings}, "
            + $"feet {_gapMin:+0.00;-0.00}..{_gapMax:+0.00;-0.00} m off the drawn ground" + (bad ? "  BAD" : ""));
        Next();
    }

    /// <summary>The slope nearest the target in each band, from the drawn ground on a 3 m lattice.</summary>
    private void Search(Vector3 at)
    {
        var found = new (float D, Vector3 P, Vector3 Up)?[Bands.Length];
        for (float dz = -SearchM; dz <= SearchM; dz += StepM)
            for (float dx = -SearchM; dx <= SearchM; dx += StepM)
            {
                var p = at + new Vector3(dx, 0, dz);
                if (!Height(p, out float h) || !Height(p + new Vector3(StepM, 0, 0), out float hx)
                    || !Height(p - new Vector3(StepM, 0, 0), out float hx0) || !Height(p + new Vector3(0, 0, StepM), out float hz)
                    || !Height(p - new Vector3(0, 0, StepM), out float hz0)) continue;
                var grad = new Vector2(hx - hx0, hz - hz0) / (2 * StepM);
                float angle = Mathf.RadToDeg(Mathf.Atan(grad.Length()));
                var dir = new Vector3(grad.X, 0, grad.Y).Normalized();
                // the slope holds for the 4 m run-up and 10 m above it, not one crease
                if (!Height(p - dir * 4f, out float below) || !Height(p + dir * 10f, out float above)) continue;
                if (h - below < 1f || Mathf.RadToDeg(Mathf.Atan((above - h) / 10f)) < angle - 8f) continue;
                // and rises all the way, metre by metre: no ledge to fall off, no cliff at the start
                if (!Rises(p - dir * 4f, dir, 14)) continue;
                for (int b = 0; b < Bands.Length; b++)
                    if (Mathf.Abs(angle - Bands[b]) < 3f && (found[b] == null || dx * dx + dz * dz < found[b]!.Value.D))
                        found[b] = (dx * dx + dz * dz, p - dir * 4f, dir);
            }
        for (int b = 0; b < Bands.Length; b++)
            if (found[b] is { } f)
            {
                Height(f.P, out float y);
                _cases.Add((Bands[b], f.P with { Y = y }, f.Up));
                var (fe, fn) = _origin.ToLv95(f.P);
                GD.Print($"[slopewalk] {Bands[b]:0}° slope from LV95 {fe:0},{fn:0}");
            }
            else GD.Print($"[slopewalk] no {Bands[b]:0}° slope within {SearchM:0} m");
    }

    private bool Height(Vector3 p, out float h) => _chunks.TryGetHeight(p, out h);

    /// <summary>The ground's slope at <paramref name="p"/> over ±2 m, as the walker judges it (FootPlayer.TooSteep).</summary>
    private float SlopeAt(Vector3 p)
    {
        if (!Height(p + new Vector3(2, 0, 0), out float e) || !Height(p - new Vector3(2, 0, 0), out float w)
            || !Height(p + new Vector3(0, 0, 2), out float s) || !Height(p - new Vector3(0, 0, 2), out float n)) return 90f;
        return Mathf.RadToDeg(Mathf.Atan(new Vector2(e - w, s - n).Length() / 4f));
    }

    private bool Rises(Vector3 from, Vector3 dir, int metres)
    {
        if (!Height(from, out float last)) return false;
        for (int k = 1; k <= metres; k++)
        {
            if (!Height(from + dir * k, out float h) || h < last - 0.2f) return false;
            last = Mathf.Max(last, h);
        }
        return true;
    }

    private void Next()
    {
        if (++_case >= _cases.Count)
        {
            GD.Print(_ok ? "[slopewalk] RESULT: ok" : "[slopewalk] RESULT: FAILED");
            Finish(_ok ? 0 : 1);
            return;
        }
        var c = _cases[_case];
        var ray = _player!.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(c.Start + Vector3.Up * 300f, c.Start + Vector3.Down * 300f));
        GD.Print($"[slopewalk] {c.Angle:0}° start: drawn ground {c.Start.Y:0.00}, collision {(ray.Count > 0 ? ((Vector3)ray["position"]).Y.ToString("0.00") : "none")}");
        // a fresh body per slope: nothing of the last walk (a fall, a placement) carries over
        var old = _player;
        _player = new FootPlayer { Name = "Probe" + _case, Terrain = _chunks };
        _player.Landed += _ => { if (_t > 0.3) _landings++; };
        AddChild(_player);
        _player.GlobalPosition = c.Start + Vector3.Up * 0.3f;
        old.QueueFree();
        _t = 0;
        _flips = _standFlips = _landings = 0;
        _airTime = 0;
        _gapMin = float.MaxValue;
        _gapMax = float.MinValue;
        _wasFloor = true;
    }

    private void Finish(int code)
    {
        Input.ActionRelease(PlayerInput.MoveForward);
        Input.ActionRelease(PlayerInput.Sprint);
        SetPhysicsProcess(false);
        GetTree().Quit(code);
    }
}
