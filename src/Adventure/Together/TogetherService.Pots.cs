using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Together;

/// <summary>A friend shared a pot to its end (#1266 part 5): raised for each seat after the feast, for quests and errands.</summary>
public sealed record FondueShared(long Peer, string Key, string SpotId, float Quality, int Seats) : AdventureEvent(Peer, Key);

/// <summary>A client's picture of the pot it sits at (the <c>PotState</c> RPC), redrawn on a change only.</summary>
public sealed class PotView
{
    public int Id;
    public PotSpot Spot;
    public bool Raclette, Cooking, Splitted, Wine, Kirsch, Ended;
    public double E, N;
    public float Alt;
    /// <summary>The split meter, 0..1.</summary>
    public float Split;
    public int Dips, Cheese;
    public float Quality;
    public long[] Seats = Array.Empty<long>();
    public byte[] Bits = Array.Empty<byte>();

    public bool HasBread(long peer) => Array.IndexOf(Seats, peer) is int i and >= 0 && (Bits[i] & 1) != 0;
}

/// <summary>
/// The shared pot (#1266 part 5, <c>docs/notes/adventure/fondue.md</c>): the server keeps the <see cref="PotBook"/>,
/// checks the spot (a burning campfire with a fondue pot brought, a mountain hut, a dining venue), the seats, every
/// ingredient, eight and dip, integrates the split meter, rolls the bread, runs the forfeits and grants the feast.
/// The seated get <c>PotState</c> at 2 Hz on a change only; the owner adds from its own pack.
/// </summary>
public partial class TogetherService
{
    /// <summary>The invite kind that asks a non-member to a table (<c>/fondue invite</c>).</summary>
    public const string FondueKind = "fondue";
    /// <summary>Spot reaches, m: a hut, a venue, a fire (the craft station's reach + 1).</summary>
    public const float HutReach = 25f, VenueReach = 18f, FireReach = Crafting.CraftStations.FireReach + 1f, SitReach = 6f;

    /// <summary><c>--forfeits off|mild|all</c> (the server's; default all).</summary>
    public static ForfeitMode Forfeits { get; set; } = FondueRules.ParseMode(CmdArgs.Value("--forfeits"));

    /// <summary>
    /// <c>--fondueseed n</c> (checks): the table's rolls come from a seeded generator, and the n-th dip at a pot loses its
    /// bread for certain (0: by the odds only).
    /// </summary>
    private static readonly int FondueSeed = int.TryParse(CmdArgs.Value("--fondueseed"), out int s) ? s : 0;

    public PotBook Pots { get; } = new();
    private Random _potRng = new();
    private readonly Dictionary<long, (int Pot, double Until)> _forfeitDue = new();
    private readonly HashSet<long> _invitedTo = new();
    private readonly Dictionary<int, string> _potSent = new();
    private readonly Dictionary<int, int> _dipCount = new();
    private double _nextPotTick;

    partial void ReadyPots()
    {
        if (FondueSeed != 0) _potRng = new Random(FondueSeed);
        if (!_server) return;
        InviteService.Server?.Register(new InviteKind
        {
            Id = FondueKind, Label = "fondue", Seconds = 60, TellAccepted = true,
            Accept = (peer, invite) =>
            {
                if (Pots.Of(invite.From) is not { } pot) return "[fondue] That table has finished.";
                _invitedTo.Add(peer);
                OfferPot(peer, pot);
                return ServeSit(peer, pot.Id) is { } why ? $"[fondue] {why}" : "[fondue] You sit down at the pot.";
            },
        });
        Multiplayer.PeerDisconnected += id => { Pots.PeerLeft(id); _forfeitDue.Remove(id); };
    }

    /// <summary>The placed objects of this world: the server's own (set by <c>ServerWorld</c>), else the client's.</summary>
    public Func<PlacedObjects?>? PlacedSource { get; set; }
    private PlacedObjects? Placed => PlacedSource?.Invoke() ?? PlacedObjects.Instance;

    // ---- authority ---------------------------------------------------------------------------------

