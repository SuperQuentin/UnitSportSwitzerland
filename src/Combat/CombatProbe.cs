using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Combat;

/// <summary>
/// <c>godot --path . -- --combatcheck[,out.png] [--at E,N]</c>
///
/// <para>
/// Puts the player in a plane 400 m up, flies a target drone straight ahead of the guns at the
/// plane's own speed, holds the real <c>fire</c> action and counts what the rounds did. Exits
/// non-zero if no round hit or the drone was never destroyed — which is what tracers that tunnel,
/// guns pointing the wrong way, or a broken damage path all look like.
/// </para>
/// </summary>
public partial class CombatProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private FootPlayer? _player;
    private double _t, _wait, _sinceKill = -1;
    private bool _started, _done, _noGround;

    public CombatProbe(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--combatcheck"))
            {
                var parts = a.Split(',');
                return (true, parts.Length > 1 ? parts[1] : null);
            }
        return (false, null);
    }

    private static CombatManager Combat => CombatManager.Instance!;

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_wait > 150) { GD.Print("[combatcheck] TIMEOUT"); Finish(2); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);

        if (_player == null)
        {
            // a fresh clone has no terrain at all; the fight is 400 m up, so fly over the void
            if (!_chunks.TryGetHeight(at, out float g))
            {
                if (_wait < 20) return;
                GD.Print("[combatcheck] no terrain under the spawn; flying over the void");
                g = at.Y;
                _noGround = true;
                // a pad to stand on, since a plane can only be boarded from the ground
                var pad = new StaticBody3D { Name = "Pad" };
                pad.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60, 1, 60) } });
                AddChild(pad);
                pad.GlobalPosition = at + Vector3.Down * 0.5f;
            }
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
            if (_noGround) _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);   // onto the pad
            _player.Announced += (text, _) => GD.Print($"[combatcheck] announce: {text}");
            Combat.LocalPlayer = () => _player;
            Combat.DronesEnabled = false;   // one target, placed by hand
            return;
        }
        if (!_started)
        {
            if ((!_noGround && !_chunks.HasCollisionAt(at)) || !_player.IsOnFloor()) return;
            _player.SetRide(RideKind.Plane);
            _player.DebugLaunch(_player.GlobalPosition + Vector3.Up * 400f, new Vector3(0, 0, -50));
            _started = true;
            return;
        }

        _t += delta;
        if (_t is > 1.0 and < 1.1 && Combat.GetTree().GetNodesInGroup(TargetDrone.Group).Count == 0)
        {
            // on the boresight 150 m out, keeping station with the plane
            var fwd = -_player.Flight.Attitude.Orthonormalized().Z;
            var drone = TargetDrone.Straight(Combat.AimPoint - fwd * 250f, _player.Flight.Velocity);
            Combat.AddChild(drone);
            GD.Print($"[combatcheck] drone placed {drone.GlobalPosition.DistanceTo(_player.GlobalPosition):F0} m ahead"
);
        }

        bool firing = _t > 1.5 && Combat.Kills == 0 && _t < 12;
        if (firing) Input.ActionPress(PlayerInput.Fire); else Input.ActionRelease(PlayerInput.Fire);

        if (Combat.Kills > 0 && _sinceKill < 0) _sinceKill = 0;
        if (_sinceKill >= 0) _sinceKill += delta;
        // a moment after the kill, so the explosion is in the picture
        if (_sinceKill > 0.35 || _t > 14) End();
    }

    private void End()
    {
        var c = Combat;
        bool alive = _player!.Ride == RideKind.Plane;
        GD.Print($"[combatcheck] END shots {c.Shots}, hits {c.Hits}, kills {c.Kills}, ammo {c.Ammo}, "
            + $"plane {(alive ? "intact" : "lost")}");
        bool ok = c.Hits > 0 && c.Kills > 0 && alive;
        GD.Print(ok ? "[combatcheck] RESULT: ok" : "[combatcheck] RESULT: FAILED");

        if (_shot != null)
        {
            var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(_shot) == Error.Ok) GD.Print($"[combatcheck] wrote {_shot}");
        }
        Finish(ok ? 0 : 1);
    }

    private void Finish(int code)
    {
        Input.ActionRelease(PlayerInput.Fire);
        _done = true;
        GetTree().Quit(code);
    }
}
