using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --voidcheck [--at E,N]</c>
///
/// <para>
/// Does a player who glitched through the world get back onto the ground (#150)? A body stands at
/// the spawn long enough to record a safe spot, then is pushed out of the world four ways:
/// 30 m under the terrain on foot, the same on a bike, 600 m down where no terrain height is known
/// at all (a tile that has not streamed), and under the floor of an interior. Each time it must be
/// back on its feet above the ground — or, for the void, back at its safe spot — within a second.
/// Non-zero exit on any that is not.
/// </para>
/// </summary>
public partial class VoidProbe : Node
{
    public static bool Requested() => Array.IndexOf(OS.GetCmdlineUserArgs(), "--voidcheck") >= 0;

    private const float Deep = 30f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private Vector3 _standing;
    private double _wait, _t;
    private int _stage;
    private int _failures;
    private bool _done;

    public VoidProbe(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        // after the player's own step, so a check sees what the rescue did
        ProcessPriority = 1;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_wait > 120) { GD.Print("[voidcheck] TIMEOUT"); Finish(2); return; }

        if (_player == null)
        {
            var (e, n) = SpawnPoint.ParseTarget();
            var at = _origin.ToWorld(e, n, 0);
            if (!_chunks.TryGetHeight(at, out float g)) return;
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
            GD.Print($"[voidcheck] player at LV95 {e:F0}/{n:F0}, ground {g:F1} m");
            return;
        }

        _t += delta;
        var p = _player;
        switch (_stage)
        {
            case 0:   // stand long enough for a safe spot to be recorded (2 s on the floor)
                if (!p.IsOnFloor()) _t = 0;
                if (_t < 3) return;
                _standing = p.GlobalPosition;
                Drop(p, Ground(p) - Deep, "on foot, 30 m under the terrain");
                return;
            case 1:
                if (_t < 1) return;
                CheckOnGround(p, "on foot");
                p.SetRide(RideKind.RoadBike);
                Next();
                return;
            case 2:
                if (_t < 1) return;
                Drop(p, Ground(p) - Deep, "on a bike, 30 m under the terrain");
                return;
            case 3:
                if (_t < 1.5) return;
                CheckOnGround(p, "on a bike");
                p.SetRide(RideKind.OnFoot);
                p.PlaceAt(_standing, 0f);
                Next();
                return;
            case 4:   // back on foot at the spawn, and a fresh safe spot there
                if (!p.IsOnFloor()) _t = 0;
                if (_t < 3) return;
                _standing = p.GlobalPosition;
                if (FarFromTerrain() is not { } far)
                {
                    GD.Print("[voidcheck] FAIL no unstreamed ground found to fall into");
                    _failures++;
                    Next(2);
                    return;
                }
                p.GlobalPosition = far;
                p.Velocity = Vector3.Zero;
                GD.Print($"[voidcheck] into the void, no terrain height known at {far.Round()}");
                Next();
                return;
            case 5:
                if (_t < 1) return;
                {
                    var flat = new Vector2(p.GlobalPosition.X - _standing.X, p.GlobalPosition.Z - _standing.Z).Length();
                    bool ok = flat < 3f && p.GlobalPosition.Y > _standing.Y - 1.5f;
                    Report(ok, $"void: back {flat:F1} m from the safe spot, {p.GlobalPosition.Y - _standing.Y:+0.0;-0.0} m in height");
                }
                Next();
                return;
            case 6:   // an interior: 3 km under the street, and nothing under its floor
                {
                    float floor = Interiors.InteriorManager.InteriorBaseY;
                    var inside = new Vector3(_standing.X, floor + 1f, _standing.Z);
                    p.EnterInterior("voidcheck", inside, 0f);
                    p.GlobalPosition = inside with { Y = floor - 50f };
                    GD.Print($"[voidcheck] indoors, dropped to {floor - 50f:F0} m, under the floor at {floor:F0}");
                }
                Next();
                return;
            case 7:   // the very next step: gravity has barely started on it again
                {
                    float floor = Interiors.InteriorManager.InteriorBaseY;
                    bool ok = p.Indoors && p.GlobalPosition.Y > floor;
                    Report(ok, $"indoors: at {p.GlobalPosition.Y:F1} m, still inside={p.Indoors}");
                    p.LeaveInterior(_standing, 0f);
                }
                Next();
                return;
            case 8:
                if (_t < 1) return;
                {
                    bool ok = !p.Indoors && p.GlobalPosition.Y > Ground(p) - 0.5f;
                    Report(ok, $"back outside at {p.GlobalPosition.Y - Ground(p):+0.00;-0.00} m over the ground");
                }
                GD.Print(_failures == 0
                    ? "[voidcheck] RESULT: every fall through the world ended back on the ground"
                    : $"[voidcheck] RESULT: FAILED — {_failures} fall(s) not rescued");
                Finish(_failures == 0 ? 0 : 1);
                return;
        }
    }

    private float Ground(FootPlayer p) => _chunks.TryGetHeight(p.GlobalPosition, out float g) ? g : float.NaN;

    private void Drop(FootPlayer p, float y, string what)
    {
        p.GlobalPosition = p.GlobalPosition with { Y = y };
        p.Velocity = Vector3.Zero;
        GD.Print($"[voidcheck] {what}");
        Next();
    }

    private void CheckOnGround(FootPlayer p, string what)
    {
        float over = p.GlobalPosition.Y - Ground(p);
        Report(over > -0.5f && over < 3f, $"{what}: {over:+0.00;-0.00} m over the ground, onFloor={p.IsOnFloor()}");
    }

    /// <summary>A point far enough away that no tile under it is loaded, 600 m below sea level.</summary>
    private Vector3? FarFromTerrain()
    {
        foreach (float d in new[] { 60_000f, 150_000f, 400_000f })
        {
            var at = _standing + new Vector3(d, 0, 0);
            if (!_chunks.TryGetHeight(at, out _)) return at with { Y = -600f };
        }
        return null;
    }

    private void Report(bool ok, string line)
    {
        if (!ok) _failures++;
        GD.Print($"[voidcheck] {(ok ? "ok  " : "FAIL")} {line}");
    }

    private void Next(int by = 1)
    {
        _stage += by;
        _t = 0;
    }

    private void Finish(int code)
    {
        _done = true;
        SetPhysicsProcess(false);
        GetTree().Quit(code);
    }
}
