namespace UnitSport.Adventure.Together;

/// <summary>One shared pot (#1266 part 5): where, who sits, what went in, the split meter, the dips left.</summary>
public sealed class Pot
{
    public int Id { get; init; }
    public PotSpot Spot { get; init; }
    public string SpotId { get; init; } = "";
    public double E { get; init; }
    public double N { get; init; }
    public float Alt { get; init; }
    public bool Raclette { get; init; }
    public int MaxSeats { get; init; } = FondueRules.MaxSeats;
    public long Starter { get; init; }
    public double Started { get; init; }

    public readonly List<long> Seats = new();
    /// <summary>The seated with their own bread in hand (they may dip).</summary>
    public readonly HashSet<long> Bread = new();
    /// <summary>Who put what in: the panel's chips.</summary>
    public readonly Dictionary<long, List<Ingredient>> Added = new();
    public int Cheese, Potatoes;
    public bool Wine, Kirsch;
    /// <summary>The split meter, 0..1; at 1 the cheese split.</summary>
    public float Split;
    public bool Splitted => Split >= 1f;
    /// <summary>Dips left; set when the cheese is in (cooking), 0 again at the end.</summary>
    public int DipsLeft;
    public bool Cooking;
    public bool Ended;
    public readonly List<float> Qs = new();
    public double LastEightAt = double.NegativeInfinity;
    public readonly Dictionary<long, double> LastEightOf = new();
    public readonly Queue<double> RecentEights = new();
    public readonly Dictionary<long, double> LastDipOf = new();
    /// <summary>Breads lost at this pot, for the progress.</summary>
    public int BreadsLost;

    public float MeanQ => Qs.Count == 0 ? 0f : Qs.Average();
    /// <summary>The mean of the last few eights: what the bread's odds read.</summary>
    public float RecentQ => Qs.Count == 0 ? 0f : Qs.Skip(Math.Max(0, Qs.Count - 4)).Average();
    public float Quality => FondueRules.Quality(MeanQ, Split, Splitted);

    /// <summary>The wire's byte per seat: bit 0 bread in hand, then a bit per ingredient that seat put in (1 &lt;&lt; (1 + Ingredient)).</summary>
    public byte SeatBits(long peer)
    {
        int b = Bread.Contains(peer) ? 1 : 0;
        if (Added.TryGetValue(peer, out var list)) foreach (var i in list) b |= 1 << (1 + (int)i);
        return (byte)b;
    }
}

/// <summary>What a dip did.</summary>
public enum DipResult : byte { Ok = 0, NotSeated = 1, NoBread = 2, TooSoon = 3, Empty = 4, NotCooking = 5, LostBread = 6 }

/// <summary>
/// The shared pots (#1266 part 5), the server's (offline the client's) memory, forgotten on a restart: start, sit,
/// add, stir, dip, leave, the split meter over time and the end. Pure (tier 0, <c>PotBookTests</c>); the
/// <c>TogetherService</c> checks the spots and bodies, sends the state and grants the feast.
/// </summary>
public sealed class PotBook
{
    private readonly Dictionary<int, Pot> _pots = new();
    private int _next = 1;

    public IReadOnlyCollection<Pot> All => _pots.Values;
    public Pot? Get(int id) => _pots.GetValueOrDefault(id);

    /// <summary>The pot <paramref name="peer"/> sits at, or null.</summary>
    public Pot? Of(long peer) => _pots.Values.FirstOrDefault(p => !p.Ended && p.Seats.Contains(peer));

    /// <summary>The pot at a spot (one a spot), or null.</summary>
    public Pot? AtSpot(string spotId) => _pots.Values.FirstOrDefault(p => !p.Ended && p.SpotId == spotId);

    /// <summary>Starts a pot with its starter seated. Null when the starter sits elsewhere or the spot has one.</summary>
    public Pot? Start(long starter, PotSpot spot, string spotId, double e, double n, float alt, bool raclette, double now)
    {
        if (Of(starter) != null || AtSpot(spotId) != null) return null;
        var p = new Pot
        {
            Id = _next++, Spot = spot, SpotId = spotId, E = e, N = n, Alt = alt, Raclette = raclette, Starter = starter, Started = now,
            MaxSeats = spot == PotSpot.Festival ? FondueRules.FestivalSeats : FondueRules.MaxSeats,
        };
        p.Seats.Add(starter);
        _pots[p.Id] = p;
        return p;
    }

    /// <summary>Seats a peer. Null when seated, else why not.</summary>
    public string? Sit(Pot p, long peer)
    {
        if (p.Ended) return "That pot is finished.";
        if (p.Seats.Contains(peer)) return null;
        if (Of(peer) != null) return "You sit at another pot.";
        if (p.Seats.Count >= p.MaxSeats) return "The table is full.";
        p.Seats.Add(peer);
        return null;
    }

