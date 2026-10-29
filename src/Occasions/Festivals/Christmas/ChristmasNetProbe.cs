using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Alcohol;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Christmas;

/// <summary>
/// <c>--christmasnet a|b</c> with <c>--connect --festival test</c> (driven by <c>tools/christmasnetcheck.sh</c>, #1348): the Christmas market over a dedicated
/// server whose calendar reads 30 November 2027 (day 6 of 29, held at the fixture start). Everything is asserted on the REMOTE peers. Lea (A) tastes at the
/// Glühwein stall (the stamp reaches her own section and the stall's state comes back over the wire), buys the mug (it arrives in her pack), drinks it (the
/// server logs 15.8 g, which reaches her own progress; she is warm) and says so. Tom (B), who never drank, then tastes at the same stall: his stamp is his
/// own (not Lea's), he is paid in his own section, his drink log is empty; a use from 150 m away and the Samichlaus's bag before 6 December are refused by the
/// server; he buys his own mug.
/// </summary>
public partial class ChristmasNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--christmasnet");

    public ChristmasNetProbe(ItemController items) : base(items, "christmasnet", "CN", "christmasnet_") { }
    public ChristmasNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("christmasmarket-2027") ?? new FestivalInstance();
    private static List<Shot> DrinkLog => Adv.Mine.Get(AlcoholProgress.Section).Drinks;

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && ChristmasGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = ChristmasRules.FestivalId;
        var game = ChristmasGame.Instance!;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 6, Site: not null }, 40), "the server runs the market on day 6 and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var site = Svc.RunningOf(Id)!.Site!;
        var stall = ChristmasRules.Layout(site)[0];
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<FestivalService.MarketEvent>();
        Svc.MarketTold += (_, _, what, _, _) => told.Add(what);
        void AtStall() => Teleport(stall.E, stall.N - 2.0);

        if (_role == "A")
        {
            AtStall();
            Expect(await Until(() => game.NearStall == 0, 10), "A stands at the Glühwein stall");
            await Seconds(1.0);   // the server learns where the body is from the movement packets: let it
            Expect(ChristmasGame.InteractionFor(Me!) is { } p && p.Text.Contains("Taste"), "the taste is offered");
            ChristmasGame.TryInteract(Me!);
            Expect(await Until(() => told.Contains(FestivalService.MarketEvent.Tasted), 10), $"the server told A it tasted ({string.Join(",", told)}; refused: {refused})");
            Expect(await Until(() => Mine.Batzen == ChristmasRules.StampBatzen && Mine.Points == ChristmasRules.StampPoints, 10), $"the taste paid in A's own section ({Mine.Batzen}/{Mine.Points})");
            Expect(ChristmasRules.Stamped(Svc.StampsToday(6), 0), "and the stall's stamp came back over the wire");
            Expect(await Until(() => ChristmasGame.InteractionFor(Me!) is { } q && q.Text.Contains("Buy Glühwein"), 10), "now the mug is offered");
            ChristmasGame.TryInteract(Me!);
            Expect(await Until(() => Count(ItemId.GluehweinMug) == 1, 10), $"the mug arrived in A's pack (refused: {refused})");
            Expect(await Until(() => Mine.Batzen == ChristmasRules.StampBatzen - 4, 10), $"for 4 Batzen ({Mine.Batzen})");
            int slot = Enumerable.Range(0, Inventory.Size).First(i => _items.Inventory[i].Id == ItemId.GluehweinMug);
            _items.Inventory.Select(Math.Min(slot, Inventory.HotbarSize - 1));
            _items.UseSlot(Me!, slot);
            Expect(await Until(() => Count(ItemId.GluehweinMug) == 0, 10), "A drinks it");
            Expect(await Until(() => DrinkLog.Count == 1, 15), $"the server logged the drink and it reached A's own progress ({DrinkLog.Count})");
            Expect(DrinkLog.Count == 1 && Math.Abs(DrinkLog[0].G - AlcoholRules.GluehweinGrams) < 0.3, $"15.8 g ({DrinkLog.FirstOrDefault()?.G:F1})");
            Expect(Me!.StaminaMeter.BuffLeft(StaminaBuff.Warm) > 200f, "and A is warm");
            Say("drank");
            Expect(await Heard("B", "done", 150), "B finished too");
        }
        else
        {
            Expect(await Heard("A", "drank", 150), "A drank");
            AtStall();
            Expect(await Until(() => game.NearStall == 0, 10), "B stands at the same stall");
            await Seconds(1.0);
            Expect(!ChristmasRules.Stamped(Svc.StampsToday(6), 0), "B's stamps are B's own: stall 0 is not stamped for B");
            Expect(DrinkLog.Count == 0 && Mine.Batzen == 0, "B never drank and holds nothing");
            ChristmasGame.TryInteract(Me!);
            Expect(await Until(() => Mine.Batzen == ChristmasRules.StampBatzen, 10), $"B's own taste paid B ({Mine.Batzen}; refused: {refused})");
            // forged requests from where B does not stand
            Teleport(site.E + 150, site.N + 150);
            await Seconds(1.0);
            refused = null;
            Svc.AskMarket(5);
            Expect(await Until(() => refused?.Contains("Go to the stall") == true, 10), $"a stall from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskMarket(FestivalService.MarketSamichlaus);
            Expect(await Until(() => refused?.Contains("Go to the Samichlaus") == true, 10), $"the Samichlaus from 150 m away is refused ({refused})");
            Expect(Mine.Batzen == ChristmasRules.StampBatzen, "nothing was paid for either");
            // the Samichlaus on day 6
            var (se, sn) = ChristmasRules.SamichlausAt(site);
            Teleport(se - 1.5, sn);
            Expect(await Until(() => game.AtSamichlaus, 10), "B stands at the Samichlaus");
            await Seconds(1.0);
            refused = null;
            ChristmasGame.TryInteract(Me!);
            Expect(await Until(() => refused?.Contains("6 December") == true, 10), $"his bag is refused before 6 December ({refused})");
            // B buys its own mug
            AtStall();
            Expect(await Until(() => game.NearStall == 0, 10), "back at the stall");
            await Seconds(1.0);
            ChristmasGame.TryInteract(Me!);
            Expect(await Until(() => Count(ItemId.GluehweinMug) == 1 && Mine.Batzen == ChristmasRules.StampBatzen - 4, 10), $"B bought a mug of its own ({Count(ItemId.GluehweinMug)}, {Mine.Batzen} Batzen)");
            Expect(DrinkLog.Count == 0, "and has not drunk it: B's drink log is still empty");
            Say("done");
        }
        await Finish(2.0);
    }
}
