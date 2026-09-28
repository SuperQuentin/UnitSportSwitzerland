using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --mantlecheck [--at E,N]</c>: runs a player at a 1.4 m block (must
/// climb onto it), a 2.8 m one (reachable only thanks to the jump: must climb) and a 3.6 m one
/// (must not). Drives the real input actions, so it tests the whole path from the button to the
/// pull-up; exits non-zero if any verdict is wrong.
/// </summary>
public partial class MantleProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private StaticBody3D? _block;
    private int _case;
    private double _t, _wait;
    private float _ground;
    private bool _ok = true;
    // a jump lifts the feet ~0.9 m and the hands reach 2.1 m above them: ~3 m is the limit
    private static readonly float[] Heights = { 1.4f, 2.8f, 3.6f };

    public MantleProbe(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public static bool Requested() => OS.GetCmdlineUserArgs().Contains("--mantlecheck");

    public override void _PhysicsProcess(double delta)
    {
        _wait += delta;
        if (_wait > 90) { GD.Print("[mantle] TIMEOUT"); Finish(2); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);

        if (_player == null)
        {
            if (!_chunks.TryGetHeight(at, out _ground)) return;
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, _ground + 1f, at.Z);
            return;
        }
        if (_block == null)
        {
            // each case starts standing still on the ground
            if (!_chunks.HasCollisionAt(at) || !_player.IsOnFloor()) return;
            StartCase();
            return;
        }

        _t += delta;
        // walk at it, and press jump just short of it
        if (_t > 0.05) Input.ActionPress(PlayerInput.MoveForward);
        if (_t > 0.55 && _t < 0.65) Input.ActionPress(PlayerInput.Jump);
        else Input.ActionRelease(PlayerInput.Jump);

        if (_t < 2.5) return;
        Input.ActionRelease(PlayerInput.MoveForward);

        float h = Heights[_case];
        float rise = _player.GlobalPosition.Y - _blockBase;
        bool onTop = Mathf.Abs(rise - h) < 0.25f;
        bool want = h <= 3.0f;
        GD.Print($"[mantle] block {h:0.0} m: feet {rise:+0.00;-0.00} m above its base -> "
            + (onTop ? "ON TOP" : "not on top") + (onTop == want ? "  ok" : "  WRONG"));
        _ok &= onTop == want;

        _block.QueueFree();
        _block = null;
        if (++_case >= Heights.Length)
        {
            GD.Print(_ok ? "[mantle] RESULT: ok" : "[mantle] RESULT: FAILED");
            Finish(_ok ? 0 : 1);
            return;
        }
        // back to the start for the next block
        _player.GlobalPosition = new Vector3(at.X, _ground + 0.5f, at.Z);
        _player.Velocity = Vector3.Zero;
    }

    private float _blockBase;

    private void StartCase()
    {
        float h = Heights[_case];
        var p = _player!.GlobalPosition;
        _blockBase = p.Y;
        // 1.6 m ahead along -Z, which is where the player faces at spawn
        _block = new StaticBody3D();
        _block.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, h, 3) } });
        _block.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(4, h, 3) } });
        AddChild(_block);
        _block.GlobalPosition = new Vector3(p.X, p.Y + h * 0.5f, p.Z - 1.6f - 1.5f);
        _t = 0;
    }

    private void Finish(int code)
    {
        Input.ActionRelease(PlayerInput.MoveForward);
        Input.ActionRelease(PlayerInput.Jump);
        SetPhysicsProcess(false);
        GetTree().Quit(code);
    }
}
