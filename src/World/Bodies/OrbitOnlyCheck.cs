using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--orbitonlycheck</c> (#1854), headless, under <c>--frame earth</c>: the bodies one can orbit but not stand on. Put
/// 100 km into Jupiter's air the spectator stays (a giant is flown into); put 300 km under its floor (500 km under the 1-bar
/// level) it is pushed back up to it; T there keeps the camera (no ground). Put 300 km under the Sun's floor (0.2 radii)
/// it is pushed back too.
/// </summary>
public partial class OrbitOnlyCheck : Node
{
    public static bool Requested => CmdArgs.Has("--orbitonlycheck");

    private readonly WorldOrigin _origin;
    private int _failed;

    public OrbitOnlyCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "OrbitOnlyCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[orbitonly] {what}"));

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failed++;
            GD.PrintErr($"[orbitonly] FAIL {what}");
        }
    }

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void Key(bool down) => Input.ParseInputEvent(new InputEventKey
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = Godot.Key.T, Keycode = Godot.Key.T, KeyLabel = Godot.Key.T, Pressed = down,
    });

    private async Task Run()
    {
        await Wait(3);
        var cam = GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as Camera3D;
        if (cam == null || OriginShifter.Instance is not { } shifter) { Finish("no spectator camera or shifter"); return; }

        // into Jupiter's air: allowed
        {
            var sky0 = SkyClock.Now;
            _origin.Rebase(BodyId.Jupiter, sky0);
            shifter.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(CelestialBody.Jupiter.Shape.ToEcef(new Geodetic(10, 30, 0)), BodyId.Jupiter));
            cam.MakeCurrent();
            cam.GlobalPosition = new Vector3(0, -100_000f, 0);
            await Wait(0.3);
            double into = _origin.AltitudeOf(cam.GlobalPosition);
            Expect(Math.Abs(into + 100_000) < 100, string.Create(CultureInfo.InvariantCulture, $"Jupiter: 100 km into its air the camera stays ({into / 1000:F1} km)"));
        }
        foreach (var body in new[] { CelestialBody.Jupiter, CelestialBody.Sun })
        {
            double below = body.FloorM - 300_000;
            // the anchor relabelled onto the body, the camera under its floor (the origin on the reference, the camera over it)
            var sky = SkyClock.Now;
            _origin.Rebase(body.Id, sky);
            var under = new Geodetic(10, 30, 0);
            shifter.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(body.Shape.ToEcef(under), body.Id));
            cam.MakeCurrent();
            cam.GlobalPosition = new Vector3(0, (float)below, 0);
            await Wait(0.3);
            double alt = _origin.AltitudeOf(cam.GlobalPosition);
            Expect(alt >= body.FloorM - 100, string.Create(CultureInfo.InvariantCulture,
                $"{body.Name}: put {below / 1000:F0} km over its reference, held at {alt / 1000:F0} km (its floor {body.FloorM / 1000:F0} km)"));

            Key(true);
            await Wait(0.05);
            Key(false);
            await Wait(0.5);
            Expect(cam.Current, $"{body.Name}: T keeps the camera (no ground to stand on)");
        }
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[orbitonly] RESULT: ok");
        else GD.PrintErr($"[orbitonly] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
