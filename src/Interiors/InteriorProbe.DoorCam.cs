using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--interiorcheck,shot.png --doorcam</c> (#388): after walking in, the third-person camera
/// around the open doorway from both sides. The player stands at several depths inside and
/// outside, facing away from the door so the arm reaches back through it, swung at several
/// angles; then walks in and out again backwards with the camera trailing. Every picture must
/// show something: not one flat colour (a far plane the depth buffer could not resolve drew only
/// the background), not mostly black (a doorway left with its dark hall, a lens inside a leaf).
/// Each pose and walk frame is saved beside the shot; <c>--doorcam-trace</c> prints the camera
/// and its arm on every walk frame.
/// </summary>
public partial class InteriorProbe
{
    private static readonly float[] CamDepths = { 0.4f, 1.0f, 2.0f, 3.5f };
    private static readonly float[] CamSwings = { 0f, -35f, 35f, -70f, 70f };
    private int _cam;
    private double _camT;
    private int _camFrame, _camBad;
    private float _yaw;

    /// <summary>Null while under way, then whether it finished.</summary>
    private bool? DoorCam(InteriorManager interiors, double delta)
    {
        _camT += delta;
        string door = _door.Key.ToString();
        int poses = CamDepths.Length * CamSwings.Length;
        if (_cam == 0)
        {
            _player!.DebugThirdPerson(true);
            _cam = 1;
            _camT = 0;
        }
        // 1..poses: inside; poses+1..2*poses: outside
        if (_cam <= 2 * poses)
        {
            int i = _cam - 1;
            bool inside = i < poses;
            float depth = CamDepths[(i % poses) / CamSwings.Length];
            float swing = CamSwings[i % CamSwings.Length];
            if (_camT < 0.05) Pose(interiors, door, inside, depth, swing);
            // the arm eases out over ~1 s; hold the view while it does
            _player!.LookYaw = _yaw + Mathf.DegToRad(swing);
            _player.LookPitch = -0.25f;
            if (_camT < 1.6) return null;
            Picture($"_cam_{(inside ? "in" : "out")}_{depth.ToString("F1", CultureInfo.InvariantCulture)}_{swing:+0;-0}", strict: true);
            _cam++;
            _camT = 0;
            return null;
        }
        // the walk: from 3 m outside, backwards in and out again, the camera trailing behind
        switch (_cam - 2 * poses - 1)
        {
            case 0:
                if (_camT < 0.05) Pose(interiors, door, inside: false, depth: 3f, swing: 0f);
                _player!.LookYaw = _yaw + Mathf.Pi;   // the back to the house
                if (_camT < 1.6) return null;
                Input.ActionPress(PlayerInput.MoveForward);
                break;
            case 1:
                if (Walking(interiors, door)) return null;
                Check(_player!.Indoors, "walked in, camera trailing outside");
                _player.LookYaw = _yaw;
                break;
            case 2:
                if (_camT < 1.0) return null;
                Input.ActionPress(PlayerInput.MoveForward);
                break;
            default:
                if (Walking(interiors, door)) return null;
                Check(!_player!.Indoors, "walked out, camera trailing inside");
                Check(_camBad == 0, $"every door camera picture shows something ({_camBad} bad)");
                return true;
        }
        _cam++;
        _camT = 0;
        return null;
    }

    /// <summary>Four seconds of walking, every frame looked at; false once over.</summary>
    private bool Walking(InteriorManager interiors, string door)
    {
        if (_camFrame < 480)
        {
            if (CmdArgs.Has("--doorcam-trace") && GetViewport().GetCamera3D() is { } c && interiors.Links.TryGetValue(door, out var l))
                GD.Print($"[doorcam] f{_camFrame} lens {(l.Inside.AffineInverse() * c.GlobalPosition):F2} (y {c.GlobalPosition.Y:F0})"
                    + $" indoors {_player!.Indoors} arm {_player.DebugArm} near {c.Near} far {c.Far} shown {interiors.Portals?.IsShown(l)}");
            Picture($"_cam_walk_f{_camFrame++:000}", strict: false);
        }
        if (_camT < 4.0) return true;
        Input.ActionRelease(PlayerInput.MoveForward);
        return false;
    }

    /// <summary>
    /// The player <paramref name="depth"/> m from the doorway on one side, facing away from it, so
    /// the third-person arm reaches back through it, swung <paramref name="swing"/> degrees.
    /// </summary>
    private void Pose(InteriorManager interiors, string door, bool inside, float depth, float swing)
    {
        if (inside)
        {
            if (!_player!.Indoors || interiors.Current is not { } layout || interiors.CurrentNode is not { } node) return;
            var way = layout.EntranceFor(door);
            var at = node.GlobalTransform * new Vector3(way.X + way.InX * depth, 0.1f, way.Z + way.InZ * depth);
            var face = node.GlobalTransform.Basis * new Vector3(way.InX, 0, way.InZ);
            _yaw = Mathf.Atan2(-face.X, -face.Z);
            _player.EnterInterior(layout.Key, at, _yaw);
        }
        else
        {
            if (_player!.Indoors) interiors.Leave(_player);
            var face = _door.Outward;
            _yaw = Mathf.Atan2(-face.X, -face.Z);
            _player.LeaveInterior(_door.World + _door.Outward * depth + Vector3.Up * 0.3f, _yaw);
        }
        _player.Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Saves the picture and judges it: one flat colour or mostly black is a bad frame. A pose
    /// (<paramref name="strict"/>) is checked on its own line; walk frames are counted.
    /// </summary>
    private void Picture(string suffix, bool strict)
    {
        if (_shot == null || DisplayServer.GetName() == "headless") return;
        var image = GetViewport().GetTexture().GetImage();
        var picture = (Image)image.Duplicate();
        image.Resize(64, 36, Image.Interpolation.Nearest);
        // colours in coarse bins (a dithered flat colour stays one or two), and black pixels;
        // the HUD covers a few percent. A wall seen up close fills ~60 % with one bin, the
        // background alone (#388) 97 %.
        const int all = 64 * 36;
        int dark = 0;
        var bins = new Dictionary<int, int>();
        for (int y = 0; y < 36; y++)
            for (int x = 0; x < 64; x++)
            {
                var p = image.GetPixel(x, y);
                // near black: the dark hall, the void; a building's shadow on Realistic's grass is lighter
                if (p.Luminance < 0.075f) dark++;
                int bin = (p.R8 >> 5) << 6 | (p.G8 >> 5) << 3 | p.B8 >> 5;
                bins[bin] = bins.GetValueOrDefault(bin) + 1;
            }
        int mode = bins.Values.Max();
        bool good = mode < all * 0.9f && dark < all * 0.4f;
        if (!good) _camBad++;
        // the poses always; walk frames when bad, or all of them with --film
        if (strict || !good || CmdArgs.Has("--film")) picture.SavePng(_shot.Replace(".png", suffix + ".png"));
        if (strict || !good) Check(good, $"door camera {suffix}: {mode * 100 / all} % one colour, {dark * 100 / all} % black");
    }
}
