using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The spectator from the Earth to the Moon (#1850, H7d of docs/plans/planets.md section 13): <c>--mooncheck</c>, headless,
/// under <c>--frame earth</c>. The spectator camera starts 400 km over the point under the Moon and flies straight for the point 100 km
/// over the Moon on its way, at the spectator's own speed rule (<see cref="Player.SpectatorSpeed.SurfaceCap"/>: half the
/// distance to the nearest surface of any body, a second), so the frame shifts, turns and, at 66,000 km from the Moon,
/// is counted from the Moon (<see cref="WorldOrigin.Rebase"/>).
///
/// <para>
/// It fails on: no crossing, or more than one; a shift on the crossing's frame (a crossing moves nothing); the camera's
/// place, counted from the anchor's body, jumping more than its own motion and a metre between two frames (past 1,000 km
/// up the origin follows the camera in 3D, <see cref="OriginShifter.DeepM"/>, or the float alone is a metre off); arriving counted from
/// any body but the Moon; taking more than <see cref="MaxSeconds"/> of game time. A windowed run saves a picture on
/// arrival (<c>test_output/moon/arrival.png</c>, with <c>--bodies</c> the Moon's globe under it).
/// </para>
/// </summary>
public partial class MoonCheck : Node
{
    public static bool Requested => CmdArgs.Has("--mooncheck");

    /// <summary>The trip must take less than this, game seconds (the plan: about a minute).</summary>
    public const double MaxSeconds = 120;

    private readonly WorldOrigin _origin;
    private Camera3D? _cam;
    private int _stage, _crossings, _crossedShifts = -1, _shiftsAtCross;
    private double _t, _flying, _worstJump;
    private (BodyId Body, DVec3 P, SkyMoment Sky, double Step)? _last;
    private string? _fail;

    public MoonCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "MoonCheck";
        _origin.BodyChanged += (from, to) =>
        {
            _crossings++;
            _shiftsAtCross = OriginShifter.Instance?.ShiftCount ?? 0;
            GD.Print($"[mooncheck] crossed from {from} to {to} after {_flying:F1} s");
        };
    }

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        switch (_stage)
        {
            case 0 when _t > 3:
                Start();
                break;
            case 1:
                Fly(delta);
                break;
        }
    }

    private void Start()
    {
        _cam = GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as Camera3D;
        if (_cam == null || OriginShifter.Instance is not { } shifter) { Finish("no spectator camera or shifter"); return; }
        // 400 km over the point right under the Moon now: the way out is straight up, never through the Earth
        var moon = Frames.Convert(BodyId.Moon, DVec3.Zero, BodyId.Earth, SkyClock.Now);
        var under = Ellipsoid.Wgs84.ToGeodetic(moon.AsEcef);
        var at = Ellipsoid.Wgs84.ToEcef(under with { H = 400_000 });
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[mooncheck] the Moon is over {under.LatDeg:F1}, {under.LonDeg:F1}"));
        shifter.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(at));
        _cam.MakeCurrent();
        _cam.GlobalPosition = Vector3.Zero;
        _stage = 1;
        GD.Print("[mooncheck] off from 400 km up, the Moon overhead");
    }

    private void Fly(double delta)
    {
        if (_cam == null || _origin.Frame.Axes is not { } axes) { Finish("no anchor frame"); return; }
        _flying += delta;
        var sky = SkyClock.Now;
        var here = axes.ToEcef(_cam.GlobalPosition.X, _cam.GlobalPosition.Y, _cam.GlobalPosition.Z);
        var p = new DVec3(here.X, here.Y, here.Z);

        if (_crossings > 0 && _crossedShifts < 0)
        {
            _crossedShifts = (OriginShifter.Instance?.ShiftCount ?? 0) - _shiftsAtCross;
        }

        // where it heads: 100 km over the Moon on the line from its centre to the camera
        var moonHere = Frames.Convert(BodyId.Moon, DVec3.Zero, axes.Body, sky);
        var away = (p - moonHere).Normalized();
        var target = moonHere + away * (CelestialBody.Moon.Shape.A + 100_000);
        var toTarget = target - p;
        double left = toTarget.Length;
        var (nearest, distance) = Frames.NearestSurface(axes.Body, p, sky);
        double speed = Player.SpectatorSpeed.SurfaceCap(distance);
        double step = Math.Min(speed * delta, left);
        // continuity in the anchor body's own frame: last frame's place, counted from this frame's body at last frame's
        // moment (across the crossing: the Earth's count of it into the Moon's), against this one. Only the camera's own
        // step may lie between them, and a metre
        if (_last is { } last)
        {
            var before = Frames.Convert(last.Body, last.P, axes.Body, last.Sky);
            double moved = (p - before).Length;
            double allowed = last.Step + 1;
            _worstJump = Math.Max(_worstJump, moved - allowed);
            if (moved > allowed) _fail ??= string.Create(CultureInfo.InvariantCulture,
                $"the camera's place jumped {moved:F1} m in a frame ({allowed:F1} m its own step) at {_flying:F1} s, counted from {axes.Body}");
        }
        _last = (axes.Body, p, sky, step);
        if ((int)(_flying / 5) != (int)((_flying - delta) / 5))
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[mooncheck] {_flying:F0} s: {left / 1000:F0} km to go, {distance / 1000:F0} km over {nearest}, {speed / 1000:F0} km/s, step {step / 1000:F1} km, camera at {_cam.GlobalPosition}, counted from {axes.Body}"));

        if (left < 1000)
        {
            Arrive(axes.Body, nearest, distance);
            return;
        }
        var dirWorld = Vector3.Zero;
        {
            var t = toTarget / left;
            dirWorld = new Vector3((float)FrameAxes.Dot(new EcefPos(t.X, t.Y, t.Z), axes.X),
                (float)FrameAxes.Dot(new EcefPos(t.X, t.Y, t.Z), axes.Y), (float)FrameAxes.Dot(new EcefPos(t.X, t.Y, t.Z), axes.Z));
        }
        _cam.GlobalPosition += dirWorld * (float)step;
        if (_flying > MaxSeconds) Finish(string.Create(CultureInfo.InvariantCulture,
            $"still {left / 1000:F0} km from lunar orbit after {_flying:F0} s"));
    }

    private void Arrive(BodyId body, BodyId nearest, double distance)
    {
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[mooncheck] arrived after {_flying:F1} s: counted from {body}, {distance / 1000:F0} km over {nearest}, {_crossings} crossing(s), {_crossedShifts} shift(s) on the crossing's frame, the worst frame {Math.Max(0, _worstJump):F1} m past its own motion"));
        if (_fail != null) { Finish(_fail); return; }
        if (_crossings != 1) { Finish($"{_crossings} crossings (one wanted)"); return; }
        if (body != BodyId.Moon) { Finish($"counted from {body} at the Moon"); return; }
        if (_crossedShifts > 0) { Finish($"{_crossedShifts} shift(s) came with the crossing"); return; }
        if (DisplayServer.GetName() != "headless")
        {
            var path = ProjectSettings.GlobalizePath("res://test_output/moon/arrival.png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[mooncheck] saved {path}");
        }
        Finish(null);
    }

    private void Finish(string? fail)
    {
        _stage = 2;
        if (fail == null) GD.Print("[mooncheck] RESULT: ok");
        else GD.PrintErr($"[mooncheck] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
