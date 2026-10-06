using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--liftsynccheck A|B</c> with <c>--connect</c> (#557): two real clients in one apartment block,
/// coordinated over chat like <see cref="LockSyncProbe"/>. Both walk in by its door and stand in its
/// elevator's cabin on the ground floor; A opens the floor list with E and picks the top floor;
/// both must arrive there together (B having pressed nothing) and see each other there. Then A
/// cracks a locked flat's front door on that floor with the real dial: B must see it unlocked and
/// swung open without doing anything, then shuts it, and A must see it shut. Windowed
/// (<c>WINDOWED=1</c>), each takes pictures for the PR (<c>test_output/liftsync_ROLE_NAME.png</c>).
/// </summary>
public partial class LiftSyncProbe : ChatProbe
{
    public static string? Role => RoleArg("--liftsynccheck");

    private readonly WorldOrigin _origin;

    public LiftSyncProbe(ItemController items, WorldOrigin origin) : base(items, "liftsync", "LF", "liftsync_") => _origin = origin;

    public LiftSyncProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        string other = _role == "A" ? "B" : "A";
        if (!await Joined(120)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;

        // A looks for a block with an elevator and a locked flat door up top: around the agreed
        // spot, then in the villages nearest it (a generated world's blocks of flats are in its
        // village cores), and names it; B goes where A says
        var (e, n) = SpawnPoint.ParseTarget();
        DoorIndex.Entry? found = null;
        InteriorLayout? plan = null;
        if (_role == "A")
        {
            var places = new List<(double E, double N)> { (e, n) };
            places.AddRange(Occasions.OccasionTowns.All.OrderBy(t => Math.Pow(t.E - e, 2) + Math.Pow(t.N - n, 2)).Take(8).Select(t => (t.E, t.N)));
            foreach (var (pe, pn) in places)
            {
                (found, plan) = await BlockNear(me, interiors, pe, pn);
                if (found != null) { (e, n) = (pe, pn); break; }
            }
            if (found is not { } f || plan == null) { Fail("no block with an elevator and a locked top-floor flat"); return; }
            Say($"at {f.Key} {e.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} {n.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        else
        {
            if (!await Heard("A", "at", 400)) { Fail("A never found a block"); return; }
            var words = _heard.Last(l => l.Contains("LF A at")).Split(' ');
            int i = Array.IndexOf(words, "at");
            string key = words[i + 1];
            (e, n) = (double.Parse(words[i + 2], System.Globalization.CultureInfo.InvariantCulture), double.Parse(words[i + 3], System.Globalization.CultureInfo.InvariantCulture));
            (found, plan) = await BlockNear(me, interiors, e, n, key);
            if (found == null || plan == null) { Fail($"could not find A's block {key}"); return; }
        }
        var door = found!.Value;
        var lift = plan.Lifts[0];
        int ground = plan.Below, topFloor = lift.Top;
        int flatDoor = plan.InnerDoors.FindIndex(x => x.Floor == topFloor && x.Locked);
        GD.Print($"[liftsync {_role}] block {door.Key} ({plan.Type}), {plan.Floors.Count} floors, {plan.Lifts.Count} elevator(s), flat door #{flatDoor}");

        if (!await WalkIn(me, interiors, door)) return;
        var node = interiors.CurrentNode!;
        if (interiors.Current!.Key != plan.Key) { Fail($"walked into {interiors.Current.Key}, not {plan.Key}"); return; }

        // both in the cabin, side by side, facing its doors
        var (ox, oz) = lift.Outward;
        float yaw = Mathf.Atan2(-(node.GlobalTransform.Basis * new Vector3(ox, 0, oz)).X, -(node.GlobalTransform.Basis * new Vector3(ox, 0, oz)).Z);
        float side = _role == "A" ? -0.35f : 0.35f;
        var across = lift.DoorSide is Side.Front or Side.Back ? new Vector3(side, 0, 0) : new Vector3(0, 0, side);
        var cabin = new Vector3((lift.X0 + lift.X1) / 2, plan.FloorY(ground) + 0.1f, (lift.Z0 + lift.Z1) / 2) + across;
        me.EnterInterior(plan.Key, node.GlobalTransform * cabin, yaw);
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
        float startY = me.GlobalPosition.Y;
        Expect(InteriorManager.FloorAt(plan, node.ToLocal(me.GlobalPosition).Y) == ground, "standing in the cabin on the ground floor");

        bool both = false;
        for (int tries = 0; tries < 40 && !both; tries++) { Say("cabin"); both = await Heard(other, "cabin", 2); }
        if (!both) { Fail($"{other} never got into the cabin"); return; }
        Say("cabin");
        await Seconds(1.5);
        Shot("cabin");

        float rise = plan.FloorY(topFloor) - plan.FloorY(ground);
        if (_role == "A")
        {
            bool pressed = me.TryInteract();
            var picker = interiors.Picker;
            Expect(pressed && picker is { IsOpen: true }, "E in the cabin opens the floor list");
            if (picker is not { IsOpen: true }) { Fail("no floor list"); return; }
            await Seconds(0.4);
            Shot("panel");
            picker.Press(topFloor);
        }
        bool rode = await Until(() => Math.Abs(me.GlobalPosition.Y - (startY + rise)) < 0.5f, 20);
        Expect(rode, $"{_role} rode up {rise:F1} m to {InteriorManager.FloorName(plan, topFloor)}" + (_role == "B" ? " without pressing anything" : ""));
        if (!rode) { Fail($"still at {me.GlobalPosition.Y - startY:F1} m"); return; }
        Say("up");
        if (!await Heard(other, "up", 30)) { Fail($"{other} never arrived"); return; }
        await Seconds(2.0);
        // and each sees the other up there: the relayed body follows the jump
        bool seen = await Until(() => Others(interiors).Any(o => Math.Abs(o.GlobalPosition.Y - me.GlobalPosition.Y) < 1.0f
            && o.GlobalPosition.DistanceTo(me.GlobalPosition) < 3f), 8);
        Expect(seen, $"{_role} sees {other} in the cabin on the top floor");
        var ride = interiors.LiftOf(plan, 0);
        Expect(ride.At(ClockSync.ServerNow) == topFloor, $"the cabin is at {InteriorManager.FloorName(plan, topFloor)} for {_role}");
        await Seconds(1.0);
        Shot("arrived");

        // the locked flat door on this floor
        var d = plan.InnerDoors[flatDoor];
        var r = plan.Floors[d.Floor].Rooms[d.Room];
        var (at, outward) = d.Side switch
        {
            Side.Front => (new Vector3(d.Center, 0, r.Z0), new Vector3(0, 0, -1)),
            Side.Back => (new Vector3(d.Center, 0, r.Z1), new Vector3(0, 0, 1)),
            Side.Left => (new Vector3(r.X0, 0, d.Center), new Vector3(-1, 0, 0)),
            _ => (new Vector3(r.X1, 0, d.Center), new Vector3(1, 0, 0)),
        };
        var landing = at + outward * (_role == "A" ? 0.6f : 1.0f) + Along(d.Side) * (_role == "A" ? -0.3f : 0.4f) + Vector3.Up * (plan.FloorY(d.Floor) + 0.1f);
        var face = node.GlobalTransform.Basis * -outward;
        me.EnterInterior(plan.Key, node.GlobalTransform * landing, Mathf.Atan2(-face.X, -face.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
        Expect(interiors.InnerDoorLocked(plan, flatDoor) && !interiors.InnerDoorOpen(plan.Key, flatDoor), "the flat's door starts locked and shut");
        Shot("flatdoor_shut");

        var loot = LootService.Instance!;
        var ui = loot.LockUi!;
        if (_role == "A")
        {
            Expect(me.TryInteract() && ui.IsOpen, "E at the locked door opens the dial");
            var combo = InnerDoorLock.Combination(plan.Key, flatDoor);
            GD.Print($"[liftsync A] cracking {string.Join("-", combo)}");
            for (int stage = 0; stage < combo.Length; stage++)
            {
                int dir = LockPickUi.Direction(stage);
                bool clicked = false;
                for (int f = 0; f < 2400 && !clicked; f++)
                {
                    ui.Turn(dir * 0.3f);
                    clicked = ui.Clicking;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                bool dropped = await Until(() => ui.Stage > stage, 3);
                Expect(clicked && dropped, $"tumbler {stage + 1}/{combo.Length} clicked and dropped");
                if (!dropped) { Fail("a tumbler would not drop"); return; }
            }
            Expect(await Until(() => !interiors.InnerDoorLocked(plan, flatDoor) && interiors.InnerDoorOpen(plan.Key, flatDoor), 6) && !ui.IsOpen,
                "the server unlocked it and swung it open; the dial went");
            Say("cracked");
            if (!await Heard("B", "shut", 60)) { Fail("B never shut it"); return; }
            Expect(await Until(() => !interiors.InnerDoorOpen(plan.Key, flatDoor) && node.InnerSwing(flatDoor) <= 0f, 6),
                "A saw B shut it");
        }
        else
        {
            if (!await Heard("A", "cracked", 120)) { Fail("A never cracked it"); return; }
            Expect(await Until(() => !interiors.InnerDoorLocked(plan, flatDoor) && node.InnerSwing(flatDoor) >= 1f, 6),
                "B saw it unlocked and swung open without doing anything");
            await Seconds(0.5);
            Shot("flatdoor_open");
            Expect(me.TryInteract() && await Until(() => !interiors.InnerDoorOpen(plan.Key, flatDoor), 5), "B shuts it with E");
            Say("shut");
        }
        await Finish(2);
    }

    /// <summary>
    /// Goes to LV95 (<paramref name="e"/>, <paramref name="n"/>) and waits for its tiles; the
    /// nearest block of flats there with an elevator and a locked top-floor flat door (or the one
    /// named <paramref name="want"/>), if any.
    /// </summary>
    private async Task<(DoorIndex.Entry?, InteriorLayout?)> BlockNear(FootPlayer me, InteriorManager interiors, double e, double n, string? want = null)
    {
        var spot = _origin.ToWorld(e, n, 0);
        me.LeaveInterior(null, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        GD.Print($"{Log} looking at {e:F0},{n:F0}");
        bool Any() => DoorIndex.All().Any(d => d.Kind == BuildingKind.Apartment && (want == null || d.Key.ToString() == want)
            && new Vector2(d.World.X - spot.X, d.World.Z - spot.Z).Length() < 600f);
        if (!await Until(() => me.IsOnFloor() && Any(), want == null ? 40 : 120)) return (null, null);
        await Seconds(2.0);
        foreach (var cand in DoorIndex.All().Where(x => x.Kind == BuildingKind.Apartment && x.Key.Slot == 0
                         && (want == null || x.Key.ToString() == want))
                     .OrderBy(x => new Vector2(x.World.X - spot.X, x.World.Z - spot.Z).Length()).Take(30))
        {
            InteriorLayout? l = null;
            try { l = await interiors.GetOrCreate(cand.Key.ToString()); } catch { }
            if (l == null || l.Lifts.Count == 0) continue;
            int top = l.Lifts[0].Top;
            if (!l.InnerDoors.Any(x => x.Floor == top && x.Locked)) continue;
            return (cand, l);
        }
        return (null, null);
    }

    /// <summary>A unit vector along a wall on <paramref name="side"/>, so the two stand side by side at a door.</summary>
    private static Vector3 Along(Side side) => side is Side.Front or Side.Back ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);

    /// <summary>The other players this client draws (every body that walks through doorways and is not ours).</summary>
    private IEnumerable<Node3D> Others(InteriorManager interiors) =>
        GetTree().GetNodesInGroup("doorway_travellers").OfType<FootPlayer>().Where(p => !p.IsMultiplayerAuthority());

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
        // headless (the net tier) draws nothing to save: pictures only with WINDOWED=1
        if (DisplayServer.GetName() == "headless") return "";
        string path = base.Shot($"{_role}_{name}");
        GD.Print($"{Log} screenshot {path}");
        return path;
    }
}
