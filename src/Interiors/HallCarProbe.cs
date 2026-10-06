using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--hallcarcheck A|B</c> with <c>--connect</c> on the <c>fixture:garage</c> course (#558, PR 3, tier 2:
/// <c>tools/hallcarnetcheck.sh</c>): the cars standing in a block's underground car park as vehicles.
///
/// <para>
/// Both walk in through the block's garage door and stand in the car park, coordinated over chat
/// like <see cref="LiftSyncProbe"/>. <b>A</b> (the driver) stands beside a bay car at one end and aims at it, which
/// wakes it through the server's hall path; gets in; and drives it, from the ramp's foot, up the
/// ramp and out through the garage door; gets out in the street and goes away. <b>B</b> stands at the
/// far end of the car park and passes only on what reached it over the wire: every bay's car is
/// drawn and solid at first, the sleeper of A's bay vanishes and goes non-solid once the real vehicle
/// exists (never both drawn), A's car is seen climbing the ramp and leaving the building, and, once
/// the car has been gone for the respawn wait (<c>--dormant-respawn</c>) and nobody is near, the bay
/// shows a car again (the server's <c>Slept</c>).
/// </para>
/// </summary>
public partial class HallCarProbe : ChatProbe
{
    public static string? Role => RoleArg("--hallcarcheck");

    private readonly WorldOrigin _origin;

    public HallCarProbe(ItemController items, WorldOrigin origin) : base(items, "hallcar", "HC", "hallcar_") => _origin = origin;

    public HallCarProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    private static VehicleBody? Vehicle(string name) => VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(name);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        string other = _role == "A" ? "B" : "A";
        if (!await Joined(120)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        bool windowed = DisplayServer.GetName() != "headless";

        // the garage nearest the agreed spawn: both clients pick the same one
        DoorIndex.Entry? found = null;
        for (int i = 0; i < 90 && found == null; i++)
        {
            found = DoorIndex.NearestOfKind(me.GlobalPosition, DoorSearch.KindReach, BuildingKind.Apartment, vehicleOnly: true);
            if (found == null) await Seconds(1);
        }
        if (found is not { } door) { Fail("no garage door of a block of flats near the spawn"); return; }
        var plan = await interiors.GetOrCreate(door.Key.ToString());
        if (plan == null) { Fail($"no plan for {door.Key}"); return; }

        // the cars in the bays, and the two ends of the car park they stand along
        var cars = Enumerable.Range(0, plan.Furniture.Count).Where(i => HallCars.IsBayCar(plan, plan.Furniture[i])).ToList();
        GD.Print($"{Log} block {door.Key} ({plan.Type}), {cars.Count} bay car(s), {plan.Floors.Count} floors");
        if (cars.Count < 6) { Fail("too few bay cars to tell the ends apart"); return; }
        var carPark = plan.RoomOf(plan.Furniture[cars[0]])!;
        float roomMidZ = (carPark.Z0 + carPark.Z1) / 2;
        // B's end: the right-most car (along X), stood in the aisle in front of it
        int farIndex = cars.OrderByDescending(i => plan.Furniture[i].X).First();
        var far = plan.Furniture[farIndex];
        // A's car: the left-most one with a free bay beside it, to stand in; A stands at that side
        // a neighbour bay's car in the same row within 3.2 m on that side (bays are 2.5 m apart)
        bool Taken(int self, float dir) => cars.Any(i => i != self
            && Math.Abs(plan.Furniture[i].Z - plan.Furniture[self].Z) < 1f
            && dir * (plan.Furniture[i].X - plan.Furniture[self].X) is > 0f and < 3.2f);
        int mine = -1;
        float side = 0;
        foreach (var i in cars.OrderBy(i => plan.Furniture[i].X))
        {
            if (!Taken(i, 1)) { mine = i; side = 1; break; }
            if (!Taken(i, -1)) { mine = i; side = -1; break; }
        }
        if (mine < 0) { Fail("every bay car has a neighbour on both sides"); return; }
        var car = plan.Furniture[mine];
        if (HallCars.SlotOf(plan, mine, _origin) is not { } slot) { Fail("no slot for A's car"); return; }
        float floorY = plan.FloorY(car.Floor);
        GD.Print($"{Log} A's car is #{mine} ({slot.NodeName}, kind {slot.KindId}) at plan {car.X:F1},{car.Z:F1}; B's end is #{farIndex} at {far.X:F1},{far.Z:F1}; "
            + $"{Math.Abs(far.X - car.X):F0} m apart");

        // ---- in through the door, one after the other (two pressing E would shut it again) -------
        if (_role == "A")
        {
            if (!await WalkIn(me, interiors, door)) return;
            // said until B answers: B may still be joining, and chat is not replayed to a late joiner
            bool got = false;
            for (int tries = 0; tries < 60 && !got; tries++) { Say("in"); got = await Heard("B", "spot", 3); }
            if (!got) { Fail("B never got to its end of the car park"); return; }
        }
        else
        {
            if (!await Heard("A", "in", 120)) { Fail("A never walked in"); return; }
            if (!await WalkIn(me, interiors, door)) return;
        }
        var node = interiors.CurrentNode!;
        if (interiors.Current?.Key != plan.Key) { Fail($"walked into {interiors.Current?.Key}, not {plan.Key}"); return; }

        var cp = node.GetNodeOrNull<ParkedCars>("HallCars");
        Expect(cp != null && cp.Count == cars.Count, $"{_role}: the car park has {cp?.Count ?? 0} sleeping car(s) of {cars.Count} bay cars");
        Expect(cars.All(i => cp?.IsDrawn(i) == true), $"{_role}: every bay's car is drawn at first");
        Expect(node.FindChild("Mesh", false, false) != null, $"{_role}: the interior itself is drawn");
        if (cp == null) { Fail("no HallCars node"); return; }

        Vector3 Local(float x, float z) => node.GlobalTransform * new Vector3(x, floorY + 0.1f, z);
        float YawToward(Vector3 from, Vector3 to) { var d = to - from; return Mathf.Atan2(-d.X, -d.Z); }

        if (_role == "B")
        {
            float zin = far.Z < roomMidZ ? far.Z + 3.4f : far.Z - 3.4f;
            var at = Local(far.X, zin);
            me.EnterInterior(plan.Key, at, YawToward(at, Local(car.X, car.Z)));
            me.Velocity = Vector3.Zero;
            await Seconds(1.0);
            Say("spot");
            await WatchA(me, interiors, plan, node, cp, mine, slot, floorY, windowed);
            return;
        }

        // ---- A: beside its car, aimed at it ----------------------------------------------------------
        var carAt = Local(car.X, car.Z);
        var stand = Local(car.X + side * 1.5f, car.Z);
        me.EnterInterior(plan.Key, stand, YawToward(stand, carAt));
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
        Expect(Vehicle(slot.NodeName) == null, "A: the car is not a vehicle yet");
        // the way a player wakes it: the aim ray hits the sleeper's box (VehicleReach)
        float pitch = -0.5f;
        bool woke = false;
        for (int tries = 0; tries < 40 && !woke; tries++)
        {
            me.LookPitch = pitch;
            await Seconds(0.25);
            VehicleReach.Find(me);
            woke = await Until(() => Vehicle(slot.NodeName) != null, 1.0);
            if (!woke && tries == 5) pitch = 0.3f;
            if (!woke && tries == 10) pitch = -0.2f;
        }
        Expect(woke, $"A: aiming at the bay car wakes it (pitch {pitch:F1})");
        if (!woke) { Fail("the bay car did not wake"); return; }
        await Seconds(0.6);
        Expect(!cp.IsDrawn(mine), "A: the sleeping copy is gone once the vehicle exists");
        var real = Vehicle(slot.NodeName)!;
        var want = _origin.ToWorld(slot.E, slot.N, slot.Height);
        Expect(real.GlobalPosition.DistanceTo(want) < 0.6f, $"A: the vehicle stands in its bay ({real.GlobalPosition.DistanceTo(want):F2} m off)");
        if (windowed) Shot("woken");
        Say("woke");
        await Seconds(4.0);   // B looks at it

        // ---- get in ----------------------------------------------------------------------------------
        bool inCar = false;
        for (int tries = 0; tries < 6 && !inCar; tries++)
        {
            me.EnterInterior(plan.Key, stand, YawToward(stand, carAt));
            me.Velocity = Vector3.Zero;
            me.LookPitch = pitch;
            await Seconds(0.8);
            me.TryGetIn();
            inCar = await Until(() => me.Ride != RideKind.OnFoot, 2.5);
        }
        Expect(inCar && CarCatalog.IsCar(me.Ride), $"A: got into the bay car (ride {me.Ride})");
        if (!inCar) { Fail("could not get into the woken car"); return; }
        Expect(Vehicle(slot.NodeName) == null, "A: the vehicle is the player's now");
        Say("in car");
        await Seconds(1.0);

        // ---- up the ramp and out through the door ---------------------------------------------
        var ramp = plan.Floors[plan.Below - 1].AllFlights().FirstOrDefault(f => f.Ramp);
        if (ramp == null) { Fail("no ramp on the car park's floor"); return; }
        var (bx, bz) = ramp.Bottom;
        var (tx, tz) = ramp.TopEnd;
        var up = new Vector2(tx - bx, tz - bz).Normalized();
        var start = Local(bx - up.X * 3.2f, bz - up.Y * 3.2f);
        var beyond = Local(bx + up.X * 3f, bz + up.Y * 3f);
        me.PlaceAt(start, YawToward(start, beyond));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        float startY = me.GlobalPosition.Y;
        Input.ActionPress(PlayerInput.Throttle);
        float peak = 0;
        bool outside = await Until(() => { peak = Math.Max(peak, me.GlobalPosition.Y - startY); return !InteriorManager.InInteriorSpace(me.GlobalPosition); }, 40);
        Input.ActionRelease(PlayerInput.Throttle);
        Input.ActionPress(PlayerInput.Brake);
        await Until(() => me.GroundSpeed < 0.3f, 8);
        Input.ActionRelease(PlayerInput.Brake);
        Expect(outside, $"A: drove the woken car up the ramp ({peak:F1} m up) and out through the door");
        if (windowed) Shot("outside");
        Say("out");
        await Seconds(2.0);
        me.ExitVehicle();
        await Seconds(1.0);
        Expect(me.Ride == RideKind.OnFoot, "A: got out in the street");
        // far from the bay and from the car, so nobody is near when the slot is restocked
        me.LeaveInterior(door.World + door.Outward * 90f + Vector3.Up * 2f, 0);
        me.Velocity = Vector3.Zero;
        Say("gone");
        bool refilled = await Heard("B", "refilled", 90);
        Expect(refilled, "A: B saw the bay refilled");
        if (!refilled) { Fail("the bay never refilled"); return; }
        await Seconds(1.0);
        await Finish(1.0);
    }

    /// <summary>B at its end of the car park: what reaches it from A's waking, driving and leaving.</summary>
    private async Task WatchA(FootPlayer me, InteriorManager interiors, InteriorLayout plan, InteriorNode node, ParkedCars cp,
        int mine, VehicleSlot slot, float floorY, bool windowed)
    {
        bool windowShot = windowed;
        var bayAt = node.GlobalTransform * new Vector3(plan.Furniture[mine].X, floorY + 0.7f, plan.Furniture[mine].Z);
        // A wakes it: the real vehicle arrives, and the sleeper in its bay goes at the same moment
        if (!await Heard("A", "woke", 90)) { Fail("A never woke its car"); return; }
        bool vehicle = await Until(() => Vehicle(slot.NodeName) != null, 10);
        await Seconds(0.5);
        Expect(vehicle, $"B: the woken car {slot.NodeName} is a vehicle here too");
        Expect(!cp.IsDrawn(mine), "B: the sleeper in that bay is not drawn (never both)");
        var real = Vehicle(slot.NodeName);
        if (real != null)
        {
            var want = _origin.ToWorld(slot.E, slot.N, slot.Height);
            Expect(real.GlobalPosition.DistanceTo(want) < 0.6f, $"B: the real car stands in the bay ({real.GlobalPosition.DistanceTo(want):F2} m off)");
        }
        // the other bays' cars are untouched
        var others = cp.Slots.Where(s => s.Ordinal != mine).ToList();
        Expect(others.Count > 0 && others.All(s => cp.IsDrawn(s.Ordinal)), $"B: the other {others.Count} cars are still asleep and drawn");
        if (windowShot) { me.LookPitch = -0.05f; await Seconds(0.5); Shot("far_end"); windowShot = false; }

        // A gets in and drives it: the vehicle becomes A's body, seen climbing and leaving
        Say("saw woke");
        if (!await Heard("A", "in car", 60)) { Fail("A never got in"); return; }
        float lowest = float.MaxValue, highest = float.MinValue;
        bool sawCar = false, sawOutside = false, sawClimb = false;
        double end = GameClock.Now + 60;
        while (GameClock.Now < end && !_heard.Any(l => l.Contains("HC A out")) )
        {
            foreach (var p in GetTree().GetNodesInGroup("doorway_travellers").OfType<FootPlayer>().Where(p => !p.IsMultiplayerAuthority()))
            {
                if (!CarCatalog.IsCar(p.Ride)) continue;
                sawCar = true;
                if (InteriorManager.InInteriorSpace(p.GlobalPosition))
                {
                    lowest = Math.Min(lowest, p.GlobalPosition.Y);
                    highest = Math.Max(highest, p.GlobalPosition.Y);
                    if (highest - lowest > 1.5f) sawClimb = true;
                }
                else sawOutside = true;
            }
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        await Seconds(1.5);
        Expect(sawCar, "B: saw A as a car");
        Expect(sawClimb, $"B: saw A's car climb the ramp ({highest - lowest:F1} m seen)");
        // out through the door: it is outside for B too, or no longer drawn inside
        foreach (var p in GetTree().GetNodesInGroup("doorway_travellers").OfType<FootPlayer>().Where(p => !p.IsMultiplayerAuthority()))
            if (!InteriorManager.InInteriorSpace(p.GlobalPosition)) sawOutside = true;
        Expect(sawOutside, "B: saw A's car outside the building");
        Expect(!cp.IsDrawn(mine), "B: the bay is still empty while the car is out");

        // the car left in the street for the respawn wait, nobody near: the bay shows a car again
        if (!await Heard("A", "gone", 60)) { Fail("A never left"); return; }
        double t0 = GameClock.Now;
        bool refilled = await Until(() => cp.IsDrawn(mine), 60);
        Expect(refilled, $"B: the bay shows a car again after {GameClock.Now - t0:F0} s");
        Expect(Vehicle(slot.NodeName) == null, "B: and no vehicle stands under it");
        var shapes = cp.GetChildren().OfType<DormantBody>().First(b => b.Slot.Ordinal == mine).GetChildren().OfType<CollisionShape3D>().ToList();
        await Seconds(0.3);
        Expect(shapes.Count > 0 && shapes.All(s => !s.Disabled), "B: and it is solid again");
        if (windowed)
        {
            var from = node.GlobalTransform * new Vector3(plan.Furniture[mine].X + 6f, floorY + 1.6f, plan.Furniture[mine].Z - 4f);
            me.EnterInterior(plan.Key, from, Mathf.Atan2(-(bayAt.X - from.X), -(bayAt.Z - from.Z)));
            await Seconds(1.0);
            Shot("refilled");
        }
        Say("refilled");
        await Finish(1.5);
    }

    /// <summary>From just outside the door: open it unless it stands open, and walk through.</summary>
    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, DoorIndex.Entry door)
    {
        door = DoorIndex.All().Where(d => d.Key.Equals(door.Key)).Select(d => (DoorIndex.Entry?)d).FirstOrDefault() ?? door;
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        await Until(() => me.IsOnFloor(), 10);
        bool open = false;
        for (int attempt = 0; attempt < 4 && !open; attempt++)
        {
            if (!interiors.IsOpen(doorKey)) me.TryInteract();
            open = await Until(() => interiors.Links.TryGetValue(doorKey, out var lk) && lk.Passable && lk.Swing >= 1f, 5 + attempt * 2);
        }
        if (!open) { Fail($"the door {doorKey} never opened"); return false; }
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);
        return true;
    }

    protected override string Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return "";
        string path = base.Shot($"{_role}_{name}");
        GD.Print($"{Log} screenshot {path}");
        return path;
    }
}
