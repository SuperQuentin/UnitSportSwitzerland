using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.XR;

namespace UnitSport.Items;

/// <summary>
/// <c>--sitepalletnet A|B</c> with <c>--connect</c> on the generated world at a building site
/// (driven by <c>tools/sitepalletnetcheck.sh</c>, #615): a site's pallet of materials forked by a
/// telehandler, seen from the second peer.
/// <list type="bullet">
/// <item>A (admin: a telehandler out of nothing is an admin's) finds the nearest site pallet
/// (<c>&lt;building&gt;:c&lt;slot&gt;</c>), lines a telehandler up across its runners from the free side,
/// drives in and lifts it on the real binding;</item>
/// <item>B passes only on what reached it: the pallet taken by the server (which worked the site
/// pallet out from the tile's own files), A's telehandler carrying it in its own pose, drawn on its
/// boom;</item>
/// <item>A lowers it; B sees a loose pallet with the same load set down where A's forks held it, and
/// A's forks empty;</item>
/// <item>then a wheel loader's bucket scoops that pallet up and dumps it (#615): B sees it in the
/// loader's own pose and drawn in its bucket, then tipped out.</item>
/// </list>
/// </summary>
public partial class SitePalletNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--sitepalletnet");

    public SitePalletNetProbe(ItemController items) : base(items, "sitepalletnet", "SP") { }
    public SitePalletNetProbe() : this(null!) { }

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

    private static PalletNode? NearestSitePallet(Vector3 from) => PalletNode.All.Values
        .Where(n => !n.Taken && n.IsInsideTree() && Pallets.TryParse(n.Id, out var r) && r.Source == PalletSource.Site)
        .OrderBy(n => n.GlobalPosition.DistanceSquaredTo(from)).FirstOrDefault();

    private static float Yaw(Vector3 toward) => Mathf.Atan2(-toward.X, -toward.Z);

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

        // the dormant layer draws the site's pallets with its machines, round the spawn
        if (!await Until(() => NearestSitePallet(me.GlobalPosition) != null, 60)) { Fail("no site pallet drawn near the spawn"); return; }
        var pallet = NearestSitePallet(me.GlobalPosition)!;
        Expect(me.SetRide(RideKind.Telehandler) && me.Vehicle is Telehandler, $"A takes a telehandler for {pallet.Id}");
        if (me.Vehicle is not Telehandler th) { Fail("not a telehandler"); return; }

        // across its runners (they run along the row of materials), from whichever side is clear
        var across = (pallet.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
        var space = me.GetWorld3D().DirectSpaceState;
        Vector3? side = null;
        foreach (var dir in new[] { across, -across })
        {
            var from = pallet.GlobalPosition + Vector3.Up * 1.2f + dir * 0.9f;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + dir * 7.5f));
            if (hit.Count == 0) { side = dir; break; }
        }
        if (side is not { } outward) { Fail($"no clear side to come at {pallet.Id} from"); return; }
        var tines = th.TinesFrame.Origin;
        var right = new Vector3(outward.Z, 0, -outward.X);
        var at = pallet.GlobalPosition + outward * (-tines.Z + Pallets.LoadAhead + 3f) - right * tines.X;
        if (me.Terrain != null && me.Terrain.TryGetHeight(at, out float g)) at.Y = g + 0.3f;
        me.PlaceAt(at, Yaw(-outward));
        await Seconds(2);

        XrPad.Press(PlayerInput.DigMode, true);
        await Seconds(0.1);
        XrPad.Press(PlayerInput.DigMode, false);
        await Seconds(0.2);
        float Ahead() => -((me.GlobalTransform * th.TinesFrame).AffineInverse() * pallet.GlobalPosition).Z;
        me.RideControls = () => new RideInput(0.25f, 0f, 0f, false);
        await Until(() => Ahead() < Pallets.LoadAhead + 0.05f || me.Velocity.Length() < 0.01f && me.GroundSpeed > 0.3f, 15);
        // no handbrake: on a telehandler the brake reverses from a standstill
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Until(() => me.GroundSpeed < 0.05f, 5);
        XrPad.Press(PlayerInput.ArmBoomUp, true);
        bool lifted = await Until(() => th.Carrying != 0, 6);
        await Seconds(1.0);
        XrPad.Press(PlayerInput.ArmBoomUp, false);
        Expect(lifted, $"A's telehandler lifts {pallet.Id} ({th.Carrying}, {Ahead():F2} m ahead of the heel)");
        if (!lifted) return;
        await Seconds(1);
        Say($"lifted {pallet.Id} {th.Carrying}");
        if (!await Heard("B", "seen", 30)) { Fail("B never saw it lifted"); return; }

        // out of the cramped yard to open ground with it on the forks, so the loader has room after
        if (OpenGround(me, pallet.GlobalPosition) is not { } open) { Fail("no open ground near the site"); return; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        var toward = (open - me.GlobalPosition) with { Y = 0 };
        me.PlaceAt(open, Yaw(toward.Normalized()));
        await Seconds(2);
        Expect(th.Carrying != 0, $"carried out to open ground, {toward.Length():F0} m off, the pallet still on the forks");

        XrPad.Press(PlayerInput.ArmBoomDown, true);
        bool down = await Until(() => th.Carrying == 0, 12);
        XrPad.Press(PlayerInput.ArmBoomDown, false);
        var set = PalletService.Instance?.Loose.Values.OrderBy(p => p.Id).LastOrDefault();
        Expect(down && set != null, $"lowered, it is set down ({(set == null ? "-" : Pallets.LooseId(set.Id))})");
        if (set == null) return;
        await Seconds(1);
        Say($"set {Pallets.LooseId(set.Id)} {set.Load}");
        if (!await Heard("B", "set seen", 30)) { Fail("B never saw it set down"); return; }
        await ScoopWithLoader(me, set);
    }

    /// <summary>
    /// The wheel loader's bucket (#615): off the telehandler, onto a loader, the bucket down and
    /// level, driven at the pallet just set down until it is over the bucket's floor, curled and the
    /// arm lifted (scooped), then dumped (tipped out).
    /// </summary>
    private async Task ScoopWithLoader(FootPlayer me, PalletService.LoosePallet set)
    {
        // straight from the telehandler onto a loader (an admin's): no machine left parked by the pallet
        me.RideControls = null;
        if (!PalletNode.All.TryGetValue(Pallets.LooseId(set.Id), out var pallet)) { Fail($"{Pallets.LooseId(set.Id)} is not drawn"); return; }
        if (!me.SetRide(RideKind.WheelLoader) || me.Vehicle is not WheelLoader loader) { Fail("could not get on a wheel loader"); return; }
        await Seconds(1);
        XrPad.Press(PlayerInput.DigMode, true);
        await Seconds(0.1);
        XrPad.Press(PlayerInput.DigMode, false);
        await Seconds(0.2);
        // arm down, bucket level: the drive in is along the pallet's runners, from whichever side is clear
        await HoldFor(PlayerInput.ArmBoomDown, (loader.Lift - WheelLoaderLayout.LiftMin) / WheelLoaderLayout.LiftRate + 0.3);
        float level = -loader.Lift - loader.Tilt;
        await HoldFor(level > 0 ? PlayerInput.ArmBucketCurl : PlayerInput.ArmBucketDump, Mathf.Abs(level) / WheelLoaderLayout.TiltRate);
        var runners = (pallet.GlobalTransform.Basis.X with { Y = 0 }).Normalized();
        var space = me.GetWorld3D().DirectSpaceState;
        Vector3? side = null;
        foreach (var dir in new[] { runners, -runners, (pallet.GlobalTransform.Basis.Z with { Y = 0 }).Normalized(), -(pallet.GlobalTransform.Basis.Z with { Y = 0 }).Normalized() })
        {
            // a loader's width of low ground clear (the materials' row has rebar and stacks knee high)
            var across = new Vector3(-dir.Z, 0, dir.X);
            bool clear = true;
            foreach (float off in new[] { -1.3f, 0f, 1.3f })
            {
                var from = pallet.GlobalPosition + Vector3.Up * 0.4f + dir * 0.9f + across * off;
                var ray = PhysicsRayQueryParameters3D.Create(from, from + dir * 9f);
                ray.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
                clear &= space.IntersectRay(ray).Count == 0;
            }
            if (clear) { side = dir; break; }
        }
        if (side is not { } outward) { Fail("no clear side to scoop the pallet from"); return; }
        var floorAhead = -(loader.BucketPinLevel * loader.BucketFloor).Z;
        var at = pallet.GlobalPosition + outward * (floorAhead + 3f);
        if (me.Terrain != null && me.Terrain.TryGetHeight(at, out float g)) at.Y = g + 0.3f;
        me.PlaceAt(at, Yaw(-outward));
        await Seconds(2);
        float InIt() => -((me.GlobalTransform * loader.BucketPinLevel).AffineInverse() * pallet.GlobalPosition).Z;
        float Aside() => ((me.GlobalTransform * loader.BucketPinLevel).AffineInverse() * pallet.GlobalPosition).X;
        GD.Print(FormattableString.Invariant($"[sitepalletnet A]   loader lined up: {InIt():F2} m ahead of the pin, {Aside():F2} m aside"));
        // steered onto it on the way in, as a driver would: the pallet aside of the bucket's middle turns it
        me.RideControls = () => new RideInput(0.25f, 0f, Mathf.Clamp(Aside() * 1.5f, -1f, 1f), false);
        await Until(() => InIt() < WheelLoaderLayout.BucketFloorZ + 0.1f || me.Velocity.Length() < 0.01f && me.GroundSpeed > 0.3f, 15);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Until(() => me.GroundSpeed < 0.05f, 5);
        await HoldFor(PlayerInput.ArmBucketCurl, (WheelLoaderLayout.TiltMax - loader.Tilt) / WheelLoaderLayout.TiltRate + 0.2);
        XrPad.Press(PlayerInput.ArmBoomUp, true);
        bool scooped = await Until(() => loader.Carrying != 0, 6);
        await Seconds(0.8);
        XrPad.Press(PlayerInput.ArmBoomUp, false);
        // (a taken loose pallet's node is freed: nothing of it to ask after this)
        Expect(scooped, $"A's loader scoops {Pallets.LooseId(set.Id)} up ({loader.Carrying})");
        if (!scooped) return;
        await Seconds(1);
        Say($"scooped {Pallets.LooseId(set.Id)} {loader.Carrying}");
        if (!await Heard("B", "scoop seen", 30)) { Fail("B never saw it scooped"); return; }
        XrPad.Press(PlayerInput.ArmBucketDump, true);
        bool tipped = await Until(() => loader.Carrying == 0, 8);
        XrPad.Press(PlayerInput.ArmBucketDump, false);
        var again = PalletService.Instance?.Loose.Values.OrderBy(p => p.Id).LastOrDefault();
        Expect(tipped && again != null, $"dumped, it is tipped out ({(again == null ? "-" : Pallets.LooseId(again.Id))})");
        if (again == null) return;
        await Seconds(1);
        Say($"tipped {Pallets.LooseId(again.Id)} {again.Load}");
        if (!await Heard("B", "tip seen", 30)) Fail("B never saw it tipped out");
        me.RideControls = null;
    }

    /// <summary>
    /// A clear patch of ground near <paramref name="near"/>, 30 to 60 m off: a 16 m square with nothing
    /// standing in it (a box query, A's own body aside), on the drawn terrain. Null if none.
    /// </summary>
    private static Vector3? OpenGround(FootPlayer me, Vector3 near)
    {
        var space = me.GetWorld3D().DirectSpaceState;
        var box = new PhysicsShapeQueryParameters3D { Shape = new BoxShape3D { Size = new Vector3(16f, 3f, 16f) } };
        box.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
        foreach (float d in new[] { 30f, 45f, 60f })
            for (int k = 0; k < 8; k++)
            {
                var dir = new Vector3(Mathf.Cos(k * Mathf.Tau / 8f), 0, Mathf.Sin(k * Mathf.Tau / 8f));
                var at = near + dir * d;
                if (me.Terrain == null || !me.Terrain.TryGetHeight(at, out float g)) continue;
                at.Y = g + 0.3f;
                box.Transform = new Transform3D(Basis.Identity, at + Vector3.Up * 1.8f);
                if (space.IntersectShape(box, 1).Count == 0) return at;
            }
        return null;
    }

    private async Task HoldFor(string action, double seconds)
    {
        XrPad.Press(action, true);
        await Seconds(seconds);
        XrPad.Press(action, false);
        await Seconds(0.2);
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 90)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!await Heard("A", "lifted", 120)) { Fail("A never lifted a site pallet"); return; }
        var w = _heard.Last(l => l.Contains("SP A lifted")).Split(' ');
        string id = w[^2];
        int carrying = int.Parse(w[^1], CultureInfo.InvariantCulture);
        var a = Other(me);
        if (a == null) { Fail("no A here"); return; }
        var service = PalletService.Instance;
        bool seen = await Until(() => service != null && service.IsTaken(id)
            && PalletService.CarryingOf(a.Ride, a.Anim) == carrying
            && (a.Visual == null || TelehandlerMeshBuilder.BoomOf(a.Visual)?.Load != null), 15);
        bool hidden = !PalletNode.All.TryGetValue(id, out var node) || node.Taken;
        Expect(seen && hidden, $"B has {id} taken by the server, hidden here, and on A's telehandler "
            + $"(its pose carries {PalletService.CarryingOf(a.Ride, a.Anim)}, A says {carrying}; drawn on its boom {TelehandlerMeshBuilder.BoomOf(a.Visual)?.Load != null})");
        Say("seen");

        if (!await Heard("A", "set", 60)) { Fail("A never set it down"); return; }
        w = _heard.Last(l => l.Contains("SP A set")).Split(' ');
        string loose = w[^2];
        byte load = (byte)int.Parse(w[^1], CultureInfo.InvariantCulture);
        bool down = await Until(() => Pallets.TryParse(loose, out var r) && service!.Loose.TryGetValue(r.Loose, out var p) && p.Load == load
            && PalletService.CarryingOf(a.Ride, a.Anim) == 0 && PalletNode.All.ContainsKey(loose), 15);
        Expect(down, $"B has {loose} set down with its load {load}, drawn here, and A's forks empty");
        Say("set seen");

        if (!await Heard("A", "scooped", 90)) { Fail("A never scooped it with a loader"); return; }
        w = _heard.Last(l => l.Contains("SP A scooped")).Split(' ');
        string scoopedId = w[^2];
        int inBucket = int.Parse(w[^1], CultureInfo.InvariantCulture);
        bool scooped = await Until(() => a.Ride == RideKind.WheelLoader && PalletService.CarryingOf(a.Ride, a.Anim) == inBucket
            && !PalletNode.All.ContainsKey(scoopedId)
            && (a.Visual == null || WheelLoaderMeshBuilder.FrontOf(a.Visual)?.Load != null), 15);
        Expect(scooped, $"B has {scoopedId} gone from the ground and in A's loader's bucket (its pose carries {PalletService.CarryingOf(a.Ride, a.Anim)}, A says {inBucket}; "
            + $"drawn in its bucket {WheelLoaderMeshBuilder.FrontOf(a.Visual)?.Load != null})");
        Say("scoop seen");

        if (!await Heard("A", "tipped", 60)) { Fail("A never tipped it out"); return; }
        w = _heard.Last(l => l.Contains("SP A tipped")).Split(' ');
        string tippedId = w[^2];
        byte tippedLoad = (byte)int.Parse(w[^1], CultureInfo.InvariantCulture);
        bool tipped = await Until(() => Pallets.TryParse(tippedId, out var r) && service!.Loose.TryGetValue(r.Loose, out var p) && p.Load == tippedLoad
            && PalletService.CarryingOf(a.Ride, a.Anim) == 0 && PalletNode.All.ContainsKey(tippedId), 15);
        Expect(tipped, $"B has {tippedId} tipped out with its load {tippedLoad}, drawn here, and A's bucket empty");
        Say("tip seen");
    }
}
