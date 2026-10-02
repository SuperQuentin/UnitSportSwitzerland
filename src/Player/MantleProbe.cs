using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --mantlecheck [--at E,N]</c>: runs a player at a 1.4 m block (must
/// climb onto it), a 2.8 m one (reachable only thanks to the jump: must climb) and a 3.6 m one
/// (must not). Then the same under a ceiling built like an interior's (one face, solid from both
/// sides): a car in a garage (no room on its roof: must stay down), a wall up to a room's ceiling,
/// and a block under a high ceiling (room to stand: must climb). The feet must never get near the
/// ceiling. Drives the real input actions, so it tests the whole path from the button to the
/// pull-up; exits non-zero if any verdict is wrong.
/// </summary>
public partial class MantleProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private StaticBody3D? _block, _ceiling;
    private int _case;
    private double _t, _wait;
    private float _ground, _highest;
    private bool _ok = true;
    // a jump lifts the feet ~0.9 m and the hands reach 2.1 m above them: ~3 m is the limit.
    // Ceiling 0 = open sky.
    private static readonly (float Block, float Ceiling, bool Climb)[] Cases =
    {
        (1.4f, 0, true), (2.8f, 0, true), (3.6f, 0, false),
        (1.4f, 2.6f, false),   // a car roof under a garage ceiling
        (2.5f, 2.5f, false),   // a wall up to a room's ceiling
        (1.4f, 3.5f, true),    // room enough to stand on it
    };

    public MantleProbe(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public static bool Requested() => CmdArgs.Has("--mantlecheck");

    public override void _PhysicsProcess(double delta)
    {
        _wait += delta;
        if (_wait > 120) { GD.Print("[mantle] TIMEOUT"); Finish(2); return; }

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
        _highest = Mathf.Max(_highest, _player.GlobalPosition.Y - _blockBase);
        // walk at it, press jump just short of it, and again once on top (into the ceiling)
        if (_t > 0.05) Input.ActionPress(PlayerInput.MoveForward);
        if ((_t > 0.55 && _t < 0.65) || (_t > 1.4 && _t < 1.5)) Input.ActionPress(PlayerInput.Jump);
        else Input.ActionRelease(PlayerInput.Jump);

        if (_t < 2.5) return;
        Input.ActionRelease(PlayerInput.MoveForward);

        var c = Cases[_case];
        float rise = _player.GlobalPosition.Y - _blockBase;
        bool onTop = Mathf.Abs(rise - c.Block) < 0.25f;
        // the head may touch the ceiling, never pass it
        bool through = c.Ceiling > 0 && _highest > c.Ceiling - 1.5f;
        bool right = onTop == c.Climb && !through;
        GD.Print($"[mantle] block {c.Block:0.0} m" + (c.Ceiling > 0 ? $" under a {c.Ceiling:0.0} m ceiling" : "")
            + $": feet {rise:+0.00;-0.00} m above its base (highest {_highest:+0.00;-0.00}) -> "
            + (through ? "THROUGH THE CEILING" : onTop ? "ON TOP" : "not on top") + (right ? "  ok" : "  WRONG"));
        _ok &= right;

        _block.QueueFree();
        _block = null;
        _ceiling?.QueueFree();
        _ceiling = null;
        if (++_case >= Cases.Length)
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
        var c = Cases[_case];
        var p = _player!.GlobalPosition;
        _blockBase = p.Y;
        _highest = 0;
        // 1.6 m ahead along -Z, which is where the player faces at spawn
        _block = new StaticBody3D();
        _block.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, c.Block, 3) } });
        _block.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(4, c.Block, 3) } });
        AddChild(_block);
        _block.GlobalPosition = new Vector3(p.X, p.Y + c.Block * 0.5f, p.Z - 1.6f - 1.5f);
        if (c.Ceiling > 0)
        {
            // as InteriorNode builds it: a single face (seen from below), solid from both sides
            const float r = 8f;
            _ceiling = new StaticBody3D();
            _ceiling.AddChild(new CollisionShape3D
            {
                Shape = new ConcavePolygonShape3D
                {
                    Data = new[]
                    {
                        new Vector3(-r, 0, -r), new Vector3(r, 0, -r), new Vector3(r, 0, r),
                        new Vector3(-r, 0, -r), new Vector3(r, 0, r), new Vector3(-r, 0, r),
                    },
                    BackfaceCollision = true,
                },
            });
            AddChild(_ceiling);
            _ceiling.GlobalPosition = new Vector3(p.X, p.Y + c.Ceiling, p.Z);
        }
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
