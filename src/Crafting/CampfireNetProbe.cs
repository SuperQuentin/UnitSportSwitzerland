using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Crafting;

/// <summary>
/// <c>--campfirenet A|B</c> with <c>--connect</c> (driven by <c>tools/campfirecheck.sh</c>), #272:
/// a campfire over loopback. A crafts one by hand and lays it through the item's real path, then
/// holds a torch. B joins after: it must get the fire from the join snapshot, burning (its light is
/// there), see the torch's light on A's hand (the replicated held item), and be refused putting out
/// A's burning fire (only its owner may). A then puts it out; B must see it go.
/// Scratch inventories (<see cref="CampfireProbe.Stock"/>).
/// </summary>
public partial class CampfireNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--campfirenet");

    public CampfireNetProbe(ItemController items) : base(items, "campfirenet", "CF") { }
    public CampfireNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => PlacedObjects.Instance != null)) return;
        await Seconds(2.0);   // the join snapshot, and the server's copy of our position
        try
        {
            if (_role == "A") await RunA(Me!, PlacedObjects.Instance!);
            else await RunB(PlacedObjects.Instance!);
        }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        await Finish(1.5);
    }

    private async Task RunA(FootPlayer me, PlacedObjects placed)
    {
        // a run that died half way left its fire on the server: put it out first
        foreach (var old in placed.All.Values.Where(o => o.Owner == "FireA").ToList()) placed.RequestRemove(old.Id);
        await Until(() => placed.All.Values.All(o => o.Owner != "FireA"), 5);

        var store = new InventoryStore(_items);
        Expect(Recipes.Craft(store, Recipes.All.First(r => r.Out == ItemId.Campfire), 1, CraftStations.At(me)) == 1, "a campfire made by hand");
        Expect(Recipes.Craft(store, Recipes.All.First(r => r.Out == ItemId.Torch), 1, CraftStations.At(me)) == 1, "a torch made by hand");

        var before = placed.All.Keys.ToHashSet();
        int slot = SlotOf(ItemId.Campfire);
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;
        await Seconds(0.3);
        var aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.Campfire));
        Expect(aim is { Kind: FlagAimKind.Plant, Valid: true }, $"a spot for the fire ({aim.Kind}, {aim.Reason})");
        _items.UseSlot(me, slot);
        Expect(await Until(() => placed.All.Values.Any(o => o.Kind == PlacedKind.Campfire && !before.Contains(o.Id)), 6), "the server lit the fire");
        var fire = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Campfire && !before.Contains(o.Id));
        if (fire == null) { Fail("no fire to go on with"); return; }
        Expect(fire.Owner == "FireA", $"it is mine ({fire.Owner})");
        Expect(CampfireClock.Burning(fire.Payload, World.WorldClock.EnvNow), $"lit by the server's clock ({fire.Payload})");
        Expect((CraftStations.At(me) & Station.Fire) != 0, "a fire station here");

        // the torch in the hand: B must see its light
        int t = SlotOf(ItemId.Torch);
        if (t > 0)
        {
            var torch = _items.Inventory[t];
            _items.Inventory.Put(t, _items.Inventory[0]);
            _items.Inventory.Put(0, torch);
        }
        _items.Inventory.Select(0);
        Expect(_items.Inventory.HeldId == ItemId.Torch, "the torch in hand");

        // chat is not replayed to a late joiner: say it until B answers
        for (int i = 0; i < 60 && !_heard.Any(l => l.Contains("CF B seen")); i++)
        {
            Say($"lit {fire.Id}");
            await Heard("B", "seen", 2.5);
        }
        if (!_heard.Any(l => l.Contains("CF B seen"))) { Fail("B never reported"); return; }
        Expect(await Heard("B", "refused", 30), "B was refused putting out my fire");
        Expect(placed.All.ContainsKey(fire.Id), "my fire still burns");

        PlacedResult? removed = null;
        placed.RequestRemove(fire.Id, r => removed = r);
        Expect(await Until(() => removed != null, 5) && removed!.Value.Ok, $"I put it out ({removed?.Refused})");
        for (int i = 0; i < 20 && !_heard.Any(l => l.Contains("CF B gone")); i++)
        {
            Say("out");
            await Heard("B", "gone", 2.5);
        }
        Expect(_heard.Any(l => l.Contains("CF B gone")), "B saw it go");
    }

    private async Task RunB(PlacedObjects placed)
    {
        if (!await Heard("A", "lit", 150)) { Fail("A never lit a fire"); return; }
        long id = long.Parse(_heard.Last(l => l.Contains("CF A lit")).Split(' ').Last());
        Expect(await Until(() => placed.All.ContainsKey(id), 10), "the join snapshot has A's fire");
        Expect(await Until(() => placed.GetNodeOrNull<Node3D>($"P{id}")?.FindChild(StationVisuals.LightName, true, false) is OmniLight3D, 10),
            "it burns here: its light is there");
        Expect(placed.All.TryGetValue(id, out var fire) && fire.Owner == "FireA", $"A's fire ({fire?.Owner})");

        // A holds a torch: its light on A's hand, from the replicated held item
        Expect(await Until(() => TorchOf() is { Visible: true }, 15), "A's torch lights up on this side");

        PlacedResult? tried = null;
        placed.RequestRemove(id, r => tried = r);
        Expect(await Until(() => tried != null, 5) && tried!.Value.Refused == "That is not yours.",
            $"putting out A's burning fire is refused ({tried?.Refused})");
        Expect(placed.All.ContainsKey(id), "it still burns");
        Say("seen");
        await Seconds(0.5);
        Say("refused");

        Expect(await Until(() => !placed.All.ContainsKey(id) && placed.GetNodeOrNull($"P{id}") == null, 60), "A's fire went out here");
        Say("gone");
        await Seconds(0.5);
        Say("gone");
    }

    /// <summary>The torch light on the other player's held item, if any.</summary>
    private OmniLight3D? TorchOf()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in GetParent().GetNodeOrNull("Players")?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer { Npc: false } p && p.Name != own
                && p.GetNodeOrNull<HeldItemVisual>("HeldItem")?.GetNodeOrNull<OmniLight3D>(HeldItemVisual.TorchLightName) is { } light)
                return light;
        return null;
    }
}
