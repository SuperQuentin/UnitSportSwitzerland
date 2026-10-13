using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--siteofficenet A|B|C</c> with <c>--connect</c> on the generated world (#822): a building
/// site's office over the network. A opens the office door, walks in and takes a stack from one
/// of its containers. B, there from the start and waiting outside, must see the door open (the
/// server's broadcast), then walk in through it and find the stack gone from the same container.
/// C joins late, after A's take: the door must already be open (the server's door table) and the
/// stack gone. Coordinated through chat lines; A repeats its take until both answer. Every peer
/// finds the same door from the tile alone (<see cref="DoorIndex.NearestOfKind"/>).
/// </summary>
public partial class SiteOfficeNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--siteofficenet");

    private readonly WorldOrigin _origin;

    public SiteOfficeNetProbe(ItemController items, WorldOrigin origin) : base(items, "siteoffice", "SO") => _origin = origin;

    public SiteOfficeNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(180)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        var loot = LootService.Instance!;

        // to the site, and the office's main door nearest it: the same on every peer
        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        DoorIndex.Entry? found = null;
        if (!await Until(() => me.IsOnFloor() && !me.KnockedOut
                && (found = DoorIndex.NearestOfKind(me.GlobalPosition, 200f, BuildingKind.UnderConstruction, also: d => d.Key.Slot == 0)) != null, 90))
        { Fail("no site office door near the spot"); return; }
        await Seconds(1.0);
        var door = found!.Value;
        string doorKey = door.Key.ToString();
        GD.Print($"{Log} the office door {doorKey} at {door.World} (kind {door.Kind})");
        Expect(door.Kind == BuildingKind.UnderConstruction, $"{doorKey} is a building site's door");

        var inward = -door.Outward;
        void Doorstep(float out_) =>
            me.LeaveInterior(door.World + door.Outward * out_ + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));

        if (_role == "A")
        {
            Doorstep(1.2f);
            me.Velocity = Vector3.Zero;
            await Seconds(1.5);   // the server checks the doorstep against its relayed copy
            await Until(() => me.IsOnFloor(), 10);
            // B is waiting to see it open: it starts shut
            Expect(!interiors.IsOpen(doorKey), $"{doorKey} starts shut");
            if (!await Heard("B", "watching", 120)) { Fail("B never came to watch"); return; }
            me.TryInteract();
            if (!await Until(() => interiors.Links.TryGetValue(doorKey, out var l) && l.Passable && l.Swing >= 1f, 15))
            { Fail($"the door {doorKey} never opened"); return; }
            Say("opened");
            if (!await WalkIn(me, interiors, doorKey)) return;

            var layout = interiors.Current!;
            var node = interiors.CurrentNode!;
            long epoch = LootTables.Epoch(layout.Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            int index = -1;
            for (int i = 0; i < layout.Furniture.Count && index < 0; i++)
                if (layout.Furniture[i].Floor == 0 && LootTables.IsLootable(layout.Furniture[i].Type) && !LootTables.IsLocked(layout.Furniture[i].Type)
                    && LootTables.ContentsOf(layout, i, epoch).Count > 0) index = i;
            if (index < 0) { Fail("no container of the office holds anything this restock period: run again"); return; }
            if (!await Search(me, layout, node, loot, index)) return;
            var start = loot.OpenContents().ToList();
            var first = start[0];
            int before = PackTotal();
            loot.Take(first.Index);
            Expect(await Until(() => !loot.Waiting && !loot.OpenContents().Any(c => c.Index == first.Index), 5), "A's take was granted");
            Expect(PackTotal() - before == first.Stack.Count, $"A's pack gained {PackTotal() - before} of {first.Stack.Count} {first.Stack.Id}");
            loot.Close();
            // by the door inside, so it is not shut for want of anybody near it
            var placement = InteriorManager.PlacementFor(layout, _origin);
            var way = layout.EntranceFor(doorKey);
            var by = placement * new Vector3(way.X + way.InX * 1.2f, 0.1f, way.Z + way.InZ * 1.2f);
            me.EnterInterior(layout.Key, by, me.Rotation.Y);
            // said over and over: C has not joined yet
            bool b = false, c = false;
            for (int t = 0; t < 120 && !(b && c); t++)
            {
                Say($"took {index} {first.Index}");
                if (t == 0) GD.Print($"{Log} took {first.Stack.Id} x{first.Stack.Count} from the {layout.Furniture[index].Type} ({index})");
                await Seconds(2);
                b |= _heard.Any(l => l.Contains("SO B done"));
                c |= _heard.Any(l => l.Contains("SO C done"));
            }
            Expect(b, "B came in and found the stack gone");
            Expect(c, "C, who joined late, came in and found the stack gone");
        }
        else
        {
            // a few metres out, facing the door
            Doorstep(3.0f);
            me.Velocity = Vector3.Zero;
            await Seconds(1.5);
            await Until(() => me.IsOnFloor(), 10);
            if (_role == "B")
            {
                Expect(!interiors.IsOpen(doorKey), $"{doorKey} is shut before A opens it");
                for (int t = 0; t < 60 && !interiors.IsOpen(doorKey); t++)
                {
                    Say("watching");
                    await Until(() => interiors.IsOpen(doorKey), 2);
                }
                Expect(interiors.IsOpen(doorKey), $"B sees A open {doorKey}");
            }
            else
                // joined after A's take: the door is open from the table the server sends a newcomer
                Expect(await Until(() => interiors.IsOpen(doorKey), 10), $"C, joining late, finds {doorKey} open");
            Expect(await Until(() => interiors.Links.TryGetValue(doorKey, out var l) && l.Passable, 20), $"{_role}'s portal of {doorKey} is open");
            if (!await Heard("A", "took", 180)) { Fail("A never took anything"); return; }
            string took = _heard.Last(l => l.Contains("SO A took"));
            var parts = took[(took.IndexOf("took", StringComparison.Ordinal) + 5)..].Split(' ');
            int index = int.Parse(parts[0]), gone = int.Parse(parts[1]);

            Doorstep(1.2f);
            me.Velocity = Vector3.Zero;
            await Seconds(1.0);
            if (!await WalkIn(me, interiors, doorKey)) return;
            var layout = interiors.Current!;
            if (!await Search(me, layout, interiors.CurrentNode!, loot, index)) return;
            var left = loot.OpenContents().ToList();
            GD.Print($"{Log} the {layout.Furniture[index].Type} holds {string.Join(", ", left.Select(x => $"{x.Stack.Id} x{x.Stack.Count}"))}");
            Expect(!left.Any(x => x.Index == gone), $"{_role} finds the stack A took gone from the {layout.Furniture[index].Type}");
            loot.Close();
            Say("done");
        }
        await Finish(2);
    }

    /// <summary>Walks through the open door into the office: the player's space must become it.</summary>
    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, string doorKey)
    {
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);   // the server learns the space a round trip after the sill
        Expect(interiors.Current!.Kind == BuildingKind.UnderConstruction, $"{_role} walked into the site office {interiors.Current.Key}");
        return true;
    }

    /// <summary>Stands in front of a piece of the office's furniture and opens it.</summary>
    private async Task<bool> Search(FootPlayer me, InteriorLayout layout, InteriorNode node, LootService loot, int index)
    {
        var f = layout.Furniture[index];
        var front = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2) * new Vector3(0, 0, f.D / 2 + 0.55f);
        var face = node.GlobalTransform.Basis * -front;
        me.EnterInterior(layout.Key, node.GlobalTransform * (new Vector3(f.X, 0.1f, f.Z) + front), Mathf.Atan2(-face.X, -face.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(0.8);
        Expect(LootService.NearestContainer(me, layout, node) == index, $"standing in front of the {f.Type}");
        Expect(me.TryInteract() && loot.IsOpen, $"opened the {f.Type}");
        if (!await Until(() => !loot.Waiting, 10)) { Fail("its contents never arrived"); return false; }
        return true;
    }
}
