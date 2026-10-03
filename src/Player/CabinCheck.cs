using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--cabincheck [shots]</c> offline on <c>--world fixture</c> (#416): the A320's cabin walked, on the
/// ground and in flight, on the real deck mechanism (<c>walk-aboard</c>).
/// <list type="bullet">
/// <item>at the controls, stopped: G opens L1 and L2, their leaves swing open;</item>
/// <item>E stands the captain up into the cockpit; walked aft through the cockpit door down the
/// aisle to row 10; E at a seat sits; E stands up again;</item>
/// <item>walked forward into the cockpit, E at the captain's seat takes the controls;</item>
/// <item>doors shut, put up at 600 m: E stands up in flight, the aircraft flies on by itself (hands
/// off, its law holds the path), the walk aft carries the player along at 120 m/s, never through the
/// floor; back in the cockpit, the controls again.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/cabin/*.png</c> at each stage. RESULT line at the end.
/// </summary>
public partial class CabinCheck : Node
{
    public static bool Requested => CmdArgs.Has("--cabincheck");
    private static bool Shots => CmdArgs.Value("--cabincheck") == "shots";

    private readonly System.Func<FootPlayer?> _player;
    private int _failures;

    public CabinCheck(System.Func<FootPlayer?> player) => _player = player;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[cabin] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private async Task Shot(string name)
    {
        if (!Shots) return;
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/cabin");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[cabin] wrote {path}");
    }

    /// <summary>The drawn A320, wherever it is (driven: the player's visual; parked: a vehicle's).</summary>
    private AirlinerRig? Rig() => Frame() as AirlinerRig;

    /// <summary>The aircraft's frame: the drawn rig, or headless a parked one's empty posed frame.</summary>
    private Node3D? Frame()
    {
        if (_player() is { } me && me.GetChildren().OfType<AirlinerRig>().FirstOrDefault(r => !r.IsQueuedForDeletion()) is { } own) return own;
        return VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.A320 && !v.Wrecked)?.Visual;
    }

    private static bool Drawn => DisplayServer.GetName() != "headless";

    /// <summary>An authored cabin point (+Z forward, +X left) in the world, as the aircraft is drawn now.</summary>
    private Vector3 Cabin(float x, float z) => Frame()!.GlobalTransform * AircraftMeshBuilder.Flip(new Vector3(x, A320Layout.FloorY + 0.05f, z));

    /// <summary>The player's spot in the cabin, authored (x, height over the floor, z).</summary>
    private Vector3 Local(FootPlayer me)
    {
        var l = AircraftMeshBuilder.Flip(Frame()!.GlobalTransform.AffineInverse() * me.GlobalPosition);
        return l with { Y = l.Y - A320Layout.FloorY };
    }

    private Vector3 _target;

    private async Task<bool> WalkTo(FootPlayer me, float x, float z, double seconds)
    {
        me.WalkControls = () =>
        {
            _target = Cabin(x, z);
            var to = (_target - me.GlobalPosition) with { Y = 0 };
            return (to.Length() < 0.2f ? Vector3.Zero : to.Normalized(), false);
        };
        bool there = await Until(() => (Cabin(x, z) - me.GlobalPosition).Length() < 0.45f, seconds);
        me.WalkControls = () => (Vector3.Zero, false);
        await Seconds(0.3);
        return there;
    }

    public override async void _Ready()
    {
        await Seconds(2);
        // offline the world starts on the free camera: the player comes with the mode key
        for (int i = 0; i < 1800 && _player() is not { } ready || i < 1800 && !_player()!.IsOnFloor(); i++)
        {
            if (_player() == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_player() is not { } found || !found.IsOnFloor()) { Finish("no player"); return; }
        var me = _player()!;
        Expect(me.SetRide(RideKind.A320), "at the controls of an A320");
        await Seconds(2);
        if (me.Vehicle is not Airliner jet) { Finish("not an airliner"); return; }

        // G at the controls, stopped: the left doors
        jet.ToggleDoor(0);
        jet.ToggleDoor(2);
        await Until(() => !Drawn || Rig()?.DoorOpen(0) >= 1f, 8);
        Expect(jet.DoorsOpen == 5 && (!Drawn || Rig()?.DoorOpen(0) >= 1f && Rig()?.DoorOpen(2) >= 1f), $"L1 and L2 open (doors {jet.DoorsOpen})");
        await Shot("doors_open");

        // standing up into the cockpit, then aft down the aisle
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 5),
            $"E stood the captain up into the cockpit (aboard {me.Aboard}, ride {me.Ride})");
        await Seconds(1);
        var l = Local(me);
        Expect(l.Z > A320Layout.CockpitWallZ - 0.3f && Mathf.Abs(l.Y) < 0.3f, $"standing in the cockpit ({l.X:F2}, {l.Y:F2}, {l.Z:F2})");
        await Shot("cockpit_standing");
        bool aisle = await WalkTo(me, 0f, A320Layout.CockpitWallZ - 0.8f, 15) && await WalkTo(me, 0f, A320Layout.RowZ(10) + 0.1f, 25);
        l = Local(me);
        Expect(aisle && Mathf.Abs(l.Y) < 0.3f, $"walked out of the cockpit and down the aisle to row 11 ({l.X:F2}, {l.Y:F2}, {l.Z:F2})");
        await Shot("aisle");

        // a seat: E sits, E stands up
        Expect(me.TryInteract() && await Until(() => me.SeatIndex > 1, 5), $"E sat in a seat (seat {me.SeatIndex})");
        await Shot("seated");
        await Seconds(1);
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.SeatIndex == 0 && me.Ride == RideKind.OnFoot, 5), "E stood up into the aisle");

        // forward into the cockpit and the controls again
        bool cockpit = await WalkTo(me, 0f, A320Layout.CockpitWallZ - 0.8f, 30) && await WalkTo(me, 0f, A320Layout.CockpitWallZ + 0.35f, 10) && await WalkTo(me, A320Layout.CaptainHip.X * 0.9f, A320Layout.CockpitWallZ + 0.4f, 10);
        Expect(cockpit, $"walked back into the cockpit ({Local(me).Z:F2})");
        GD.Print($"[cabin] at the seat: prompt '{me.DeckHint}', button {me.ButtonInReach()?.Door}");
        bool took = me.TryInteract();
        Expect(took && await Until(() => me.Vehicle is Airliner && me.SeatIndex == 0, 6),
            $"E at the captain's seat: the controls (E {took}, ride {me.Ride}, seat {me.SeatIndex}, riding with {me.RidingWith}, vehicle {me.Vehicle?.Kind})");
        if (me.Vehicle is not Airliner flying) { Finish("not back at the controls"); return; }

        // in flight: the doors shut, up at 600 m, stand up and walk while it flies itself
        flying.ToggleDoor(0);
        flying.ToggleDoor(2);
        await Until(() => !Drawn || Rig()?.DoorOpen(0) <= 0f && Rig()?.DoorOpen(2) <= 0f, 8);
        me.DebugLaunch(me.GlobalPosition + Vector3.Up * 600f, -me.GlobalTransform.Basis.Z * 120f);
        await Seconds(3);
        float y0 = me.GlobalPosition.Y;
        bool up = me.TryInteract();
        Expect(up && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 5), $"E stood up in flight (E {up}, aboard {me.Aboard}, ride {me.Ride}, at {me.GlobalPosition.Y - y0:F0} m)");
        bool walked = await WalkTo(me, 0f, A320Layout.CockpitWallZ - 0.8f, 15) && await WalkTo(me, 0f, A320Layout.RowZ(6), 25);
        l = Local(me);
        float speed = VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.A320)?.Velocity.Length() ?? 0f;
        Expect(walked && me.Aboard && Mathf.Abs(l.Y) < 0.35f, $"walked aft in flight at {speed:F0} m/s, on the floor ({l.X:F2}, {l.Y:F2}, {l.Z:F2})");
        Expect(me.GlobalPosition.Y > y0 - 200f, $"it flew on by itself ({me.GlobalPosition.Y - y0:+0;-0} m)");
        await Shot("aisle_in_flight");
        cockpit = await WalkTo(me, 0f, A320Layout.CockpitWallZ - 0.8f, 30) && await WalkTo(me, 0f, A320Layout.CockpitWallZ + 0.35f, 10) && await WalkTo(me, A320Layout.CaptainHip.X * 0.9f, A320Layout.CockpitWallZ + 0.4f, 10);
        Expect(cockpit && me.TryInteract() && await Until(() => me.Vehicle is Airliner && me.SeatIndex == 0, 6), $"back at the controls in flight ({me.Ride})");
        await Shot("controls_in_flight");
        Finish(null);
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        if (_player() is { } p) p.WalkControls = null;
        GD.Print(_failures == 0 ? "[cabin] RESULT: ok" : $"[cabin] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
