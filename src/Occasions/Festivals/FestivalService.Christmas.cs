using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Alcohol;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.Christmas;

namespace UnitSport.Occasions.Festivals;

// The Christmas market on the festival service (#1348, protocol 76, docs/notes/occasions/festival-christmas.md): one request, "use the stall
// (or the Samichlaus) I stand at". At a stall the first use of the day is a free taste (a stamp: Batzen and party points through Award, the
// Marktgang when all eight are stamped), every later use buys the stall's ware for Batzen (the book's Spend, the item through Grant). The Glühwein
// mug carries its alcohol on its ItemDef (2 dl, 10 %), so drinking it reaches AlcoholService.ServeDrink by the same path as a beer. On 6 December
// the Samichlaus gives his bag once. A bot takes ServeMarket too: it tastes (and drinks the Glühwein), earns and buys nothing.
public partial class FestivalService
{
    /// <summary>Authority: this day's stamps per player and festival instance (memory: a restart only gives the day's cap again).</summary>
    private readonly Dictionary<(string Instance, long Peer), (int Day, int Mask)> _stamps = new();

    /// <summary>Authority: wares sold, tastes given and bots served since start (checks).</summary>
    public int MarketSold { get; private set; }

    public int MarketBotTastes { get; private set; }

    /// <summary>Client: the market told this player something: the festival day, the stamps of that day, what happened, the stall, a line to show.</summary>
    public event Action<int, int, MarketEvent, int, string>? MarketTold;

    /// <summary>Client: the stalls this player has stamped today, as the server last told it (<see cref="MarketDay"/> is the day it was for).</summary>
    public int MarketStamps { get; private set; }

    public int MarketDay { get; private set; }

    /// <summary>What a market request came to: a state sync, a taste, the Marktgang, a ware bought, the Samichlaus's bag.</summary>
    public enum MarketEvent { Sync = 0, Tasted = 1, Marktgang = 2, Bought = 3, Bag = 4 }

    /// <summary>The market, if it is on and held in this region.</summary>
    public (RunningFestival Run, ResolvedSite Site)? MarketNow() =>
        RunningOf(ChristmasRules.FestivalId) is { Site: { } site } run ? (run, site) : null;

    /// <summary>The stalls this player has stamped today, from what the server told it (0 once the day moved on).</summary>
    public int StampsToday(int day) => MarketDay == day ? MarketStamps : 0;

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> uses the stall <paramref name="target"/> (0 to 7) or, with
    /// <see cref="MarketSamichlaus"/>, the Samichlaus. Returns null when it counted, else why not (the peer is told too).
    /// </summary>
    public string? ServeMarket(long peer, int target)
    {
        if (!Authority) return "Not here.";
        if (MarketNow() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def) return Tell(peer, "The Christmas market is not open.");
        if (target != MarketSamichlaus && target is < 0 or >= ChristmasRules.Stalls) return Tell(peer, "There is no such stall.");
        var (run, site) = m;
        if (peer < 0) return ServeMarketBot(peer, site, target);

        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Tell(peer, "Go to the market.");
        return target == MarketSamichlaus ? ServeSamichlaus(peer, p, run, def, site, at.E, at.N) : ServeStall(peer, p, run, def, site, target, at.E, at.N);
    }

    /// <summary>The target number of the Samichlaus.</summary>
    public const int MarketSamichlaus = 100;

    private string? ServeStall(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, int stall, double e, double n)
    {
        var st = ChristmasRules.Layout(site)[stall];
        if ((st.E - e) * (st.E - e) + (st.N - n) * (st.N - n) > ChristmasRules.StallReach * ChristmasRules.StallReach)
            return Tell(peer, "Go to the stall first.");
        string name = ChristmasRules.StallName(st.Kind);
        var key = (run.Instance, peer);
        var (day, mask) = _stamps.GetValueOrDefault(key);
        if (day != run.Day) mask = 0;

        // the first use of the day at a stall is its free taste: a stamp
        if (!ChristmasRules.Stamped(mask, stall))
        {
            mask = ChristmasRules.Stamp(mask, stall);
            _stamps[key] = (run.Day, mask);
            int granted = Award(peer, run.Id, ChristmasRules.StampBatzen, $"tasted at the {name}", ChristmasRules.StallGame, ChristmasRules.StampPoints);
            bool all = mask == ChristmasRules.AllStamps;
            if (all)
                granted += Award(peer, run.Id, ChristmasRules.MarktgangBatzen, "the Marktgang: every stall in a day", ChristmasRules.StallGame, ChristmasRules.MarktgangPoints);
            NoteMarket(peer, run.Day, mask, all ? MarketEvent.Marktgang : MarketEvent.Tasted, stall,
                all ? $"Marktgang! All {ChristmasRules.Stalls} stalls today: +{granted} Batzen"
                    : $"A taste at the {name}: +{granted} Batzen ({ChristmasRules.Count(mask)}/{ChristmasRules.Stalls} stalls today)");
            return null;
        }

        // later: the ware, for Batzen
        var inst = p.Get(FestivalBook.Section).Of(run.Instance, def.Id);
        int price = ChristmasRules.PriceOf(st.Kind);
        string ware = ChristmasRules.WareName(st.Kind);
        if (!FestivalRules.Spend(inst, price)) return Tell(peer, $"{ware} costs {price} Batzen; you hold {inst.Batzen}.");
        _adventure.Touch(peer, FestivalBook.Section);
        _adventure.Grant(peer, new Item(ChristmasRules.ItemOf(st.Kind), 1), "market:" + st.Kind);
        MarketSold++;
        GD.Print($"[festival] {Short(p.Key)} bought a {ware} for {price} Batzen ({inst.Batzen} left)");
        NoteMarket(peer, run.Day, mask, MarketEvent.Bought, stall, $"{ware}: -{price} Batzen");
        return null;
    }

