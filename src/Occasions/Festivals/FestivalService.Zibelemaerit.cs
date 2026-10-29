using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.Zibelemaerit;

namespace UnitSport.Occasions.Festivals;

// The Zibelemärit on the festival service (#1373, docs/notes/occasions/festival-zibelemaerit.md): one request, "use the stall I stand at" (0 to 3) or, with
// ZibeleThrow, "I threw a confetti egg (or Räppli) on the square". At a stall the first use of the day is a free taste (a stamp: Batzen and party points through
// Award, the Marktgang when all four are stamped), every later use buys the stall's ware for Batzen (the book's Spend, the item through Grant). Every fifth
// throw on the square pays. A bot takes ServeZibele too: it tastes (counted) and throws (counted), earns and buys nothing.
public partial class FestivalService
{
    /// <summary>The target number of a confetti throw.</summary>
    public const int ZibeleThrow = 100;

    /// <summary>What a Zibelemärit request came to: a state sync, a taste, the Marktgang, a ware bought, a paid round of throws.</summary>
    public enum ZibeleEvent { Sync = 0, Tasted = 1, Marktgang = 2, Bought = 3, Thrown = 4 }

    /// <summary>Authority: this day's stamps per player and festival instance (memory: a restart only gives the day's cap again).</summary>
    private readonly Dictionary<(string Instance, long Peer), (int Day, int Mask)> _zibeleStamps = new();

    private readonly Dictionary<(string Instance, string Key), int> _zibeleThrows = new();
    private readonly Dictionary<long, double> _zibeleThrowAt = new();

    /// <summary>Authority: wares sold, tastes given, throws counted and bot uses since start (checks).</summary>
    public int ZibeleSold { get; private set; }

    public int ZibeleThrows { get; private set; }

    public int ZibeleBotActs { get; private set; }

    /// <summary>Client: the market told this player something: the festival day, the stamps of that day, what happened, the stall, a line to show.</summary>
    public event Action<int, int, ZibeleEvent, int, string>? ZibeleTold;

    /// <summary>Client: the stalls this player has stamped today, as the server last told it (<see cref="ZibeleDay"/> is the day it was for).</summary>
    public int ZibeleStamps { get; private set; }

    public int ZibeleDay { get; private set; }

    /// <summary>The Zibelemärit, if it is on and held in this region.</summary>
    public (RunningFestival Run, ResolvedSite Site)? ZibeleNow() =>
        RunningOf(ZibeleRules.FestivalId) is { Site: { } site } run ? (run, site) : null;

    /// <summary>The stalls this player has stamped today, from what the server told it (0 once the day moved on).</summary>
    public int ZibeleStampsToday(int day) => ZibeleDay == day ? ZibeleStamps : 0;

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> uses the stall <paramref name="target"/> (0 to 3) or, with <see cref="ZibeleThrow"/>, threw confetti.
    /// Returns null when it counted, else why not (a refusal is told to the peer too, except a throw away from the market, which is nobody's business).
    /// </summary>
    public string? ServeZibele(long peer, int target)
    {
        if (!Authority) return "Not here.";
        bool thrown = target == ZibeleThrow;
        if (!thrown && target is < 0 or >= ZibeleRules.Stalls) return Tell(peer, "There is no such stall.");
        if (ZibeleNow() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def) return thrown ? "Not on." : Tell(peer, "The Zibelemärit is not on.");
        var (run, site) = m;
        if (peer < 0) { ZibeleBotActs++; return null; }
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return thrown ? "Not adventuring." : Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return thrown ? "No position." : Tell(peer, "Go to the market.");
        return thrown ? ServeZibeleThrow(peer, p, run, site, at.E, at.N) : ServeZibeleStall(peer, p, run, def, site, target, at.E, at.N);
    }

