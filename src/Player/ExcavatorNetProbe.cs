using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Vehicles;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--excavatornet A|B</c> with <c>--connect</c> on <c>--world fixture</c> (driven by
/// <c>tools/excavatornetcheck.sh</c>, #611): an excavator another player digs with, seen from the
/// second peer, which is where a replicated arm goes wrong if it goes wrong at all.
/// <list type="bullet">
/// <item>A (admin) takes an excavator, drives it, puts it in dig mode and works the arm on the real
/// bindings: slews, raises the boom, runs the stick out, curls the bucket;</item>
/// <item>B finds A's copy and draws its arm at A's angles (the pose is all a copy is drawn from), and
/// A's copy where A is;</item>
/// <item>A gets out and leaves it parked; B's parked machine keeps the arm A left, in its state and
/// in its drawing (the flags are all a parked one is drawn from).</item>
/// </list>
/// </summary>
public partial class ExcavatorNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--excavatornet");

    public ExcavatorNetProbe(ItemController items) : base(items, "excavatornet", "EX") { }
    public ExcavatorNetProbe() : this(null!) { }

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

    private static VehicleBody? Parked() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>()
        .FirstOrDefault(v => v.Kind == RideKind.Excavator && !v.IsQueuedForDeletion());

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private async Task Hold(string action, double seconds)
    {
        XrPad.Press(action, true);
        await Seconds(seconds);
        XrPad.Press(action, false);
        await Seconds(0.2);
    }

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

        Expect(me.SetRide(RideKind.Excavator) && me.Vehicle is Excavator, "A takes an excavator");
        if (me.Vehicle is not Excavator ex) { Fail("not an excavator"); return; }
        await Seconds(1);
        // a few metres on its tracks, then dig mode and the arm on the real bindings
        me.RideControls = () => new RideInput(1f, 0f, 0.3f, false);
        await Seconds(2);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(1);
        XrPad.Press(PlayerInput.DigMode, true);
        await Seconds(0.1);
        XrPad.Press(PlayerInput.DigMode, false);
        await Seconds(0.2);
        Expect(ex.Digging, "A is digging");
        await Hold(PlayerInput.ArmSlewLeft, 1.5);
        await Hold(PlayerInput.ArmBoomUp, 1.2);
        await Hold(PlayerInput.ArmStickOut, 1.0);
        await Hold(PlayerInput.ArmBucketCurl, 0.8);
        await Seconds(1.5);
        var at = me.GlobalPosition;
        Say($"posed {F(ex.Slew)} {F(ex.Boom)} {F(ex.Stick)} {F(ex.Bucket)} {F(at.X)} {F(at.Z)}");
        if (!await Heard("B", "seen", 30)) { Fail("B never compared the arm"); return; }

        // out, leaving it parked with its arm where it is
        me.RideControls = null;
        me.ExitVehicle();
        if (!await Until(() => me.Ride == RideKind.OnFoot && Parked() != null, 10)) { Fail("the excavator is not parked"); return; }
        Say($"parked {F(ex.Slew)} {F(ex.Boom)} {F(ex.Stick)} {F(ex.Bucket)}");
        if (!await Heard("B", "compared", 30)) { Fail("B never compared the parked one"); return; }

        // the wheel loader (#612): bent, its arm up and its bucket tipped, seen from B
        me.PlaceAt(me.GlobalPosition + new Vector3(12f, 0.5f, 0f), me.Rotation.Y);
        await Seconds(1);
        Expect(me.SetRide(RideKind.WheelLoader) && me.Vehicle is WheelLoader, "A takes a wheel loader");
        if (me.Vehicle is not WheelLoader loader) { Fail("not a wheel loader"); return; }
        await Seconds(1);
        me.RideControls = () => new RideInput(0.5f, 0f, -0.6f, false);
        await Seconds(2);
        // braked to a stop, then let go: held on, a loader's brake would back it away
        me.RideControls = () => new RideInput(0f, 1f, -0.6f, false);
        await Until(() => me.GroundSpeed < 0.1f, 5);
        me.RideControls = () => new RideInput(0f, 0f, -0.6f, false);
        await Seconds(1);
        XrPad.Press(PlayerInput.DigMode, true);
        await Seconds(0.1);
        XrPad.Press(PlayerInput.DigMode, false);
        await Seconds(0.2);
        await Hold(PlayerInput.ArmBoomUp, 2.0);
        await Hold(PlayerInput.ArmBucketDump, 0.6);
        await Seconds(1.5);
        at = me.GlobalPosition;
        Say($"loader {F(loader.Articulation)} {F(loader.Lift)} {F(loader.Tilt)} {F(at.X)} {F(at.Z)}");
        if (!await Heard("B", "loader seen", 30)) Fail("B never compared the loader");
        me.RideControls = null;
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        me.PlaceAt(me.GlobalPosition + new Vector3(0f, 0f, 25f), 0f);
        await Seconds(1.5);
        Say("ready");
        if (!await Heard("A", "posed", 90)) { Fail("A never posed its arm"); return; }
        var w = _heard.Last(l => l.Contains("EX A posed")).Split(' ');
        var said = new Vector4(Float(w[^6]), Float(w[^5]), Float(w[^4]), Float(w[^3]));
        var aAt = new Vector2(Float(w[^2]), Float(w[^1]));
        var a = Other(me);
        if (a == null) { Fail("no A here"); return; }
        bool drawn = await Until(() => a.RideModel is Excavator && ExcavatorMeshBuilder.ArmOf(a.Visual) is { } arm
            && (arm.Drawn - said).Length() < 0.03f, 10);
        var seen = ExcavatorMeshBuilder.ArmOf(a.Visual)?.Drawn ?? new Vector4(float.NaN, 0, 0, 0);
        Expect(drawn, $"B draws A's arm at A's angles (A {said}, B {seen})");
        float off = new Vector2(a.GlobalPosition.X - aAt.X, a.GlobalPosition.Z - aAt.Y).Length();
        Expect(off < 0.5f, $"B has A's excavator where A has it ({off:F2} m)");
        Say("seen");

        if (!await Heard("A", "parked", 60)) { Fail("A never parked it"); return; }
        w = _heard.Last(l => l.Contains("EX A parked")).Split(' ');
        var left = new Vector4(Float(w[^4]), Float(w[^3]), Float(w[^2]), Float(w[^1]));
        // what it keeps is its state, from the parked flags (eight bits a joint); what it draws, the
        // same, where it is drawn at all (a headless client draws no parked vehicle)
        static Vector4 Of(Excavator e) => new(e.Slew, e.Boom, e.Stick, e.Bucket);
        bool parked = await Until(() => Parked() is { Ride: Excavator k } p && (Of(k) - left).Length() < 0.03f
            && (p.Visual == null || ExcavatorMeshBuilder.ArmOf(p.Visual) is { } arm && (arm.Drawn - left).Length() < 0.03f), 15);
        var body = Parked();
        var kept = body?.Ride is Excavator kb ? Of(kb) : new Vector4(float.NaN, 0, 0, 0);
        var shown = ExcavatorMeshBuilder.ArmOf(body?.Visual)?.Drawn ?? new Vector4(float.NaN, 0, 0, 0);
        Expect(parked, $"B's parked excavator keeps the arm A left (A {left}, kept {kept}, drawn {shown})");
        Say("compared");

        if (!await Heard("A", "loader", 60)) { Fail("A never worked the loader"); return; }
        w = _heard.Last(l => l.Contains("EX A loader")).Split(' ');
        var bent = new Vector3(Float(w[^5]), Float(w[^4]), Float(w[^3]));
        var lAt = new Vector2(Float(w[^2]), Float(w[^1]));
        bool loaderDrawn = await Until(() => a.RideModel is WheelLoader && WheelLoaderMeshBuilder.FrontOf(a.Visual) is { } f
            && (f.Drawn - bent).Length() < 0.03f, 10);
        var seenFront = WheelLoaderMeshBuilder.FrontOf(a.Visual)?.Drawn ?? new Vector3(float.NaN, 0, 0);
        Expect(loaderDrawn, $"B draws A's loader bent, lifted and tipped as A has it (A {bent}, B {seenFront})");
        float lOff = new Vector2(a.GlobalPosition.X - lAt.X, a.GlobalPosition.Z - lAt.Y).Length();
        Expect(lOff < 0.2f, $"B has A's loader where A has it ({lOff:F2} m)");
        Say("loader seen");
    }
}