    private string? ServeSamichlaus(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, double e, double n)
    {
        if (!ChristmasRules.NearSamichlaus(site, e, n)) return Tell(peer, "Go to the Samichlaus first.");
        if (run.Day < ChristmasRules.SamichlausDay) return Tell(peer, "The Samichlaus comes on 6 December.");
        if (run.Day > ChristmasRules.SamichlausDay) return Tell(peer, "The Samichlaus has gone until next year.");
        var inst = p.Get(FestivalBook.Section).Of(run.Instance, def.Id);
        if (inst.Played.GetValueOrDefault(FestivalRules.PlayKey(ChristmasRules.SamichlausGame, run.Day)) > 0)
            return Tell(peer, "He has given you his bag already.");
        int granted = Award(peer, run.Id, ChristmasRules.SamichlausBatzen, "the Samichlaus's bag", ChristmasRules.SamichlausGame, ChristmasRules.SamichlausPoints);
        _adventure.Grant(peer, new Item(ItemId.Grittibaenz, 1), "market:samichlaus");
        _adventure.Grant(peer, new Item(ItemId.Mandarin, 2), "market:samichlaus");
        NoteMarket(peer, run.Day, StampsMask(run, peer), MarketEvent.Bag, MarketSamichlaus, $"The Samichlaus's bag: +{granted} Batzen, a Grittibänz and two mandarins");
        return null;
    }

    private int StampsMask(RunningFestival run, long peer) =>
        _stamps.GetValueOrDefault((run.Instance, peer)) is var (d, mask) && d == run.Day ? mask : 0;

    /// <summary>A bot at a stall: it tastes (the Glühwein stall's mug is drunk through the alcohol hook), earns and buys nothing, takes nothing from a Samichlaus.</summary>
    private string? ServeMarketBot(long bot, ResolvedSite site, int target)
    {
        if (target == MarketSamichlaus) return "A bot earns nothing from the Samichlaus.";
        var st = ChristmasRules.Layout(site)[target];
        MarketBotTastes++;
        if (st.Kind == StallKind.Gluehwein && ItemDefs.Get(ItemId.GluehweinMug) is { } mug)
            AlcoholService.Instance?.ServeDrink(bot, mug.DrinkMl, mug.Abv, false, "market");
        return null;
    }

    private void NoteMarket(long peer, int day, int mask, MarketEvent what, int stall, string text)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellMarket, day, mask, (int)what, stall, text);
        else if (!_networked && peer > 0) TellMarket(day, mask, (int)what, stall, text);
    }

    /// <summary>A joiner is told its stamps of today (a player who reconnects keeps them while the server runs).</summary>
    private void MarketWelcome(long peer)
    {
        if (MarketNow() is not { } m || peer <= 0) return;
        NoteMarket(peer, m.Run.Day, StampsMask(m.Run, peer), MarketEvent.Sync, -1, "");
    }

    // ---- the owner's request ---------------------------------------------------------------------------

    /// <summary>Client: use the stall (or the Samichlaus, <see cref="MarketSamichlaus"/>) I stand at. Offline the local player is the authority.</summary>
    public void AskMarket(int target)
    {
        if (Authority) ServeMarket(AdventureService.LocalPeer, target);
        else RpcId(1, MethodName.RequestMarket, target);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestMarket(int target)
    {
        if (_server) ServeMarket(Multiplayer.GetRemoteSenderId(), target);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellMarket(int day, int mask, int what, int stall, string text)
    {
        MarketDay = day;
        MarketStamps = mask;
        MarketTold?.Invoke(day, mask, (MarketEvent)what, stall, text);
    }
}
