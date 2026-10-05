using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>--shopnet A|B</c> with <c>--connect</c> (#273, <c>tools/shopcheck.sh</c>): two real clients
/// at a village shop of the generated world, through chat lines like <see cref="BankProbe"/>.
/// A gets 1000 CHF on its account (<c>/login</c>, <c>/bank me set</c>), finds the nearest shop
/// whose counter takes the card, walks in, buys the last items of a slot for cash, then one line
/// on the card: the server's own balance must drop by the price. A sells something back. B then
/// walks into the same shop and must see that slot sold out, and be refused when it tries to buy
/// it. Each role ends on a RESULT line.
/// </summary>
public partial class ShopProbe : ChatProbe
{
    public static string? Role => RoleArg("--shopnet");

    private readonly WorldOrigin _origin;

    public ShopProbe(ItemController items, WorldOrigin origin) : base(items, "shopnet", "SH", "shop_") => _origin = origin;

    public ShopProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(240, () => GetParent() is ClientWorld { Stage: LoadStage.Ready })) return;
        await Seconds(2.0);
        if (_role == "B") await RunB();
        else await RunA();
        await Finish(2);
    }

    private static ShopService Shops => ShopService.Instance!;

    // ---- A: buys a slot empty, pays by card, sells back -----------------------------------------

    private async Task RunA()
    {
        var me = Me!;
        var bank = Bank.Instance!;
        Chat!.Send("/login shopcheck");
        await Seconds(2.0);
        Chat.Send("/bank me set 1000");
        if (!await Until(() => bank.Balance == 1000, 20)) { Fail($"the account was not set to 1000 (it says {bank.Balance})"); return; }
        Expect(bank.Balance == 1000, "1000 CHF on the account");

        var (door, layout) = await FindShop(me);
        if (door is not { } d || layout == null) return;
        int counter = layout.Furniture.FindIndex(f => f.Type == FurnitureType.ShopCounter && f.Floor == layout.Below);
        GD.Print($"{Log} shop {layout.Key} ({layout.Shop}) at {d.World}, counter #{counter}");
        Expect(counter >= 0, "the shop has its counter on the ground floor");
        if (counter < 0) return;

        // the sign over the door
        int signs = GetTree().Root.FindChildren("BankSigns", "Node3D", true, false).Sum(s => s.GetChildCount());
        Expect(signs > 0, $"shop signs are up ({signs})");

        if (!await WalkIn(me, d)) return;
        var interiors = InteriorManager.Instance!;
        if (interiors.Current?.Key != layout.Key) { Fail($"walked into {interiors.Current?.Key}, not {layout.Key}"); return; }
        var node = interiors.CurrentNode!;
        if (!await StandAt(me, layout, node, counter, f => ShopService.NearestCounter(me, layout, node) == f)) return;
        Expect(Shops.PromptFor(me)?.Contains(ShopTables.Name(layout.Shop)) == true, $"the prompt offers the shop ({Shops.PromptFor(me)})");
        Expect(me.TryInteract() && Shops.IsOpen, "E opens the shop");
        if (!await Until(() => !Shops.Waiting && Shops.OpenStock.Count > 0, 15)) { Fail("the stock never came"); return; }
        Shot("counter");

        // the last items of a cheap cash line
        var line = Shops.OpenStock.Where(l => Shops.Left(l.Slot) > 0 && l.Pay != Payment.Card)
            .OrderBy(l => Shops.Left(l.Slot) * l.Price).FirstOrDefault();
        if (line.Stock == 0) { Fail("nothing to buy for cash"); return; }
        int left = Shops.Left(line.Slot);
        _items.Inventory.Add(ItemId.Francs, left * line.Price + 50);
        int cash = _items.Inventory.Cash, had = CountOf(line.Id);
        Expect(Shops.Buy(line.Slot, left, PayWith.Cash), $"asks for the last {left} {line.Id} at {line.Price} CHF");
        await Until(() => !Shops.Waiting, 10);
        Expect(Shops.LastOutcome == BuyOutcome.Sold && Shops.Left(line.Slot) == 0, $"sold, the slot is empty ({Shops.LastOutcome}, {Shops.Left(line.Slot)} left)");
        Expect(_items.Inventory.Cash == cash - left * line.Price, $"paid {left * line.Price} CHF cash ({cash} -> {_items.Inventory.Cash})");
        Expect(CountOf(line.Id) == had + left, $"{left} {line.Id} in the pack ({had} -> {CountOf(line.Id)})");
        Expect(!Shops.Buy(line.Slot, 1, PayWith.Cash), "a sold-out slot is not even asked for");

        // one line on the card: the server takes it from the account
        var card = Shops.OpenStock.Where(l => Shops.Left(l.Slot) > 0 && l.Pay != Payment.Cash && l.Price <= bank.Balance)
            .OrderBy(l => l.Price).FirstOrDefault();
        if (card.Stock == 0) Expect(false, $"a {layout.Shop} line taking the card");
        else
        {
            long before = bank.Balance;
            cash = _items.Inventory.Cash;
            Expect(Shops.Buy(card.Slot, 1, PayWith.Card), $"one {card.Id} on the card, {card.Price} CHF");
            await Until(() => !Shops.Waiting, 10);
            Expect(Shops.LastOutcome == BuyOutcome.Sold && bank.Balance == before - card.Price && _items.Inventory.Cash == cash,
                $"the account paid ({before} -> {bank.Balance}), the pocket did not ({cash} -> {_items.Inventory.Cash})");
            // ask again: the balance must be the server's, not this client's sums
            bank.Refresh();
            await Seconds(2.0);
            Expect(bank.Balance == before - card.Price, $"the server's account says {bank.Balance}");
        }

        // sell something it buys back
        var sell = ItemDefs.All.FirstOrDefault(def => Shops.SellPriceOf(def.Id) > 0 && def.Use != ItemUse.Bag && def.MaxStack > 1);
        if (sell != null)
        {
            _items.Inventory.Add(sell.Id, 3);
            cash = _items.Inventory.Cash;
            int have = _items.Inventory.CountPlain(sell.Id), each = Shops.SellPriceOf(sell.Id);
            Expect(Shops.Sell(sell.Id, 3), $"sells 3 {sell.Id} at {each} CHF");
            await Until(() => !Shops.Waiting, 10);
            Expect(_items.Inventory.Cash == cash + 3 * each && _items.Inventory.CountPlain(sell.Id) == have - 3,
                $"+{3 * each} CHF cash for them ({cash} -> {_items.Inventory.Cash})");
        }
        var never = ItemDefs.All.First(def => Shops.SellPriceOf(def.Id) == 0 && def.Id != ItemId.Francs && def.Value > 0);
        _items.Inventory.Add(never.Id, 1);
        Expect(!Shops.Sell(never.Id, 1), $"the {layout.Shop} does not buy {never.Id}");
        Shot("bought");
        me.TryInteract();   // closes the shop
        await Seconds(0.5);
        Expect(!Shops.IsOpen, "E closes it");
        await Vending(me);

        // B comes and must see the slot empty; repeat until it answers (chat is not replayed to late joiners)
        var (e, n) = _origin.ToLv95(d.World);
        string news = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"soldout {layout.Key} {counter} {line.Slot} {e:F1} {n:F1}");
        bool seen = false;
        for (int i = 0; i < 150 && !seen; i++)
        {
            Say(news);
            seen = await Heard("B", "seen", 2);
        }
        Expect(seen, "B saw it");
        if (seen) Expect(_heard.Any(l => l.Contains("SH B seen ok")), "B saw the slot sold out");
    }

    // ---- B: walks in after A and finds the slot empty --------------------------------------------

    private async Task RunB()
    {
        var me = Me!;
        if (!await Heard("A", "soldout", 420)) { Fail("A never sold a slot out"); return; }
        var words = _heard.Last(l => l.Contains("SH A soldout")).Split(' ');
        int at = Array.IndexOf(words, "soldout");
        string key = words[at + 1];
        int counter = int.Parse(words[at + 2]), slot = int.Parse(words[at + 3]);
        double e = double.Parse(words[at + 4], System.Globalization.CultureInfo.InvariantCulture);
        double n = double.Parse(words[at + 5], System.Globalization.CultureInfo.InvariantCulture);
        GD.Print($"{Log} A emptied slot {slot} of {key} #{counter}");
        if (!BuildingKey.TryParse(key, out var bk)) { Fail($"bad key {key}"); return; }

        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        var dk = new DoorKey(bk);
        if (!await Until(() => me.IsOnFloor() && DoorIndex.Find(dk) != null, 90)) { Fail("the shop's door never loaded"); return; }
        var door = DoorIndex.Find(dk)!.Value;
        var interiors = InteriorManager.Instance!;
        InteriorLayout? layout = null;
        try { layout = await interiors.GetOrCreate(key); } catch { }
        if (layout == null) { Fail("no plan"); return; }
        if (!await WalkIn(me, door)) return;
        var node = interiors.CurrentNode!;
        if (!await StandAt(me, layout, node, counter, f => ShopService.NearestCounter(me, layout, node) == f)) return;
        Expect(me.TryInteract() && Shops.IsOpen, "E opens the shop");
        if (!await Until(() => !Shops.Waiting && Shops.OpenStock.Count > slot, 15)) { Fail("the stock never came"); return; }
        bool empty = Shops.OpenStock[slot].Stock > 0 && Shops.Left(slot) == 0;
        Expect(empty, $"the slot A emptied is sold out here ({Shops.OpenStock[slot].Id}: {Shops.Left(slot)} of {Shops.OpenStock[slot].Stock})");
        Shot("soldout");
        Shops.Close();
        Expect(me.TryInteract() && await Until(() => !Shops.Waiting && Shops.OpenStock.Count > slot, 15), "opens it again");
        Expect(Shops.Left(slot) == 0, "still sold out after asking again");
        Say(empty ? "seen ok" : "seen FAILED");
        await Seconds(1.0);
        Say(empty ? "seen ok" : "seen FAILED");
    }

    // ---- finding and walking into a shop ----------------------------------------------------------

    /// <summary>The nearest shop whose counter takes the card (not a grocery, kiosk or machine), with its plan.</summary>
    private async Task<(DoorIndex.Entry?, InteriorLayout?)> FindShop(FootPlayer me)
    {
        var interiors = InteriorManager.Instance!;
        var tried = new HashSet<string>();
        for (int round = 0; round < 40; round++)
        {
            var here = me.GlobalPosition;
            var doors = DoorIndex.All().Where(d => d.Kind == BuildingKind.Commercial && !tried.Contains(d.Key.ToString()))
                .OrderBy(d => new Vector2(d.World.X - here.X, d.World.Z - here.Z).Length()).Take(30).ToList();
            foreach (var d in doors)
            {
                tried.Add(d.Key.ToString());
                InteriorLayout? l = null;
                try { l = await interiors.GetOrCreate(d.Key.ToString()); } catch { }
                if (l == null || l.Key != d.Key.ToString()) continue;
                GD.Print($"{Log} {l.Key}: {l.Shop}");
                if (_machine == null && l.Furniture.Any(f => f.Type == FurnitureType.VendingMachine)) _machine = (d, l);
                if (l.Shop is ShopType.None or ShopType.Grocery or ShopType.Kiosk) continue;
                return (d, l);
            }
            await Seconds(3.0);
        }
        Fail($"no shop taking the card among {tried.Count} commercial doors");
        return (null, null);
    }

    /// <summary>A building with a PAUSA machine met on the way, if any.</summary>
    private (DoorIndex.Entry Door, InteriorLayout Layout)? _machine;

    /// <summary>
    /// A's PAUSA machine, if one stood in a building it looked at (a shop or office lobby, one in
    /// five): one item for cash, which sticks (the server runs with <c>--shopstuck</c>), then a hit
    /// frees it. Best effort: no machine near the shops is not a failure.
    /// </summary>
    private async Task Vending(FootPlayer me)
    {
        if (_machine == null)
        {
            var here = me.GlobalPosition;
            foreach (var d in DoorIndex.All().Where(d => d.Kind is BuildingKind.Commercial or BuildingKind.Civic or BuildingKind.Industrial)
                         .OrderBy(d => new Vector2(d.World.X - here.X, d.World.Z - here.Z).Length()).Take(60))
            {
                InteriorLayout? l = null;
                try { l = await InteriorManager.Instance!.GetOrCreate(d.Key.ToString()); } catch { }
                if (l != null && l.Key == d.Key.ToString() && l.Furniture.Any(f => f.Type == FurnitureType.VendingMachine)) { _machine = (d, l); break; }
            }
        }
        if (_machine is not { } m) { GD.Print($"{Log} no PAUSA machine in the 60 nearest buildings that may have one: skipped"); return; }
        GD.Print($"{Log} PAUSA machine in {m.Layout.Key} ({m.Layout.Kind})");
        if (!await WalkIn(me, m.Door)) return;
        var interiors = InteriorManager.Instance!;
        var node = interiors.CurrentNode!;
        int i = m.Layout.Furniture.FindIndex(f => f.Type == FurnitureType.VendingMachine);
        if (!await StandAt(me, m.Layout, node, i, f => ShopService.NearestMachine(me, m.Layout, node) == f)) return;
        Expect(me.TryInteract() && Shops.IsOpen && Shops.Open?.Type == ShopType.Vending, "E opens the PAUSA machine");
        if (!await Until(() => !Shops.Waiting && Shops.OpenStock.Count == ShopTables.VendingSlots, 15)) { Expect(false, "the machine's stock came"); return; }
        if (Shops.OutOfOrder) { GD.Print($"{Log} the machine is hors service this period: skipped"); Shops.Close(); return; }
        var line = Shops.OpenStock.First(l => Shops.Left(l.Slot) > 0);
        int left = Shops.Left(line.Slot), had = CountOf(line.Id);
        _items.Inventory.Add(ItemId.Francs, line.Price);
        Expect(Shops.Buy(line.Slot, 1, PayWith.Cash), $"{ShopTables.SlotName(line.Slot)}: one {line.Id} for {line.Price} CHF");
        await Until(() => !Shops.Waiting, 10);
        Expect(Shops.LastOutcome == BuyOutcome.Stuck && Shops.Stuck && CountOf(line.Id) == had && Shops.Left(line.Slot) == left - 1,
            $"it sticks on the spiral (forced): paid, not in the pack ({Shops.LastOutcome})");
        Expect(Shops.Bump(), "hits the machine");
        await Until(() => !Shops.Waiting, 10);
        Expect(!Shops.Stuck && CountOf(line.Id) >= had + 1, $"the hit drops it ({had} -> {CountOf(line.Id)})");
        Shops.Close();
    }

    // walking in and standing at a counter: as BankProbe does

    private async Task<bool> WalkIn(FootPlayer me, DoorIndex.Entry door)
    {
        var interiors = InteriorManager.Instance!;
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        if (me.Indoors) interiors.Leave(me);
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        await Until(() => me.IsOnFloor(), 10);
        bool open = false;
        for (int attempt = 0; attempt < 6 && !open; attempt++)
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

    private async Task<bool> StandAt(FootPlayer me, InteriorLayout layout, InteriorNode node, int index, Func<int, bool> facing)
    {
        var f = layout.Furniture[index];
        var turn = new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2);
        var front = turn * new Vector3(0, 0, f.D / 2 + 0.55f);
        var face = node.GlobalTransform.Basis * -front;
        float sign = _role == "B" ? 1f : -1f;
        foreach (float offset in new[] { 0.3f, 0.15f, 0f, 0.45f })
        {
            var side = turn * new Vector3(sign * offset, 0, 0);
            me.EnterInterior(layout.Key, node.GlobalTransform * (new Vector3(f.X, layout.FloorY(f.Floor) + 0.1f, f.Z) + front + side), Mathf.Atan2(-face.X, -face.Z));
            me.Velocity = Vector3.Zero;
            await Seconds(0.8);
            if (InteriorManager.Instance?.Current?.Key == layout.Key && facing(index)) return true;
        }
        Fail($"could not stand in front of #{index} ({f.Type})");
        return false;
    }

    protected override string Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return "";
        string path = base.Shot($"{_role}_{name}");
        GD.Print($"{Log} screenshot {path}");
        return path;
    }
}
