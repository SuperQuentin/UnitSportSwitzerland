using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Fixture;
using UnitSport.Vehicles;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>--boattrailercheck jetski|speedboat[,shots]</c> on <c>--chunks fixture:lake --traffic 0</c> (#463; the
/// fixture's traffic drives the slipway road and would run into the trailer), headless
/// or windowed (<c>shots</c>: pictures in <c>test_output/progress/</c>). The Raptor takes the boat's
/// trailer on the lake's slipway, facing up it, and with the real input path
/// (<see cref="FootPlayer.RideControls"/>):
/// <list type="bullet">
/// <item>out of the water the boat will not launch;</item>
/// <item>it reverses down the ramp until the water behind the trailer floats the boat, and stops;</item>
/// <item>launched: a parked boat of that kind afloat behind the trailer, the trailer empty, the
/// pickup not sinking;</item>
/// <item>winched back: the boat gone from the water, aboard again;</item>
/// <item>it pulls the trailer back up the ramp out of the water.</item>
/// </list>
/// Prints <c>[boattrailer] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>.
/// </summary>
public partial class BoatTrailerCheck : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--boattrailercheck");
            return i >= 0 ? (i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "speedboat") : null;
        }
    }

    private readonly System.Func<FootPlayer?> _local;
    private readonly RideKind _boat;
    private readonly bool _shots;
    private int _failures;

    public BoatTrailerCheck(string role, System.Func<FootPlayer?> local)
    {
        _local = local;
        var parts = role.Split(',');
        _boat = parts[0] == "jetski" ? RideKind.Jetski : RideKind.Speedboat;
        _shots = parts.Length > 1 && parts[1] == "shots" && DisplayServer.GetName() != "headless";
        Name = "BoatTrailerCheck";
    }

    public override void _Ready()
    {
        MouseCapture.Disabled = true;
        _ = Run();
    }

    private static void Log(string what) => GD.Print($"[boattrailer] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failures++;
            GD.PrintErr($"[boattrailer] FAIL {what}");
            Log($"FAIL {what}");
        }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> done, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.1)
        {
            if (done()) return true;
            await Wait(0.1);
        }
        return done();
    }

    /// <summary>A point of the course (metres from the start, X east, Y north) in world space at altitude y.</summary>
    private static Vector3 At(double x, double y, float alt = 0f)
    {
        var (e, n) = SpawnPoint.ParseTarget();
        WaterField.TryWorld(e + x, n + y, out var w);
        return w with { Y = alt };
    }

    private const float West = Mathf.Pi / 2f;

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
    /// <summary>Wheel per radian of joint while reversing (the sign: the trailer's way, the wheel's way).</summary>
    private static readonly float ReverseGain = CmdArgs.Float("--reversegain") ?? 3f;

    private static IEnumerable<VehicleBody> Boats(RideKind kind) =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Where(v => !v.Wrecked && !v.IsQueuedForDeletion() && v.Ride.Kind == kind) ?? Enumerable.Empty<VehicleBody>();

    private async Task Shot(string name)
    {
        if (!_shots) return;
        await Wait(0.3);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/progress");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"463-{_boat.ToString().ToLowerInvariant()}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"wrote {path}");
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Finish("no local player"); return; }
        if (!await Until(() => WaterField.TryGetStill(At(Lake.ShoreX + 100, Lake.SlipwayY), out _, out _), 90)) { Finish("the lake's water layer never loaded"); return; }
        Chat()?.Send("/seastate calm");

        // on the slipway above the water, facing up it (west), the lake behind
        var foot = At(Lake.ShoreX - 6, Lake.SlipwayY);
        float ground = me.Terrain != null && me.Terrain.TryGetHeight(foot, out float g) ? g : 0f;
        me.PlaceAt(foot with { Y = ground + 1f }, West);
        if (!await Until(() => me.IsOnFloor(), 10)) { Finish("never stood on the slipway"); return; }
        me.Announced += (text, _) => Log($"  announced: {text}");
        var raptor = HeavyCatalog.All.First(h => h.Class == HeavyClass.Pickup);
        Expect(me.SetRide(raptor.Kind), "in the Raptor on the slipway");
        if (me.Vehicle is not Truck truck) { Finish("not in the pickup"); return; }
        await Wait(1.0);
        int index = TrailerCatalog.TrailerFor(_boat);
        Expect(me.SpawnTrailer(index, 1f) && truck.Trailer?.Boat == _boat, $"{TrailerCatalog.All[index].Label} on the ball, the {_boat} aboard");
        await Wait(1.5);
        Expect(!me.CanLaunchBoat(truck), "on dry land: nothing to launch into");
        await Shot("1-slipway");

        // back down the ramp, slowly, until the water behind the trailer floats the boat
        // the reverse needs a hand on the wheel: steered against the joint, or the trailer folds
        // at a walking pace: in reverse the brake pedal drives and the throttle brakes
        me.RideControls = () =>
        {
            float err = 1.5f - me.GroundSpeed;
            return new RideInput(err < 0f ? Mathf.Clamp(-err, 0f, 1f) : 0f, err > 0f ? Mathf.Clamp(0.35f + err * 0.15f, 0.31f, 0.6f) : 0f,
                Mathf.Clamp(ReverseGain * truck.Articulation[0], -1f, 1f), false);
        };
        bool wet = false;
        for (int i = 0; i < 600 && !wet; i++)
        {
            wet = me.CanLaunchBoat(truck);
            if (i % 20 == 0 && me.BoatSpot(truck) is { } spot)
            {
                WaterField.TryLevelAt(spot.At, out float level);
                float bed = me.Terrain != null && me.Terrain.TryGetHeight(spot.At, out float b) ? b : float.NaN;
                Log(string.Create(CultureInfo.InvariantCulture,
                    $"  t {i / 10f:F1} s: {me.GlobalPosition.X - foot.X:F1} m east {me.GlobalPosition.Z - foot.Z:F1} m south, y {me.GlobalPosition.Y:F2} floor {me.IsOnFloor()} hits {me.SectionHits}, {me.GroundSpeed * 3.6f:F1} km/h {truck.GearLabel}, joint {Mathf.RadToDeg(truck.Articulation[0]):F0}°, behind the trailer water {level:F2} bed {bed:F2}"));
            }
            await Wait(0.1);
        }
        // a metre further, past the very edge of deep enough, as a driver would
        if (wet) await Wait(0.7);
        // stopped on the parking brake: an automatic creeps in reverse at idle
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.2f, 8);
        Expect(me.CanLaunchBoat(truck), "stopped there, the boat's water still behind the trailer");
        float backed = Flat(me.GlobalPosition - foot);
        Expect(wet, string.Create(CultureInfo.InvariantCulture, $"reversed {backed:F1} m down the ramp until the boat's water: joint {Mathf.RadToDeg(truck.Articulation[0]):F0}°"));
        if (!wet) { Finish(null); return; }
        Expect(me.Sinking <= 0f, "the pickup wades, it does not float off");
        await Shot("2-backed-in");

        // launch
        int before = Boats(_boat).Count();
        Log(string.Create(CultureInfo.InvariantCulture, $"  launching at {me.GroundSpeed * 3.6f:F1} km/h, can {me.CanLaunchBoat(truck)}"));
        me.ToggleBoat(truck);
        Expect(await Until(() => Boats(_boat).Count() == before + 1, 5), "launched: a parked boat in the water");
        Expect(TrailerCatalog.BoatAboard(truck.TrailerCode) == 0 && TrailerCatalog.BoatAboard(me.TrailerCode) == 0, "the trailer is empty");
        await Wait(4);
        var boat = Boats(_boat).OrderBy(v => v.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        if (boat != null)
        {
            WaterField.TryLevelAt(boat.GlobalPosition, out float level);
            Expect(Mathf.Abs(boat.GlobalPosition.Y - level) < 1f, string.Create(CultureInfo.InvariantCulture,
                $"it floats ({boat.GlobalPosition.Y - level:F2} m from the surface), {boat.GlobalPosition.DistanceTo(me.GlobalPosition):F1} m behind the pickup"));
        }
        await Shot("3-launched");

        // winch it back
        Expect(me.BoatToWinch(truck) != null, "the boat is within the winch's reach");
        me.ToggleBoat(truck);
        Expect(await Until(() => TrailerCatalog.BoatAboard(truck.TrailerCode) == _boat, 5), "winched back aboard");
        Expect(await Until(() => Boats(_boat).Count() == before, 2), "and gone from the water");

        // pull out
        var stop = me.GlobalPosition;
        me.RideControls = () => new RideInput(Mathf.Clamp(0.35f + (2.5f - me.GroundSpeed) * 0.2f, 0f, 0.7f), 0f, 0f, false);
        bool up = await Until(() => Flat(me.GlobalPosition - stop) > 20f, 25);
        Log(string.Create(CultureInfo.InvariantCulture, $"  pulling out: {Flat(me.GlobalPosition - stop):F1} m, {me.GroundSpeed * 3.6f:F1} km/h {truck.GearLabel}"));
        me.RideControls = () => new RideInput(0f, 0.6f, 0f, false);
        await Until(() => me.GroundSpeed < 0.3f, 8);
        me.RideControls = null;
        Expect(up && me.Vehicle is Truck && me.Sinking <= 0f && !me.CanLaunchBoat(truck), string.Create(CultureInfo.InvariantCulture,
            $"pulled the boat out of the water: {Flat(me.GlobalPosition - foot):F1} m from where it started"));
        await Shot("4-out");
        Finish(null);
    }

    private Net.ChatManager? Chat() => GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) as Net.ChatManager;

    private void Finish(string? fatal)
    {
        if (_local() is { } me) me.RideControls = null;
        if (fatal != null) { _failures++; Log($"FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
