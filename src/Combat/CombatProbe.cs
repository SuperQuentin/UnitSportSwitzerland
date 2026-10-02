using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Combat;

/// <summary>
/// <c>godot --path . -- --combatcheck[,out.png] [--craft paraglider] [--at E,N]</c>
///
/// <para>
/// Puts the player in a plane 400 m up, flies a target drone straight ahead of the guns at the
/// plane's own speed, holds the real <c>fire</c> action and counts what the rounds did. Exits
/// non-zero if no round hit or the drone was never destroyed — which is what tracers that tunnel,
/// guns pointing the wrong way, or a broken damage path all look like.
/// </para>
///
/// <para>
/// <c>--craft paraglider</c>: the shooter flies a paraglider and fires its hand gun, aimed by the
/// camera, at the WING of a second paraglider pilot 60 m ahead — the wing has no collider of its
/// own, so this checks both that a paraglider can fire and that its fabric can be hit. Passes when
/// a round lands and the target pilot lost health.
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
    private readonly bool _paraglider = System.Array.IndexOf(CmdArgs.All, "paraglider") > 0
        && CmdArgs.Has("--craft");
    private FootPlayer? _target;
    private RideKind Craft => _paraglider ? RideKind.Paraglider : RideKind.Plane;

    public CombatProbe(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--combatcheck");

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
            if (_paraglider)
            {
                // the target pilot stands beside the shooter; it can only strap in on the ground
                _target = new FootPlayer { Name = "Target", Terrain = _chunks };
                AddChild(_target);
                _target.GlobalPosition = _player.GlobalPosition + new Vector3(4f, 0, 0);
                if (_noGround) _target.DebugLaunch(_target.GlobalPosition, Vector3.Zero);
            }
            Combat.LocalPlayer = () => _player;
            Combat.DronesEnabled = false;   // one target, placed by hand
            return;
        }
        if (!_started)
        {
            if ((!_noGround && !_chunks.HasCollisionAt(at)) || !_player.IsOnFloor()
                || (_target != null && !_target.IsOnFloor())) return;
            _player.SetRide(Craft);
            _player.DebugLaunch(_player.GlobalPosition + Vector3.Up * 400f, new Vector3(0, 0, _paraglider ? -10 : -50));
            if (_target != null && !_target.SetRide(RideKind.Paraglider))
            {
                GD.Print("[combatcheck] target could not strap into its paraglider");
                Finish(1);
                return;
            }
            _started = true;
            return;
        }

        _t += delta;
        if (_paraglider) { StepParaglider(); return; }
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

    /// <summary>
    /// Once the shooter's camera has settled, put the target's WING on its line of sight 80 m out,
    /// flying the shooter's own velocity so the two keep station; then hold the trigger.
    /// </summary>
    private void StepParaglider()
    {
        if (_t is > 0.8 and < 0.85)
        {
            var cam = _player!.Camera;
            var wing = cam.GlobalPosition - cam.GlobalBasis.Z * 80f;
            _target!.DebugLaunch(wing - _target.GlobalBasis * new Vector3(0, 7.6f, 0), _player.Flight.Velocity);
            GD.Print($"[combatcheck] target wing placed {wing.DistanceTo(_player.GlobalPosition):F0} m out, ride {_target.Ride}");
        }
        bool firing = _t > 1.0 && Combat.Hits < 5 && _t < 10;
        if (firing) Input.ActionPress(PlayerInput.Fire); else Input.ActionRelease(PlayerInput.Fire);
        if (Combat.Hits >= 5 || _t > 11) End();
    }

    private void End()
    {
        var c = Combat;
        bool alive = _player!.Ride == Craft;
        GD.Print($"[combatcheck] END {Craft}: shots {c.Shots}, hits {c.Hits}, kills {c.Kills}, ammo {c.Ammo}, "
            + $"craft {(alive ? "intact" : "lost")}"
            + (_target != null ? $", target pilot health {_target.Health:F0}" : ""));
        bool ok = _paraglider
            ? c.Shots > 0 && c.Hits > 0 && _target!.Health < FootPlayer.MaxHealth && alive
            : c.Hits > 0 && c.Kills > 0 && alive;
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
