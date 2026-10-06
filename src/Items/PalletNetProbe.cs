using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;
using UnitSport.XR;

namespace UnitSport.Items;

/// <summary>
/// <c>--palletnetcheck drive &lt;password&gt;</c> / <c>--palletnetcheck watch</c> on two clients of a
/// loopback server on the generated world (#583 phase 2, tier 2: <c>tools/palletnetcheck.sh</c>).
///
/// <para>
/// Both pick the same bay: from the spawn point, the nearest industrial door a vehicle goes through
/// whose hall has a loose floor pallet a forklift can drive square at (<see cref="PalletCheck.Approach"/>).
/// <b>drive</b> (A, an admin: a forklift out of nothing is an admin's) takes a forklift, drives in
/// through the bay, forks that pallet and lifts it, drives back out through the bay with it on the
/// forks and sets it down in the yard. <b>watch</b> (B) stands beside the bay and passes only on
/// what reached it over the wire, none of which it could guess: A's forks carrying a load inside
/// the hall and then out in the yard (A's pose), the hall's pallet taken and a loose one set down
/// outside (the server's <see cref="PalletService"/>), and A's forks empty again.
/// </para>
/// </summary>
public partial class PalletNetProbe : Node
{
    public static string? ParseArgs() => CmdArgs.Value("--palletnetcheck");
    private static string? Password => CmdArgs.Value("--palletnetcheck", 2, notFlag: true);

    private readonly string _role;
    /// <summary>The site's dormant forklift was there to be woken: then waking it is part of the verdict.</summary>
    private bool _liftSeen;
    private bool _woke;
    private readonly Func<FootPlayer?> _local;
    private double _clock;

    public PalletNetProbe(string role, Func<FootPlayer?> local)
    {
        _role = role;
        _local = local;
        Name = "PalletNetProbe";
    }

    private void Log(string what) => GD.Print($"[palletnet] {_role} t={_clock,5:F1} {what}");

    public override void _PhysicsProcess(double delta) => _clock += delta;

    public override void _Ready() => _ = Run();

