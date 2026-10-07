using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Vehicles;
using UnitSport.XR;

namespace UnitSport.Items;

/// <summary>
/// <c>--bednet A|B</c> with <c>--connect</c> on <c>fixture:flat</c> (driven by
/// <c>tools/bednetcheck.sh</c>, #615): pallets set into a tipper's body over the network, both ways
/// the server allows.
/// <list type="bullet">
/// <item><b>Parked</b>: A (admin) parks an empty tipper (its own, so A is its authority), takes a
/// telehandler with a pallet up on its forks and lowers it into the body. The server has A's
/// vehicle take it (<c>VehicleManager.LoadBed</c>); B sees the parked tipper's replicated body load.</item>
/// <item><b>Driven</b>: B gets into that tipper (the load comes with it), tips it out on the real
/// binding; A sees the pallet set down behind it. Then A lowers another pallet into the body B is
/// driving: the server hands it to B, B's body holds it, and A sees it in B's pose.</item>
/// </list>
/// </summary>
public partial class BedNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--bednet");

    public BedNetProbe(ItemController items) : base(items, "bednet", "BN") { }
    public BedNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private const byte FirstLoad = 40, SecondLoad = 0x80 | 90;
    private static readonly RideKind Tipper = (RideKind)104;

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
        .FirstOrDefault(v => v.Kind == Tipper && !v.Wrecked && !v.IsQueuedForDeletion());

    private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private async Task Hold(string action, double seconds, System.Func<bool>? done = null)
    {
        XrPad.Press(action, true);
        double end = GameClock.Now + seconds;
        while (GameClock.Now < end && done?.Invoke() != true) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        XrPad.Press(action, false);
        await Seconds(0.1);
    }

    private async Task Tap(string action)
    {
        XrPad.Press(action, true);
        await Seconds(0.1);
        XrPad.Press(action, false);
        await Seconds(0.2);
    }

    /// <summary>A's telehandler with a pallet up on its forks, its boom run out past its nose by a tipper's half width.</summary>
    private async Task<Telehandler?> Forks(FootPlayer me, byte load)
    {
        if (me.Vehicle is not Telehandler)
        {
            Expect(me.SetRide(RideKind.Telehandler) && me.Vehicle is Telehandler, "A takes a telehandler");
            await Seconds(1);
            await Tap(PlayerInput.DigMode);
        }
        if (me.Vehicle is not Telehandler boom) return null;
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        var shape = TruckMeshBuilder.TipperBed(HeavyCatalog.For(Tipper)!, 0f);
        float half = HeavyCatalog.For(Tipper)!.Sections[0].Width * 0.5f;
        var (box, size) = boom.ParkedBox;
        float clear = -(box.Z - size.Z * 0.5f) + half + 0.5f;
        await Hold(PlayerInput.ArmBoomUp, 8, () => boom.ForkHeight > shape.Floor.Y + 0.8f);
        await Hold(PlayerInput.ShiftUp, 8, () => -(boom.TinesFrame * new Vector3(0f, 0f, -Pallets.LoadAhead)).Z > clear);
        await Hold(PlayerInput.ArmBoomUp, 8, () => boom.ForkHeight > shape.Floor.Y + 0.8f);
        // given up there (lowered tines with one on them set it down at once): the take is the
        // other checks', this one is what goes into a body
        boom.Carrying = Pallets.Carried(load);
        await Seconds(0.5);
        return boom;
    }

    /// <summary>The telehandler stood square to a tipper's side, the pallet it holds over the middle of the body's floor.</summary>
    private static void Over(FootPlayer me, Telehandler boom, Transform3D host, BedShape bed)
    {
        var rides = boom.TinesFrame * new Vector3(0f, 0f, -Pallets.LoadAhead);
        var floor = host * bed.Floor;
        float yaw = host.Basis.GetEuler().Y - Mathf.Pi * 0.5f;
        var at = floor - new Basis(Vector3.Up, yaw) * rides;
        me.PlaceAt(new Vector3(at.X, me.GlobalPosition.Y + 0.3f, at.Z), yaw);
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

        // ---- a parked tipper of A's own, 14 m ahead ----------------------------------------------
        var ahead = (-me.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
        me.PlaceAt(me.GlobalPosition + ahead * 14f + Vector3.Up * 0.5f, me.GlobalRotation.Y + Mathf.Pi * 0.5f);
        await Seconds(1);
        Expect(me.SetRide(Tipper) && me.Heavy is { HasBed: true }, "A takes a tipper");
        await Seconds(1.5);
        me.ExitVehicle();
        if (!await Until(() => me.Ride == RideKind.OnFoot && Parked() is { BedLoad: 0 }, 15)) { Fail("the tipper is not parked"); return; }
        var parked = Parked()!;
        await Seconds(2);
        // back off its side, then the telehandler
        me.PlaceAt(parked.GlobalPosition + parked.GlobalTransform.Basis.X * 9f + Vector3.Up * 0.5f, me.GlobalRotation.Y);
        await Seconds(1);
        if (await Forks(me, FirstLoad) is not { } boom) { Fail("no telehandler"); return; }
        var bed = ((IBed)parked.Ride).Bed;
        Over(me, boom, parked.GlobalTransform, bed);
        await Seconds(1.5);
        await Hold(PlayerInput.ArmBoomDown, 12, () => boom.Carrying == 0);
        await Seconds(1);
        int first = parked.BedLoad;
        Expect(boom.Carrying == 0 && Pallets.LoadCarried(first) == FirstLoad,
            $"A lowers a pallet into its parked tipper: the forks empty, the body's load {first}");
        Say($"loaded {parked.Name} {first}");

        // ---- B takes it, tips it out; A sees it set down -------------------------------------------
        if (!await Heard("B", "tipped out", 90)) { Fail("B never tipped the pallet out"); return; }
        bool down = await Until(() => PalletService.Instance?.Loose.Values.Any(p => p.Load == FirstLoad) == true, 15);
        Expect(down, "A sees the pallet B tipped out set down on the ground");
        Say("loose seen");

        // ---- A sets another into the body B drives -------------------------------------------------
        if (!await Heard("B", "body down", 60)) { Fail("B never brought the body down"); return; }
        if (Other(me) is not { } b || b.RideModel is not IBed { HasBed: true } bBed) { Fail("B is not on a tipper here"); return; }
        if (await Forks(me, SecondLoad) is not { } boom2) { Fail("no telehandler"); return; }
        Over(me, boom2, b.GlobalTransform, bBed.Bed);
        await Seconds(1.5);
        await Hold(PlayerInput.ArmBoomDown, 12, () => boom2.Carrying == 0);
        // what A has of B is B's pose
        bool inB = await Until(() => Other(me) is { } o && PalletService.BedOf(o.Ride, o.Anim) is { } c && Pallets.LoadCarried(c) == SecondLoad, 10);
        Expect(boom2.Carrying == 0 && inB,
            $"A lowers a pallet into the body B drives: the forks empty, B's pose has it ({(Other(me) is { } ob ? PalletService.BedOf(ob.Ride, ob.Anim) : null)})");
        Say("loaded B");
        if (!await Heard("B", "holds it", 30)) Fail("B never had the pallet");
        me.RideControls = null;
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 120)) { Fail("A never said hello"); return; }
        Say("ready");

        // ---- the parked tipper with A's pallet in it -----------------------------------------------
        if (!await Heard("A", "loaded", 120)) { Fail("A never loaded the tipper"); return; }
        var w = _heard.Last(l => l.Contains("BN A loaded ")).Split(' ');
        string name = w[^2];
        int first = int.Parse(w[^1], CultureInfo.InvariantCulture);
        bool seen = await Until(() => VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(name) is { } v && v.BedLoad == first, 10);
        var parked = VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(name);
        Expect(seen, $"B has the parked tipper's body holding A's pallet ({parked?.BedLoad}, A {first})");
        if (parked == null) return;

        // ---- B gets in, tips it out ----------------------------------------------------------------
        me.PlaceAt(parked.GlobalPosition + parked.GlobalTransform.Basis.X * -3f + Vector3.Up * 0.5f, me.GlobalRotation.Y);
        await Seconds(1.5);
        bool took = false;
        VehicleManager.Instance!.Claim(parked, state => { me.TakeVehicle(state, 0); took = true; });
        await Until(() => took, 10);
        Expect(took && me.Vehicle is IBed { HasBed: true } held && held.BedLoad == first, $"B gets into it, the pallet in its body ({(me.Vehicle as IBed)?.BedLoad})");
        if (me.Vehicle is not IBed bed) return;
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(1);
        await Tap(PlayerInput.Destination);
        bool out_ = await Until(() => bed.BedLoad == 0, 10);
        Expect(out_ && PalletService.Instance?.Loose.Values.Any(p => p.Load == FirstLoad) == true, "B tips it out: the body empty, the pallet set down");
        Say("tipped out");
        if (!await Heard("A", "loose seen", 30)) { Fail("A never saw it"); return; }
        await Tap(PlayerInput.Destination);
        await Until(() => !bed.BedUp && me.Visual is not HeavyRig { TippedShown: > 0f }, 6);
        await Seconds(1);
        Say("body down");

        // ---- A sets another into the body B drives -------------------------------------------------
        if (!await Heard("A", "loaded B", 120)) { Fail("A never loaded B's body"); return; }
        bool holds = await Until(() => Pallets.LoadCarried(bed.BedLoad) == SecondLoad, 10);
        Expect(holds, $"the server hands B the pallet A lowered into B's body ({bed.BedLoad})");
        Say("holds it");
        me.RideControls = null;
    }
}
