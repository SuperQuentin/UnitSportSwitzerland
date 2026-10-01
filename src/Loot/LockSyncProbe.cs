using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>--locksynccheck A|B</c> with <c>--connect</c> (#165): two real clients in one building with a
/// gun locker or safe, coordinated through chat lines like <see cref="LootSyncProbe"/>.
/// A first reads the building with the smart binoculars at its door (the table must list what the
/// locked container gives). Inside, B submits a wrong combination: the server must refuse it and the
/// door stay shut. A then cracks it through the real dial (<see cref="LockPickUi.Turn"/>, stopping on
/// each click until the tumbler drops), the server opens it, A's panel opens on its contents; B must
/// see the door swing without doing anything, search it straight away (no dial) and see the same
/// stacks, and, after leaving and coming back, still find it open (the server's lock state, not a
/// local memory). Run server and clients with the same <c>--lootepoch N</c> and <c>--at E,N</c>.
/// </summary>
public partial class LockSyncProbe : ChatProbe
{
    public static string? Role => RoleArg("--locksynccheck");

    private readonly WorldOrigin _origin;

    public LockSyncProbe(ItemController items, WorldOrigin origin) : base(items, "locksync", "LS", "locksync_") => _origin = origin;

    public LockSyncProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        string other = _role == "A" ? "B" : "A";

        if (!await Joined(120)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        var loot = LootService.Instance!;

        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        if (!await Until(() => me.IsOnFloor() && !me.KnockedOut && DoorIndex.Nearest(me.GlobalPosition, 400f) != null, 60)) { Fail("no door near the spot"); return; }
        await Seconds(1.0);

        // the nearest building (to the agreed spot, so both pick the same) with a ground-floor lock
        DoorIndex.Entry? found = null;
        InteriorLayout? plan = null;
        int index = -1;
        var doors = DoorIndex.All()
            .Where(d => d.Kind != BuildingKind.Garage && new Vector2(d.World.X - spot.X, d.World.Z - spot.Z).Length() < 300f)
            .OrderBy(d => new Vector2(d.World.X - spot.X, d.World.Z - spot.Z).Length()).Take(80).ToList();
        foreach (var d in doors)
        {
            InteriorLayout? l = null;
            try { l = await interiors.GetOrCreate(d.Key.ToString()); } catch { }
            if (l == null || l.Key != d.Key.ToString()) continue;   // a church member's door: plan is the primary's
            int i = l.Furniture.FindIndex(f => f.Floor == 0 && LootTables.IsLocked(f.Type));
            if (i < 0) continue;
            (found, plan, index) = (d, l, i);
            break;
        }
        if (found is not { } door || plan == null) { Fail($"no building with a ground-floor lock among {doors.Count} doors"); return; }
        string doorKey = door.Key.ToString();
        var lockType = plan.Furniture[index].Type;
        GD.Print($"[locksync {_role}] building {doorKey} ({door.Kind}), {lockType} #{index} in the {plan.RoomOf(plan.Furniture[index])?.Type}");

        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        await Until(() => me.IsOnFloor(), 10);

        if (_role == "A")
        {
            // the smart binoculars at the door: no aiming, the table names what the lock holds
            _items.Inventory.Put(0, new ItemStack(ItemId.SmartBinoculars, 1));
            _items.Inventory.Select(0);
            var hud = _items.SmartHud;
            bool read = await Until(() => hud.BuildingKey == doorKey && hud.Table != null, 20);
            Expect(read, $"the smart binoculars read {doorKey} at its door (reading {hud.BuildingKey})");
            if (read)
            {
                GD.Print($"[locksync A] scan: {string.Join(", ", hud.Table!.Take(8).Select(r => $"{r.Item} {r.Chance * 100:F0}%"))}");
                var wanted = lockType == FurnitureType.GunLocker ? ItemId.Shotgun : ItemId.Francs;
                Expect(hud.Table!.Any(r => r.Item == wanted && r.Chance > 0.05), $"the scan lists {wanted}");
                Shot("scan");
            }
            _items.Inventory.Select(1);
        }

        if (!await WalkIn(me, interiors, door)) return;
        var layout = interiors.Current!;
        var node = interiors.CurrentNode!;
        if (layout.Key != plan.Key) { Fail($"walked into {layout.Key}, not {plan.Key}"); return; }
        Expect(!node.IsLockOpen(index) && !loot.IsUnlocked(layout.Key, index), "the lock starts shut");

        if (!await StandAt(me, layout, node, index)) return;
        Expect(LootService.NearestContainer(me, layout, node) == index, $"standing in front of the {lockType}");
        Shot("shut");

        bool both = false;
        for (int tries = 0; tries < 40 && !both; tries++) { Say("in"); both = await Heard(other, "in", 2); }
        if (!both) { Fail($"{other} never came in"); return; }
        Say("in");
        await Seconds(1.0);

        long epoch = LootTables.Epoch(layout.Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var combo = LootTables.Combination(layout.Key, index, epoch, lockType);
        var ui = loot.LockUi!;

        if (_role == "B")
        {
            // E on a locked container opens the dial, not the contents
            int facing = LootService.NearestContainer(me, layout, node);
            bool atExit = interiors.AtExit(me);
            bool pressed = me.TryInteract();
            Expect(pressed && ui.IsOpen && !loot.IsOpen, $"E on the lock opens the dial (E {pressed}, dial {ui.IsOpen}, panel {loot.IsOpen}, facing #{facing}, at exit {atExit})");
            var wrong = combo.Select(v => (v + 37) % 100).ToArray();
            loot.SubmitCombination(wrong);
            await Seconds(2.0);
            Expect(!loot.IsUnlocked(layout.Key, index) && !node.IsLockOpen(index) && ui.Stage == 0,
                "the server refuses a wrong combination: still locked, the tumblers fall back");
            loot.StopPicking();
            Say("refused");

            if (!await Heard("A", "cracked", 120)) { Fail("A never cracked it"); return; }
            Expect(await Until(() => node.IsLockOpen(index) && loot.IsUnlocked(layout.Key, index), 5),
                "B saw the door swing open without doing anything");
            await Seconds(1.2);
            Shot("open");
            Expect(me.TryInteract() && loot.IsOpen && !ui.IsOpen, "B searches it straight away, no dial");
            await Until(() => !loot.Waiting, 10);
            string seen = string.Join(",", loot.OpenContents().Select(c => $"{c.Stack.Id}x{c.Stack.Count}"));
            string theirs = _heard.Last(l => l.Contains("LS A cracked"));
            int lb = theirs.IndexOf('['), rb = theirs.LastIndexOf(']');
            theirs = lb >= 0 && rb > lb ? theirs[(lb + 1)..rb] : "?";
            Expect(seen == theirs, $"B sees what A saw: {seen} / {theirs}");
            loot.Close();

            // out and back in through the door: the open locker comes from the server's lock state, not from memory
            interiors.Leave(me);
            me.LeaveInterior(door.World + door.Outward * 2.5f + Vector3.Up * 0.3f, 0);
            await Seconds(1.5);
            Expect(interiors.CurrentNode == null && !loot.IsUnlocked(layout.Key, index), "outside, the client keeps no lock state");
            if (!await WalkIn(me, interiors, door)) return;
            Expect(interiors.CurrentNode != node || !IsInstanceValid(node), "(the interior was rebuilt: its lock doors start shut)");
            node = interiors.CurrentNode!;
            if (!await StandAt(me, layout, node, index)) return;
            Expect(await Until(() => node.IsLockOpen(index) && loot.IsUnlocked(layout.Key, index), 5),
                "back inside, the server says it is open and the door is shown open");
            Say("done");
        }
        else
        {
            if (!await Heard("B", "refused", 60)) { Fail("B never tried a wrong combination"); return; }
            Expect(me.TryInteract() && ui.IsOpen, "A opens the dial");
            GD.Print($"[locksync A] cracking {string.Join("-", combo)}");
            // turn the real dial: the right way until it clicks, then hold still until the tumbler drops
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
                if (stage == 0) Shot("dial");
                Expect(clicked && dropped, $"tumbler {stage + 1}/{combo.Length} clicked at {ui.Dial:F1} (wanted {combo[stage]}) and dropped");
                if (!dropped) { Fail("a tumbler would not drop"); return; }
            }
            bool opened = await Until(() => loot.IsOpen && !loot.Waiting, 10);
            Expect(opened && !ui.IsOpen, "the server opened it, and the panel opened on its contents");
            Expect(node.IsLockOpen(index), "A's door is shown open");
            var contents = loot.OpenContents().ToList();
            GD.Print($"[locksync A] it holds {string.Join(", ", contents.Select(c => $"{c.Stack.Id} x{c.Stack.Count}"))}");
            Say($"cracked [{string.Join(",", contents.Select(c => $"{c.Stack.Id}x{c.Stack.Count}"))}]");
            if (!await Heard("B", "done", 90)) { Fail("B never finished"); return; }
            int before = PackTotal();
            loot.TakeAll();
            await Until(() => !loot.Waiting && !loot.OpenContents().Any(), 10);
            Expect(PackTotal() - before == contents.Sum(c => c.Stack.Count), $"A took it all ({PackTotal() - before})");
        }

        await Finish(2);
    }

    /// <summary>From just outside the door: open it unless it stands open, and walk through.</summary>
    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, DoorIndex.Entry door)
    {
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);   // the server checks the doorstep against its relayed copy
        await Until(() => me.IsOnFloor(), 10);
        // both clients may reach for the door at once, one shutting what the other just opened: try again
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
        await Seconds(1.0);   // the server learns the space a round trip after the sill
        return true;
    }

    /// <summary>Into the building, in front of the piece, side by side with the other client.</summary>
    private async Task<bool> StandAt(FootPlayer me, InteriorLayout layout, InteriorNode node, int index)
    {
        var f = layout.Furniture[index];
        var turn = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2);
        var front = turn * new Vector3(0, 0, f.D / 2 + 0.55f);
        var face = node.GlobalTransform.Basis * -front;
        float sign = _role == "A" ? -1f : 1f;
        foreach (float offset in new[] { 0.3f, 0.15f, 0f, 0.45f })
        {
            var side = turn * new Vector3(sign * offset, 0, 0);
            me.EnterInterior(layout.Key, node.GlobalTransform * (new Vector3(f.X, f.Floor * layout.StoreyHeight + 0.1f, f.Z) + front + side), Mathf.Atan2(-face.X, -face.Z));
            me.Velocity = Vector3.Zero;
            await Seconds(0.8);
            if (InteriorManager.Instance?.Current?.Key == layout.Key && LootService.NearestContainer(me, layout, node) == index) return true;
        }
        Fail($"could not stand in front of #{index}");
        return false;
    }

    /// <summary>What this client sees, for the PR: test_output/locksync_ROLE_NAME.png.</summary>
    protected override string Shot(string name)
    {
        string path = base.Shot($"{_role}_{name}");
        GD.Print($"{Log} screenshot {path}");
        return path;
    }
}
