using Godot;
using UnitSport.Core;
using UnitSport.Crafting;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// <c>--handfarmcheck</c> (offline, flat fixture, <c>--systems ui,physics,loot,farming --farmmonth 7
/// --farmdir test_output/handfarm_store --farmfresh</c>), #494: hand farming through the player's own
/// item path, never calling the farm code directly. An empty pack is lent for the run
/// (<see cref="Inventory.BeginMatch"/>); a hoe, two bags of wheat seed and two of fertiliser come in
/// through <see cref="ItemController.Give"/>; each is picked with the hotbar slot action and used with
/// the <c>use_item</c> action (the event a key, the pad's RB and the VR trigger all become):
/// the gather hold harvests a ripe cell into the pack, the hoe tills it, one seed bag sows exactly
/// <see cref="FarmTables.CellsPerSeed"/> cells and the 51st opens the next, fertiliser is used up and
/// its cells ripen first (<see cref="FarmField.ClockSkew"/>), the gather hold brings the wheat in;
/// then a field workbench mills it to flour, a campfire bakes bread and the bread is eaten. The
/// prompts are checked on the keyboard, and their labels for the pad and VR. Everything placed is
/// removed and the pack given back at the end.
/// </summary>
public partial class HandFarmCheck : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--handfarmcheck") >= 0;

    private const string Tag = "[handfarmcheck]";
    private readonly ItemController _items;
    private readonly WorldOrigin _origin;
    private int _failures;

    public HandFarmCheck(ItemController items, WorldOrigin origin) { _items = items; _origin = origin; }
    public HandFarmCheck() : this(null!, null!) { }

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;
    private Inventory Inv => _items.Inventory;

    public override async void _Ready()
    {
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        await Seconds(2.0);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        var (se, sn) = SpawnPoint.ParseTarget();
        var tile = TileId.FromLv95(se + 60, sn);
        if (!await Until(() => Me is { } m && m.IsOnFloor() && FarmField.Instance?.HasFields(tile) == true
                                && HandFarming.Instance != null && PlacedObjects.Instance != null, 120))
        {
            GD.Print($"{Tag} RESULT: FAILED - no player on the ground, or no field tile");
            GetTree().Quit(1);
            return;
        }
        await Seconds(0.5);
        var gathering = GetParent().GetNodeOrNull<Loot.Gathering>("Gathering");
        var placed = PlacedObjects.Instance!;
        var before = placed.All.Keys.ToHashSet();
        double skew = FarmField.ClockSkew;
        Inv.BeginMatch();
        try
        {
            if (gathering == null) Expect(false, "Gathering is on (--systems loot)");
            else await Run(Me!, FarmField.Instance!, HandFarming.Instance!, gathering, placed, before, se, sn);
        }
        catch (Exception e) { Expect(false, $"threw: {e}"); }
        finally
        {
            Input.ActionRelease(PlayerInput.Gather);
            foreach (var o in placed.All.Values.Where(o => !before.Contains(o.Id)).ToList()) placed.RequestRemove(o.Id);
            FarmField.ClockSkew = skew;
            Inv.EndMatch();
        }
        GD.Print(_failures == 0 ? $"{Tag} RESULT: ok" : $"{Tag} RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run(FootPlayer me, FarmField farm, HandFarming hand, Loot.Gathering gathering, PlacedObjects placed,
        HashSet<long> before, double se, double sn)
    {
        // 0. the lent pack is empty; the farm kit comes in the way a find or a purchase does
        int held = 0;
        for (int i = 0; i < Inventory.Size; i++) if (!Inv[i].IsEmpty) held++;
        Expect(held == 0 && Inv.InMatch, $"an empty pack is lent ({held} stacks)");
        _items.Give(new ItemStack(ItemId.Hoe, 1));
        _items.Give(new ItemStack(ItemId.WheatSeed, 2));
        _items.Give(new ItemStack(ItemId.Fertiliser, 2));
        int hoe = SlotOf(ItemId.Hoe), seed = SlotOf(ItemId.WheatSeed), fert = SlotOf(ItemId.Fertiliser);
        Expect(Inventory.IsHotbar(hoe) && Inventory.IsHotbar(seed) && Inventory.IsHotbar(fert),
            $"hoe, seed and fertiliser on the hotbar (slots {hoe + 1}, {seed + 1}, {fert + 1})");
        foreach (var id in new[] { ItemId.Hoe, ItemId.WheatSeed, ItemId.Fertiliser })
        {
            string blurb = InputHints.Format(ItemDefs.Get(id)!.Blurb, InputDevice.KeyboardMouse);
            Expect(!blurb.Contains('{') && blurb.Contains(InputHints.Label(PlayerInput.UseItem, InputDevice.KeyboardMouse)),
                $"{id}'s description names the key: \"{blurb[..Math.Min(32, blurb.Length)]}…\"");
        }

        // 1. the controls on the three devices: use_item and gather, each named
        foreach (var device in new[] { InputDevice.KeyboardMouse, InputDevice.Gamepad, InputDevice.VR })
        {
            string use = InputHints.Label(PlayerInput.UseItem, device), gather = InputHints.Label(PlayerInput.Gather, device);
            Expect(use != "?" && gather != "?", $"{device}: use = \"{use}\", gather = \"{gather}\"");
        }

        // 2. a ripe cell: the gather hold harvests it into the pack
        // on a cell's centre line, so the cells ahead are never on a border
        const float cs = FieldFormat.CellSize;
        double ae = (Math.Floor((se + 50 + HandFarming.Reach) / cs) + 0.5) * cs, an = (Math.Floor((sn - 2) / cs) + 0.5) * cs;
        await Stand(me, ae - HandFarming.Reach, an, East);
        await Seconds(0.4);
        Expect(hand.Readout.StartsWith("Wheat — ripe"), $"the readout: \"{hand.Readout}\"");
        string gatherTag = InputHints.Tag(PlayerInput.Gather);
        Expect(gathering.Target == Loot.Gathering.Resource.Crop && gathering.Prompt == $"{gatherTag} Hold to harvest wheat",
            $"the gather prompt: \"{gathering.Prompt}\" ({gathering.Target})");
        Expect(await HoldGather(() => farm.CellAtLv95(ae, an)?.Stage == FieldStage.Stubble), $"the gather hold harvests it ({farm.CellAtLv95(ae, an)?.Stage})");
        int wheat = Inv.CountPlain(ItemId.Wheat);
        Expect(wheat == FarmRules.HandYield(CropKind.Wheat), $"wheat in the pack: {wheat}");
        await Seconds(0.3);
        Expect(hand.Readout == "Wheat — stubble", $"the readout: \"{hand.Readout}\"");

        // 3. the hoe from the hotbar: its prompt, then Use tills the stubble
        await PressSlot(hoe);
        Expect(Inv.Selected == hoe && Inv.HeldId == ItemId.Hoe, $"slot {hoe + 1} picks the hoe ({Inv.HeldId})");
        await Seconds(0.3);
        string useTag = InputHints.Tag(PlayerInput.UseItem);
        Expect(hand.Prompt == $"{useTag} Till the soil", $"the hoe's prompt: \"{hand.Prompt}\"");
        Expect(await PressUse(hand) && farm.CellAtLv95(ae, an)?.Stage == FieldStage.Ploughed, $"Use with the hoe tills it ({farm.CellAtLv95(ae, an)?.Stage})");
        Expect(Inv.CountPlain(ItemId.Hoe) == 1, "the hoe is kept");
        // a ripe cell is tilled too (the hoe turns the crop under), and a second one beside it
        var (be, bn) = (ae, an + FieldFormat.CellSize);
        await Stand(me, be - HandFarming.Reach, bn, East);
        // switching to another slot half way puts the hoe away: the stroke stops, the cell is left
        await PressUseItem();
        bool started = hand.Busy;
        await PressSlot(seed);
        Expect(started && !hand.Busy && farm.CellAtLv95(be, bn)?.Stage == FieldStage.Ripe, $"switching slots stops the stroke ({farm.CellAtLv95(be, bn)?.Stage})");
        await PressSlot(hoe);
        Expect(await PressUse(hand) && farm.CellAtLv95(be, bn)?.Stage == FieldStage.Ploughed, $"a ripe cell tilled ({farm.CellAtLv95(be, bn)?.Stage})");

        // the rest of a block ploughed the machine way (FarmWork.Sweep), so 51 cells are ready to sow
        var cells = new List<(double E, double N)> { (ae, an), (be, bn) };
        double c0 = (Math.Floor((ae + cs) / cs) + 0.5) * cs;
        for (int col = 0; col < 4; col++)
        {
            double x = c0 + col * cs;
            FarmWork.Sweep(FarmTool.Plough, W(x, sn - 30), W(x, sn + 30), 3f);
            for (double n = (Math.Floor((sn - 28) / cs) + 0.5) * cs; n < sn + 28; n += cs)
                if (farm.CellAtLv95(x, n)?.Stage == FieldStage.Ploughed) cells.Add((x, n));
        }
        Expect(cells.Count > FarmTables.CellsPerSeed, $"{cells.Count} cells ploughed to sow");

        // 4. a seed bag from the hotbar: one bag sows CellsPerSeed cells, then the next is opened
        await PressSlot(seed);
        await Seconds(0.3);
        Expect(Inv.HeldId == ItemId.WheatSeed && hand.Prompt == $"{useTag} Sow wheat", $"the seed's prompt: \"{hand.Prompt}\"");
        int sown = 0;
        bool bagsRight = true;
        foreach (var (e, n) in cells)
        {
            if (sown > FarmTables.CellsPerSeed) break;
            await Stand(me, e - HandFarming.Reach, n, East, 0.1);
            if (!await PressUse(hand) || farm.CellAtLv95(e, n) is not { Stage: FieldStage.Sown, Crop: CropKind.Wheat })
            {
                Expect(false, $"sowing cell {sown + 1} at {e - se:F0},{n - sn:F0} ({farm.CellAtLv95(e, n)?.Stage})");
                break;
            }
            sown++;
            int bags = Inv.CountPlain(ItemId.WheatSeed);
            int want = sown <= FarmTables.CellsPerSeed ? 1 : 0;
            if (bags != want && bagsRight)
            {
                bagsRight = false;
                Expect(false, $"after {sown} cells {bags} bags are left (want {want})");
            }
        }
        Expect(sown == FarmTables.CellsPerSeed + 1 && bagsRight,
            $"{sown} cells sown by hand: the first bag covered {FarmTables.CellsPerSeed}, the 51st opened the second ({Inv.CountPlain(ItemId.WheatSeed)} left)");
        var (le, ln) = cells[sown - 1];
        await Seconds(0.3);   // the readout is polled
        Expect(hand.Readout.StartsWith("Wheat — sown"), $"the readout: \"{hand.Readout}\"");

        // 5. fertiliser from the hotbar on the last cell sown: one bag a use, its 3x3 fed
        await PressSlot(fert);
        await Seconds(0.3);
        Expect(Inv.HeldId == ItemId.Fertiliser && hand.Prompt == $"{useTag} Spread fertiliser", $"the fertiliser's prompt: \"{hand.Prompt}\"");
        Expect(await PressUse(hand) && hand.LastStroke.Cells >= 1, $"fertiliser spread on {hand.LastStroke.Cells} sown cells");
        Expect(Inv.CountPlain(ItemId.Fertiliser) == 1, $"one bag used ({Inv.CountPlain(ItemId.Fertiliser)} left)");

        // 6. time: the fertilised cell ripens first (0.6 of the time), the first cell sown is still growing
        FarmField.ClockSkew += FarmTables.GrowSeconds(CropKind.Wheat) * FarmTables.FertilisedGrowth + 120;
        var (fe, fn) = cells[0];
        Expect(farm.CellAtLv95(le, ln)?.Stage == FieldStage.Ripe, $"the fertilised cell is ripe ({farm.CellAtLv95(le, ln)?.Stage})");
        Expect(farm.CellAtLv95(fe, fn)?.Stage == FieldStage.Growing, $"an unfed cell still grows ({farm.CellAtLv95(fe, fn)?.Stage} {farm.CellAtLv95(fe, fn)?.Growth:F2})");

        // 7. harvest it with the gather hold: wheat into the pack
        await Stand(me, le - HandFarming.Reach, ln, East);
        await Seconds(0.3);
        Expect(gathering.Target == Loot.Gathering.Resource.Crop, $"Gathering offers the crop again ({gathering.Target})");
        Expect(await HoldGather(() => farm.CellAtLv95(le, ln)?.Stage == FieldStage.Stubble), $"harvested ({farm.CellAtLv95(le, ln)?.Stage})");
        Expect(Inv.CountPlain(ItemId.Wheat) == wheat + FarmRules.HandYield(CropKind.Wheat), $"wheat in the pack: {Inv.CountPlain(ItemId.Wheat)}");
        Expect(FarmTables.IsHarvest(ItemId.Wheat) && !FarmTables.IsHarvest(ItemId.Flour), "the co-op takes the wheat as a harvest, not the flour");

        // 8. off the field: a field workbench mills a sack to flour
        var store = new InventoryStore(_items);
        await Stand(me, se + 10, sn, North);
        me.LookPitch = -0.6f;
        _items.Give(new ItemStack(ItemId.FieldWorkbench, 1));
        await PressSlot(SlotOf(ItemId.FieldWorkbench));
        await Seconds(0.3);
        await PressUseItem();
        Expect(await Until(() => New(placed, before, PlacedKind.FieldWorkbench) != null, 4), "a field workbench set up with Use");
        if (New(placed, before, PlacedKind.FieldWorkbench) is { } bench) await StepUpTo(me, bench.WorldTransform(placed.Origin).Origin);
        var here = CraftStations.Where(me);
        var flour = Recipes.All.First(r => r.Out == ItemId.Flour && r.In[0].Id == ItemId.Wheat);
        int wheatNow = Inv.CountPlain(ItemId.Wheat);
        Expect((here.Here & Station.Workbench) != 0 && Recipes.Craft(store, flour, 1, here.Here) == 1, $"milled at {here.Label}");
        Expect(Inv.CountPlain(ItemId.Flour) == flour.Count && Inv.CountPlain(ItemId.Wheat) == wheatNow - 1,
            $"1 wheat -> {Inv.CountPlain(ItemId.Flour)} flour ({Inv.CountPlain(ItemId.Wheat)} wheat left)");

        // 9. a campfire made by hand and lit with Use: bread baked from the flour
        _items.Give(new ItemStack(ItemId.Firewood, 5));
        _items.Give(new ItemStack(ItemId.Stone, 4));
        _items.Give(new ItemStack(ItemId.WaterBottle, 1));
        Expect(Recipes.Craft(store, Recipes.All.First(r => r.Out == ItemId.Campfire), 1, CraftStations.At(me)) == 1, "a campfire made by hand");
        me.LookYaw = South;
        me.LookPitch = -0.6f;
        await PressSlot(SlotOf(ItemId.Campfire));
        await Seconds(0.3);
        await PressUseItem();
        Expect(await Until(() => New(placed, before, PlacedKind.Campfire) != null, 4), "the campfire laid with Use");
        if (New(placed, before, PlacedKind.Campfire) is { } fire) await StepUpTo(me, fire.WorldTransform(placed.Origin).Origin);
        here = CraftStations.Where(me);
        var bread = Recipes.All.First(r => r.Out == ItemId.Bread && r.In.Any(i => i.Id == ItemId.Flour));
        Expect((here.Here & Station.Fire) != 0 && Recipes.Craft(store, bread, 1, here.Here) == 1, $"baked at {here.Label}");
        int loaves = Inv.CountPlain(ItemId.Bread);
        Expect(loaves == bread.Count && Inv.CountPlain(ItemId.Flour) == flour.Count - 1, $"flour + water -> {loaves} bread");

        // 10. eat a loaf from the hotbar: Use heals and takes one
        me.TakeDamage(30f);
        float hurt = me.Health;
        await PressSlot(SlotOf(ItemId.Bread));
        await Seconds(0.3);
        await PressUseItem();
        Expect(await Until(() => me.Health > hurt, 4) && Inv.CountPlain(ItemId.Bread) == loaves - 1,
            $"a loaf eaten: health {hurt:F0} -> {me.Health:F0}, {Inv.CountPlain(ItemId.Bread)} left");
    }

    private const float East = -Mathf.Pi * 0.5f, North = 0f, South = Mathf.Pi;

    private Vector3 W(double e, double n) => _origin.ToWorld(e, n, 0);

    private int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (!Inv[i].IsEmpty && Inv[i].Id == id) return i;
        return -1;
    }

    /// <summary>Presses and lets go of an action, as a key, a pad button or a VR control would (they all arrive as the action).</summary>
    private async Task Press(StringName action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true, Strength = 1f });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private Task PressSlot(int slot) => slot is >= 0 and < Inventory.HotbarSize ? Press(PlayerInput.Slots[slot]) : Task.CompletedTask;

    private Task PressUseItem() => Press(PlayerInput.UseItem);

    /// <summary>Use (the use_item action) on the held farm item, then wait for its bar.</summary>
    private async Task<bool> PressUse(HandFarming hand)
    {
        await PressUseItem();
        if (!hand.Busy) return false;
        return await Until(() => !hand.Busy, 5);
    }

    /// <summary>Holds the gather action until <paramref name="done"/> (or 5 s), then lets go.</summary>
    private async Task<bool> HoldGather(Func<bool> done)
    {
        Input.ActionPress(PlayerInput.Gather);
        bool ok = await Until(done, 5);
        Input.ActionRelease(PlayerInput.Gather);
        await Seconds(0.2);
        return ok;
    }

    private async Task Stand(FootPlayer me, double e, double n, float yaw, double settle = 0.3)
    {
        me.DebugLaunch(_origin.ToWorld(e, n, me.GlobalPosition.Y + 0.6f), Vector3.Zero);
        me.LookYaw = yaw;
        me.LookPitch = -0.3f;
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(settle);
    }

    /// <summary>Within a station's reach of <paramref name="at"/>, facing it.</summary>
    private async Task StepUpTo(FootPlayer me, Vector3 at)
    {
        var away = (me.GlobalPosition - at) with { Y = 0 };
        away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back;
        me.GlobalPosition = new Vector3(at.X, me.GlobalPosition.Y, at.Z) + away * 1.4f;
        me.Velocity = Vector3.Zero;
        await Seconds(0.4);
    }

    private static PlacedObject? New(PlacedObjects placed, HashSet<long> before, PlacedKind kind) =>
        placed.All.Values.FirstOrDefault(o => o.Kind == kind && !before.Contains(o.Id));

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
        GD.Print($"{Tag}   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
