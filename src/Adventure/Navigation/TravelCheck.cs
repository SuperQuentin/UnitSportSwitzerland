using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// <c>--travelcheck --world fixture --systems ui,physics,adventure --pois fixture --adventure-travel network</c>
/// (quick tier, #1226): offline in Network mode on the flat fixture. Free jumps are off; walking to the bus stop and the
/// station unlocks them after two ticks (the section, <see cref="TravelUnlocked"/> with its totals, "All aboard" started,
/// the toast); the station's sign stands lit and E opens its board (the bus stop priced, the locked cable car absent);
/// refused: a locked point, from the map without the GA, driving; the trip from the sign charges the account, plays the
/// curtain and lands within 40 m; the 10 s rule; the half-fare reward halves the next fare; the GA bought at the
/// ticket office travels from anywhere, rail free; the save file holds the network. Progress in
/// <c>test_output/travelcheck</c>; the bank writes nothing (<c>Bank.Scratch</c>). Windowed: <c>test_output/travel_*.png</c>.
/// </summary>
public partial class TravelCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--travelcheck");

    private readonly Func<FootPlayer?> _me;
    private readonly List<AdventureEvent> _events = new();

    public TravelCheck(ItemController items, Func<FootPlayer?> me) : base(items, "travelcheck", "", "travel_") => _me = me;
    public TravelCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static bool Windowed => DisplayServer.GetName() != "headless";

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => AdventureService.Instance != null && PlacesService.Instance is { Catalogue.Count: > 0 } && AdventureTravel.Instance != null
                && Me is { } me && me.IsOnFloor(), 90))
        {
            Fail("no adventure, places, travel or player on the ground");
            return;
        }
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        await Seconds(1);
        var svc = AdventureService.Instance!;
        var travel = AdventureTravel.Instance!;
        var bank = Bank.Instance!;
        var cat = PlacesService.Instance!.Catalogue;
        svc.Events.Raised += e => _events.Add(e);
        var stop = cat.OfKind(PoiKind.BusStop)[0];
        var station = cat.OfKind(PoiKind.RailStation)[0];
        var cable = cat.OfKind(PoiKind.CableCarStation)[0];
        var book = () => travel.MyBook;
        bank.AdminAdjust(null, set: 500);

        Expect(travel.Mode == TravelMode.Network, $"offline Network travel ({travel.Mode})");
        Expect(!Permissions.CanTeleport, "free jumps are off in Network");

        // walking past the bus stop and the station unlocks them
        await WalkTo(stop.E, stop.N - 10);
        Expect(await Until(() => book().Has(stop.Id), 5), "walking to the bus stop unlocks it");
        Expect(_events.OfType<TravelUnlocked>().Any(t => t.PointId == stop.Id && t.Kind == "busstop" && t.Canton == "VS" && t.Points == 1 && t.Cantons == 1),
            "TravelUnlocked raised with the network's totals");
        Expect(svc.Mine.Get(QuestsProgress.Section).Active.ContainsKey("all-aboard"), "All aboard started on the first unlock");
        Expect(await Until(() => StampToast.Instance?.Last.Contains("Travel point unlocked") == true, 6), $"the toast says so ({StampToast.Instance?.Last})");
        await WalkTo(station.E - 20, station.N);
        Expect(await Until(() => book().Has(station.Id), 5), "and the station");
        Expect(!book().Has(cable.Id), "the cable car, not visited, stays locked");

        // the station's sign: lit, its board
        await WalkTo(station.E + 1.5, station.N + 1.5);
        var signs = TravelSigns.Instance!;
        Expect(await Until(() => signs.InReach(Me!) == station, 4), $"the station's sign is in reach ({signs.Placed.Count} signs)");
        Expect(TravelSigns.PromptFor(Me!)?.Contains("Departures") == true, $"the prompt ({TravelSigns.PromptFor(Me!)})");
        Expect(Me!.TryInteract(), "E at the sign");
        Expect(await Until(() => GameShell.Instance?.Top is DepartureScreen, 2), "its departure board opens");
        if (GameShell.Instance?.Top is DepartureScreen screen)
        {
            await Seconds(0.4);
            var rows = screen.Board.Rows;
            Expect(rows.Any(r => r.Poi == stop && r.Ok && r.Fare > 0), $"the bus stop is a destination with its fare ({string.Join(", ", rows.Select(r => $"{r.Poi.Name}:{r.Ok}:{r.Fare}"))})");
            Expect(!rows.Any(r => r.Poi == cable), "the locked cable car is not on it");
            if (Windowed) GD.Print($"{Log} shot {Shot("board")}");
            GameShell.Instance?.Back();
            await Seconds(0.3);
        }

        // refusals
        Expect(travel.Travel(AdventureService.LocalPeer, cable.Id, station.Id) == TravelNetwork.Why(TravelRefusal.Locked), "a locked point is refused");
        Expect(travel.Travel(AdventureService.LocalPeer, stop.Id, "") == TravelNetwork.Why(TravelRefusal.NotAtSign), "from the map without the GA is refused");
        bool rode = Me!.SetRide((RideKind)CarCatalog.First);
        await Seconds(0.3);
        Expect(rode && travel.Travel(AdventureService.LocalPeer, stop.Id, station.Id) == TravelNetwork.Why(TravelRefusal.Driving), "driving is refused");
        Me!.TryInteract();
        await Until(() => Me!.RideKindId == 0, 3);
        await WalkTo(station.E + 1.5, station.N + 1.5);

        // the trip: charged, the curtain, the arrival
        long had = bank.Balance;
        var fare = TravelNetwork.MayTravel(new TravelAsk(TravelMode.Network, false, true, false, false, false, false, book(), stop, station,
            Me!.Global.E, Me.Global.N, AdventureTravel.UnixNow, had), travel.Rules).Fare;
        int played = TravelCurtain.Instance?.Played ?? 0;
        travel.AskTravel(stop.Id, station.Id);
        Expect(await Until(() => (TravelCurtain.Instance?.Played ?? 0) > played, 2), "the curtain plays");
        if (Windowed) { await Seconds(1.0); GD.Print($"{Log} shot {Shot("curtain")}"); }
        Expect(await Until(() => Near(stop, 40), 8), $"arrived within 40 m of the bus stop ({Dist(stop):F0} m)");
        Expect(fare.Chf > 0 && bank.Balance == had - fare.Chf, $"the fare was charged: CHF {fare.Chf} ({had} -> {bank.Balance})");
        Expect(book().Trips == 1 && book().LastTravel > 0, "the trip is in the travel section");
        Expect(travel.Travel(AdventureService.LocalPeer, station.Id, stop.Id) == TravelNetwork.Why(TravelRefusal.Cooldown), "a second trip at once waits");
        book().LastTravel = 0;

        // the half-fare card halves fares
        int halfBefore = CountOf(ItemId.HalfFare), gaBefore = CountOf(ItemId.GaCard);
        svc.Grant(AdventureService.LocalPeer, new Unlock("travel", AdventureTravel.HalfFareArg), "check");
        Expect(book().HasHalfFare, "the half-fare reward sets the card");
        Expect(await Until(() => CountOf(ItemId.HalfFare) == halfBefore + 1, 2), "and puts it in the pack");
        var full = travel.Rules.Price(TravelKind.Bus, 0, TravelKind.Rail, 0, 30000, false, false);
        var half = travel.Rules.Price(TravelKind.Bus, 0, TravelKind.Rail, 0, 30000, false, true);
        Expect(half.Chf < full.Chf && half.Chf >= full.Chf / 2, $"30 km: CHF {full.Chf} full, {half.Chf} half");

        // the GA at the ticket office: travel from anywhere, rail free
        bank.AdminAdjust(null, set: 5000);
        await WalkTo(station.E + 1.5, station.N + 1.5);
        Expect(travel.BuyCard(AdventureService.LocalPeer, true, stop.Id) != null, "no GA from a bus stop: it is sold at a station");
        Expect(travel.BuyCard(AdventureService.LocalPeer, true, station.Id) == null && book().HasGa, "bought at the station's ticket office");
        int ga = TravelRules.CardPrice(travel.Rules.GaCard, AdventureTravel.Today);
        Expect(bank.Balance == 5000 - ga, $"CHF {ga} charged ({bank.Balance} left)");
        Expect(await Until(() => CountOf(ItemId.GaCard) == gaBefore + 1, 2), "the GA is in the pack");
        await WalkTo(station.E + 60, station.N + 60);
        had = bank.Balance;
        Expect(travel.Travel(AdventureService.LocalPeer, stop.Id, "") == null, "with the GA, from anywhere");
        Expect(await Until(() => Near(stop, 40), 8), "arrived");
        Expect(bank.Balance == had, "free with the GA");

        svc.SaveAll();
        SaveQueue.Flush();
        try
        {
            var back = AdventureBook.FromJson(System.IO.File.ReadAllText(svc.FileOf(AdventureService.LocalKey)));
            var saved = back.Get(TravelBook.Section);
            Expect(saved.Has(stop.Id) && saved.Has(station.Id) && saved.HasGa && saved.HasHalfFare && saved.Trips == 2, "the save file holds the network, the cards and the trips");
        }
        catch (Exception ex) { Expect(false, $"the save file reads back: {ex.Message}"); }
        // the pack is the player's own user:// one: the cards go again
        _items.Inventory.TakePlain(ItemId.HalfFare, CountOf(ItemId.HalfFare));
        _items.Inventory.TakePlain(ItemId.GaCard, CountOf(ItemId.GaCard));
        await Finish(0);
    }

    private double Dist(Poi p) => TravelNetwork.Distance(p, Me!.Global.E, Me.Global.N);
    private bool Near(Poi p, double m) => Dist(p) <= m;

    /// <summary>Walks the body in 20 m steps twice a second (under the jump rule's 40 m/s), on the flat ground.</summary>
    private async Task WalkTo(double e, double n)
    {
        var me = Me!;
        while (true)
        {
            var g = me.Global;
            double de = e - g.E, dn = n - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 0.5) break;
            double step = Math.Min(d, 20);
            me.GlobalPosition += new Vector3((float)(de / d * step), 0.2f, (float)(-dn / d * step));
            me.Velocity = Vector3.Zero;
            await Seconds(0.5);
        }
    }
}
