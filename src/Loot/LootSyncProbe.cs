using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Loot;

/// <summary>
/// <c>--lootsynccheck A|B</c> with <c>--connect</c>: two real clients search the SAME container in
/// the same building on one server, coordinated through chat lines. A takes one stack while B has
/// the container open; B must see it go without doing anything, a take of it by B must be refused,
/// B then takes the rest, and A searching again must find it empty — every stack in exactly one pack.
/// Run the server and both clients with the same <c>--lootepoch N</c> (a restock period no earlier
/// run touched) and the same <c>--at E,N</c>. Scratch inventories; the server keeps the taken mask.
/// </summary>
public partial class LootSyncProbe : ChatProbe
{
    public static string? Role => RoleArg("--lootsynccheck");

    private readonly WorldOrigin _origin;

    public LootSyncProbe(ItemController items, WorldOrigin origin) : base(items, "lootsync", "LS", "lootsync_") => _origin = origin;

    public LootSyncProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        string other = _role == "A" ? "B" : "A";

        if (!await Joined(120)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        var loot = LootService.Instance!;

        // to the same door on both clients: the one nearest the agreed spot
        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        // a knocked-out player cannot use a door, and waking up moves them to their last safe spot
        if (!await Until(() => me.IsOnFloor() && !me.KnockedOut && DoorIndex.Nearest(me.GlobalPosition, 400f) != null, 60)) { Fail("no door near the spot"); return; }
        await Seconds(1.0);
        var door = DoorIndex.Nearest(me.GlobalPosition, 400f)!.Value;
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);   // the server checks the doorstep against its relayed copy
        await Until(() => me.IsOnFloor(), 10);

        // doors are portals (#59): E opens it — unless the other client already did, when E would
        // shut it in their face — it swings, and you walk through
        if (!interiors.IsOpen(doorKey)) me.TryInteract();
        if (!await Until(() => interiors.Links.TryGetValue(doorKey, out var l) && l.Passable && l.Swing >= 1f, 15))
        { Fail($"the door {doorKey} never opened"); return; }
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return; }
        await Seconds(1.0);   // the server learns the space a round trip after the sill
        var layout = interiors.Current!;
        var node = interiors.CurrentNode!;
        GD.Print($"[lootsync {_role}] inside {layout.Key}");

        // the first ground-floor container with something in it this restock period
        long epoch = LootTables.Epoch(layout.Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        int index = layout.Furniture.FindIndex(f => f.Floor == 0 && LootTables.IsLootable(f.Type) && !LootTables.IsLocked(f.Type)
            && LootTables.ContentsOf(layout, layout.Furniture.IndexOf(f), epoch).Count >= 2);
        if (index < 0) { Fail("no ground-floor container holding two stacks"); return; }
        var f = layout.Furniture[index];
        var front = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2) * new Vector3(0, 0, f.D / 2 + 0.55f);
        var face = node.GlobalTransform.Basis * -front;
        // side by side, or the two bodies shove each other off the spot — but still in front of
        // THIS piece: a step too far sideways and the search finds the cupboard next to it
        float sign = _role == "A" ? -1f : 1f;
        foreach (float offset in new[] { 0.35f, 0.2f, 0.1f, 0.45f, 0f })
        {
            var side = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2) * new Vector3(sign * Mathf.Min(offset, f.W / 2), 0, 0);
            me.EnterInterior(layout.Key, node.GlobalTransform * (new Vector3(f.X, 0.1f, f.Z) + front + side), Mathf.Atan2(-face.X, -face.Z));
            me.Velocity = Vector3.Zero;
            await Seconds(0.6);
            if (LootService.NearestContainer(me, layout, node) == index) break;
        }
        Expect(LootService.NearestContainer(me, layout, node) == index, $"standing in front of the {f.Type}");

        Expect(me.TryInteract() && loot.IsOpen, $"opened the {f.Type}");
        if (!await Until(() => !loot.Waiting, 10)) { Fail("contents never arrived"); return; }
        var start = loot.OpenContents().ToList();
        int total = start.Sum(c => c.Stack.Count);
        GD.Print($"[lootsync {_role}] it holds {string.Join(", ", start.Select(c => $"{c.Stack.Id} x{c.Stack.Count}"))}");
        Expect(start.Count >= 2, "both see the same container with two or more stacks");

        // said again until the other side answers: it may not have been listening the first time
        bool both = false;
        for (int tries = 0; tries < 40 && !both; tries++)
        {
            Say("open");
            both = await Heard(other, "open", 2);
        }
        if (!both) { Fail($"{other} never opened it"); return; }
        Say("open");
        await Seconds(1.5);

        if (_role == "A")
        {
            var first = start[0];
            int before = PackTotal();
            loot.Take(first.Index);
            Expect(await Until(() => !loot.Waiting && !loot.OpenContents().Any(c => c.Index == first.Index), 5), "A's take was granted");
            Expect(PackTotal() - before == first.Stack.Count, $"A's pack gained {PackTotal() - before} of {first.Stack.Count}");
            Say($"took {first.Index}");
            if (!await Heard("B", "done", 60)) { Fail("B never finished"); return; }

            // search again: nothing may be left for anyone
            loot.Close();
            await Seconds(0.5);
            me.TryInteract();
            await Until(() => loot.IsOpen && !loot.Waiting, 10);
            Expect(!loot.OpenContents().Any(), "A searching again finds it empty");
            string done = _heard.Last(l => l.Contains("LS B done"));
            int bGot = int.Parse(done[(done.LastIndexOf(' ') + 1)..]);
            Expect(first.Stack.Count + bGot == total, $"A {first.Stack.Count} + B {bGot} = {total}: every stack in exactly one pack");
        }
        else
        {
            if (!await Heard("A", "took", 60)) { Fail("A never took"); return; }
            string took = _heard.Last(l => l.Contains("LS A took"));
            int gone = int.Parse(took[(took.LastIndexOf(' ') + 1)..]);
            await Seconds(1.0);
            Expect(!loot.OpenContents().Any(c => c.Index == gone),
                "B's open panel lost the stack A took, without B doing anything");

            // even with a stale view, the server must not hand the same stack out twice
            int before = PackTotal();
            var stackA = start.First(c => c.Index == gone).Stack;
            loot.Take(gone);
            await Until(() => !loot.Waiting, 5);
            await Seconds(0.3);
            Expect(PackTotal() == before && !loot.OpenContents().Any(c => c.Index == gone),
                $"B taking {stackA.Id} as well is refused, and B's view catches up");

            loot.TakeAll();
            await Until(() => !loot.Waiting && !loot.OpenContents().Any(), 10);
            Expect(!loot.OpenContents().Any(), "B took the rest");
            Say($"done {PackTotal() - before}");
        }

        await Finish(2);
    }
}
