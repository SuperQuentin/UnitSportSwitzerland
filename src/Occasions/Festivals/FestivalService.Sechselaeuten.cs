using System.Text.Json;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.Sechselaeuten;

namespace UnitSport.Occasions.Festivals;

// The Sechseläuten on the festival service (#1358, protocol 78, docs/notes/occasions/festival-sechselaeuten.md): one request, "bet on this bucket, grill a
// Cervelat, take the Zunft's purse", and one message, the round's view. The server keeps the round (BooggRound): bets open until the sky clock crosses 18:00,
// when the pyre is lit and the burn time is drawn from a random seed and kept in the round, where nothing reads it until the head bursts. Until then no
// message, view, log line or field carries it. At the bang the pool is shared out by closeness (the shares are staked Batzen coming back, credited straight
// to the player or owed on their next load), a bet that was played also pays a little through Award, and the next round opens a minute later.
public partial class FestivalService
{
    public const int SechseBet = 0, SechseGrill = 1, SechsePurse = 2;

    private readonly Dictionary<string, BooggRound> _boogg = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sechseCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Instance, int Round, string Key)> _grilled = new();
    private double? _sechseLastHour;
    private double _nextSechseTick;

    /// <summary>Real seconds a minute of the burn lasts: 6 (a median burn of 13 minutes is 78 s); <c>--sechsefast</c> (0.4) or <c>--sechsepace S</c> change it for the checks.</summary>
    public static double SechseSecondsPerMinute { get; set; } = ArgNumber("--sechsepace") ?? (CmdArgs.Has("--sechsefast") ? 0.4 : SechselaeutenRules.DefaultSecondsPerMinute);

    /// <summary>Check seam, never set in the game: replaces the draw of the burn time (the seed in, minutes out); <c>--sechsedraw M</c> fixes it on a check's server.</summary>
    public static Func<int, double>? SechseDraw { get; set; } = ArgNumber("--sechsedraw") is { } fixedMinutes ? _ => fixedMinutes : null;

    private static double? ArgNumber(string flag) =>
        CmdArgs.Value(flag) is { } s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>Authority: lights, bangs, bot bets and grilled Cervelats since start (checks).</summary>
    public int SechseLights { get; private set; }

    public int SechseBangs { get; private set; }

    public int SechseBotBets { get; private set; }

    public int SechseGrills { get; private set; }

    /// <summary>The round as this player was last told it (the owner's mirror; offline the authority's own view of itself).</summary>
    public BooggView? SechseView { get; private set; }

    /// <summary>Client: the round changed (a bet, the light, the bang, a new round) or the server said something to this player: the view.</summary>
    public event Action<BooggView>? SechseTold;

    /// <summary>Client: every message exactly as it came off the wire (a check reads them for a leak of the secret).</summary>
    public event Action<string>? SechseRaw;

    /// <summary>The Sechseläuten, if it is on and held in this region.</summary>
    public (RunningFestival Run, ResolvedSite Site)? SechseNow() =>
        RunningOf(SechselaeutenRules.FestivalId) is { Site: { } site } run ? (run, site) : null;

    /// <summary>Authority: the round of a running instance, as the server has it (checks).</summary>
    public BooggRound? SechseRound(string instance) => _boogg.GetValueOrDefault(instance);

    private BooggRound RoundOf(string instance)
    {
        if (_boogg.TryGetValue(instance, out var round)) return round;
        int number = _sechseCount[instance] = _sechseCount.GetValueOrDefault(instance) + 1;
        round = _boogg[instance] = new BooggRound(instance, number);
        GD.Print($"[festival] {SechselaeutenRules.FestivalId}: round {number} opens its bets");
        BroadcastSechse(round);
        return round;
    }

    // ---- the requests ------------------------------------------------------------------------------------

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> bets (<see cref="SechseBet"/>, <paramref name="arg"/> the bucket 1 to 12), grills a Cervelat on the
    /// embers (<see cref="SechseGrill"/>) or takes the Zunft's purse (<see cref="SechsePurse"/>). Returns null when it counted, else why not (the peer is told too).
    /// A bot only bets, for the crowd: its bet has no stake and wins nothing.
    /// </summary>
    public string? ServeSechse(long peer, int kind, int arg)
    {
        if (!Authority) return "Not here.";
        if (SechseNow() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def) return Tell(peer, "The Sechseläuten is not on.");
        if (kind is < SechseBet or > SechsePurse) return Tell(peer, "There is nothing like that here.");
        var (run, site) = m;
        var round = RoundOf(run.Instance);
        if (peer < 0) return ServeSechseBot(peer, round, kind, arg);
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Tell(peer, "Go to the square.");
        return kind switch
        {
            SechseBet => ServeBet(peer, p, round, run, def, site, at, arg),
            SechseGrill => ServeGrill(peer, p, round, site, at),
            _ => ServePurse(peer, p, run, def, site, at),
        };
    }

    private string? ServeBet(long peer, PlayerProgress p, BooggRound round, RunningFestival run, FestivalDef def, ResolvedSite site, GlobalPos at, int bucket)
    {
        if (!SechselaeutenRules.NearPyre(site, at.E, at.N)) return Tell(peer, "Go to the pyre first.");
        if (round.WhyNotBet(p.Key, bucket) is { } why) return Tell(peer, why);
        var inst = p.Get(FestivalBook.Section).Of(run.Instance, def.Id);
        if (!FestivalRules.Spend(inst, SechselaeutenRules.Stake))
            return Tell(peer, $"A bet is {SechselaeutenRules.Stake} Batzen; you hold {inst.Batzen}. The Zunft's Weibel hands out a purse.");
        _adventure.Touch(peer, FestivalBook.Section);
        round.Add(new SechselaeutenRules.Bet(p.Key, NameOf(peer, p), bucket, SechselaeutenRules.Stake, false));
        GD.Print($"[festival] {Short(p.Key)}: bet {SechselaeutenRules.Stake} Batzen on {SechselaeutenRules.Label(bucket)} in round {round.Number} ({round.Bets.Count} bets, pool {round.Pool})");
        BroadcastSechse(round, forPeer: peer, msg: $"You bet {SechselaeutenRules.Stake} Batzen on {SechselaeutenRules.Label(bucket)}. The pool holds {round.Pool}.");
        return null;
    }

    private string? ServeGrill(long peer, PlayerProgress p, BooggRound round, ResolvedSite site, GlobalPos at)
    {
        if (!SechselaeutenRules.NearPyre(site, at.E, at.N)) return Tell(peer, "Go to the embers first.");
        if (round.Phase == BooggPhase.Betting) return Tell(peer, "The pyre is not lit yet.");
        if (round.Phase == BooggPhase.Burning) return Tell(peer, "The fire is too hot: wait for the head to burst.");
        if (!_grilled.Add((round.Instance, round.Number, p.Key))) return Tell(peer, "You have grilled your Cervelat this round.");
        _adventure.Grant(peer, new Item(ItemId.BooggCervelat, SechselaeutenRules.Cervelats), "sechse:grill");
        SechseGrills++;
        SendSechse(peer, round, "You grill a Cervelat on the embers.");
        return null;
    }

    private string? ServePurse(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, GlobalPos at)
    {
        if (!SechselaeutenRules.NearWeibel(site, at.E, at.N)) return Tell(peer, "Go to the Zunft's Weibel first.");
        if (PlayedToday(p, run, def, SechselaeutenRules.PurseGame) > 0) return Tell(peer, "You have the Zunft's purse today.");
        int granted = Award(peer, run.Id, SechselaeutenRules.PurseBatzen, "the Zunft's purse", SechselaeutenRules.PurseGame, SechselaeutenRules.PursePoints);
        if (_boogg.GetValueOrDefault(run.Instance) is { } round)
            SendSechse(peer, round, $"The Weibel hands you the Zunft's purse: +{granted} Batzen. A bet is {SechselaeutenRules.Stake}.");
        return null;
    }

    private string? ServeSechseBot(long peer, BooggRound round, int kind, int bucket)
    {
        if (kind != SechseBet) return "A bot only bets.";
        string key = $"bot{-peer}";
        if (round.WhyNotBet(key, bucket) is { } why) return why;
        round.Add(new SechselaeutenRules.Bet(key, $"Bot {-peer}", bucket, 0, true));
        SechseBotBets++;
        BroadcastSechse(round);
        return null;
    }

    // ---- the round's clock -----------------------------------------------------------------------------------

    /// <summary>Every quarter second: opens a round, lights it when the sky crosses 18:00, bursts the head when its time has come, voids or retires a round.</summary>
    private void SechseTick(double now)
    {
        if (now < _nextSechseTick) return;
        _nextSechseTick = now + 0.25;
        double hour = Hour(), sim = Net.ClockSync.SimNow;
        double? last = _sechseLastHour;
        _sechseLastHour = hour;
        var running = SechseNow();
        foreach (var round in _boogg.Values.ToArray())
        {
            bool live = running is { } m && m.Run.Instance == round.Instance;
            if (round.Phase == BooggPhase.Betting && !live) VoidRound(round);
            else if (round.Due(sim)) BangRound(round, sim);
            else if (round.Phase == BooggPhase.Done && (!live || sim - round.DoneAt >= SechselaeutenRules.DoneHold)) _boogg.Remove(round.Instance);
        }
        if (running is not { } now2) return;
        var current = RoundOf(now2.Run.Instance);
        if (current.Phase == BooggPhase.Betting && SechselaeutenRules.Lights(last, hour))
        {
            int seed = Random.Shared.Next();
            current.Light(sim, seed, SechseSecondsPerMinute, SechseDraw);
            SechseLights++;
            GD.Print($"[festival] {SechselaeutenRules.FestivalId}: round {current.Number}: the Böögg is lit at {hour:F2} with {current.Bets.Count} bets, pool {current.Pool}");
            BroadcastSechse(current);
        }
    }

    /// <summary>The festival ended with the bets still open: every stake goes back.</summary>
    private void VoidRound(BooggRound round)
    {
        _boogg.Remove(round.Instance);
        foreach (var bet in round.Bets)
            if (!bet.Bot) CreditBatzen(round.Instance, bet.Key, bet.Stake);
        GD.Print($"[festival] {SechselaeutenRules.FestivalId}: round {round.Number} was never lit, {round.Bets.Count} stake(s) returned");
    }

    private void BangRound(BooggRound round, double sim)
    {
        double minutes = round.Bang(sim);
        SechseBangs++;
        var lines = new List<string>();
        foreach (var bet in round.Bets)
        {
            if (bet.Bot) continue;
            int share = round.Shares.GetValueOrDefault(bet.Key);
            CreditBatzen(round.Instance, bet.Key, share);
            lines.Add($"{bet.Name} {SechselaeutenRules.Label(bet.Bucket)} +{share}");
            long peer = PeerOfKey(bet.Key);
            if (peer > 0 && _adventure.Active(peer))
                Award(peer, SechselaeutenRules.FestivalId, SechselaeutenRules.PlayBatzen, $"a bet on the Böögg, {share} from the pool", SechselaeutenRules.BetGame,
                    SechselaeutenRules.PlayPoints + share);
        }
        GD.Print($"[festival] {SechselaeutenRules.FestivalId}: round {round.Number}: the head burst after {minutes:F1} min (bucket {SechselaeutenRules.BucketOf(minutes)}); " +
            $"pool {round.Pool} shared: {(lines.Count == 0 ? "no bets" : string.Join(", ", lines))}");
        BroadcastSechse(round, msgFor: key =>
        {
            var bet = round.BetOf(key);
            string head = $"The head burst after {minutes:F1} minutes.";
            return bet is not { Bot: false } b ? $"{head} You did not bet."
                : $"{head} You bet {SechselaeutenRules.Label(b.Bucket)}: +{round.Shares.GetValueOrDefault(key)} Batzen from the pool of {round.Pool}.";
        });
    }

    /// <summary>Pays Batzen into a player's instance: straight in when they are here (converting at once if the festival is over), else owed to their next load.</summary>
    private void CreditBatzen(string instance, string key, int batzen)
    {
        if (batzen <= 0) return;
        long peer = PeerOfKey(key);
        if (peer > 0 && _adventure.Progress(peer) is { } p)
        {
            var inst = p.Get(FestivalBook.Section).Of(instance, SechselaeutenRules.FestivalId);
            inst.Batzen += batzen;
            _adventure.Touch(peer, FestivalBook.Section);
            if (inst.Converted)
            {
                inst.Converted = false;
                ConvertFor(peer);
            }
            return;
        }
        GetBoard(instance, SechselaeutenRules.FestivalId).Owe(key, batzen);
        _boardsDirty.Add(instance);
    }

    // ---- the wire ------------------------------------------------------------------------------------------

    private void BroadcastSechse(BooggRound round, long forPeer = 0, string msg = "", Func<string, string>? msgFor = null)
    {
        foreach (long peer in _adventure.Peers.ToArray())
        {
            if (peer <= 0) continue;
            string key = _adventure.KeyOf(peer) ?? "";
            SendSechse(peer, round, peer == forPeer ? msg : msgFor?.Invoke(key) ?? "");
        }
    }

    private void SendSechse(long peer, BooggRound round, string msg = "")
    {
        var view = round.ViewFor(_adventure.KeyOf(peer) ?? "", msg);
        string json = JsonSerializer.Serialize(view, _json);
        if (Connected(peer)) RpcId(peer, MethodName.TellSechse, json);
        else if (!_networked && peer > 0) TellSechse(json);
    }

    /// <summary>A joiner is told the round as it stands.</summary>
    private void SechseWelcome(long peer)
    {
        if (SechseNow() is { } m && _boogg.GetValueOrDefault(m.Run.Instance) is { } round && peer > 0) SendSechse(peer, round);
    }

    /// <summary>Client: bet on a bucket (1 to 12), grill a Cervelat or take the purse at what I stand at. Offline the local player is the authority.</summary>
    public void AskSechse(int kind, int arg = 0)
    {
        if (Authority) ServeSechse(AdventureService.LocalPeer, kind, arg);
        else RpcId(1, MethodName.RequestSechse, kind, arg);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSechse(int kind, int arg)
    {
        if (_server) ServeSechse(Multiplayer.GetRemoteSenderId(), kind, arg);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellSechse(string json)
    {
        SechseRaw?.Invoke(json);
        BooggView? view;
        try { view = JsonSerializer.Deserialize<BooggView>(json, _json); }
        catch (JsonException) { return; }
        if (view == null) return;
        SechseView = view;
        SechseTold?.Invoke(view);
    }
}
