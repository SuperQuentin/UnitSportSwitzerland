using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--shuttleaboardcheck [shots]</c> offline on <c>--world fixture</c> (#1493): the shuttle's crew module walked and
/// floated in, on the real deck mechanism (<c>walk-aboard</c>, <c>shuttle-aboard</c>).
/// <list type="bullet">
/// <item>on the ground: E stands the commander up behind the seat on the flight deck (not floating: the Earth's g is felt
/// down); walked to the interdeck opening and down the ladder to the mid deck, back up it, down again; E by the hatch
/// gets out onto the ground beside the hull; E outside gets in again, at the controls;</item>
/// <item>in a 400 km orbit (<c>/orbit 400</c>): E stands up, and the commander floats (nothing felt); left alone they do
/// not fall to the floor; pushed off toward the opening and through it down to the mid deck; let go, they hold on to
/// what is in reach and stop; the hatch will not open; pushed back up to the flight deck, E behind the commander's seat
/// takes the controls again.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/shuttleaboard/*.png</c>. RESULT line at the end.
/// </summary>
public partial class ShuttleAboardCheck : Node
{
    public static bool Requested => CmdArgs.Has("--shuttleaboardcheck");
    private static bool Shots => CmdArgs.Value("--shuttleaboardcheck") == "shots";

    private readonly System.Func<FootPlayer?> _player;
    private int _failures;

    public ShuttleAboardCheck(System.Func<FootPlayer?> player) => _player = player;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[shuttleaboard] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private async Task Shot(string name)
    {
        if (!Shots) return;
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/shuttleaboard");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[shuttleaboard] wrote {path}");
    }

    /// <summary>The shuttle's frame: the drawn rig (driven) or a parked one's posed frame.</summary>
    private Node3D? Frame()
    {
        if (_player() is { } me && me.GetChildren().OfType<AirlinerRig>().FirstOrDefault(r => !r.IsQueuedForDeletion()) is { } own) return own;
        return VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.Shuttle && !v.Wrecked)?.Visual;
    }

    /// <summary>The frame as the walker aboard stands in it (<see cref="FootPlayer.DeckFrame"/>), else as drawn.</summary>
    private Transform3D Pose() => _player()?.DeckFrame ?? Frame()!.GlobalTransform.Orthonormalized();

    /// <summary>An authored cabin point (+Z forward, +X left, height over the ground) in the world.</summary>
    private Vector3 Cabin(float x, float y, float z) => Pose() * AircraftMeshBuilder.Flip(new Vector3(x, y, z));

    /// <summary>The player's spot in the cabin, authored.</summary>
    private Vector3 Local(FootPlayer me) => AircraftMeshBuilder.Flip(Pose().AffineInverse() * me.GlobalPosition);

    private static string Str(Vector3 v) => System.FormattableString.Invariant($"({v.X:F2}, {v.Y:F2}, {v.Z:F2})");

    private async Task<bool> WalkTo(FootPlayer me, float x, float y, float z, double seconds)
    {
        me.WalkControls = () =>
        {
            var to = (Cabin(x, y, z) - me.GlobalPosition) with { Y = 0 };
            return (to.Length() < 0.15f ? Vector3.Zero : to.Normalized(), false);
        };
        bool there = await Until(() => (Cabin(x, y, z) - me.GlobalPosition).Length() < 0.45f, seconds);
        me.WalkControls = () => (Vector3.Zero, false);
        await Seconds(0.3);
        return there;
    }

    /// <summary>Floating: pushed toward a cabin point (the body's feet there) until within 0.4 m of it.</summary>
    private async Task<bool> FloatTo(FootPlayer me, float x, float y, float z, double seconds)
    {
        bool there = await Until(() =>
        {
            var to = Cabin(x, y, z) - me.GlobalPosition;
            me.FloatWish = to.Length() < 0.1f ? Vector3.Zero : to.Normalized() * Mathf.Min(1f, to.Length());
            return to.Length() < 0.4f;
        }, seconds);
        me.FloatWish = Vector3.Zero;
        return there;
    }

    public override async void _Ready()
    {
        await Seconds(2);
        ulong deadline = Time.GetTicksMsec() + 30_000;
        for (int i = 0; Time.GetTicksMsec() < deadline && _player() is not { } ready || Time.GetTicksMsec() < deadline && !_player()!.IsOnFloor(); i++)
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
        Expect(me.SetRide(RideKind.Shuttle), "at the controls of the shuttle");
        await Seconds(3);
        if (me.Vehicle is not Airliner { Kind: RideKind.Shuttle }) { Finish("not the shuttle"); return; }

        // ---- on the ground ----
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 6),
            $"E stood the commander up into the cabin (aboard {me.Aboard}, ride {me.Ride})");
        await Seconds(1);
        var l = Local(me);
        Expect(Mathf.Abs(l.Y - ShuttleLayout.FlightDeckY) < 0.3f && l.Z > 21.6f, $"standing on the flight deck behind the seat {Str(l)}");
        Expect(!me.Floating && me.FeltAboard.Y < -8f, $"not floating on the ground, the Earth's g felt (felt {Str(me.FeltAboard)})");
        await Shot("flight_deck_ground");
        float lx = (ShuttleLayout.LadderInX + ShuttleLayout.LadderOutX) * 0.5f;
        float md = ShuttleLayout.MidDeckY, fd = ShuttleLayout.FlightDeckY;
        float top = ShuttleLayout.LadderTopZ + 0.3f, foot = ShuttleLayout.LadderFootZ + 0.12f;
        bool down = await WalkTo(me, -0.2f, fd, 22.1f, 10) && await WalkTo(me, lx, fd, top, 10) && await WalkTo(me, lx, md, foot, 15);
        l = Local(me);
        Expect(down && Mathf.Abs(l.Y - md) < 0.3f, $"down the ladder to the mid deck {Str(l)}");
        await Shot("mid_deck_ground");
        bool up = await WalkTo(me, lx, fd, top + 0.1f, 15);
        l = Local(me);
        Expect(up && Mathf.Abs(l.Y - fd) < 0.3f, $"up the ladder to the flight deck {Str(l)}");
        down = await WalkTo(me, lx, md, foot, 15);
        Expect(down, $"down the ladder again {Str(Local(me))}");
        bool hatch = await WalkTo(me, 0.6f, md, 21.2f, 10) && await WalkTo(me, ShuttleDeck.HatchInside.X, md, ShuttleDeck.HatchInside.Z, 10);
        Expect(hatch && me.DeckHint == "Out by the hatch", $"by the hatch inside, prompt '{me.DeckHint}' {Str(Local(me))}");
        Expect(me.TryInteract() && await Until(() => !me.Aboard && me.IsOnFloor(), 5), $"E got out by the hatch onto the ground (aboard {me.Aboard})");
        await Seconds(1);
        float fromHull = Frame() is { } outside ? (outside.GlobalTransform.AffineInverse() * me.GlobalPosition).X : float.NaN;
        Expect(fromHull < -ShuttleLayout.HalfWidth, $"outside on the hatch's side ({fromHull:F1} m to the right of the middle, node frame)");
        await Shot("outside");
        bool inAgain = false;
        for (int i = 0; i < 10 && !inAgain; i++)
        {
            me.TryGetIn();
            inAgain = await Until(() => me.Vehicle is Airliner { Kind: RideKind.Shuttle }, 1);
        }
        Expect(inAgain && me.SeatIndex == 0, $"E outside: in again, at the controls ({me.Ride}, seat {me.SeatIndex})");
        if (me.Vehicle is not Airliner) { Finish("not back in the shuttle"); return; }

        // ---- in orbit ----
        GD.Print("[shuttleaboard] " + me.PutInOrbit(400));
        await Seconds(3);
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 6),
            $"E stood up in orbit (aboard {me.Aboard}, ride {me.Ride})");
        Expect(await Until(() => me.Floating, 3), $"floating (felt {Str(me.FeltAboard)}, {me.FeltAboard.Length():F2} m/s²)");
        var before = Local(me);
        me.FloatWish = Vector3.Zero;
        await Seconds(3);
        l = Local(me);
        Expect(me.Floating && l.Y > before.Y - 0.2f && (l - before).Length() < 0.6f, $"left alone, not fallen to the floor: {Str(before)} -> {Str(l)}");
        await Shot("floating_flight_deck");
        // round the open end of the opening's rail, then down through it
        bool through = await FloatTo(me, -0.2f, fd + 0.3f, 22.1f, 10) && await FloatTo(me, lx, fd + 0.3f, ShuttleLayout.LadderTopZ + 0.3f, 10)
            && await FloatTo(me, lx, md + 0.6f, 20.8f, 15)
            && await FloatTo(me, 0f, md + 0.5f, 21.3f, 15);
        l = Local(me);
        Expect(through && l.Y < fd - 1f, $"pushed down through the opening to the mid deck {Str(l)}");
        await Seconds(2.5);
        Expect(me.FloatInReach && me.Velocity.Length() < 0.2f, $"let go: held on by what is in reach and stopped ({me.Velocity.Length():F2} m/s, in reach {me.FloatInReach})");
        await Shot("floating_mid_deck");
        Expect(!me.OutByShuttleHatch() && me.Aboard, "the hatch will not open in orbit");
        // to the ladder's foot by the bulkhead, then up along it as one pulls oneself up a ladder
        bool back = await FloatTo(me, lx, md + 0.15f, ShuttleLayout.LadderFootZ + 0.15f, 15) && await FloatTo(me, lx, fd + 0.25f, ShuttleLayout.LadderTopZ + 0.25f, 15)
            && await FloatTo(me, -0.2f, fd + 0.3f, 22.1f, 10)
            && await FloatTo(me, ShuttleLayout.CommanderHip.X, fd + 0.1f, ShuttleLayout.CommanderHip.Z - 0.75f, 15);
        Expect(back, $"pushed back up to the commander's seat {Str(Local(me))}");
        await Seconds(1);
        Expect(me.TryInteract() && await Until(() => me.Vehicle is Airliner && me.SeatIndex == 0, 6), $"E behind the commander's seat floating: the controls ({me.Ride}, prompt was '{me.DeckHint}')");
        bool orbiting = await Until(() => me.Vehicle is Airliner { InSpace: true }, 5);
        double km = me.Origin?.AltitudeOf(me.GlobalPosition) / 1000 ?? double.NaN;
        Expect(orbiting && km > 350, System.FormattableString.Invariant($"still in orbit at the controls ({km:F0} km)"));
        Finish(null);
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        if (_player() is { } p) { p.WalkControls = null; p.FloatWish = null; }
        GD.Print(_failures == 0 ? "[shuttleaboard] RESULT: ok" : $"[shuttleaboard] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
