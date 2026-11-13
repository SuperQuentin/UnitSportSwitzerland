using System;
using System.Globalization;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --headless --path . -- --frame earth --chunks fixture:straight --spectatorturncheck</c> (#1800): the spectator flown across
/// many of the earth frame's turning re-anchors with the look untouched, as the user flew from Geneva to Asia. The
/// camera starts at 2 km looking 10° down, flies 60 km at 1 km/s over the ground (a re-anchor every 3 km), then climbs to
/// 100 km and flies 2,000 km at 50 km/s 60° off its heading (the Space regime's 20 km steps, ~18° of the Earth's turn). After every
/// re-anchor the view must stand as it did against the local horizon: no roll (its right vector level within 0.01°),
/// its pitch kept within 0.01°. At the end a one-pixel mouse look may turn the view by no more than one pixel's angle
/// (0.0025 rad) and must not roll it: before, the view rolled and pitched with the world's turn and the next look snapped
/// it back. RESULT ok or FAILED.
/// </summary>
public partial class SpectatorTurnCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--spectatorturncheck");

    private const float PitchDeg = -10f, StartM = 2_000f, GroundSpeed = 1_000f, SpaceM = 100_000f, SpaceSpeed = 50_000f;
    private const double GroundKm = 60, SpaceKm = 2_000, TolDeg = 0.01, TimeoutS = 300;

    private readonly SpectatorCamera _camera;
    private readonly WorldOrigin _origin;
    private int _stage, _shifts, _checked;
    private double _t, _flown, _worstRollDeg, _worstPitchDeg;
    private bool _pending;
    private string? _fault;

    public SpectatorTurnCheck(SpectatorCamera camera, WorldOrigin origin)
    {
        _camera = camera;
        _origin = origin;
        Name = "SpectatorTurnCheck";
        _origin.Shifted += s => { if (s.Turns) { _shifts++; _pending = true; } };
    }

    // the camera's roll (its right vector against the world's level) and pitch, degrees
    private (double Roll, double Pitch) Attitude()
    {
        var b = _camera.GlobalTransform.Basis;
        double roll = Math.Asin(Math.Clamp(b.X.Normalized().Y, -1, 1)) * 180 / Math.PI;
        double pitch = Math.Asin(Math.Clamp(-b.Z.Normalized().Y, -1, 1)) * 180 / Math.PI;
        return (roll, pitch);
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > TimeoutS) { Finish($"FAILED timeout at stage {_stage} after {_shifts} turning re-anchors"); return; }
        switch (_stage)
        {
            case 0:
                // the look set as a player would, by the mouse: 10° down
                _camera.Position = new Vector3(0, StartM, 0);
                _camera.Rotation = Vector3.Zero;
                _camera.Look(new Vector2(0, -Mathf.DegToRad(PitchDeg) / _camera.MouseSensitivity));
                _stage = 1;
                return;
            case 1:
            case 2:
            {
                if (_pending)
                {
                    _pending = false;
                    _checked++;
                    var (roll, pitch) = Attitude();
                    _worstRollDeg = Math.Max(_worstRollDeg, Math.Abs(roll));
                    _worstPitchDeg = Math.Max(_worstPitchDeg, Math.Abs(pitch - PitchDeg));
                }
                bool space = _stage == 2;
                float speed = space ? SpaceSpeed : GroundSpeed;
                // level, along the view's heading on the ground, 60° off it in Space (the Earth's turn then rolls a view
                // turned rigidly with the world, as the user's did)
                var heading = new Basis(Vector3.Up, _camera.Rotation.Y + (space ? Mathf.DegToRad(60) : 0)) * Vector3.Forward;
                _camera.Position += heading * speed * (float)delta;
                _flown += speed * delta;
                if (!space && _flown > GroundKm * 1000)
                {
                    _camera.Position = new Vector3(_camera.Position.X, SpaceM, _camera.Position.Z);
                    _flown = 0;
                    _stage = 2;
                }
                else if (space && _flown > SpaceKm * 1000) _stage = 3;
                return;
            }
            case 3:
            {
                var before = -_camera.GlobalTransform.Basis.Z;
                var (rollBefore, _) = Attitude();
                _camera.Look(new Vector2(1, 0));
                var after = -_camera.GlobalTransform.Basis.Z;
                var (rollAfter, _) = Attitude();
                double turn = before.AngleTo(after);
                Print($"{_shifts} turning re-anchors ({_checked} checked): worst roll {_worstRollDeg:F4}°, worst pitch off {_worstPitchDeg:F4}°; "
                    + $"a one-pixel look turned the view {turn * 180 / Math.PI:F4}° (roll {rollBefore:F4}° -> {rollAfter:F4}°)");
                if (_shifts < 50) _fault = $"only {_shifts} turning re-anchors";
                else if (_worstRollDeg > TolDeg) _fault = $"the view rolled {_worstRollDeg:F3}° across the re-anchors";
                else if (_worstPitchDeg > TolDeg) _fault = $"the view's pitch moved {_worstPitchDeg:F3}° across the re-anchors";
                else if (turn > 1.5 * _camera.MouseSensitivity) _fault = $"a one-pixel look snapped the view {turn * 180 / Math.PI:F2}°";
                Finish(_fault == null ? "ok" : $"FAILED {_fault}");
                return;
            }
        }
    }

    private static void Print(string s) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[spectatorturncheck] {s}"));

    private void Finish(string result)
    {
        Print($"RESULT: {result}");
        SetProcess(false);
        GetTree().Quit(result == "ok" ? 0 : 1);
    }
}