    /// <summary>
    /// Puts an ingredient in (the owner already took it from its pack: a refusal gives it back). Bread is the peer's own
    /// (one in hand at a time); cheese and potatoes start the cooking once there is enough; wine and kirsch once each.
    /// </summary>
    public string? Add(Pot p, long peer, Ingredient i)
    {
        if (p.Ended) return "That pot is finished.";
        if (!p.Seats.Contains(peer)) return "Sit down first.";
        switch (i)
        {
            case Ingredient.Bread:
                if (p.Raclette) return "Raclette needs no bread.";
                if (!p.Bread.Add(peer)) return "You have your bread already.";
                break;
            case Ingredient.Cheese:
                p.Cheese++;
                if (p.Cooking) p.DipsLeft += FondueRules.DipsPerCheese;
                break;
            case Ingredient.Potato:
                if (!p.Raclette) return "Potatoes are for a raclette.";
                p.Potatoes++;
                break;
            case Ingredient.Wine:
                if (p.Wine) return "The wine is in already.";
                p.Wine = true;
                break;
            case Ingredient.Kirsch:
                if (p.Kirsch) return "The kirsch is in already.";
                p.Kirsch = true;
                break;
        }
        if (!p.Added.TryGetValue(peer, out var list)) p.Added[peer] = list = new List<Ingredient>();
        list.Add(i);
        TryCook(p);
        return null;
    }

    /// <summary>Starts cooking when the table has its cheese (a raclette its potatoes too) and two seats.</summary>
    private static void TryCook(Pot p)
    {
        if (p.Cooking || p.Seats.Count < FondueRules.MinSeats) return;
        if (p.Cheese < FondueRules.CheeseNeeded(p.Seats.Count)) return;
        if (p.Raclette && p.Potatoes < FondueRules.PotatoesPerSeat * p.Seats.Count) return;
        p.Cooking = true;
        p.DipsLeft = FondueRules.Dips(p.Cheese);
    }

    /// <summary>An eight stirred (or a raclette's scrape) of quality <paramref name="q"/>: counted, or not (too fast, a crowd of spoons).</summary>
    public bool Stir(Pot p, long peer, float q, double now)
    {
        if (p.Ended || !p.Cooking || !p.Seats.Contains(peer) || p.Splitted) return false;
        if (now - p.LastEightOf.GetValueOrDefault(peer, double.NegativeInfinity) < StirRules.PerPeerEvery) return false;
        p.LastEightOf[peer] = now;
        while (p.RecentEights.Count > 0 && now - p.RecentEights.Peek() > StirRules.CrowdWindow) p.RecentEights.Dequeue();
        if (p.RecentEights.Count >= StirRules.CrowdMax) return false;
        p.RecentEights.Enqueue(now);
        p.LastEightAt = now;
        q = Math.Clamp(q, 0f, 1f);
        p.Qs.Add(q);
        p.Split = StirRules.AfterEight(p.Split, q);
        return true;
    }

    /// <summary>The meter over <paramref name="dt"/>: nobody stirring for 3 s, or no wine (a high heat), it rises.</summary>
    public static void Tick(Pot p, float dt, double now)
    {
        if (p.Ended || !p.Cooking || p.Raclette) return;
        p.Split = StirRules.Step(p.Split, dt, now - p.LastEightAt < StirRules.IdleAfter, hot: !p.Wine);
    }

    /// <summary>
    /// A dip: seated, with bread, one per 2 s, while the pot cooks. <paramref name="roll"/> (uniform 0..1) decides a
    /// lost bread at <see cref="FondueRules.BreadLossChance"/>; a lost bread still takes a dip and heals nothing.
    /// </summary>
    public static DipResult Dip(Pot p, long peer, double now, double roll)
    {
        if (p.Ended || !p.Seats.Contains(peer)) return DipResult.NotSeated;
        if (!p.Cooking) return DipResult.NotCooking;
        if (p.DipsLeft <= 0) return DipResult.Empty;
        if (!p.Raclette && !p.Bread.Contains(peer)) return DipResult.NoBread;
        double last = p.LastDipOf.GetValueOrDefault(peer, double.NegativeInfinity);
        if (now - last < FondueRules.DipEvery) return DipResult.TooSoon;
        p.LastDipOf[peer] = now;
        p.DipsLeft--;
        if (!p.Raclette && roll < FondueRules.BreadLossChance(p.RecentQ, now - last < FondueRules.HurriedWithin))
        {
            p.Bread.Remove(peer);
            p.BreadsLost++;
            return DipResult.LostBread;
        }
        return DipResult.Ok;
    }

    /// <summary>A peer leaves the table (Esc, gone): no feast for it. A pot left by everyone ends with nobody fed.</summary>
    public void Leave(Pot p, long peer)
    {
        p.Seats.Remove(peer);
        p.Bread.Remove(peer);
        if (p.Seats.Count == 0) End(p);
    }

    /// <summary>Whether the pot is done: emptied by the dips.</summary>
    public static bool Done(Pot p) => p.Cooking && p.DipsLeft <= 0 && !p.Ended;

    public void End(Pot p)
    {
        p.Ended = true;
        _pots.Remove(p.Id);
    }

    /// <summary>A peer gone from the server: it leaves its pot.</summary>
    public void PeerLeft(long peer)
    {
        if (Of(peer) is { } p) Leave(p, peer);
    }
}
