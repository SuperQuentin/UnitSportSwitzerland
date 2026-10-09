using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Farming;

/// <summary>
/// <c>--sellnet A|B</c> with <c>--connect</c> (driven by <c>tools/sellnetcheck.sh</c>), #494: a farm
/// stand over loopback on the fixture's straight road. A sets one up with Use (the server measures
/// the road), stocks 30 potatoes and says so; B joins after, gets the stand and its crates from the
/// join snapshot (not its own, heaps drawn), walks up and buys 3 for cash; A sees the crate drop to
/// 27 and 3 × the price in its honesty box, collects it; B sees the box empty and the same 27, and
/// is refused collecting A's box. Scratch inventories.
/// </summary>
public partial class SellNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--sellnet");

    public SellNetProbe(ItemController items) : base(items, "sellnet", "SN") { }
    public SellNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => PlacedObjects.Instance != null && FarmStands.Instance != null)) return;
        await Seconds(2.0);
        try
        {
            if (_role == "A") await RunA(Me!, PlacedObjects.Instance!, FarmStands.Instance!);
            else await RunB(Me!, PlacedObjects.Instance!, FarmStands.Instance!);
        }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        await Finish(1.5);
    }

    private static int Potatoes(StandState? s) => s?.Slots.Where(x => x.Item == ItemId.Potato).Sum(x => x.Count) ?? -1;

    private async Task RunA(FootPlayer me, PlacedObjects placed, FarmStands stands)
    {
        var before = placed.All.Keys.ToHashSet();
        _items.Give(new ItemStack(ItemId.FarmStand, 1));
        _items.Give(new ItemStack(ItemId.Potato, 30));
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;
        await Seconds(0.3);
        _items.UseSlot(me, SlotOf(ItemId.FarmStand));
        Expect(await Until(() => placed.All.Values.Any(o => o.Kind == PlacedKind.FarmStand && !before.Contains(o.Id)), 8), "the server set my stand up (a road by the spawn)");
        var stand = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.FarmStand && !before.Contains(o.Id));
        if (stand == null) { Fail("no stand"); return; }
        Expect(stand.Owner == "SellA" && await Until(() => stands.IsMine(stand.Id) && stands.All.TryGetValue(stand.Id, out var s) && s.RoadM >= 0 && s.RoadM < 20, 20),
            $"mine, the road measured by the server ({(stands.All.TryGetValue(stand.Id, out var m) ? m.RoadM : float.NaN):F1} m)");
        // the answer takes the potatoes out of the pack after the crate update: wait for both, a loaded box is slow
        await Until(() => !stands.Waiting, 20);
        bool asked = stands.Stock(stand.Id, ItemId.Potato, 30);
        bool stocked = asked && await Until(() => !stands.Waiting && Potatoes(stands.All.GetValueOrDefault(stand.Id)) == 30 && CountOf(ItemId.Potato) == 0, 20);
        Expect(stocked, $"30 potatoes stocked from my pack (asked {asked}, crate {Potatoes(stands.All.GetValueOrDefault(stand.Id))}, pack {CountOf(ItemId.Potato)})");

        for (int i = 0; i < 60 && !_heard.Any(l => l.Contains("SN B bought")); i++)
        {
            Say($"stand {stand.Id}");
            await Heard("B", "bought", 2.5);
        }
        if (!_heard.Any(l => l.Contains("SN B bought"))) { Fail("B never bought"); return; }
        int price = FarmStands.PriceOf(ItemId.Potato);
        Expect(await Until(() => Potatoes(stands.All.GetValueOrDefault(stand.Id)) == 27 && stands.All[stand.Id].Cash == 3 * price, 15),
            $"B's purchase here: {Potatoes(stands.All.GetValueOrDefault(stand.Id))} left, {stands.All.GetValueOrDefault(stand.Id)?.Cash} CHF in my box (expected {3 * price})");
        int cash = _items.Inventory.Cash;
        Expect(stands.Collect(stand.Id) && await Until(() => !stands.Waiting, 15) && _items.Inventory.Cash == cash + 3 * price && stands.All[stand.Id].Cash == 0,
            $"I collected the box: +{_items.Inventory.Cash - cash} CHF");
        Say($"collected {Potatoes(stands.All[stand.Id])}");
        Expect(await Heard("B", "done", 30), "B saw it");
        // leave the world as it was: take the potatoes back and pack the stand up
        int slot = stands.All[stand.Id].Slots.FindIndex(x => x.Count > 0);
        stands.Take(stand.Id, slot, 40);
        await Until(() => !stands.Waiting, 15);
        PlacedResult? packed = null;
        placed.RequestRemove(stand.Id, r => packed = r);
        Expect(await Until(() => packed != null, 15) && packed!.Value.Ok, $"emptied and packed up ({packed?.Refused})");
    }

    private async Task RunB(FootPlayer me, PlacedObjects placed, FarmStands stands)
    {
        if (!await Heard("A", "stand", 150)) { Fail("A never set a stand up"); return; }
        long id = long.Parse(_heard.Last(l => l.Contains("SN A stand")).Split(' ').Last());
        Expect(await Until(() => placed.All.ContainsKey(id) && Potatoes(stands.All.GetValueOrDefault(id)) == 30, 20),
            $"the join snapshot: A's stand with 30 potatoes ({Potatoes(stands.All.GetValueOrDefault(id))})");
        Expect(!stands.IsMine(id) && placed.All[id].Owner == "SellA", "it is A's, not mine");
        Expect(await Until(() => placed.GetNodeOrNull<FarmStandNode>($"P{id}")?.HeapsShown == 1, 15), "its crate is drawn full here");
        // walk up to it
        var at = placed.All[id].WorldTransform(placed.Origin).Origin;
        var away = (me.GlobalPosition - at) with { Y = 0 };
        away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back;
        me.DebugLaunch(at + away * 1.8f + Vector3.Up * 0.5f, Vector3.Zero);
        await Until(() => me.IsOnFloor(), 15);
        // the server.s copy of where we are catches up: wait for the prompt rather than a fixed time
        await Until(() => FarmSales.PromptFor(me)?.Contains("SellA's farm stand: buy") == true, 15);
        await Seconds(0.5);
        string? prompt = FarmSales.PromptFor(me);
        Expect(prompt?.Contains("SellA's farm stand: buy") == true, $"the prompt: \"{prompt}\"");
        _items.Give(new ItemStack(ItemId.Francs, 100));
        int cash = _items.Inventory.Cash, price = FarmStands.PriceOf(ItemId.Potato);
        int slot = stands.All[id].Slots.FindIndex(x => x.Item == ItemId.Potato && x.Count > 0);
        Expect(stands.Buy(id, slot, 3) && await Until(() => !stands.Waiting, 15) && stands.LastAnswer.Refused == "",
            $"bought 3 ({stands.LastAnswer.Refused})");
        Expect(CountOf(ItemId.Potato) == 3 && _items.Inventory.Cash == cash - 3 * price, $"3 potatoes in my pack, {cash - _items.Inventory.Cash} CHF paid (expected {3 * price})");
        Expect(await Until(() => Potatoes(stands.All[id]) == 27, 15), $"27 left on the stand here ({Potatoes(stands.All[id])})");
        Say("bought");
        if (!await Heard("A", "collected", 60)) { Fail("A never collected"); return; }
        int aSees = int.Parse(_heard.Last(l => l.Contains("SN A collected")).Split(' ').Last());
        Expect(await Until(() => stands.All[id].Cash == 0, 15) && Potatoes(stands.All[id]) == aSees, $"the same stand as A: {Potatoes(stands.All[id])} potatoes (A: {aSees}), box {stands.All[id].Cash}");
        Expect(stands.Collect(id) && await Until(() => !stands.Waiting, 15) && stands.LastAnswer.Refused == "That is not your stand.",
            $"collecting A's box is refused ({stands.LastAnswer.Refused})");
        Say("done");
        await Seconds(0.5);
        Say("done");
    }
}