    /// <summary>A pot spot near a peer: kind, id, where. Null when there is none.</summary>
    public (PotSpot Spot, string Id, double E, double N, float Alt)? SpotNear(long peer, bool campfireOnly = false)
    {
        if (PositionOf(peer) is not { } at) return null;
        // a burning campfire: the placed objects' (the server's, offline the client's)
        if (Placed is { } placed)
        {
            double now = World.WorldClock.EnvNow;
            foreach (var o in placed.All.Values)
            {
                if (o.Kind != PlacedKind.Campfire || !Crafting.CampfireClock.Burning(o.Payload, now)) continue;
                var g = placed.Origin.ToGlobal(o.WorldTransform(placed.Origin).Origin);
                if (g.DistanceTo(at) <= FireReach) return (PotSpot.Campfire, $"fire:{o.Id}", g.E, g.N, (float)g.Alt);
            }
        }
        if (campfireOnly) return null;
        // a mountain hut (or a chalet restaurant, which the places map to one)
        var near = new List<Places.Poi>();
        Places.PoiCatalogueHost.Current.Near(at.E, at.N, HutReach, near, PoiKind.Hut);
        if (near.Count > 0) return (PotSpot.Hut, $"hut:{near[0].Id}", near[0].E, near[0].N, (float)at.Alt);
        // a dining venue (a restaurant, a pub, a Beiz)
        if (DiningVenueNear(at.E, at.N) is { } v) return (PotSpot.Venue, $"venue:{v.Osm}", v.E, v.N, (float)at.Alt);
        return null;
    }

