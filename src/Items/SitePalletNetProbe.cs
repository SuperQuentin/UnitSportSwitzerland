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

    /// <summary>Half the telehandler's width and a margin, m: how far either side of a pallet's centreline the way in must be clear.</summary>
    private const float MachineHalfWidth = 1.3f;

    /// <summary>The most the floor may rise or fall along the way in, m.</summary>
    private const float MaxStep = 0.12f;

    /// <summary>
    /// The sites whose materials the planner kept a way in to (#1926): a topped-out site was assumed to have one, and on
    /// <c>fixture:site</c> under flat none did (a yard 14.5 m deep between the scaffolding and the street).
    /// </summary>
    private static HashSet<string> ReachableSites(IEnumerable<Terrain.Format.TileId> tiles)
    {
        var keys = new HashSet<string>();
        if (Loot.LootService.Instance?.Source?.Invoke() is not { } source) return keys;
        foreach (var tile in tiles)
        {
            var t = Terrain.Construction.SitePlans.OfTile(source, tile);
            if (t.Tile is not { } bt) continue;
            foreach (var site in t.Sites)
                if (Terrain.Construction.SitePlans.MaterialsReachable(bt, t.Roads, site)) keys.Add(site.Key);
        }
        return keys;
    }

    private static float Yaw(Vector3 toward) => Mathf.Atan2(-toward.X, -toward.Z);

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private async Task RunA(FootPlayer me)
    {
        if (!await LogIn("test")) return;
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        // the dormant layer draws the site's pallets with its machines, round the spawn
        if (!await Until(() => NearestSitePallet(me.GlobalPosition) != null, 60)) { Fail("no site pallet drawn near the spawn"); return; }
        var space = me.GetWorld3D().DirectSpaceState;
        // A's own body aside: it spawns at the site, and is placed at the side found anyway
        Godot.Collections.Dictionary Ray(Vector3 a, Vector3 b)
        {
            var q = PhysicsRayQueryParameters3D.Create(a, b);
            q.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
            return space.IntersectRay(q);
        }
        // across its runners (they run along the row of materials), from whichever side is clear
        Vector3? ClearSide(PalletNode pallet)
        {
            var across = (pallet.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
            Vector3? side = null;
            foreach (var dir in new[] { across, -across })
            {
                var from = pallet.GlobalPosition + Vector3.Up * 1.2f + dir * 0.9f;
                // the machine's width, not a line (#1718: a ray down the middle passed a way the telehandler stuck in):
                // its centre and either side, high and low (the materials' rebar bundle is 0.35 m high, #619 turns it
                // to the building)
                var aside = new Vector3(-dir.Z, 0, dir.X) * MachineHalfWidth;
                var hit = new Godot.Collections.Dictionary();
                foreach (var off in new[] { Vector3.Zero, aside, -aside })
                    foreach (var up in new[] { 0f, -0.95f })
                    {
                        if (hit.Count > 0) break;
                        var at = from + off + Vector3.Up * up;
                        hit = Ray(at, at + dir * 7.5f);
                    }
                // and ground to stand on: a site's pit (#619) is clear at that height, and empty under it
                bool overPit = false;
                for (float k = 0f; k <= 7.5f && !overPit; k += 0.5f) overPit = me.Terrain?.InPit(from + dir * k) == true;
                // and level all the way in, at the pallet's own floor: a slab's edge or a kerb stops the machine short of it
                bool step = false;
                float floor = float.NaN;
                for (float k = 1f; k <= 7.5f && !step; k += 0.5f)
                {
                    var top = pallet.GlobalPosition + dir * k + Vector3.Up * 3f;
                    var down = Ray(top, top - Vector3.Up * 6f);
                    if (down.Count == 0) continue;
                    float y = down["position"].AsVector3().Y;
                    if (float.IsNaN(floor)) floor = y;
                    step = Math.Abs(y - floor) > MaxStep;
                }
                if (hit.Count == 0 && !overPit && !step) { side = dir; break; }
                if (step) GD.Print(FormattableString.Invariant($"[sitepalletnet A]   side {dir.X:F2},{dir.Z:F2}: a step on the way in"));
                GD.Print(FormattableString.Invariant($"[sitepalletnet A]   side {dir.X:F2},{dir.Z:F2}: {(hit.Count == 0 ? "clear" : $"hits {(hit["collider"].AsGodotObject() as Node)?.GetPath()} at {hit["position"]}")}{(overPit ? ", over the pit" : "")}"));
            }
            return side;
        }

        // the nearest site pallet a machine can come at: one beside a pit or hemmed in by a slab is passed over (#1718:
        // on the fixture the nearest stands by the foundations' pit)
        // a site's whose materials have a way in first (#1926: the planner keeps one where the yard has room; a topped-out
        // site was taken for one, and on fixture:site under flat none of them had room)
        var palletTiles = new HashSet<Terrain.Format.TileId>();
        foreach (var n in PalletNode.All.Values)
            if (Pallets.TryParse(n.Id, out var r) && r.Source == PalletSource.Site && Interiors.BuildingKey.TryParse(r.Building, out var b))
                palletTiles.Add(b.Tile);
        var reachable = await Task.Run(() => ReachableSites(palletTiles));
        PalletNode? pallet = null;
        Vector3? clear = null;
        foreach (var candidate in PalletNode.All.Values
                     .Where(n => !n.Taken && n.IsInsideTree() && Pallets.TryParse(n.Id, out var r) && r.Source == PalletSource.Site)
                     .OrderBy(n => Pallets.TryParse(n.Id, out var r) && reachable.Contains(r.Building) ? 0 : 1)
                     .ThenBy(n => n.GlobalPosition.DistanceSquaredTo(me.GlobalPosition)).Take(40).ToList())
        {
            GD.Print($"[sitepalletnet A]   trying {candidate.Id}");
            if (ClearSide(candidate) is { } found) { pallet = candidate; clear = found; break; }
        }
        if (pallet == null || clear is not { } outward) { Fail("no site pallet with a clear side to come at near the spawn"); return; }
        Expect(me.SetRide(RideKind.Telehandler) && me.Vehicle is Telehandler, $"A takes a telehandler for {pallet.Id}");
        if (me.Vehicle is not Telehandler th) { Fail("not a telehandler"); return; }
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
        await BrakeToStop(me);
        GD.Print(FormattableString.Invariant($"[sitepalletnet A]   telehandler stopped: {Ahead():F2} m ahead of the heel"));
        if (Ahead() > Pallets.LoadAhead + 0.5f)
        {
            // what stops it: rays ahead of the machine at a few heights (#1704)
            var fwd = (-me.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
            foreach (float up in new[] { 0.3f, 0.8f, 1.5f, 2.5f })
            {
                var from = me.GlobalPosition + Vector3.Up * up;
                var q = PhysicsRayQueryParameters3D.Create(from, from + fwd * 8f);
                q.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
                var hit = space.IntersectRay(q);
                GD.Print(FormattableString.Invariant($"[sitepalletnet A]   ahead at +{up:F1} m: {(hit.Count == 0 ? "clear" : $"{(hit["collider"].AsGodotObject() as Node)?.GetPath()} shape {hit["shape"]} at {((Vector3)hit["position"] - from).Length():F2} m")}"));
            }
            GD.Print(FormattableString.Invariant($"[sitepalletnet A]   the pallet at {pallet.GlobalPosition}, A at {me.GlobalPosition}"));
        }
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
        if (await OpenGround(me, pallet.GlobalPosition) is not { } open) { Fail("no open ground near the site"); return; }
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
        await BrakeToStop(me);
        GD.Print(FormattableString.Invariant($"[sitepalletnet A]   loader stopped: {InIt():F2} m ahead of the pin, {Aside():F2} m aside"));
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
    private static async Task<Vector3?> OpenGround(FootPlayer me, Vector3 near)
    {
        // the points to try: rings round the site, and along the roads near it (graded and clear: a site's open ground on
        // the leaves' real relief, #1731)
        var points = new List<Vector3>();
        foreach (float d in new[] { 30f, 45f, 60f, 75f, 90f, 105f, 120f })
            for (int k = 0; k < 8; k++)
                points.Add(near + new Vector3(Mathf.Cos(k * Mathf.Tau / 8f), 0, Mathf.Sin(k * Mathf.Tau / 8f)) * d);
        if (me.Terrain is { Content: { } source } ground)
        {
            var here = ground.ContentName(ground.TileAt(near));
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var id = here.Neighbour(dx, dz);
                    if (await source.LoadRoadsAsync(id) is not { } roads) continue;
                    foreach (var seg in roads.Segments)
                    {
                        if (seg.Class is not (Terrain.Format.RoadClass.Road or Terrain.Format.RoadClass.Minor or Terrain.Format.RoadClass.Major)) continue;
                        if ((seg.Flags & (Terrain.Format.RoadFlags.Tunnel | Terrain.Format.RoadFlags.Bridge)) != 0) continue;
                        for (int i = 0; i < seg.PointCount; i++)
                        {
                            var w = Terrain.Construction.SiteTiles.WorldLocal(me, id, new Vector3(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]));
                            float far = new Vector2(w.X - near.X, w.Z - near.Z).Length();
                            if (far > 25f && far < 150f) points.Add(w);
                        }
                    }
                }
        }
        var space = me.GetWorld3D().DirectSpaceState;
        var box = new PhysicsShapeQueryParameters3D { Shape = new BoxShape3D { Size = new Vector3(10f, 3f, 10f) } };
        box.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
        // the flattest clear spot of all (on the leaves' real relief nothing may be quite level, #1731)
        Vector3? best = null;
        float flattest = 1.8f, leastSpread = float.MaxValue;
        int unknown = 0, steep = 0, blocked = 0;
        foreach (var point in points)
            {
                var at = point;
                if (me.Terrain == null || !me.Terrain.TryGetHeight(at, out float g)) continue;
                // the ground round it, to the box's corners: level enough to set a pallet down, and the box lifted just
                // clear of its highest point, so it tests what stands there, not a slope (the leaves' real relief, #1731)
                float lo = g, hi = g;
                bool known = true;
                foreach (var (cx, cz) in new[] { (-5f, -5f), (5f, -5f), (5f, 5f), (-5f, 5f) })
                {
                    if (!me.Terrain.TryGetHeight(at + new Vector3(cx, 0, cz), out float c)) { known = false; break; }
                    (lo, hi) = (Math.Min(lo, c), Math.Max(hi, c));
                }
                if (!known) { unknown++; continue; }
                leastSpread = Math.Min(leastSpread, hi - lo);
                if (hi - lo >= flattest) { steep++; continue; }   // level enough for the loader after it, too
                at.Y = g + 0.3f;
                box.Transform = new Transform3D(Basis.Identity, new Vector3(at.X, hi + 0.4f + 1.5f, at.Z));
                if (space.IntersectShape(box, 1).Count == 0) (best, flattest) = (at, hi - lo);
                else blocked++;
            }
        GD.Print(FormattableString.Invariant($"[sitepalletnet]   open ground: {points.Count} spots tried, {unknown} with no ground known, {steep} too steep (the least {leastSpread:F2} m across), {blocked} blocked; {(best is { } b ? $"{flattest:F2} m across, {new Vector2(b.X - near.X, b.Z - near.Z).Length():F0} m off" : "none")}"));
        return best;
    }

    /// <summary>
    /// Braked while it still rolls on, then let go: at the 1.3 m/s a quarter throttle makes, a machine
    /// coasts its tines or bucket past a pallet and into it. No handbrake: the brake of a telehandler or
    /// a loader reverses from a standstill.
    /// </summary>
    private async Task BrakeToStop(FootPlayer me)
    {
        float Onward() => me.Velocity.Dot(-me.GlobalTransform.Basis.Z);
        me.RideControls = () => new RideInput(0f, Onward() > 0.15f ? 1f : 0f, 0f, false);
        await Until(() => me.GroundSpeed < 0.05f, 5);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
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
        // out of the yard: both spawn at the site, and B standing by a pallet blocks A's way in to it
        if (await OpenGround(me, me.GlobalPosition) is { } aside) me.PlaceAt(aside, 0f);
        await Seconds(1);
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
