using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--orbitonlycheck</c> (#1854), headless, under <c>--frame earth</c>: the bodies one can orbit but not stand on. The
/// spectator put 10 km over Jupiter's 1-bar radius is pushed back up to its cloud tops' floor (50 km); T there keeps the
/// camera (no ground); the same over the Sun at 100,000 km, pushed back to 0.2 radii.
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

        foreach (var (body, below) in new[] { (CelestialBody.Jupiter, 10_000.0), (CelestialBody.Sun, 1e8) })
        {
            // the anchor relabelled onto the body, the camera under its floor (the origin on the reference, the camera over it)
            var sky = SkyClock.Now;
            _origin.Rebase(body.Id, sky);
            var under = new Geodetic(10, 30, 0);
            shifter.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(body.Shape.ToEcef(under), body.Id));
            cam.MakeCurrent();
            cam.GlobalPosition = new Vector3(0, (float)below, 0);
            await Wait(0.3);
            double alt = _origin.AltitudeOf(cam.GlobalPosition);
            Expect(alt >= body.FloorM * 0.999, string.Create(CultureInfo.InvariantCulture,
                $"{body.Name}: put {below / 1000:F0} km over it, held at {alt / 1000:F0} km (its floor {body.FloorM / 1000:F0} km)"));

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