    private string? ServeZibeleStall(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, int stall, double e, double n)
    {
        var st = ZibeleRules.Layout(site)[stall];
        if (ZibeleRules.Dist(e, n, st.E, st.N) > ZibeleRules.StallReach + ZibeleRules.ServerSlack) return Tell(peer, "Go to the stall first.");
        string name = ZibeleRules.StallName(st.Kind);
        var key = (run.Instance, peer);
        var (day, mask) = _zibeleStamps.GetValueOrDefault(key);
        if (day != run.Day) mask = 0;

        // the first use of the day at a stall is its free taste: a stamp
        if (!ZibeleRules.Stamped(mask, stall))
        {
            mask = ZibeleRules.Stamp(mask, stall);
            _zibeleStamps[key] = (run.Day, mask);
            int granted = Award(peer, run.Id, ZibeleRules.StampBatzen, $"tasted at the {name}", ZibeleRules.StallGame, ZibeleRules.StampPoints);
            bool all = mask == ZibeleRules.AllStamps;
            if (all)
                granted += Award(peer, run.Id, ZibeleRules.MarktgangBatzen, "the Marktgang: every stall in a day", ZibeleRules.StallGame, ZibeleRules.MarktgangPoints);
            NoteZibele(peer, run.Day, mask, all ? ZibeleEvent.Marktgang : ZibeleEvent.Tasted, stall,
                all ? $"Marktgang! All {ZibeleRules.Stalls} stalls today: +{granted} Batzen"
                    : $"A taste at the {name}: +{granted} Batzen ({ZibeleRules.Count(mask)}/{ZibeleRules.Stalls} stalls today)");
            return null;
        }

        // later: the ware, for Batzen
        var inst = p.Get(FestivalBook.Section).Of(run.Instance, def.Id);
        int price = ZibeleRules.PriceOf(st.Kind);
        string ware = ZibeleRules.WareName(st.Kind);
        if (!FestivalRules.Spend(inst, price)) return Tell(peer, $"{ware} costs {price} Batzen; you hold {inst.Batzen}.");
        _adventure.Touch(peer, FestivalBook.Section);
        _adventure.Grant(peer, new Item(ZibeleRules.ItemOf(st.Kind), ZibeleRules.CountOf(st.Kind)), "zibele:" + st.Kind);
        ZibeleSold++;
        GD.Print($"[festival] {Short(p.Key)} bought {ware} for {price} Batzen ({inst.Batzen} left)");
        NoteZibele(peer, run.Day, mask, ZibeleEvent.Bought, stall, $"{ware}: -{price} Batzen");
        return null;
    }

    private string? ServeZibeleThrow(long peer, PlayerProgress p, RunningFestival run, ResolvedSite site, double e, double n)
    {
        if (!ZibeleRules.NearSite(site, e, n, ZibeleRules.ServerSlack)) return "Too far from the market.";
        double now = GameClock.Now;
        if (_zibeleThrowAt.TryGetValue(peer, out double last) && now - last < ZibeleRules.ThrowGap) return "Too fast.";
        _zibeleThrowAt[peer] = now;
        var key = (run.Instance, p.Key);
        int count = _zibeleThrows.GetValueOrDefault(key) + 1;
        _zibeleThrows[key] = count;
        ZibeleThrows++;
        if (count % ZibeleRules.ThrowsPerPlay != 0) return null;
        int granted = Award(peer, run.Id, ZibeleRules.ThrowBatzen, "five throws of confetti", ZibeleRules.ConfettiGame, ZibeleRules.ThrowPoints);
        NoteZibele(peer, run.Day, ZibeleMask(run, peer), ZibeleEvent.Thrown, ZibeleThrow, granted > 0 ? $"Confetti over the square: +{granted} Batzen" : "Confetti over the square (today's Batzen are in)");
        return null;
    }

    private int ZibeleMask(RunningFestival run, long peer) =>
        _zibeleStamps.GetValueOrDefault((run.Instance, peer)) is var (d, mask) && d == run.Day ? mask : 0;

    private void NoteZibele(long peer, int day, int mask, ZibeleEvent what, int stall, string text)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellZibele, day, mask, (int)what, stall, text);
        else if (!_networked && peer > 0) TellZibele(day, mask, (int)what, stall, text);
    }

    /// <summary>A joiner is told its stamps of today (a player who reconnects keeps them while the server runs).</summary>
    private void ZibeleWelcome(long peer)
    {
        if (ZibeleNow() is not { } m || peer <= 0) return;
        NoteZibele(peer, m.Run.Day, ZibeleMask(m.Run, peer), ZibeleEvent.Sync, -1, "");
    }

    // ---- the owner's request ---------------------------------------------------------------------------

    /// <summary>Client: use the stall I stand at (0 to 3), or tell the server I threw confetti (<see cref="ZibeleThrow"/>). Offline the local player is the authority.</summary>
    public void AskZibele(int target)
    {
        if (Authority) ServeZibele(AdventureService.LocalPeer, target);
        else RpcId(1, MethodName.RequestZibele, target);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestZibele(int target)
    {
        if (_server) ServeZibele(Multiplayer.GetRemoteSenderId(), target);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellZibele(int day, int mask, int what, int stall, string text)
    {
        ZibeleDay = day;
        ZibeleStamps = mask;
        ZibeleTold?.Invoke(day, mask, (ZibeleEvent)what, stall, text);
    }
}