    private async Task Run()
    {
        bool ok;
        try { ok = _role == "drive" ? await Drive() : await Watch(); }
        catch (Exception e)
        {
            Log($"threw {e.GetType().Name}: {e.Message}");
            ok = false;
        }
        Log(ok ? "RESULT: ok" : "RESULT: FAILED");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        double end = _clock + seconds;
        while (!done())
        {
            if (_clock > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private Task Seconds(double s) => Until(() => false, s);

    private static string Where(FootPlayer p) =>
        !InteriorManager.InInteriorSpace(p.GlobalPosition) ? "outside"
        : InteriorManager.Instance?.LayoutAt(p.GlobalPosition) is { } l ? $"inside {l.Key}" : "inside (not built here)";

    /// <summary>The bay both clients pick, its hall's plan, the pallet in it and the way to drive at it.</summary>
    private sealed record Bay(DoorIndex.Entry Door, InteriorLayout Layout, int Pallet, Vector3 Toward);

    /// <summary>
    /// From the spawn point both clients share, the nearest industrial bay whose hall has a pallet
    /// with a clear run in to it; tried until the tiles around have drawn their doors.
    /// </summary>
    private async Task<Bay?> FindBay(FootPlayer me)
    {
        double end = _clock + DoorSearch.GiveUp + 20;
        var told = new HashSet<string>();
        while (_clock < end)
        {
            if (me.Terrain?.Origin is { } origin && InteriorManager.Instance is { } interiors)
            {
                var (e, n) = SpawnPoint.ParseTarget();
                var from = origin.ToWorld(e, n, 0);
                var doors = DoorIndex.All().Where(d => d.Kind == BuildingKind.Industrial && d.Vehicle)
                    .OrderBy(d => new Vector2(d.World.X - from.X, d.World.Z - from.Z).Length()).Take(8).ToList();
                foreach (var door in doors)
                {
                    var layout = await interiors.GetOrCreate(door.Key.ToString());
                    if (layout == null) continue;
                    bool tell = told.Add(door.Key.ToString());
                    // the plan beside the log, to see what a refusal below was about
                    if (tell && _role == "watch")
                        System.IO.File.WriteAllText(ProjectSettings.GlobalizePath($"res://test_output/palletnet_{layout.Key}.svg"), InteriorValidator.ToSvg(layout));
                    int i = PalletCheck.Approach(layout, out var toward, tell ? w => Log($"    {w}") : null);
                    if (i < 0)
                    {
                        if (tell) Log($"  {door.Key}: {layout.Type}, {layout.Furniture.Count(InteriorMeshBuilder.IsLoosePallet)} floor pallet(s), none to drive square at");
                        continue;
                    }
                    Log($"bay {door.Key} ({door.Width:F1} x {door.Height:F1} m) of a {layout.Type}, pallet {Pallets.HallId(layout.Key, i)}");
                    return new Bay(door, layout, i, toward);
                }
            }
            await Seconds(1);
        }
        return null;
    }

    /// <summary>Stands <paramref name="me"/> <paramref name="outward"/> m out from the door and <paramref name="aside"/> along the wall, facing it.</summary>
    private static void StandAt(FootPlayer me, DoorIndex.Entry door, float outward, float aside)
    {
        var o = door.Outward;
        var at = door.World + o * outward + new Vector3(-o.Z, 0, o.X) * aside;
        if (me.Terrain != null && me.Terrain.TryGetHeight(at, out float g)) at.Y = g + 0.3f;
        var look = door.World - at;
        me.PlaceAt(at, Mathf.Atan2(-look.X, -look.Z));
    }

    private static float Yaw(Vector3 toward) => Mathf.Atan2(-toward.X, -toward.Z);

    // ---- drive: A ---------------------------------------------------------------------------------

    private async Task<bool> Drive()
    {
        if (!await Until(() => _local() is { } p && p.IsOnFloor(), 120)) { Log("never stood on the ground"); return false; }
        var me = _local()!;
        if (Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
            chat.Send($"/login {pw}");
        await Until(() => Permissions.IsAdmin, 10);
        Log($"admin: {Permissions.IsAdmin}");

        if (await FindBay(me) is not { } bay) { Log("no industrial bay with a pallet near the spawn"); return false; }
        // the door as the index holds it now: a teleport across the map rebases the origin, and a
        // copy kept from before the move is kilometres off (DoorIndex.Shift moves only its own)
        DoorIndex.Entry Door() => DoorIndex.Find(bay.Door.Key) ?? bay.Door;
        // to the bay on foot first: the dormant layer works out the tiles round a player, and a
        // mount teleported hundreds of metres lands on ground whose collision is not there yet
        StandAt(me, Door(), 12f, 0f);
        await Seconds(1.5);
        // the works' own forklift, standing dormant on its apron (phase 3): woken by getting in, as
        // a player does; conjured (the admin's SetRide) only where the slot was dropped
        bool woke = _woke = await GetInYardForklift(me, bay);
        if (!woke)
        {
            StandAt(me, Door(), 12f, 0f);
            await Seconds(1.5);
            if (!me.SetRide(RideKind.Forklift)) { Log("could not get on a forklift"); return false; }
        }
        if (me.Vehicle is not Forklift fork) { Log("not on a forklift"); return false; }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Seconds(1);
        // lined up 12 m out from the bay, on it (a short way from the apron: the ground is loaded)
        var o = Door().Outward;
        var lineUp = Door().World + o * 12f;
        if (me.Terrain != null && me.Terrain.TryGetHeight(lineUp, out float lineGround)) lineUp.Y = lineGround + 0.3f;
        me.PlaceAt(lineUp, Yaw(-o));
        // the watcher takes its place beside the bay meanwhile
        await Seconds(6);

        // ---- in through the bay -------------------------------------------------------------
        Log($"driving in, {Where(me)}");
        me.RideControls = () => new RideInput(0.6f, 0f, 0f, false);
        double traceAt = 0;
        bool inside = await Until(() =>
        {
            if (_clock >= traceAt)
            {
                traceAt = _clock + 1;
                Log(FormattableString.Invariant($"  at {me.GlobalPosition}, {me.GroundSpeed:F1} m/s, {(me.GlobalPosition - Door().World).Length():F1} m from the door, ride {me.Ride}, vel {me.Velocity}"));
            }
            return InteriorManager.InInteriorSpace(me.GlobalPosition);
        }, 25);
        await Seconds(0.6);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.1f, 5);
        Log($"in: {inside}, {Where(me)}");
        if (!inside || InteriorManager.Instance?.CurrentNode is not { } hall || hall.Layout.Key != bay.Layout.Key)
        {
            Log("did not get into the hall");
            return false;
        }

        // ---- the pallet: lined up square to it, driven at, the forks raised ------------------
        var plan = hall.Layout.Furniture[bay.Pallet];
        string id = Pallets.HallId(hall.Layout.Key, bay.Pallet);
        if (!PalletNode.All.TryGetValue(id, out var pallet)) { Log($"{id} is not drawn"); return false; }
        var toward = hall.GlobalTransform.Basis * bay.Toward;
        var start = hall.GlobalTransform * (new Vector3(plan.X, hall.Layout.FloorY(plan.Floor), plan.Z) - bay.Toward * PalletCheck.RunIn);
        me.PlaceAt(start + Vector3.Up * 0.05f, Yaw(toward));
        await Seconds(1);
        me.RideControls = () => new RideInput(0.3f, 0f, 0f, false);
        await Until(() => -(me.GlobalTransform.AffineInverse() * pallet.GlobalPosition).Z
            < ForkliftLayout.MastZ + 0.1f + Pallets.Length * 0.5f + 0.1f || _clock > 0 && me.Velocity.Length() < 0.01f && me.GroundSpeed > 0.3f, 10);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.05f, 4);
        XrPad.Press(PlayerInput.ShiftUp, true);
        bool lifted = await Until(() => fork.Carrying != 0, 6);
        await Until(() => fork.Lift >= 0.3f, 3);
        XrPad.Press(PlayerInput.ShiftUp, false);
        Log($"lifted {id}: {lifted}, carrying {fork.Carrying} at {fork.Lift:F2} m");
        if (!lifted) return false;
        await Seconds(2);

        // ---- out through the bay with it ------------------------------------------------------
        if (InteriorManager.Instance?.Links.GetValueOrDefault(Door().Key.ToString()) is not { } link)
        {
            Log("no link for the bay");
            return false;
        }
        var map = link.ToInside;
        var outward = map.Basis * Door().Outward;
        outward = new Vector3(outward.X, 0, outward.Z).Normalized();
        me.PlaceAt(map * (Door().World - Door().Outward * 7f) + Vector3.Up * 0.1f, Yaw(outward));
        await Seconds(1);
        Log($"lined up inside the bay, {Where(me)}, carrying {fork.Carrying}");
        me.RideControls = () => new RideInput(0.6f, 0f, 0f, false);
        bool outside = await Until(() => !InteriorManager.InInteriorSpace(me.GlobalPosition), 25);
        int carriedOut = fork.Carrying;
        Log($"out: {outside}, {Where(me)}, carrying {carriedOut}");
        var at = me.GlobalPosition;
        await Until(() => (me.GlobalPosition - at).Length() > 7f, 8);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.05f, 5);

        // ---- set down in the yard ---------------------------------------------------------------
        XrPad.Press(PlayerInput.ShiftDown, true);
        bool down = await Until(() => fork.Carrying == 0, 8);
        XrPad.Press(PlayerInput.ShiftDown, false);
        var set = PalletService.Instance?.Loose.Values.OrderBy(p => p.Id).LastOrDefault();
        Log($"set down: {down}" + (set == null ? "" : FormattableString.Invariant($", {Pallets.LooseId(set.Id)} at LV95 {set.E:F1}/{set.N:F1} alt {set.Altitude:F1}")));
        // the watcher needs a moment to see the empty forks
        await Seconds(3);
        bool apron = await ForkApronPallet(me, fork);
        await Seconds(6);
        return outside && carriedOut != 0 && down && set is { Altitude: > InteriorManager.InteriorBaseY + 1000f } && apron
            && (_woke || !_liftSeen);
    }

    /// <summary>The bay's site's dormant forklift (phase 3), if the dormant layer has it: its yard is the building's.</summary>
    private static VehicleSlot? YardForklift(Bay bay) =>
        DormantVehicles.Instance?.Slots().Where(s => s.Ordinal == DormantSlots.ForkliftOrdinal
            && s.KindId == (int)RideKind.Forklift && s.Owner == bay.Door.Key.Building.ToString()).Cast<VehicleSlot?>().FirstOrDefault();

    /// <summary>
    /// Walks up to the site's own forklift where it stands dormant on the apron, wakes it and gets
    /// in: the path a player takes, through the server. False where there is no such slot (it was
    /// dropped, standing on a road or in a neighbour) or it would not wake.
    /// </summary>
    private async Task<bool> GetInYardForklift(FootPlayer me, Bay bay)
    {
        // the dormant layer works the tiles round the player out a moment after they arrive
        await Until(() => YardForklift(bay) != null, 15);
        if (YardForklift(bay) is not { } slot || me.Terrain?.Origin is not { } origin)
        {
            Log("no dormant forklift on the site's apron (its slot was dropped)");
            return false;
        }
        _liftSeen = true;
        var at = origin.ToWorld(slot.E, slot.N, slot.Height);
        // beside it, on the side its step is (Forklift.EntryPoint, node frame), facing it
        var step = new Basis(Vector3.Up, slot.Yaw) * new Forklift().EntryPoint;
        var stand = at + step + step.Normalized() * 0.6f + Vector3.Up * 0.3f;
        var look = at - stand;
        me.PlaceAt(stand, Mathf.Atan2(-look.X, -look.Z));
        await Seconds(1.5);
        DormantVehicles.Instance!.Wake(slot);
        string name = slot.NodeName;
        bool woken = await Until(() => VehicleManager.Instance?.GetNodeOrNull(name) != null, 10);
        Log(FormattableString.Invariant($"the site's forklift {name}: woken {woken}"));
        if (!woken) return false;
        await Seconds(0.5);
        bool asked = me.TryGetIn();
        bool inside = await Until(() => me.Vehicle is Forklift, 5);
        Log($"got into it: {inside} (asked {asked})");
        return inside;
    }

    /// <summary>
    /// Phase 3: one of the stacks out on a site's apron, the nearest the dormant layer has drawn,
    /// forked from the yard side (across its runners, which run along the facade), lifted and set
    /// down again. Its id is a yard one, which the server works out from the tile's own files.
    /// </summary>
    private async Task<bool> ForkApronPallet(FootPlayer me, Forklift fork)
    {
        // back off the pallet just set down
        var from = me.GlobalPosition;
        me.RideControls = () => new RideInput(0f, 0.4f, 0f, false);
        await Until(() => (me.GlobalPosition - from).Length() > 2.5f, 6);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.05f, 4);

        var stack = PalletNode.All.Values
            .Where(n => !n.Taken && n.IsInsideTree() && Pallets.TryParse(n.Id, out var r) && r.Source == PalletSource.Yard)
            .OrderBy(n => n.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        if (stack == null) { Log("no apron pallet drawn near"); return false; }
        // out of the facade is the stack's -Z: its runners (+X) run along the facade
        var outward = -stack.GlobalTransform.Basis.Z;
        outward = new Vector3(outward.X, 0, outward.Z).Normalized();
        Log(FormattableString.Invariant($"apron pallet {stack.Id}, {stack.GlobalPosition.DistanceTo(me.GlobalPosition):F0} m off"));
        me.PlaceAt(stack.GlobalPosition + outward * PalletCheck.RunIn + Vector3.Up * 0.1f, Yaw(-outward));
        await Seconds(1);
        me.RideControls = () => new RideInput(0.3f, 0f, 0f, false);
        await Until(() => -(me.GlobalTransform.AffineInverse() * stack.GlobalPosition).Z
            < ForkliftLayout.MastZ + 0.1f + Pallets.Length * 0.5f + 0.1f || me.Velocity.Length() < 0.01f && me.GroundSpeed > 0.3f, 10);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
        await Until(() => me.GroundSpeed < 0.05f, 4);

        XrPad.Press(PlayerInput.ShiftUp, true);
        bool lifted = await Until(() => fork.Carrying != 0, 6);
        await Until(() => fork.Lift >= 0.3f, 3);
        XrPad.Press(PlayerInput.ShiftUp, false);
        Log($"lifted {stack.Id}: {lifted}, carrying {fork.Carrying}");
        if (!lifted) return false;
        await Seconds(2);
        XrPad.Press(PlayerInput.ShiftDown, true);
        bool down = await Until(() => fork.Carrying == 0, 8);
        XrPad.Press(PlayerInput.ShiftDown, false);
        Log($"set it down again: {down}");
        return down;
    }

    // ---- watch: B ---------------------------------------------------------------------------------

    private async Task<bool> Watch()
    {
        if (!await Until(() => _local() is { } p && p.IsOnFloor(), 120)) { Log("never stood on the ground"); return false; }
        var me = _local()!;
        if (await FindBay(me) is not { } bay) { Log("no industrial bay with a pallet near the spawn"); return false; }
        // beside the bay, near enough that it does not shut on its own, out of the forklift's lane
        StandAt(me, bay.Door, 4.5f, 3f);

        bool carriedInside = false, carriedOutside = false, taken = false, setDown = false, drawnDown = false, emptied = false;
        // phase 3: an apron stack taken (a yard id, from the server) and then on A's forks
        bool apronTaken = false, apronCarried = false;
        // phase 3: the site's own forklift, dormant on its apron, woken by A (the server spawned it)
        VehicleSlot? yardLift = null;
        bool liftWoken = false;
        string last = "";
        double end = _clock + 200;
        while (_clock < end)
        {
            await Seconds(0.25);
            var other = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p != me);
            var service = PalletService.Instance;
            if (other == null || service == null) continue;
            int carry = other.Ride == RideKind.Forklift ? Mathf.RoundToInt(other.Anim.Z) : 0;
            bool inside = InteriorManager.InInteriorSpace(other.GlobalPosition);
            bool drawn = ForkliftMeshBuilder.MastOf(other.Visual)?.Load != null;
            var hall = service.Taken.FirstOrDefault(t => t.StartsWith(bay.Layout.Key + ":f", StringComparison.Ordinal));
            var loose = service.Loose.Values.FirstOrDefault(p => p.Altitude > InteriorManager.InteriorBaseY + 1000f);

            if (carry != 0 && inside) carriedInside = true;
            if (carry != 0 && !inside) carriedOutside = true;
            if (hall != null) taken = true;
            if (loose != null)
            {
                setDown = true;
                drawnDown |= PalletNode.All.ContainsKey(Pallets.LooseId(loose.Id));
            }
            if (setDown && carriedOutside && carry == 0 && other.Ride == RideKind.Forklift) emptied = true;
            var apron = service.Taken.FirstOrDefault(t => Pallets.TryParse(t, out var r) && r.Source == PalletSource.Yard);
            if (apron != null) apronTaken = true;
            if (apronTaken && carry != 0) apronCarried = true;
            yardLift ??= YardForklift(bay);
            if (yardLift is { } ys && DormantVehicles.Instance?.IsAwake(ys) == true) liftWoken = true;
            bool liftOk = yardLift == null || liftWoken;

            string now = $"A: {other.Ride} {(inside ? "inside" : "outside")}, forks carrying {carry} (drawn: {drawn}); "
                + $"taken {hall ?? "-"} {apron ?? "-"}, loose {(loose == null ? "-" : FormattableString.Invariant($"{Pallets.LooseId(loose.Id)} load {loose.Load} at LV95 {loose.E:F1}/{loose.N:F1} alt {loose.Altitude:F1}"))}";
            if (now != last) { Log(now); last = now; }
            if (carriedInside && carriedOutside && taken && setDown && drawnDown && emptied && apronTaken && apronCarried && liftOk) break;
        }
        Log($"saw: carried inside {carriedInside}, carried out in the yard {carriedOutside}, the hall's pallet taken {taken}, "
            + $"set down outside {setDown} (drawn here {drawnDown}), forks empty after {emptied}, an apron stack taken {apronTaken} and carried {apronCarried}, "
            + $"the site's forklift {(yardLift == null ? "not on its apron here" : $"{yardLift.Value.NodeName} woken {liftWoken}")}");
        return carriedInside && carriedOutside && taken && setDown && drawnDown && emptied && apronTaken && apronCarried
            && (yardLift == null || liftWoken);
    }
}
