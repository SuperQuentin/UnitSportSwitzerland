using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Crafting;

/// <summary>
/// <c>--campfirecheck</c> (offline, <c>--systems ui,physics</c>: the flat fixture), #272: crafts a
/// campfire by hand, lays it through the item's real path (aim, Use), checks it burns (its light is
/// there) and makes a Fire station, cooks a fondue at it, holds a torch (its light on the hand), plants
/// and pulls up a flag (the path it shares), sets up a field workbench (a Workbench station) and packs it up again with an empty hand (the bench
/// comes back), then puts the fire out with an empty hand (no station any more, nothing refunded).
/// Everything it placed is gone at the end, so <c>user://placed/offline.json</c> is left as it was.
/// </summary>
public partial class CampfireProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--campfirecheck") >= 0;

    private readonly ItemController _items;
    private int _failures;

    public CampfireProbe(ItemController items) => _items = items;
    public CampfireProbe() : this(null!) { }

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    /// <summary>The scratch pack: what the campfire, the torch and a fondue take.</summary>
    public static void Stock(Inventory inv)
    {
        inv.Add(ItemId.Firewood, 6);
        inv.Add(ItemId.Stone, 4);
        inv.Add(ItemId.Cloth, 1);
        inv.Add(ItemId.Coal, 1);
        inv.Add(ItemId.Cheese, 2);
        inv.Add(ItemId.Bread, 1);
        inv.Add(ItemId.MineralWater, 1);
    }

    public override async void _Ready()
    {
        // offline starts as the spectator high up: wait for the world, then walk (T)
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        await Seconds(2.0);
        if (Me == null) GetParent<Core.ClientWorld>().ToggleMode();
        if (!await Until(() => Me is { } m && m.IsOnFloor() && PlacedObjects.Instance != null, 120))
        {
            Fail("no player on the ground");
            return;
        }
        await Seconds(1.0);
        try { await Run(Me!, PlacedObjects.Instance!); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? "[campfirecheck] RESULT: ok" : $"[campfirecheck] RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run(FootPlayer me, PlacedObjects placed)
    {
        var inv = _items.Inventory;
        var store = new InventoryStore(_items);
        var before = placed.All.Keys.ToHashSet();
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;

        // 1. by hand: a campfire and a torch; no fire yet, so no fondue
        var here = CraftStations.Where(me);
        Expect(here.Here == Station.Hands, $"by hand only to start with ({here.Here}, \"{here.Label}\")");
        var fondue = Recipes.All.First(r => r.Out == ItemId.Fondue);
        Expect(Recipes.Craft(store, fondue, 1, here.Here) == 0, "no fondue away from a fire");
        Expect(Recipes.Craft(store, Recipes.All.First(r => r.Out == ItemId.Campfire), 1, here.Here) == 1
               && inv.CountPlain(ItemId.Campfire) == 1 && inv.CountPlain(ItemId.Firewood) == 1, "a campfire made by hand");
        Expect(Recipes.Craft(store, Recipes.All.First(r => r.Out == ItemId.Torch), 1, here.Here) == 1, "a torch made by hand");

        // 2. lay the fire through the item's real path
        int fireSlot = Hold(ItemId.Campfire);
        await Seconds(0.3);   // the camera follows the new pitch
        var aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.Campfire));
        Expect(aim is { Kind: FlagAimKind.Plant, Valid: true }, $"the ghost: a spot for the fire ({aim.Kind}, {aim.Reason})");
        _items.UseSlot(me, fireSlot);
        Expect(await Until(() => New(placed, before, PlacedKind.Campfire) != null, 4), "the campfire is placed");
        var fire = New(placed, before, PlacedKind.Campfire);
        Expect(inv.CountPlain(ItemId.Campfire) == 0, "the campfire left the pack");
        await Seconds(0.3);
        var node = fire == null ? null : placed.GetNodeOrNull<Node3D>($"P{fire.Id}");
        Expect(node?.FindChild(StationVisuals.LightName, true, false) is OmniLight3D, "it burns: its light is there");
        Expect(fire != null && CampfireClock.SecondsLeft(fire.Payload, World.WorldClock.EnvNow) > CampfireClock.BurnEnvSeconds - 1800,
            $"lit just now, by the server's clock ({fire?.Payload})");

        // 3. a fire station: cook
        here = CraftStations.Where(me);
        Expect((here.Here & Station.Fire) != 0 && here.Label.Contains("campfire"), $"at a fire ({here.Here}, \"{here.Label}\")");
        Expect(Recipes.Craft(store, fondue, 1, here.Here) == 1 && inv.CountPlain(ItemId.Fondue) == 1, "a fondue cooked at the campfire");

        // 4. the torch in the hand lights up
        Hold(ItemId.Torch);
        await Seconds(0.3);
        var torchLight = me.GetNodeOrNull<HeldItemVisual>("HeldItem")?.GetNodeOrNull<OmniLight3D>(HeldItemVisual.TorchLightName);
        Expect(torchLight is { Visible: true }, "the torch lights up in the hand");

        // 5. the flag still plants and comes back up through the same path, to the other side
        inv.Add(ItemId.SwissFlag, 1);
        int flagSlot = Hold(ItemId.SwissFlag);
        me.LookYaw = -Mathf.Pi * 0.5f;
        await Seconds(0.3);
        aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.SwissFlag));
        Expect(aim is { Kind: FlagAimKind.Plant, Valid: true, Target: PlacedKind.Flag }, $"the ghost: a spot for the flag ({aim.Kind}, {aim.Reason})");
        _items.UseSlot(me, flagSlot);
        Expect(await Until(() => New(placed, before, PlacedKind.Flag) != null, 4), "the flag is planted");
        var flag = New(placed, before, PlacedKind.Flag);
        await Seconds(1.0);   // the plant stroke is over
        aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.SwissFlag));
        inv.Add(ItemId.SwissFlag, 1);   // a flag in hand picks a planted one up
        flagSlot = Hold(ItemId.SwissFlag);
        Expect(aim is { Kind: FlagAimKind.PickUp, Target: PlacedKind.Flag } && aim.Id == flag?.Id, $"a flag in hand points at the planted one ({aim.Kind})");
        int flags = inv.CountPlain(ItemId.SwissFlag);
        _items.UseSlot(me, flagSlot);
        Expect(await Until(() => flag != null && !placed.All.ContainsKey(flag.Id), 4), "the flag is pulled up");
        Expect(inv.CountPlain(ItemId.SwissFlag) == flags + 1, "and back in the pack");
        await Seconds(0.8);
        inv.TakePlain(ItemId.SwissFlag, inv.CountPlain(ItemId.SwissFlag));

        // 6. a field workbench, to the side: a workbench station, then packed up with an empty hand
        inv.Add(ItemId.FieldWorkbench, 1);
        int benchSlot = Hold(ItemId.FieldWorkbench);
        me.LookYaw = Mathf.Pi * 0.5f;
        await Seconds(0.2);
        aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.FieldWorkbench));
        Expect(aim is { Kind: FlagAimKind.Plant, Valid: true }, $"the ghost: a spot for the bench ({aim.Kind}, {aim.Reason})");
        _items.UseSlot(me, benchSlot);
        Expect(await Until(() => New(placed, before, PlacedKind.FieldWorkbench) != null, 4), "the field workbench is set up");
        var bench = New(placed, before, PlacedKind.FieldWorkbench);
        // it is a little farther than the bench's reach: step up to it
        if (bench != null)
        {
            var at = bench.WorldTransform(placed.Origin).Origin;
            var away = (me.GlobalPosition - at) with { Y = 0 };
            away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back;
            me.GlobalPosition = new Vector3(at.X, me.GlobalPosition.Y, at.Z) + away * 1.4f;
            me.Velocity = Vector3.Zero;
            await Seconds(0.4);
            var facing = at - me.GlobalPosition;
            me.LookYaw = Mathf.Atan2(-facing.X, -facing.Z);
        }
        here = CraftStations.Where(me);
        Expect((here.Here & Station.Workbench) != 0, $"at a workbench ({here.Here}, \"{here.Label}\")");
        Expect(Recipes.Craft(store, Recipes.All.First(r => r.Out == ItemId.Fondue), 1, Station.Workbench) == 0, "a bench is not a fire");

        inv.Select(EmptyHotbarSlot());
        await Seconds(0.2);
        aim = FlagGhost.Aim(me, null);
        Expect(aim is { Kind: FlagAimKind.PickUp, Target: PlacedKind.FieldWorkbench }, $"an empty hand points at the bench ({aim.Kind}, {aim.Target})");
        _items.UseHeld(me);
        Expect(await Until(() => bench != null && !placed.All.ContainsKey(bench.Id), 4), "the bench is packed up");
        Expect(inv.CountPlain(ItemId.FieldWorkbench) == 1, "and back in the pack");

        // 7. put the fire out with an empty hand: no station, nothing back
        if (fire != null)
        {
            inv.Select(EmptyHotbarSlot());   // the bench came back into the empty hand
            var at = fire.WorldTransform(placed.Origin).Origin;
            var to = at - me.GlobalPosition;
            me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
            me.LookPitch = -Mathf.Atan2(me.Camera.GlobalPosition.Y - at.Y - 0.1f, new Vector2(to.X, to.Z).Length());
            await Seconds(0.8);   // and the bench's stroke is over
            aim = FlagGhost.Aim(me, null);
            Expect(aim is { Kind: FlagAimKind.PickUp, Target: PlacedKind.Campfire }, $"an empty hand points at the fire ({aim.Kind}, {aim.Target})");
            if (aim.Kind != FlagAimKind.PickUp) placed.RequestRemove(fire.Id);   // still clean up
            else _items.UseHeld(me);
            Expect(await Until(() => !placed.All.ContainsKey(fire.Id), 4), "the fire is put out");
            Expect(inv.CountPlain(ItemId.Campfire) == 0, "a fire put out is spent, not refunded");
        }
        Expect((CraftStations.At(me) & Station.Fire) == 0, "no fire station any more");

        // whatever a failure left behind goes too
        foreach (var o in placed.All.Values.Where(o => !before.Contains(o.Id)).ToList()) placed.RequestRemove(o.Id);
    }

    private static PlacedObject? New(PlacedObjects placed, HashSet<long> before, PlacedKind kind) =>
        placed.All.Values.FirstOrDefault(o => o.Kind == kind && !before.Contains(o.Id));

    /// <summary>Moves the first stack of <paramref name="id"/> to the last hotbar slot and selects it.</summary>
    private int Hold(ItemId id)
    {
        var inv = _items.Inventory;
        int slot = Inventory.HotbarSize - 1;
        for (int i = 0; i < Inventory.Size; i++)
        {
            if (inv[i].IsEmpty || inv[i].Id != id || i == slot) continue;
            var other = inv[slot];
            inv.Put(slot, inv[i]);
            inv.Put(i, other);
            break;
        }
        inv.Select(slot);
        return slot;
    }

    private int EmptyHotbarSlot()
    {
        for (int i = 0; i < Inventory.HotbarSize; i++) if (_items.Inventory[i].IsEmpty) return i;
        _items.Inventory.Put(0, ItemStack.Empty);
        return 0;
    }

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[campfirecheck]   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[campfirecheck] RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
