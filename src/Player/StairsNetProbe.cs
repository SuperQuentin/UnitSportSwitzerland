using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--stairsnet A|B</c> with <c>--connect</c> on <c>--world fixture</c> (driven by
/// <c>tools/stairsnetcheck.sh</c>, #417): riding on airstairs another player drives.
/// <list type="bullet">
/// <item>A (admin) parks an A320 with L1 and L2 open, takes airstairs 10 m out from L2;</item>
/// <item>B stands on A's platform; A drives at L2 and lets go: the stairs dock and the platform rises
/// with B on it, carried all the way;</item>
/// <item>both peers agree where B stands on the stairs, and on the platform's height;</item>
/// <item>B walks over the plate through L2 into the cabin, back out, and down the flight to the ground.</item>
/// </list>
/// </summary>
public partial class StairsNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--stairsnet");

    public StairsNetProbe(ItemController items) : base(items, "stairsnet", "SN") { }
    public StairsNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private FootPlayer? Other(FootPlayer me)
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != me && !p.Npc) return p;
        return null;
    }

    private static VehicleBody? Plane() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>()
        .FirstOrDefault(v => v.Kind == RideKind.A320 && !v.IsQueuedForDeletion() && v.Posed);

    private static Transform3D FrameOf(Node3D host) => (AirstairsDock.FrameOf(host, 0) ?? host).GlobalTransform.Orthonormalized();

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private async Task RunA(FootPlayer me)
    {
        Chat?.Send("/login test");
        await Seconds(1.5);
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        // an A320 parked here with its left doors open
        Expect(me.SetRide(RideKind.A320), "A takes an A320");
        await Seconds(2);
        if (me.Vehicle is not Airliner jet) { Fail("not an airliner"); return; }
        jet.ToggleDoor(0);
        jet.ToggleDoor(2);
        me.ExitVehicle();
        if (!await Until(() => me.Ride == RideKind.OnFoot && Plane() is { DoorsOpen: 5 }, 10)) { Fail("the A320 is not parked with L1 and L2 open"); return; }
        await Seconds(2);
        var plane = Plane()!;
        var deck = plane.Ride.Decks[0];
        var sill = AirstairsDock.LocalSill(deck, 2)!.Value;
        var f = FrameOf(plane);
        var (dockAt, dockYaw, _) = AirstairsDock.Pose(f * sill.Edge, (f.Basis * sill.Out) with { Y = 0 }, plane.GlobalPosition.Y);
        var outward = new Vector3(Mathf.Sin(dockYaw), 0, Mathf.Cos(dockYaw));
        var side = new Vector3(outward.Z, 0, -outward.X);

        // airstairs 10 m out from L2, a little askew
        me.PlaceAt(dockAt + outward * 10f + side * 1f + Vector3.Up * 0.3f, dockYaw + 0.15f);
        await Seconds(2);
        Expect(me.SetRide(RideKind.Airstairs) && me.Vehicle is Airstairs, "A takes airstairs");
        if (me.Vehicle is not Airstairs stairs) { Fail("not airstairs"); return; }
        await Seconds(1.5);
        Say("board");
        if (!await Heard("B", "aboard", 40)) { Fail("B never got on the stairs"); return; }

        // drive at L2, let go: it docks and rises, B on the platform
        bool letGo = false;
        me.RideControls = () =>
        {
            if (letGo) return new RideInput(0f, 0f, 0f, false);
            var to = (AirstairsDock.Lip(new Transform3D(new Basis(Vector3.Up, dockYaw), dockAt)) - AirstairsDock.Lip(me.GlobalTransform)) with { Y = 0 };
            if (to.Length() < 2.5f) { letGo = true; return new RideInput(0f, 0f, 0f, false); }
            float e = MathX.WrapAngle(Mathf.Atan2(-to.X, -to.Z) - me.Rotation.Y);
            return new RideInput(me.GroundSpeed < 1.2f ? 0.8f : 0f, 0f, Mathf.Clamp(-e * 2.5f, -1f, 1f), false);
        };
        float rideFrom = me.GlobalPosition.DistanceTo(dockAt);
        bool docked = await Until(() => stairs.Docked != null && Mathf.Abs(stairs.Height - stairs.TargetHeight) < 0.005f, 60);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        Expect(docked, $"A drove {rideFrom:F1} m to L2 and it docked (door {stairs.Docked?.Door}, platform {stairs.Height:F3})");
        await Seconds(1.5);
        Say($"docked {F(stairs.Height)}");

        // where B is on A's stairs, as A draws it, against what B says
        if (!await Heard("B", "on platform", 20)) { Fail("B is not on the platform"); return; }
        var w = _heard.Last(l => l.Contains("SN B on platform")).Split(' ');
        var bSays = new Vector3(Float(w[^4]), Float(w[^3]), Float(w[^2]));
        float bHeight = Float(w[^1]);
        var b = Other(me);
        if (b == null || me.Visual == null) { Fail("no B or nothing drawn here"); return; }
        var here = me.Visual.GlobalTransform.Orthonormalized().AffineInverse() * b.GlobalPosition;
        Expect(b.DeckOn == me.Name, $"A's copy of B is on A's stairs (deck '{b.DeckOn}')");
        Expect(here.DistanceTo(bSays) < 0.1f, $"A sees B where B is on the platform (B {bSays}, A {here}, {here.DistanceTo(bSays) * 100:F1} cm)");
        Expect(Mathf.Abs(bHeight - stairs.Height) < 0.02f, $"both peers have the platform at one height (A {stairs.Height:F3}, B {bHeight:F3})");
        Say("compared");
        if (!await Heard("B", "down", 90)) Fail("B never came back down");
        me.RideControls = null;
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        me.PlaceAt(me.GlobalPosition + new Vector3(70f, 0f, 0f), 0f);
        await Seconds(1.5);
        Say("ready");
        if (!await Heard("A", "board", 90)) { Fail("A never took the stairs"); return; }
        var a = Other(me);
        if (a == null) { Fail("no A here"); return; }
        bool drawn = await Until(() => a.RideModel is Airstairs && a.Visual != null, 10);
        if (!drawn) { Fail("A's stairs are not here"); return; }
        var ride = (Airstairs)a.RideModel!;
        // onto A's platform, a step behind the gate
        Vector3 Spot(float authoredZ) => FrameOf(a) * new Vector3(0f, AirstairsLayout.FloorAt(ride.Height, authoredZ) + 0.05f, -authoredZ);
        bool aboard = false;
        for (int i = 0; i < 300 && !aboard; i++)
        {
            if (i % 60 == 0) me.PlaceAt(Spot(3f), a.Rotation.Y);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            aboard = me.Aboard && me.DeckOn == a.Name;
        }
        Expect(aboard, $"B stands on A's stairs (deck '{me.DeckOn}', B's copy's platform {ride.Height:F2})");
        await Seconds(1);
        Say("aboard");

        // carried while A drives and docks; on the platform all the way
        float worst = 0f;
        bool docked = false;
        for (int i = 0; i < 90 * 60 && !docked; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (me.Aboard)
            {
                var l = FrameOf(a).AffineInverse() * me.GlobalPosition;
                worst = Mathf.Max(worst, Mathf.Abs(l.Y - AirstairsLayout.FloorAt(ride.Height, -l.Z)));
            }
            else
            {
                if (worst < 99f)
                {
                    var l = FrameOf(a).AffineInverse() * me.GlobalPosition;
                    GD.Print($"{Log} off the deck at frame {i}: local {l}, platform {ride.Height:F3} (deck {ride.DeckHeight:F3}), at door {ride.AtDoor}, decks {me.DeckSetsBuilt}, floor {me.IsOnFloor()}, v {me.Velocity}, {me.WalkState}");
                }
                worst = 99f;
            }
            if (i % 120 == 0)
            {
                var l = FrameOf(a).AffineInverse() * me.GlobalPosition;
                GD.Print($"{Log}   t{i}: local {l}, platform {ride.Height:F3}, aboard '{me.DeckOn}', A at {a.GlobalPosition}");
            }
            docked = _heard.Any(h => h.Contains("SN A docked"));
        }
        await Seconds(1.5);
        var at = FrameOf(a).AffineInverse() * me.GlobalPosition;
        Expect(docked && me.Aboard && worst < 0.15f,
            $"B rode the stairs to L2 and up, aboard all the way (worst {worst * 100:F0} cm off the floor, platform {ride.Height:F3}, at {at})");
        Say($"on platform {F(at.X)} {F(at.Y)} {F(at.Z)} {F(ride.Height)}");
        await Heard("A", "compared", 20);

        // over the plate through L2 into the cabin, back out, down the flight to the ground
        var plane = Plane();
        if (plane == null) { Fail("no parked A320 here"); return; }
        Vector3 Cabin(float x, float z) => FrameOf(plane) * AircraftMeshBuilder.Flip(new Vector3(x, A320Layout.FloorY + 0.05f, z));
        bool inside = await Walk(me, () => Cabin(0.9f, A320Layout.AftDoorZ), 20) && await Walk(me, () => Cabin(0f, A320Layout.AftDoorZ), 10);
        Expect(inside && me.DeckOn == "v:" + plane.Name, $"B walked off the stairs through L2 into the cabin (deck '{me.DeckOn}')");
        bool back = await Walk(me, () => Spot(3f), 20);
        Expect(back && me.DeckOn == a.Name, $"B walked back out onto the platform (deck '{me.DeckOn}')");
        bool down = await Walk(me, () => Spot(-6f), 30);
        float over = me.GlobalPosition.Y - FrameOf(a).Origin.Y;
        Expect(down && !me.Aboard && Mathf.Abs(over) < 0.3f, $"B walked down the flight to the ground ({over:F2} m up, aboard '{me.DeckOn}')");
        Say("down");
    }

    private async Task<bool> Walk(FootPlayer me, System.Func<Vector3> target, double seconds)
    {
        me.WalkControls = () =>
        {
            var to = (target() - me.GlobalPosition) with { Y = 0 };
            return (to.Length() < 0.15f ? Vector3.Zero : to.Normalized(), false);
        };
        bool there = await Until(() => ((target() - me.GlobalPosition) with { Y = 0 }).Length() < 0.4f, seconds);
        me.WalkControls = () => (Vector3.Zero, false);
        await Seconds(0.5);
        return there;
    }
}
