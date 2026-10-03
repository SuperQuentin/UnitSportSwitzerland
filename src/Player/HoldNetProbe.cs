using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--holdnet A|B</c> with <c>--connect</c> on <c>--world fixture</c> (driven by
/// <c>tools/holdnetcheck.sh</c>, #418): a car carried in another player's aircraft, over the network.
/// Both clients give the A320 the checks' test hold (<see cref="HoldCheck.TestHold"/>).
/// <list type="bullet">
/// <item>A (admin) takes an A320, its ramp down. B drives a car up into the hold and ties it down;
/// A's copy of B's car is carried by A's aircraft, at the spot B says.</item>
/// <item>A shuts the ramp, taxis (thrust, a turn, the brakes), flies (put up at 200 m, banked, then
/// down on its wheels again, braked to a stop): B's car never leaves its spot on either peer.</item>
/// <item>B gets out: the parked car is carried by A's aircraft (its <c>VehicleState.Carrier</c>), and
/// stays on its spot on both peers as A taxis again.</item>
/// <item>B gets back in, still tied down, reverses down the ramp and out; both peers put it on the
/// ground behind the aircraft, no longer carried.</item>
/// </list>
/// </summary>
public partial class HoldNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--holdnet");

    public HoldNetProbe(ItemController items) : base(items, "holdnet", "HN")
    {
        Airliner.ProbeHold = HoldCheck.TestHold();
        // measured as drawn: after the carrier and what it carries have been put where they are this frame
        ProcessPriority = 100;
    }
    public HoldNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private System.Action? _onFrame;
    public override void _Process(double delta) => _onFrame?.Invoke();

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150))
        {
            await Finish(0);
            return;
        }
        Chat?.Send("/login test");   // both: B parks a car it conjured (admin-only-spawning)
        await Seconds(1.5);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private FootPlayer? Other(FootPlayer me)
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != me && !p.Npc) return p;
        return null;
    }

    private static string F(Vector3 v) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.X:F3} {v.Y:F3} {v.Z:F3}");
    private static Vector3 V(string[] w, int at) => new(Float(w[at]), Float(w[at + 1]), Float(w[at + 2]));

    /// <summary>The last "HN role what x y z" heard, its three numbers.</summary>
    private Vector3 Heard3(string role, string what)
    {
        var w = _heard.Last(l => l.Contains($"HN {role} {what}")).Split(' ');
        return V(w, w.Length - 3);
    }

    /// <summary>A point in the carrier's section frame (node space) as this peer draws it.</summary>
    private static Vector3 InFrame(Node3D carrier, Vector3 world) => FootPlayer.HoldFrame(carrier, 0).AffineInverse() * world;

    private VehicleBody? ParkedCar() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>()
        .FirstOrDefault(v => v.Kind == (RideKind)CarCatalog.First && !v.IsQueuedForDeletion());

    /// <summary>Runs <paramref name="each"/> every drawn frame for <paramref name="seconds"/>.</summary>
    private async Task Watch(double seconds, System.Action each)
    {
        _onFrame = each;
        await Seconds(seconds);
        _onFrame = null;
    }

    /// <summary>Sets the parking brake (the command toggles it, and takes effect in the aircraft's next step).</summary>
    private async Task ParkingBrake(Airliner jet, bool on)
    {
        if (jet.State.ParkingBrake != on) jet.Command(AirlinerCommand.ParkingBrake);
        await Until(() => jet.State.ParkingBrake == on, 2);
    }

    private static void Hold(string action, bool on) { if (on) Input.ActionPress(action); else Input.ActionRelease(action); }

    // ---- A: the carrier's pilot ------------------------------------------------------------------

    private async Task RunA(FootPlayer me)
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        Expect(me.SetRide(RideKind.A320), "A takes an A320");
        await Seconds(1);
        if (me.Vehicle is not Airliner jet) { Fail("not an airliner"); return; }
        await ParkingBrake(jet, true);
        jet.ToggleDoor(HoldCheck.RampDoor);
        Expect((jet.DoorsOpen >> HoldCheck.RampDoor & 1) != 0 && jet.State.ParkingBrake,
            $"the ramp down, the parking brake on (doors {jet.DoorsOpen}, parking brake {jet.State.ParkingBrake}, on the ground {jet.State.OnGround}, may open {jet.MayOpenDoors})");
        await Seconds(1);
        Say("carrier");

        if (!await Heard("B", "tied", 90)) { Fail("B never tied its car down in the hold"); return; }
        var spot = Heard3("B", "tied");
        var b = Other(me);
        if (b == null) { Fail("no B here"); return; }
        await Seconds(0.5);
        var seen = InFrame(me, b.GlobalPosition);
        Expect(b.DeckOn == me.Name && seen.DistanceTo(spot) < 0.1f,
            $"A's copy of B's car is carried by A's aircraft ('{b.DeckOn}') at the spot B says ({F(seen)}; B {F(spot)})");

        // the ramp up, taxi: thrust, a turn, the brakes
        jet.ToggleDoor(HoldCheck.RampDoor);
        await ParkingBrake(jet, false);
        float worst = 0f;
        int lost = 0;
        void Track()
        {
            if (Other(me) is not { } copy) return;
            worst = Mathf.Max(worst, InFrame(me, copy.GlobalPosition).DistanceTo(spot));
            if (copy.DeckOn != me.Name) lost++;
        }
        Hold(PlayerInput.Sprint, true);
        await Watch(6, Track);
        Hold(PlayerInput.Sprint, false);
        Hold(PlayerInput.CrouchSlide, true);
        Hold(PlayerInput.MoveRight, true);
        await Watch(4, Track);
        Hold(PlayerInput.MoveRight, false);
        Hold(PlayerInput.Jump, true);
        _onFrame = Track;
        await Until(() => me.GroundSpeed < 0.3f, 40);
        float taxied = worst;
        Say(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"taxied {taxied:F3}"));

        // flown: put up at 200 m and 85 m/s, banked into a turn, then down on its wheels and braked
        var ground = me.GlobalPosition;
        var ahead = -me.GlobalTransform.Basis.Z with { Y = 0 };
        Hold(PlayerInput.Jump, false);
        me.DebugLaunch(ground + Vector3.Up * 200f, ahead.Normalized() * 85f);
        // as --airlinernet puts one up: its gear down until the first step has it airborne
        jet.State.GearDown = true;
        jet.State.Gear = 1f;
        await Seconds(0.5);
        GD.Print($"{Log}   launched: {me.GroundSpeed:F0} m/s, {me.GlobalPosition.Y - ground.Y:F0} m up, parking brake {jet.State.ParkingBrake}, on ground {jet.State.OnGround}");
        Hold(PlayerInput.CrouchSlide, false);
        Hold(PlayerInput.MoveLeft, true);
        await Watch(3, Track);
        Hold(PlayerInput.MoveLeft, false);
        await Watch(4, Track);
        Hold(PlayerInput.MoveRight, true);
        await Watch(3, Track);
        Hold(PlayerInput.MoveRight, false);
        await Watch(2, Track);
        Expect(!jet.State.OnGround && me.GlobalPosition.Y - ground.Y > 50f, $"flew, banked both ways: {me.GroundSpeed:F0} m/s, {me.GlobalPosition.Y - ground.Y:F0} m up");
        Hold(PlayerInput.CrouchSlide, true);
        float flown = worst;
        jet.State.GearDown = true;
        jet.State.Gear = 1f;
        me.DebugLaunch(ground + Vector3.Up * 0.3f, ahead.Normalized() * 15f);
        jet.State.GearDown = true;
        jet.State.Gear = 1f;
        Hold(PlayerInput.Jump, true);
        _onFrame = Track;
        bool landed = await Until(() => jet.State.OnGround && me.GroundSpeed < 0.5f, 40);
        _onFrame = null;
        Hold(PlayerInput.Jump, false);
        Hold(PlayerInput.CrouchSlide, false);
        Expect(landed && me.VehicleHealth > 0f, $"down on its wheels and stopped (health {me.VehicleHealth:F0}, on the ground {jet.State.OnGround}, {me.GroundSpeed:F1} m/s, ride {me.Ride}, lever {jet.State.Lever:F2})");
        Expect(lost == 0 && worst < 0.1f,
            $"A's copy of B's car never left its spot: taxi {taxied * 100f:F1} cm, flight {flown * 100f:F1} cm, all {worst * 100f:F1} cm (frames off the carrier: {lost})");
        await ParkingBrake(jet, true);
        jet.ToggleDoor(HoldCheck.RampDoor);
        Say(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"landed {worst:F3}"));

        // B parks its car in the hold: carried by A's aircraft, and stays put as it taxis again
        if (!await Heard("B", "parked", 30)) { Fail("B never parked its car in the hold"); return; }
        bool there = await Until(() => ParkedCar() is { InHold: true } p && p.Carrier == me.Name, 10);
        var parked = ParkedCar();
        Expect(there, $"A sees B's parked car carried by its aircraft ('{parked?.Carrier}')");
        if (parked == null) return;
        var left = InFrame(me, parked.GlobalPosition);
        jet.ToggleDoor(HoldCheck.RampDoor);
        await ParkingBrake(jet, false);
        worst = 0f;
        Hold(PlayerInput.CrouchSlide, false);
        Hold(PlayerInput.Jump, false);
        Hold(PlayerInput.Sprint, true);
        await Watch(6, () =>
        {
            worst = Mathf.Max(worst, InFrame(me, parked.GlobalPosition).DistanceTo(left));
        });
        Hold(PlayerInput.Sprint, false);
        Hold(PlayerInput.CrouchSlide, true);
        Hold(PlayerInput.Jump, true);
        _onFrame = () => worst = Mathf.Max(worst, InFrame(me, parked.GlobalPosition).DistanceTo(left));
        await Until(() => me.GroundSpeed < 0.3f, 40);
        _onFrame = null;
        Hold(PlayerInput.Jump, false);
        Hold(PlayerInput.CrouchSlide, false);
        Expect(worst < 0.05f, $"the parked car stays on its spot in the hold as A taxis (off by {worst * 100f:F1} cm at most)");
        await ParkingBrake(jet, true);
        jet.ToggleDoor(HoldCheck.RampDoor);
        Say("taxied again");

        // B drives out: both put it on the ground behind the aircraft
        if (!await Heard("B", "out", 60)) { Fail("B never drove out"); return; }
        var outside = Heard3("B", "out");
        await Seconds(0.5);
        b = Other(me);
        var here = b != null ? InFrame(me, b.GlobalPosition) : Vector3.Zero;
        GD.Print($"{Log}   out: B {F(b?.GlobalPosition ?? default)} me {F(me.GlobalPosition)} my frame {F(FootPlayer.HoldFrame(me, 0).Origin)}");
        Expect(b is { DeckOn: "" } && here.DistanceTo(outside) < 0.3f && here.Z > HoldCheck.BayLength * 0.5f + 4f,
            $"A sees B's car out behind the ramp, not carried ('{b?.DeckOn}' at {F(here)}; B {F(outside)})");
        Say("compared");
    }

    // ---- B: the car's driver ---------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        // out from under the airliner A is about to take where both spawned
        me.PlaceAt(me.GlobalPosition + new Vector3(70f, 0f, 0f), 0f);
        await Seconds(1.5);
        Say("ready");
        if (!await Heard("A", "carrier", 40)) { Fail("A never took its carrier"); return; }
        var a = Other(me);
        if (a == null) { Fail("no A here"); return; }
        await Until(() => a.Ride == RideKind.A320 && SectionFrameReady(a), 10);

        // the car, behind the ramp, nose to the hold; driven up into it, stopped, the handbrake
        Expect(me.SetRide((RideKind)CarCatalog.First), "B in a car");
        await Seconds(1);
        var frame = FootPlayer.HoldFrame(a, 0);
        me.PlaceAt(frame * new Vector3(0f, 0.3f, HoldCheck.BayLength * 0.5f + 8f), a.Rotation.Y);
        await Seconds(1.5);
        float throttle = 0.35f, brake = 0f;
        bool handbrake = false;
        me.RideControls = () => new RideInput(throttle, brake, 0f, false, handbrake);
        bool inside = await Until(() =>
        {
            if (me.GroundSpeed > 3f) throttle = 0f; else if (me.GroundSpeed < 1.5f) throttle = 0.35f;
            return me.DeckOn == a.Name && me.DeckPos.Z < 1f;
        }, 40);
        throttle = 0f;
        brake = 1f;
        await Until(() => me.GroundSpeed < 0.2f, 10);
        brake = 0f;
        handbrake = true;
        bool tied = await Until(() => me.TiedDown, 5);
        handbrake = false;
        Expect(inside && tied, $"B drove up into A's hold and tied the car down ('{me.DeckOn}', tied {me.TiedDown}, at {F(me.DeckPos)})");
        var spot = me.DeckPos;
        Say("tied " + F(spot));

        // through the taxi and the flight: on its spot, tied, carried, here too
        float worst = 0f;
        int lost = 0;
        _onFrame = () =>
        {
            if (!me.TiedDown || me.DeckOn != a.Name) lost++;
            worst = Mathf.Max(worst, InFrame(a, me.GlobalPosition).DistanceTo(spot));
        };
        bool landed = await Heard("A", "landed", 180);
        _onFrame = null;
        Expect(landed, "A landed");
        Expect(lost == 0 && worst < 0.05f, $"B's car stayed tied on its spot through the taxi and the flight (off by {worst * 100f:F1} cm at most, frames off: {lost})");

        // out of the car: parked in the hold, carried by A's aircraft
        me.RideControls = null;
        me.ExitVehicle();
        bool parkedHere = await Until(() => ParkedCar() is { InHold: true } p && p.Carrier == a.Name, 10);
        var parked = ParkedCar();
        Expect(parkedHere, $"B's parked car stands in A's hold ('{parked?.Carrier}')");
        Say("parked");
        if (parked == null) return;
        var left = parked.CarrierPos;
        worst = 0f;
        _onFrame = () => { if (IsInstanceValid(parked)) worst = Mathf.Max(worst, InFrame(a, parked.GlobalPosition).DistanceTo(left)); };
        if (!await Heard("A", "taxied again", 90)) { Fail("A never taxied again"); return; }
        _onFrame = null;
        Expect(worst < 0.05f, $"here too the parked car stays on its spot as A taxis (off by {worst * 100f:F1} cm at most)");

        // back in and out: still tied down, reversed down the ramp
        bool back = false;
        VehicleManager.Instance?.Claim(parked, state => { me.TakeVehicle(state, 0); back = true; });
        Expect(await Until(() => back && me.Vehicle is Car, 10) && me.TiedDown && me.DeckOn == a.Name,
            $"B back in its car: tied down in A's hold ('{me.DeckOn}', tied {me.TiedDown})");
        brake = 0.4f;
        me.RideControls = () => new RideInput(0f, brake, 0f, false, false);
        bool outside = await Until(() =>
        {
            brake = me.GroundSpeed > 3f ? 0f : 0.4f;
            return me.DeckOn == "" && InFrame(a, me.GlobalPosition).Z > HoldCheck.BayLength * 0.5f + 6f;
        }, 40);
        // the handbrake alone: a brake pedal at a standstill is reverse
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, true);
        await Until(() => me.GroundSpeed < 0.2f, 10);
        await Seconds(1);
        var at = InFrame(a, me.GlobalPosition);
        Expect(outside && me.IsOnFloor() && Mathf.Abs(at.Y) < 0.3f, $"B reversed down the ramp, out on the ground behind it ({F(at)}, '{me.DeckOn}')");
        GD.Print($"{Log}   out: me {F(me.GlobalPosition)} A {F(a.GlobalPosition)} A's frame {F(FootPlayer.HoldFrame(a, 0).Origin)}");
        Say("out " + F(at));
        await Heard("A", "compared", 20);
        me.RideControls = null;
    }

    private static bool SectionFrameReady(FootPlayer a) => FootPlayer.SectionFrame(a, 0) is { } f && f.IsInsideTree();
}
