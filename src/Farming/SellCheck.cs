using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Farming;

/// <summary>
/// <c>--sellcheck [shots]</c> (offline, flat fixture: <c>--systems ui,physics,loot,farming --farmmonth 7
/// --selldir test_output/sellcheck_store</c>), #494: the selling possibilities through the game's own
/// paths, on an empty pack lent for the run (<see cref="Inventory.BeginMatch"/>) and the economy's clock
/// moved by hand (<see cref="FarmSales.ClockOverride"/>):
/// the prices (July: wheat at harvest glut, the stand between the counter and a load, a wanted crop's
/// bonus) at a stand-in co-op (<see cref="FarmMarket.StandIn"/>) by a load and in the delivery prompt;
/// a contract taken and completed by deliveries (bonus paid) and another lapsing; a stand-in specialty
/// buyer (the Aarberg sugar factory moved next to the spawn) buying sugar beet by the load and on foot
/// with E; and a farm stand set up with Use (refused far from a road), stocked from the pack, selling
/// to passers-by over an hour, the honesty box collected, the rest taken back and the stand packed up.
/// With <c>shots</c> (windowed) it saves <c>test_output/494-sell-*.png</c>. Everything is put back.
/// </summary>
public partial class SellCheck : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--sellcheck") >= 0;
    private static bool Shots => Array.IndexOf(OS.GetCmdlineUserArgs(), "shots") > Array.IndexOf(OS.GetCmdlineUserArgs(), "--sellcheck")
                                 && DisplayServer.GetName() != "headless";

    private const string Tag = "[sellcheck]";
    private readonly ItemController _items;
    private readonly WorldOrigin _origin;
    private int _failures;
    private double _clock;

    public SellCheck(ItemController items, WorldOrigin origin) { _items = items; _origin = origin; }
    public SellCheck() : this(null!, null!) { }

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;
    private Inventory Inv => _items.Inventory;

    public override async void _Ready()
    {
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        await Seconds(2.0);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => Me is { } m && m.IsOnFloor() && FarmSales.Instance != null && FarmStands.Instance != null
                                && PlacedObjects.Instance != null && ShopService.Instance != null, 120))
        {
            GD.Print($"{Tag} RESULT: FAILED - no player on the ground, or no FarmSales / FarmStands / shops");
            GetTree().Quit(1);
            return;
        }
        await Seconds(0.5);
        var placed = PlacedObjects.Instance!;
        var before = placed.All.Keys.ToHashSet();
        _clock = (Math.Floor(World.WorldClock.EnvNow / FarmCalendar.WeekSeconds) + 1) * FarmCalendar.WeekSeconds + 3600;   // early in a farm week
        FarmSales.ClockOverride = () => _clock;
        FarmSales.Instance!.ClearContracts();
        Inv.BeginMatch();
        try { await Run(Me!, FarmSales.Instance!, FarmStands.Instance!, placed, before); }
        catch (Exception e) { Expect(false, $"threw: {e}"); }
        finally
        {
            foreach (var o in placed.All.Values.Where(o => !before.Contains(o.Id)).ToList()) placed.RequestRemove(o.Id);
            FarmSales.Instance?.ClearContracts();
            FarmSales.ClockOverride = null;
            FarmStands.RoadDistanceOverride = null;
            FarmBuyers.StandIn = null;
            Inv.EndMatch();
        }
        GD.Print(_failures == 0 ? $"{Tag} RESULT: ok" : $"{Tag} RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static float V(ItemId id) => ItemDefs.Get(id)?.Value ?? 0;

    private async Task Run(FootPlayer me, FarmSales sales, FarmStands stands, PlacedObjects placed, HashSet<long> before)
    {
        var (se, sn) = SpawnPoint.ParseTarget();
        long week = FarmSales.Week;
        Expect(FarmSales.Month == 7, $"the month is July (--farmmonth 7): {FarmSales.Month}");

        // ---- 1. prices with the game's values ----
        Expect(FarmPrices.Season(ItemId.Wheat, 7) == FarmPrices.GlutFactor, "July: the wheat harvest, its price sags");
        bool ordered = true;
        foreach (var h in FarmPrices.Harvests)
        {
            int counter = FarmPrices.Counter(V(h), h, 7, null, week), standPrice = FarmStands.PriceOf(h);
            long load = FarmPrices.Delivery(V(h), h, 10, 7, null, week);
            if (!(load >= 10 * counter && standPrice >= counter && 10 * standPrice <= load + 10)) { ordered = false; Expect(false, $"{h}: counter {counter}, stand {standPrice}, ten by the load {load}"); }
        }
        Expect(ordered, "every harvest: the counter < the stand <= a load, with the game's values");

        // ---- 2. a stand-in co-op 30 m east: a load at its door, the prompt says the price ----
        var door = _origin.ToWorld(se + 30, sn, 0);
        door.Y = me.GlobalPosition.Y;
        FarmMarket.StandIn(door, Vector3.Back);
        string coop = new Interiors.BuildingKey(FarmMarket.StandInTile.E, FarmMarket.StandInTile.N, 0).ToString();
        var wish = FarmPrices.Wishes(coop, week)[0];
        Expect(FarmMarket.NearCoop(door + Vector3.Back * 5), "the stand-in co-op's yard is a market");
        string hint = sales.DeliveryHint(wish.Item, 40, door + Vector3.Back * 5);
        long expect = FarmPrices.Delivery(V(wish.Item), wish.Item, 40, 7, coop, week);
        Expect(hint.Contains($"{expect} CHF") && hint.Contains("WANTED"), $"the delivery prompt: \"{hint}\"");
        int got = await Deliver(door + Vector3.Back * 5, wish.Item, 40);
        Expect(got == expect && expect > (long)(V(wish.Item) * 40 * FarmPrices.Season(wish.Item, 7)),
            $"40 {wish.Item} (wanted +{(wish.Bonus - 1) * 100:0} %) delivered for {got} CHF (expected {expect})");
        var plain = FarmPrices.Harvests.First(h => FarmPrices.WishBonus(coop, week, h) == 1.0);
        got = await Deliver(door + Vector3.Back * 5, plain, 10);
        Expect(got == FarmPrices.Delivery(V(plain), plain, 10, 7, coop, week), $"10 {plain} (not wanted) for {got} CHF");
        Expect(await Deliver(door + Vector3.Back * 5, ItemId.Flour, 10) == 0, "flour is not taken by the load");
        Expect(ShopTables.CounterPrice(ShopType.FarmCoop, wish.Item, ItemCategory.Produce, V(wish.Item), 7, coop, week)
               == FarmPrices.Counter(V(wish.Item), wish.Item, 7, coop, week), "the counter's price is the same function");
        Expect(CoopPanel.MarketLine(coop).Contains(FarmSales.NameOfItem(wish.Item)), $"the panel's market line: \"{CoopPanel.MarketLine(coop)}\"");

        // ---- 3. contracts: one completed by two deliveries, one lapsing ----
        var orders = FarmSales.OrdersOf(coop);
        sales.Accept(coop, orders[0].Index);
        sales.Accept(coop, orders[1].Index);
        Expect(await Until(() => sales.Mine.Count == 2, 3), $"two orders taken ({sales.Mine.Count}): {orders[0].Count} {orders[0].Item}, {orders[1].Count} {orders[1].Item}");
        if (Shots) await ShotPanel(coop);
        int cash = Inv.Cash, half = orders[0].Count / 2;
        int paid = await Deliver(door + Vector3.Back * 5, orders[0].Item, half);
        Expect(sales.Mine.FirstOrDefault(c => c.Index == orders[0].Index)?.Delivered == half, $"half delivered counts ({sales.Mine.FirstOrDefault(c => c.Index == orders[0].Index)?.Delivered}/{orders[0].Count})");
        paid += await Deliver(door + Vector3.Back * 5, orders[0].Item, orders[0].Count - half);
        int bonus = FarmContracts.Bonus(orders[0], V(orders[0].Item));
        Expect(await Until(() => sales.Mine.Count == 1, 3) && sales.LastBonus == bonus && Inv.Cash == cash + paid + bonus,
            $"completed: bonus {sales.LastBonus} (expected {bonus}), pocket {cash} -> {Inv.Cash} (+{paid} for the loads)");
        _clock = sales.Mine[0].Deadline + 5;
        Expect(await Until(() => sales.Mine.Count == 0, 5) && sales.LastNote.Contains("lapsed"), $"the other lapsed at its deadline: \"{sales.LastNote}\"");

        // ---- 4. a specialty buyer: the Aarberg sugar factory moved 60 m west of the spawn ----
        var aarberg = FarmBuyers.ByKey("zucker-aarberg")!;
        Expect(Math.Abs(aarberg.E - 2587711) < 1 && Math.Abs(aarberg.N - 1209764.5) < 1, $"Aarberg at LV95 {aarberg.E:F0}/{aarberg.N:F0}");
        var far = _origin.ToWorld(aarberg.E, aarberg.N, 0);
        Expect(sales.PriceLoad(1, ItemId.SugarBeet, 100, far, null, 0).Total == FarmPrices.Delivery(V(ItemId.SugarBeet), ItemId.SugarBeet, 100, 7, null, week, aarberg.Premium)
               && sales.PriceLoad(1, ItemId.Wheat, 100, far, null, 0).Total == 0, "at its real place it pays its premium for beet, nothing for wheat");
        FarmBuyers.StandIn = aarberg with { Key = "standin", E = se - 60, N = sn, Reach = 25f };
        var yard = _origin.ToWorld(se - 60, sn, 0);
        yard.Y = me.GlobalPosition.Y;
        long beet = FarmPrices.Delivery(V(ItemId.SugarBeet), ItemId.SugarBeet, 200, 7, null, week, aarberg.Premium);
        got = await Deliver(yard, ItemId.SugarBeet, 200);
        Expect(got == beet && beet > ShopTables.DeliveryPrice(ItemCategory.Produce, ItemId.SugarBeet, V(ItemId.SugarBeet), 200, 7, coop, week)
               || FarmPrices.WishBonus(coop, week, ItemId.SugarBeet) > aarberg.Premium, $"200 beet by the load at the factory: {got} CHF (the co-op: {ShopTables.DeliveryPrice(ItemCategory.Produce, ItemId.SugarBeet, V(ItemId.SugarBeet), 200, 7, coop, week)})");
        Expect(await Deliver(yard, ItemId.Wheat, 10) == 0, "the sugar factory does not take wheat");
        // on foot with sacks: E sells them
        _items.Give(new ItemStack(ItemId.SugarBeet, 20));
        await Stand(me, se - 60, sn + 5, 0f);
        await Seconds(0.3);
        string? prompt = FarmSales.PromptFor(me);
        Expect(prompt?.Contains("Sell 20 Sugar beet") == true && prompt.Contains(InputHints.Tag(PlayerInput.InteractMount)), $"the prompt: \"{prompt}\"");
        cash = Inv.Cash;
        await Press(PlayerInput.InteractMount);
        long twenty = FarmPrices.Delivery(V(ItemId.SugarBeet), ItemId.SugarBeet, 20, 7, null, week, aarberg.Premium);
        Expect(await Until(() => Inv.Cash == cash + twenty, 3) && Inv.CountPlain(ItemId.SugarBeet) == 0, $"E sold the 20 sacks: +{Inv.Cash - cash} CHF (expected {twenty})");
        FarmBuyers.StandIn = null;

        // ---- 5. the farm stand ----
        await Stand(me, se, sn - 20, 0f);
        me.LookPitch = -0.6f;
        _items.Give(new ItemStack(ItemId.FarmStand, 1));
        _items.Give(new ItemStack(ItemId.Potato, 25));
        _items.Give(new ItemStack(ItemId.Roesti, 5));
        Expect(Recipes().Any(r => r.Out == ItemId.FarmStand), "a farm stand is made at a workbench");
        FarmStands.RoadDistanceOverride = (_, _) => 500f;
        await Seconds(0.3);
        _items.UseSlot(me, SlotOf(ItemId.FarmStand));
        Expect(await Until(() => Inv.CountPlain(ItemId.FarmStand) == 1 && New(placed, before) == null, 4) && New(placed, before) == null,
            "500 m from a road: refused, the stand is given back");
        await Seconds(1.2);
        FarmStands.RoadDistanceOverride = (_, _) => 8f;
        _items.UseSlot(me, SlotOf(ItemId.FarmStand));
        Expect(await Until(() => New(placed, before) != null, 4), "8 m from a road: set up with Use");
        if (New(placed, before) is not { } stand) return;
        Expect(await Until(() => stands.All.TryGetValue(stand.Id, out var st) && st.RoadM == 8f, 4) && stands.IsMine(stand.Id),
            $"its state: road {(stands.All.TryGetValue(stand.Id, out var s0) ? s0.RoadM : -1)} m, mine {stands.IsMine(stand.Id)}");
        await StepUpTo(me, stand.WorldTransform(placed.Origin).Origin);
        prompt = FarmSales.PromptFor(me);
        Expect(prompt?.StartsWith(InputHints.Tag(PlayerInput.InteractMount) + " Your farm stand") == true, $"the prompt: \"{prompt}\"");
        await Press(PlayerInput.InteractMount);
        Expect(stands.IsOpen, "E opens its panel");
        Expect(stands.Stock(stand.Id, ItemId.Potato, 25) && await Until(() => !stands.Waiting, 3) && Inv.CountPlain(ItemId.Potato) == 0, "25 potatoes stocked from the pack");
        Expect(stands.Stock(stand.Id, ItemId.Roesti, 5) && await Until(() => !stands.Waiting, 3) && Inv.CountPlain(ItemId.Roesti) == 0, "5 rösti stocked");
        Expect(!stands.Stock(stand.Id, ItemId.WheatSeed, 1), "seed does not go on a stand");
        var st1 = stands.All[stand.Id];
        Expect(st1.Stocked == 30, $"30 on the stand ({st1.Stocked})");
        var node = placed.GetNodeOrNull<FarmStandNode>($"P{stand.Id}");
        Expect(node?.HeapsShown == 2, $"two crates show their heaps ({node?.HeapsShown})");
        if (Shots)
        {
            await Seconds(0.5);
            GD.Print($"{Tag} shot {Shot("494-sell-stand-panel")}");
            await Press(PlayerInput.InteractMount);
            await Stand(me, se + 1.5, sn - 24.5, 0.25f);
            me.LookPitch = -0.2f;
            await Seconds(0.6);
            GD.Print($"{Tag} shot {Shot("494-sell-stand")}");
            await StepUpTo(me, stand.WorldTransform(placed.Origin).Origin);
        }
        else await Press(PlayerInput.InteractMount);
        Expect(!stands.IsOpen, "E closes it");

        // two days of passers-by: 6 potatoes, 5 rösti (cooked: twice as fast, only 5 there)
        int potato = FarmStands.PriceOf(ItemId.Potato), roesti = FarmStands.PriceOf(ItemId.Roesti);
        _clock += 2 * FarmCalendar.DaySeconds;
        stands.Tick();
        var st = stands.All[stand.Id];
        Expect(st.Slots.Sum(x => x.Item == ItemId.Potato ? x.Count : 0) == 19 && st.Slots.Sum(x => x.Item == ItemId.Roesti ? x.Count : 0) == 0
               && st.Cash == 6 * potato + 5 * roesti, $"two days later: {st.Stocked} left, {st.Cash} CHF in the box (expected {6 * potato + 5 * roesti})");
        Expect(placed.GetNodeOrNull<FarmStandNode>($"P{stand.Id}")?.HeapsShown == 1, "the empty crate shows no heap");
        PlacedResult? packed = null;
        placed.RequestRemove(stand.Id, r => packed = r);
        Expect(await Until(() => packed != null, 3) && !packed!.Value.Ok, $"packing it up full is refused ({packed?.Refused})");
        cash = Inv.Cash;
        int box = st.Cash;
        Expect(stands.Collect(stand.Id) && await Until(() => !stands.Waiting, 3) && Inv.Cash == cash + box && stands.All[stand.Id].Cash == 0,
            $"the box collected: +{Inv.Cash - cash} CHF");
        int slot = stands.All[stand.Id].Slots.FindIndex(x => x.Count > 0);
        Expect(stands.Take(stand.Id, slot, 40) && await Until(() => !stands.Waiting, 3) && Inv.CountPlain(ItemId.Potato) == 19, $"the 19 potatoes taken back ({Inv.CountPlain(ItemId.Potato)})");
        packed = null;
        placed.RequestRemove(stand.Id, r => packed = r);
        Expect(await Until(() => packed != null, 3) && packed!.Value.Ok && !stands.All.ContainsKey(stand.Id), $"empty, it packs up ({packed?.Refused})");
    }

    private static IEnumerable<Crafting.Recipe> Recipes() => Crafting.Recipes.All;

    private async Task ShotPanel(string coop)
    {
        var layer = new CanvasLayer { Layer = 20 };
        var panel = new PanelContainer { Theme = UiTheme.Get(), CustomMinimumSize = new Vector2(720, 0), Position = new Vector2(40, 40) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.92f, 12, 18));
        var box = UiKit.VBox(8);
        panel.AddChild(box);
        box.AddChild(UiKit.Text("Farm co-op", UiTheme.FontHeading, UiTheme.Text, bold: true));
        box.AddChild(UiKit.Text(CoopPanel.MarketLine(coop), UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        foreach (var h in FarmPrices.Harvests)
        {
            string wanted = FarmSales.WantedTag(coop, h);
            box.AddChild(UiKit.Text($"{FarmSales.NameOfItem(h)}: counter {FarmPrices.Counter(V(h), h, 7, coop, FarmSales.Week)} CHF, by the load {FarmPrices.Delivery(V(h), h, 1, 7, coop, FarmSales.Week)} CHF  {wanted}",
                UiTheme.FontSmall, wanted.Length > 0 ? UiTheme.Amber : UiTheme.Text));
        }
        var orders = UiKit.VBox(4);
        box.AddChild(orders);
        CoopPanel.Fill(orders, coop);
        layer.AddChild(panel);
        AddChild(layer);
        await Seconds(0.6);
        GD.Print($"{Tag} shot {Shot("494-sell-coop-panel")}");
        layer.QueueFree();
    }

    private string Shot(string name)
    {
        System.IO.Directory.CreateDirectory(ProjectSettings.GlobalizePath("res://test_output"));
        string path = ProjectSettings.GlobalizePath($"res://test_output/{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        return path;
    }

    /// <summary>A load sold through <see cref="FarmMarket.Deliver"/> (what a machine calls): the francs.</summary>
    private async Task<int> Deliver(Vector3 at, ItemId item, int count)
    {
        int? francs = null;
        FarmMarket.Deliver(this, at, item, count, f => francs = f);
        await Until(() => francs != null, 3);
        await Seconds(0.6);   // NearCoop caches for half a second
        return francs ?? -1;
    }

    private int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (!Inv[i].IsEmpty && Inv[i].Id == id) return i;
        return -1;
    }

    private static PlacedObject? New(PlacedObjects placed, HashSet<long> before) =>
        placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.FarmStand && !before.Contains(o.Id));

    private async Task Press(StringName action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true, Strength = 1f });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Stand(FootPlayer me, double e, double n, float yaw)
    {
        me.DebugLaunch(_origin.ToWorld(e, n, me.GlobalPosition.Y + 0.6f), Vector3.Zero);
        me.LookYaw = yaw;
        me.LookPitch = -0.3f;
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(0.3);
    }

    private async Task StepUpTo(FootPlayer me, Vector3 at)
    {
        var away = (me.GlobalPosition - at) with { Y = 0 };
        away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back;
        me.GlobalPosition = new Vector3(at.X, me.GlobalPosition.Y, at.Z) + away * 1.6f;
        me.Velocity = Vector3.Zero;
        await Seconds(0.4);
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
        GD.Print($"{Tag}   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