    private static VenueRecord? DiningVenueNear(double e, double n)
    {
        var list = Interiors.Venues.List;
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].E < e - VenueReach) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < list.Count && list[i].E <= e + VenueReach; i++)
        {
            var v = list[i];
            if (Interiors.Venues.IsDining(v.Type) && (v.E - e) * (v.E - e) + (v.N - n) * (v.N - n) <= VenueReach * VenueReach) return v;
        }
        return null;
    }

    /// <summary>
    /// Starts a pot where <paramref name="peer"/> stands (E at a hut or a venue, a fondue pot used at a fire, <c>/fondue</c>):
    /// adventuring, a spot near, no pot there yet. <paramref name="atFire"/>: the starter brought a pot to a fire. Null when started.
    /// </summary>
    public string? ServeStart(long peer, bool atFire, bool raclette = false)
    {
        if (!Authority) return "Not here.";
        if (!_adventure.Active(peer)) return Refuse(peer, "No adventuring now.");
        if (SpotNear(peer, campfireOnly: atFire) is not { } spot) return Refuse(peer, atFire ? "No burning fire here." : "No table for a pot here: a mountain hut, a restaurant, or a fire with a fondue pot.");
        if (spot.Spot == PotSpot.Campfire && !atFire) return Refuse(peer, "Bring a fondue pot to the fire: use it there.");
        if (Pots.AtSpot(spot.Id) is { } there) return ServeSit(peer, there.Id);
        if (Pots.Start(peer, spot.Spot, spot.Id, spot.E, spot.N, spot.Alt, raclette, GameClock.Now) is not { } pot) return Refuse(peer, "You sit at another pot.");
        GD.Print($"[fondue] {Who(peer)} starts a {(raclette ? "raclette" : "fondue")} at {spot.Id}");
        _dipCount[pot.Id] = 0;
        SendPot(pot, force: true);
        // the party is told it may sit down (E at the pot)
        if (Party(peer) is { } party)
            foreach (long m in party)
                if (m != peer)
                {
                    OfferPot(m, pot);
                    Tell(m, $"{Who(peer)} starts a {(raclette ? "raclette" : "fondue")}: sit down at the pot.");
                }
        return null;
    }

    /// <summary>Sits a peer at a pot: the starter's party, or invited to it, within <see cref="SitReach"/> m. Null when seated.</summary>
    public string? ServeSit(long peer, int potId)
    {
        if (!Authority) return "Not here.";
        if (Pots.Get(potId) is not { } pot) return Refuse(peer, "That pot is finished.");
        if (!_adventure.Active(peer)) return Refuse(peer, "No adventuring now.");
        bool bot = Body(peer) is { Bot: true };
        if (bot) _adventure.SessionFor(peer);
        if (!bot && peer != pot.Starter && !SameParty(peer, pot.Starter) && !_invitedTo.Contains(peer)) return Refuse(peer, "Only the table's party, or whom it invites.");
        if (PositionOf(peer) is not { } at || Dist(at, pot.E, pot.N) > SitReach + (pot.Spot == PotSpot.Venue ? VenueReach : 2f))
            return Refuse(peer, "Come to the table first.");
        if (Pots.Sit(pot, peer) is { } why) return Refuse(peer, why);
        GD.Print($"[fondue] {Who(peer)} sits at pot {pot.Id} ({pot.Seats.Count} seated)");
        SendPot(pot, force: true);
        return null;
    }

    private static double Dist(GlobalPos a, double e, double n) => Math.Sqrt((a.E - e) * (a.E - e) + (a.N - n) * (a.N - n));

    /// <summary>An ingredient in (the owner took it from its pack first; a refusal gives it back). Null when added.</summary>
    public string? ServeAdd(long peer, int potId, ItemId item)
    {
        string? why = Pots.Get(potId) is not { } pot ? "That pot is finished."
            : FondueRules.Of(item) is not { } ing ? "That does not go in a pot."
            : Pots.Add(pot, peer, ing);
        if (why != null)
        {
            TellAddResult(peer, item, false, why);
            return why;
        }
        var p = Pots.Get(potId)!;
        GD.Print($"[fondue] {Who(peer)} adds {item} (cheese {p.Cheese}, dips {p.DipsLeft}{(p.Cooking ? ", cooking" : "")})");
        TellAddResult(peer, item, true, "");
        SendPot(p, force: true);
        return null;
    }

    /// <summary>An eight stirred (or a raclette scraped) of quality <paramref name="q"/> 0..1. True when it counted.</summary>
    public bool ServeStir(long peer, int potId, float q)
    {
        if (!Authority || Pots.Get(potId) is not { } pot) return false;
        bool split = pot.Splitted;
        bool counted = Pots.Stir(pot, peer, q, GameClock.Now);
        if (counted) GD.Print(FormattableString.Invariant($"[fondue] {Who(peer)} stirs an eight q {q:F2}: split {pot.Split:F2}"));
        if (!split && pot.Splitted) GD.Print($"[fondue] pot {pot.Id} split");
        return counted;
    }

    /// <summary>A dip (or a scrape's portion): the server rolls the bread. The result.</summary>
    public DipResult ServeDip(long peer, int potId)
    {
        if (!Authority || Pots.Get(potId) is not { } pot) return DipResult.NotSeated;
        int n = _dipCount.GetValueOrDefault(potId) + 1;
        double roll = FondueSeed > 0 && n == FondueSeed && pot.Bread.Contains(peer) ? 0 : _potRng.NextDouble();
        var r = PotBook.Dip(pot, peer, GameClock.Now, roll);
        if (r is DipResult.Ok or DipResult.LostBread) _dipCount[potId] = n;
        switch (r)
        {
            case DipResult.Ok:
                TellDip(peer, FondueRules.DipHeal);
                break;
            case DipResult.LostBread:
                GD.Print($"[fondue] {Who(peer)} lost the bread in pot {pot.Id}");
                if (_adventure.Progress(peer) is { } lostP)
                {
                    lostP.Get(TogetherProgress.Section).BreadLost++;
                    lostP.Touch(TogetherProgress.Section);
                }
                foreach (long s in pot.Seats) SendBreadLost(s, pot.Id, peer);
                if (Forfeits != ForfeitMode.Off) _forfeitDue[peer] = (pot.Id, GameClock.Now + FondueRules.ForfeitSeconds);
                break;
            default:
                Refuse(peer, r switch
                {
                    DipResult.NoBread => "No bread on your fork: add one.",
                    DipResult.TooSoon => "Not so fast.",
                    DipResult.NotCooking => "The cheese is not melted yet.",
                    DipResult.Empty => "The pot is empty.",
                    _ => "Sit down first.",
                });
                break;
        }
        SendPot(pot, force: true);
        return r;
    }

    /// <summary>
    /// A raclette's scrape (Use in the bubbling's window): its quality from the server's own clock, counted like an eight,
    /// then a portion (a dip). The portion's result.
    /// </summary>
    public DipResult ServeScrape(long peer, int potId)
    {
        if (!Authority || Pots.Get(potId) is not { Raclette: true } pot) return DipResult.NotSeated;
        float q = FondueRules.ScrapeQ(FondueRules.SincePeak(pot.Id, ClockSync.ServerNow));
        Pots.Stir(pot, peer, q, GameClock.Now);
        GD.Print(FormattableString.Invariant($"[fondue] {Who(peer)} scrapes q {q:F2}"));
        return ServeDip(peer, potId);
    }

    /// <summary>
    /// The forfeit of a lost bread (the popup, <c>/fondue forfeit 1|2|3</c>, or the default after 20 s): a line to the
    /// table, a round (CHF 5 a head, a beer each for the others), or a yodel. Null when done.
    /// </summary>
    public string? ServeForfeit(long peer, Forfeit choice)
    {
        if (!Authority || !_forfeitDue.Remove(peer, out var due)) return "No forfeit owed.";
        var pot = Pots.Get(due.Pot);
        var table = pot?.Seats.ToArray() ?? new[] { peer };
        int others = table.Count(s => s != peer);
        long balance = Bank.Instance?.BalanceOf(peer) ?? 0;
        if (!FondueRules.Allowed(Forfeits, choice, balance, others)) choice = Forfeit.Line;
        string line;
        switch (choice)
        {
            case Forfeit.Round:
                if (Bank.Instance?.Charge(peer, FondueRules.RoundCost(others), out _) != true) goto default;
                foreach (long s in table)
                    if (s != peer) _adventure.Grant(s, new Adventure.Item(ItemId.BeerBottle), "round");
                line = $"{Who(peer)} buys a round: a beer for everyone at the table.";
                break;
            case Forfeit.Yodel:
                line = $"{Who(peer)} yodels for the table.";
                break;
            default:
                choice = Forfeit.Line;
                line = string.Format(FondueRules.Lines[_potRng.Next(FondueRules.Lines.Length)], Who(peer));
                break;
        }
        GD.Print($"[fondue] forfeit {choice}: {line}");
        foreach (long s in table) SendForfeit(s, due.Pot, peer, choice, line);
        return null;
    }

    /// <summary>Leaves the table (Esc, B, the panel): no feast for it.</summary>
    public void ServeLeave(long peer, int potId)
    {
        if (!Authority || Pots.Get(potId) is not { } pot || !pot.Seats.Contains(peer)) return;
        Pots.Leave(pot, peer);
        _invitedTo.Remove(peer);
        GD.Print($"[fondue] {Who(peer)} leaves pot {pot.Id}");
        SendPotTo(peer, null);
        if (!pot.Ended) SendPot(pot, force: true);
    }

    partial void ProcessPots(double delta, double now)
    {
        if (!_server) LookForSpot(now);
        if (!Authority || now < _nextPotTick) return;
        float dt = (float)(now - _nextPotTick + 0.5);
        _nextPotTick = now + 0.5;
        if (Pots.All.Count == 0 && _forfeitDue.Count == 0) return;
        foreach (var pot in Pots.All.ToList())
        {
            bool split = pot.Splitted;
            PotBook.Tick(pot, dt, now);
            if (!split && pot.Splitted) GD.Print($"[fondue] pot {pot.Id} split (nobody stirred)");
            if (PotBook.Done(pot)) Feast(pot);
            else SendPot(pot, force: false);
        }
        // a forfeit nobody chose: the first
        foreach (var (peer, due) in _forfeitDue.ToList())
            if (now >= due.Until) ServeForfeit(peer, Forfeit.Line);
    }

    private void Feast(Pot pot)
    {
        float q = pot.Quality;
        byte mask = FondueRules.BuffMask(pot.Wine, pot.Kirsch);
        float seconds = FondueRules.FedSeconds(q);
        GD.Print(FormattableString.Invariant($"[fondue] pot {pot.Id} done: quality {q:F2}{(pot.Splitted ? " (split)" : "")}, {pot.Seats.Count} fed for {seconds:F0} s"));
        var seats = pot.Seats.ToArray();
        Pots.End(pot);
        _potSent.Remove(pot.Id);
        foreach (long s in seats)
        {
            SendFeast(s, mask, seconds, FondueRules.FeastHeal);
            // wine and kirsch in the pot carry alcohol (#1271): 4 g a wine pour, 3 g with kirsch; they just ate, so slowly absorbed
            double grams = (pot.Wine ? Alcohol.AlcoholRules.FondueWineGrams : 0) + (pot.Kirsch ? Alcohol.AlcoholRules.FondueKirschGrams : 0);
            if (grams > 0) Alcohol.AlcoholService.Instance?.ServeGrams(s, grams, fed: true, "fondue");
            SendPotTo(s, null);
            CountPot(s, pot);
            PotEnded?.Invoke(s, pot, q);
            if (_adventure.Progress(s) is { } p) _adventure.Events.Raise(new FondueShared(s, p.Key, pot.SpotId, q, seats.Length));
        }
    }

    /// <summary>Authority: a seat's pot ended with its feast (the progress counts it).</summary>
    public event Action<long, Pot, float>? PotEnded;

    // ---- the wire, server to the seated -----------------------------------------------------------------

    private void SendPot(Pot pot, bool force)
    {
        byte flags = (byte)((pot.Raclette ? 1 : 0) | (pot.Cooking ? 2 : 0) | (pot.Splitted ? 4 : 0) | (pot.Wine ? 8 : 0) | (pot.Kirsch ? 16 : 0));
        byte split = (byte)Math.Round(pot.Split * 255), quality = (byte)Math.Round(pot.Quality * 255);
        var bits = pot.Seats.Select(pot.SeatBits).ToArray();
        string key = $"{flags}|{split}|{pot.DipsLeft}|{string.Join(",", pot.Seats)}|{Convert.ToHexString(bits)}|{pot.Cheese}";
        if (!force && _potSent.GetValueOrDefault(pot.Id) == key) return;
        _potSent[pot.Id] = key;
        var seats = pot.Seats.ToArray();
        foreach (long s in seats)
        {
            if (Connected(s)) RpcId(s, MethodName.PotState, pot.Id, (byte)pot.Spot, flags, pot.E, pot.N, pot.Alt, split, pot.DipsLeft, seats, bits, pot.Cheese, quality);
            else if (!_networked && s == AdventureService.LocalPeer) PotState(pot.Id, (byte)pot.Spot, flags, pot.E, pot.N, pot.Alt, split, pot.DipsLeft, seats, bits, pot.Cheese, quality);
        }
    }

    private void SendPotTo(long peer, Pot? _)
    {
        if (Connected(peer)) RpcId(peer, MethodName.PotGone);
        else if (!_networked && peer == AdventureService.LocalPeer) PotGone();
    }

    private void TellAddResult(long peer, ItemId item, bool ok, string why)
    {
        if (Connected(peer)) RpcId(peer, MethodName.AddResult, (int)item, ok, why);
        else if (!_networked && peer == AdventureService.LocalPeer) AddResult((int)item, ok, why);
    }

    private void TellDip(long peer, float heal)
    {
        if (Connected(peer)) RpcId(peer, MethodName.Dipped, heal);
        else if (!_networked && peer == AdventureService.LocalPeer) Dipped(heal);
        else if (Body(peer) is { } b && (b.Bot || b.Npc)) b.Heal(heal);
    }

    private void SendBreadLost(long seat, int pot, long who)
    {
        if (Connected(seat)) RpcId(seat, MethodName.BreadLost, pot, who);
        else if (!_networked && seat == AdventureService.LocalPeer) BreadLost(pot, who);
    }

    private void SendForfeit(long seat, int pot, long who, Forfeit choice, string line)
    {
        if (Connected(seat)) RpcId(seat, MethodName.ForfeitDone, pot, who, (byte)choice, line);
        else if (!_networked && seat == AdventureService.LocalPeer) ForfeitDone(pot, who, (byte)choice, line);
    }

    private void SendFeast(long seat, byte mask, float seconds, float heal)
    {
        if (Connected(seat)) RpcId(seat, MethodName.TellFeast, mask, seconds, heal);
        else if (!_networked && seat == AdventureService.LocalPeer) TellFeast(mask, seconds, heal);
        else if (Body(seat) is { } b && (b.Bot || b.Npc)) FeastOn(b, mask, seconds, heal);
    }

    // ---- the owner's side -------------------------------------------------------------------------------

    /// <summary>Client: the pot the local player sits at, or null.</summary>
    public PotView? MyPot { get; private set; }
    /// <summary>Client: the pot's state changed (the panel and the table redraw).</summary>
    public event Action? PotChanged;
    /// <summary>Client: someone at the table lost their bread (peer); the local player's own opens the forfeit choice.</summary>
    public event Action<long>? BreadLostAt;
    /// <summary>Client: a forfeit was done at the table (peer, choice, the line).</summary>
    public event Action<long, Forfeit, string>? ForfeitShown;
    /// <summary>Client: the forfeit the local player owes, until (game time); 0 when none.</summary>
    public double ForfeitUntil { get; private set; }
    /// <summary>Client: the last feast's line, for the checks.</summary>
    public string LastFeast { get; private set; } = "";

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PotState(int id, byte spot, byte flags, double e, double n, float alt, byte split, int dips, long[] seats, byte[] bits, int cheese, byte quality)
    {
        var v = MyPot?.Id == id ? MyPot : new PotView { Id = id };
        bool wasSplit = v.Splitted;
        v.Spot = (PotSpot)spot;
        v.Raclette = (flags & 1) != 0;
        v.Cooking = (flags & 2) != 0;
        v.Splitted = (flags & 4) != 0;
        v.Wine = (flags & 8) != 0;
        v.Kirsch = (flags & 16) != 0;
        (v.E, v.N, v.Alt) = (e, n, alt);
        v.Split = split / 255f;
        v.Dips = dips;
        v.Seats = seats;
        v.Bits = bits;
        v.Cheese = cheese;
        v.Quality = quality / 255f;
        bool fresh = MyPot != v;
        MyPot = v;
        if (fresh) GD.Print($"[fondue] at pot {id}");
        if (!wasSplit && v.Splitted) PlayAt(Self, TogetherSounds.Hiss);
        GD.Print(FormattableString.Invariant($"[fondue] state pot {id}: split {v.Split:F2}, dips {dips}, {seats.Length} seated"));
        UpdateTable();
        PotChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PotGone()
    {
        if (MyPot == null) return;
        GD.Print($"[fondue] left pot {MyPot.Id}");
        MyPot = null;
        ForfeitUntil = 0;
        UpdateTable();
        PotChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AddResult(int item, bool ok, string why)
    {
        if (ok) return;
        // refused: the item goes back where it came from
        _adventure.GiveItems?.Invoke(new ItemStack((ItemId)item, 1));
        Toast?.Invoke(why);
        GD.Print($"[fondue] {(ItemId)item} refused: {why}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Dipped(float heal)
    {
        if (Me?.Invoke() is { } me) me.Heal(heal);
        PlayAt(Self, TogetherSounds.Scrape);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BreadLost(int pot, long who)
    {
        GD.Print($"[fondue] {Who(who)} lost the bread");
        Toast?.Invoke(who == Self ? "Your bread is in the cheese! Choose your forfeit." : $"{Who(who)} lost the bread!");
        PlayAt(who == Self ? Self : who, TogetherSounds.Plop);
        _table?.SinkBread();
        if (who == Self && Forfeits != ForfeitMode.Off) ForfeitUntil = GameClock.Now + FondueRules.ForfeitSeconds;
        BreadLostAt?.Invoke(who);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ForfeitDone(int pot, long who, byte choice, string line)
    {
        GD.Print($"[fondue] forfeit line: {line}");
        if (who == Self) ForfeitUntil = 0;
        Toast?.Invoke(line);
        if ((Forfeit)choice == Forfeit.Yodel)
        {
            PlayAt(who, TogetherSounds.Yodel);
            // the yodel's emote (Cheer) for 4 s, on the yodeller's own body: everyone sees it
            if (who == Self && Me?.Invoke() is { } me) _ = YodelEmote(me);
        }
        ForfeitShown?.Invoke(who, (Forfeit)choice, line);
    }

    private const int YodelEmoteIndex = 1;

    private async System.Threading.Tasks.Task YodelEmote(FootPlayer me)
    {
        me.DanceId = FootPlayer.EmoteDanceBase + YodelEmoteIndex;
        await ToSignal(GetTree().CreateTimer(4.0), SceneTreeTimer.SignalName.Timeout);
        if (IsInstanceValid(me) && me.DanceId == FootPlayer.EmoteDanceBase + YodelEmoteIndex) me.DanceId = 0;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellFeast(byte mask, float seconds, float heal)
    {
        if (Me?.Invoke() is { } me) FeastOn(me, mask, seconds, heal);
        PlayAt(Self, TogetherSounds.Prost);
    }

    private void FeastOn(FootPlayer body, byte mask, float seconds, float heal)
    {
        body.Heal(heal);
        foreach (var b in new[] { StaminaBuff.Fed, StaminaBuff.Warm, StaminaBuff.Hearty })
            if (FondueRules.Has(mask, b)) body.BuffStamina(b, seconds);
        string line = FormattableString.Invariant($"[stamina] buff fed {seconds:F0}{(FondueRules.Has(mask, StaminaBuff.Warm) ? " warm" : "")}{(FondueRules.Has(mask, StaminaBuff.Hearty) ? " hearty" : "")}");
        if (body == Me?.Invoke()) LastFeast = line;
        GD.Print(line);
    }

    private FondueTable? _table;

    /// <summary>The pot drawn where it stands while the local player sits at it; freed when it ends or they leave.</summary>
    private void UpdateTable()
    {
        if (Net.NetworkManager.DedicatedServer) return;
        if (MyPot is not { } pot || Me?.Invoke()?.Origin is not { } origin)
        {
            _table?.QueueFree();
            _table = null;
            return;
        }
        if (_table == null || !IsInstanceValid(_table))
        {
            _table = new FondueTable();
            AddChild(_table);
            // at a fire, the caquelon stands beside the flames; elsewhere on the table's spot
            var at = origin.ToWorld(pot.E, pot.N, pot.Alt);
            if (Me?.Invoke()?.Terrain is { } t && t.TryGetHeight(at, out float h) && pot.Spot == PotSpot.Campfire) at.Y = h;
            _table.GlobalPosition = at + (pot.Spot == PotSpot.Campfire ? new Vector3(0.9f, 0, 0) : Vector3.Zero);
        }
        _table.Show(pot);
    }

    /// <summary>The table drawn now (checks), or null.</summary>
    public FondueTable? Table => _table;

    /// <summary>A peer's display name (the panel's seats).</summary>
    public string NameOfPeer(long peer) => Who(peer);

    // ---- the owner's requests ---------------------------------------------------------------------------

    public void AskStart(bool atFire, bool raclette = false)
    {
        if (Authority) ServeStart(Self, atFire, raclette);
        else RpcId(1, MethodName.RequestStart, atFire, raclette);
    }

    public void AskSit(int pot)
    {
        if (Authority) ServeSit(Self, pot);
        else RpcId(1, MethodName.RequestSit, pot);
    }

    /// <summary>Adds an ingredient from the pack: taken now, given back if the server refuses.</summary>
    public bool AskAdd(ItemId item)
    {
        if (MyPot is not { } pot || _adventure.Pack is not { } pack || pack.TakePlain(item, 1) < 1)
        {
            Toast?.Invoke($"You have no {ItemDefs.Get(item)?.Name ?? item.ToString()}.");
            return false;
        }
        if (Authority) ServeAdd(Self, pot.Id, item);
        else RpcId(1, MethodName.RequestAdd, pot.Id, (int)item);
        return true;
    }

    public void AskStir(float q)
    {
        if (MyPot is not { } pot) return;
        if (Authority) ServeStir(Self, pot.Id, q);
        else RpcId(1, MethodName.StirEight, pot.Id, (byte)Math.Round(Math.Clamp(q, 0f, 1f) * 255));
    }

    public void AskDip()
    {
        if (MyPot is not { } pot) return;
        if (pot.Raclette)
        {
            if (Authority) ServeScrape(Self, pot.Id);
            else RpcId(1, MethodName.RequestScrape, pot.Id);
            return;
        }
        if (Authority) ServeDip(Self, pot.Id);
        else RpcId(1, MethodName.RequestDip, pot.Id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestScrape(int pot) { if (_server) ServeScrape(Multiplayer.GetRemoteSenderId(), pot); }

    public void AskForfeit(Forfeit f)
    {
        if (Authority) ServeForfeit(Self, f);
        else RpcId(1, MethodName.RequestForfeit, (byte)f);
    }

    public void AskLeave()
    {
        if (MyPot is not { } pot) return;
        if (Authority) ServeLeave(Self, pot.Id);
        else RpcId(1, MethodName.RequestLeave, pot.Id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestStart(bool atFire, bool raclette) { if (_server) ServeStart(Multiplayer.GetRemoteSenderId(), atFire, raclette); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSit(int pot) { if (_server) ServeSit(Multiplayer.GetRemoteSenderId(), pot); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAdd(int pot, int item) { if (_server) ServeAdd(Multiplayer.GetRemoteSenderId(), pot, (ItemId)item); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StirEight(int pot, byte q) { if (_server) ServeStir(Multiplayer.GetRemoteSenderId(), pot, q / 255f); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDip(int pot) { if (_server) ServeDip(Multiplayer.GetRemoteSenderId(), pot); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestForfeit(byte choice) { if (_server) ServeForfeit(Multiplayer.GetRemoteSenderId(), (Forfeit)choice); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLeave(int pot) { if (_server) ServeLeave(Multiplayer.GetRemoteSenderId(), pot); }

    /// <summary>
    /// Using a fondue pot (#478) at a burning fire starts a shared pot instead (the item is used up); anywhere else it is
    /// today's share. True when it started one here.
    /// </summary>
    public bool TryStartAtFire(FootPlayer player, int slot)
    {
        if (Permissions.InMatch || MyPot != null || _adventure.Pack is not { } pack) return false;
        if ((Crafting.CraftStations.Where(player).Here & Crafting.Station.Fire) == 0) return false;
        pack.TakeOne(slot);
        AskStart(atFire: true);
        Toast?.Invoke("The caquelon on the fire: friends sit down with E, everyone adds cheese and bread.");
        return true;
    }

    /// <summary>E with nothing else at hand: sit at a pot offered near, or start one at a hut or a venue. False when neither.</summary>
    public bool TryPotInteract(FootPlayer me)
    {
        if (Permissions.InMatch || MyPot != null) return false;
        if (OfferNear() is int id) { AskSit(id); return true; }
        if (_spotHere) { AskStart(atFire: false); return true; }
        return false;
    }

    /// <summary>"Sit down at the pot" / "Start a fondue" for E, or null.</summary>
    private string? PotPrompt() => Permissions.InMatch || MyPot != null ? null
        : OfferNear() != null ? "Sit down at the pot" : _spotHere ? "Start a fondue" : null;

    // pots offered to the local player (its party's, an accepted invite's): id -> where; the server says when one starts
    private readonly Dictionary<int, (double E, double N, double Until)> _offers = new();
    private bool _spotHere;
    private double _nextSpotLook;

    /// <summary>A pot is offered near the local player (checks).</summary>
    public bool OfferedHere => OfferNear() != null;

    private int? OfferNear()
    {
        if (_offers.Count == 0 || PositionOf(Self) is not { } at) return null;
        foreach (var (id, o) in _offers)
            if (GameClock.Now < o.Until && Dist(at, o.E, o.N) <= SitReach + 2f) return id;
        return null;
    }

    /// <summary>Client, 4 times a second: is there a hut or a venue to start a pot at (campfires take a fondue pot used).</summary>
    private void LookForSpot(double now)
    {
        if (now < _nextSpotLook) return;
        _nextSpotLook = now + 0.25;
        _spotHere = MyPot == null && !Permissions.InMatch && Me?.Invoke() != null && SpotNear(Self) is { Spot: not PotSpot.Campfire };
    }

    private void OfferPot(long peer, Pot pot)
    {
        if (Connected(peer)) RpcId(peer, MethodName.PotOffered, pot.Id, pot.E, pot.N);
        else if (!_networked && peer == AdventureService.LocalPeer) PotOffered(pot.Id, pot.E, pot.N);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PotOffered(int id, double e, double n)
    {
        _offers[id] = (e, n, GameClock.Now + 600);
        GD.Print($"[fondue] pot {id} offered");
    }

    /// <summary>
    /// <c>/fondue</c> (start at the spot here), <c>/fondue raclette</c>, <c>/fondue sit</c>, <c>/fondue invite &lt;player&gt;</c>,
    /// <c>/fondue leave</c>, <c>/fondue forfeit 1|2|3</c>. The reply.
    /// </summary>
    public string FondueCommand(long sender, string rest)
    {
        long peer = _networked ? sender : AdventureService.LocalPeer;
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        switch (verb)
        {
            case "" or "start" or "raclette":
                return ServeStart(peer, atFire: false, raclette: verb == "raclette") is { } why ? $"[fondue] {why}" : "[fondue] The pot is on the table.";
            case "sit":
                if (Pots.All.FirstOrDefault(p => PositionOf(peer) is { } at && Dist(at, p.E, p.N) <= SitReach + VenueReach) is not { } pot) return "[fondue] No pot near.";
                return ServeSit(peer, pot.Id) is { } no ? $"[fondue] {no}" : "[fondue] You sit down at the pot.";
            case "leave":
                if (Pots.Of(peer) is not { } mine) return "[fondue] You sit at no pot.";
                ServeLeave(peer, mine.Id);
                return "[fondue] You leave the table.";
            case "forfeit":
                int n = words.Length > 1 && int.TryParse(words[1], out int v) ? v : 1;
                return ServeForfeit(peer, (Forfeit)Math.Clamp(n - 1, 0, 2)) is { } nope ? $"[fondue] {nope}" : "";
            case "invite":
                if (Pots.Of(peer) is not { } at) return "[fondue] Start or sit at a pot first.";
                long other = Chat?.PeerByName(string.Join(' ', words.Skip(1))) ?? -1;
                if (other <= 0 || InviteService.Server is not { } invites) return "[fondue] No such player online.";
                return "[fondue] " + invites.Send(FondueKind, peer, other, $"{Who(peer)} invites you to their fondue", $"{at.Seats.Count} at the table", out _);
            default:
                return "[fondue] /fondue [start | raclette | sit | invite <player> | leave | forfeit 1|2|3]";
        }
    }
}
