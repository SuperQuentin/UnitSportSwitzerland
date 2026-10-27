using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// <c>--navnet walk|watch</c> with <c>--connect</c> and <c>--pois fixture</c> (driven by <c>tools/navnetcheck.sh</c>,
/// #1226): a server with <c>--adventure-travel network</c>. What each role checks reached this client:
/// <list type="bullet">
/// <item><c>walk</c> (A, Lea): the server's Network mode and no free jumps (<c>/city</c> refused); walking to the bus
/// stop unlocks it (the travel section arrives); a shared pin; a trip to a locked point and one from the map without the
/// GA are refused; with CHF 500 from B (an admin), the trip from the station's sign is charged by the server and lands at
/// the bus stop; the GA bought at the station travels from anywhere for nothing; in a Battle Royale lobby a trip is
/// refused; A leaves the party.</item>
/// <item><c>watch</c> (B, Tom, at the same time, its own <c>user://</c>): joins A's party, sees A's shared pin
/// (<c>[nav] party pin</c>), sees A's remote body arrive at the bus stop, gives A the money, sees the pin go when A
/// leaves; none of A's network reaches B's own section.</item>
/// </list>
/// </summary>
public partial class NavNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--navnet");

    public NavNetProbe(ItemController items) : base(items, "navnet", "NV", "navnet_") { }
    public NavNetProbe() : this(null!) { }

    private static AdventureTravel Travel => AdventureTravel.Instance!;
    private static Poi Of(PoiKind k) => PlacesService.Instance!.Catalogue.OfKind(k)[0];

    public override async void _Ready()
    {
        _role = Role ?? "WALK";
        if (!await Joined(150, () => AdventureService.Instance != null && PlacesService.Instance is { Catalogue.Count: > 0 } && AdventureTravel.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        var svc = AdventureService.Instance!;
        Expect(await Until(() => svc.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        Expect(await Until(() => Travel.Mode == TravelMode.Network, 15), $"the server's travel mode reached it ({Travel.Mode})");
        if (_role == "WALK") await Walk();
        else await Watch();
        await Finish(4.0);
    }

    private async Task Walk()
    {
        var stop = Of(PoiKind.BusStop);
        var station = Of(PoiKind.RailStation);
        var cable = Of(PoiKind.CableCarStation);
        Expect(!Permissions.CanTeleport, "no free jumps on a network server");
        Chat!.Send("/city Sion");
        Expect(await Until(() => _heard.Any(l => l.Contains("does not allow teleporting")), 10), "/city is refused");

        await WalkTo(stop.E, stop.N - 10);
        Expect(await Until(() => Travel.MyBook.Has(stop.Id), 10), "walking to the bus stop: the server unlocked it, the travel section shows it");
        Say("unlocked");

        // the party and a shared pin
        Expect(await Heard("WATCH", "ready", 60), "B is on");
        Chat.Send("/party invite Tom");
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "A's party holds A and B");
        var at = Me!.Global;
        Travel.AskPinAdd(at.E + 25, at.N + 5, (byte)PinIcon.Flag, "Meet here", shared: true);
        Expect(await Until(() => Travel.MyPins.Pins.Count == 1, 10), "the pin is in A's section");
        Say("pinned");

        // refusals
        Travel.AskTravel(cable.Id, "");
        Expect(await Until(() => Travel.LastRefusal == TravelNetwork.Why(TravelRefusal.Locked), 10), $"a locked point is refused ({Travel.LastRefusal})");
        await Seconds(1.2);
        Travel.AskTravel(stop.Id, "");
        Expect(await Until(() => Travel.LastRefusal == TravelNetwork.Why(TravelRefusal.NotAtSign), 10), $"from the map without the GA is refused ({Travel.LastRefusal})");

        // a fare
        Say("fund");
        Expect(await Until(() => Bank.Instance!.Balance >= 500, 30), $"B's admin put CHF 500 on A's account ({Bank.Instance!.Balance})");
        await WalkTo(station.E - 20, station.N);
        Expect(await Until(() => Travel.MyBook.Has(station.Id), 10), "the station is unlocked");
        await WalkTo(station.E + 1.5, station.N + 1.5);
        await Seconds(1.0);
        long had = Bank.Instance!.Balance;
        Travel.AskTravel(stop.Id, station.Id);
        Expect(await Until(() => Dist(stop) < 40, 15), $"the trip from the station's sign lands at the bus stop ({Dist(stop):F0} m)");
        Expect(await Until(() => Bank.Instance!.Balance < had, 10), $"the server charged the fare ({had} -> {Bank.Instance!.Balance})");
        Expect(Travel.MyBook.Trips == 1, "the trip is in the travel section");
        await Seconds(1.5);
        Say("arrived");

        // the GA: bought at the station, from anywhere, free
        Expect(await Heard("WATCH", "seen", 40), "B saw A arrive");
        Say("fundga");
        Expect(await Until(() => Bank.Instance!.Balance >= 5000, 30), "CHF 5000 for the GA");
        await WalkTo(station.E + 1.5, station.N + 1.5);
        await Seconds(1.0);
        Travel.AskBuy(true, station.Id);
        Expect(await Until(() => Travel.MyBook.HasGa, 10), "the server sold A the GA at the station");
        await Until(() => CountOf(ItemId.GaCard) == 1, 5);
        await WalkTo(station.E + 60, station.N + 60);
        await Until(() => false, 11);   // the 10 s between trips
        had = Bank.Instance!.Balance;
        Travel.AskTravel(stop.Id, "");
        Expect(await Until(() => Dist(stop) < 40, 15), $"with the GA from anywhere ({Dist(stop):F0} m)");
        await Seconds(1.0);
        Expect(Bank.Instance!.Balance == had, $"and free ({had} -> {Bank.Instance!.Balance})");

        // nothing in a Battle Royale round, even for an admin (opening a lobby is an admin's)
        if (!await LogIn("navpw")) return;
        Chat.Send("/br open here");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.State.Phase is BattleRoyale.BrPhase.Lobby, 15), "a Battle Royale lobby is open");
        Chat.Send("/br join");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.MyEntry != null, 15), "A joined it");
        Travel.AskTravel(station.Id, "");
        Expect(await Until(() => Travel.LastRefusal == TravelNetwork.Why(TravelRefusal.NoAdventure), 10), $"in a Battle Royale round a trip is refused ({Travel.LastRefusal})");
        Chat.Send("/br leave");
        await Until(() => BattleRoyale.BrManager.Instance?.MyEntry == null, 15);
        Chat.Send("/br cancel");

        Chat.Send("/party leave");
        Expect(await Until(() => Parties.Client is { InParty: false }, 10), "A left the party");
        Say("left");
    }

    private async Task Watch()
    {
        var stop = Of(PoiKind.BusStop);
        Say("ready");
        Expect(await Until(() => InviteService.Client?.Incoming.Any(v => v.Kind == Parties.Kind) == true, 60), "B is invited to A's party");
        if (InviteService.Client?.Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } party) InviteService.Client.Accept(party.Id);
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 20), "B's party holds A and B");
        Expect(await Heard("WALK", "pinned", 90), "A pinned");
        Expect(await Until(() => Travel.PartyPins.Values.Any(p => p.Pins.Any(x => x.Note == "Meet here")), 15), "A's shared pin reached B");
        GD.Print($"[nav] party pin seen: {Travel.PartyPins.Count} member(s)");
        Expect(await Heard("WALK", "fund", 120), "A asks for money");
        if (!await LogIn("navpw")) return;
        Chat!.Send("/bank Lea set 500");
        Expect(await Heard("WALK", "arrived", 120), "A travelled");
        var a = Parties.Client!.Members.First(m => m.Name == "Lea").Peer;
        Expect(await Until(() => AdventureCompass.PeerAt?.Invoke(a) is { } p && TravelNetwork.Distance(stop, p.E, p.N) < 40, 15),
            $"B sees A's remote body at the bus stop ({(AdventureCompass.PeerAt?.Invoke(a) is { } q ? TravelNetwork.Distance(stop, q.E, q.N) : -1):F0} m)");
        Say("seen");
        Expect(await Heard("WALK", "fundga", 60), "A wants the GA");
        Chat.Send("/bank Lea set 5000");
        Expect(await Heard("WALK", "left", 180), "A left the party");
        Expect(await Until(() => Travel.PartyPins.Count == 0, 10), "A's pin is gone from B");
        Expect(!Travel.MyBook.Has(stop.Id), "none of A's network is B's");
    }

    private double Dist(Poi p) => TravelNetwork.Distance(p, Me!.Global.E, Me.Global.N);

    /// <summary>Walks the body in 20 m steps twice a second (under the jump rule's 40 m/s).</summary>
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
        GD.Print(FormattableString.Invariant($"{Log} at {me.Global.E:F1}/{me.Global.N:F1}"));
    }
}
