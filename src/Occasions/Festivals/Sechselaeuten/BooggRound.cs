// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-sechselaeuten.md).

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

public enum BooggPhase { Betting = 0, Burning = 1, Done = 2 }

/// <summary>One line of the result: a bet and what it won.</summary>
public sealed record BooggRow(string Name, int Bucket, int Won, bool Bot);

/// <summary>
/// What the server tells one player of a round (JSON on the wire). <b>The burn time is in it only once the head has burst</b>:
/// <see cref="Minutes"/> is -1 and <see cref="DoneAt"/> 0 until then, and no other field says it (the light's time is public, the bang's is not).
/// </summary>
public sealed record BooggView(int Round, int Phase, double LitAt, double DoneAt, int Bets, int Pool, int Mine, int Stake, double Minutes, int Won, string Msg, BooggRow[] Board);

/// <summary>
/// One round of the Böögg on the server (#1358): bets open from the end of the last round; at the light the burn time is drawn from a seed and
/// kept in a private field that nothing reads until <see cref="Bang"/>. Pure: the service gives it the clock.
/// </summary>
public sealed class BooggRound
{
    private double _secret = -1;
    private double _bangAt;

    public int Number { get; }

    public string Instance { get; }

    public BooggPhase Phase { get; private set; } = BooggPhase.Betting;

    /// <summary>Server time of the light, and of the bang once it was.</summary>
    public double LitAt { get; private set; }

    public double DoneAt { get; private set; }

    public List<SechselaeutenRules.Bet> Bets { get; } = new();

    /// <summary>What each player won (progress key to Batzen), once the head has burst.</summary>
    public Dictionary<string, int> Shares { get; private set; } = new();

    public BooggRound(string instance, int number)
    {
        Instance = instance;
        Number = number;
    }

    public int Pool => Bets.Where(b => !b.Bot).Sum(b => b.Stake);

    public SechselaeutenRules.Bet? BetOf(string key)
    {
        foreach (var b in Bets)
            if (b.Key == key) return b;
        return null;
    }

    /// <summary>Why a bet cannot be placed now, or null when it can.</summary>
    public string? WhyNotBet(string key, int bucket)
    {
        if (Phase != BooggPhase.Betting) return "Bets are closed: the Böögg is lit.";
        if (bucket is < 1 or > SechselaeutenRules.Buckets) return "There is no such minute.";
        if (BetOf(key) is { } b) return $"You have bet on {SechselaeutenRules.Label(b.Bucket)}.";
        return null;
    }

    public void Add(SechselaeutenRules.Bet bet) => Bets.Add(bet);

    /// <summary>The guilds light the pyre at <paramref name="now"/>: draws the time from <paramref name="seed"/> and keeps it. Betting closes.</summary>
    public void Light(double now, int seed, double secondsPerMinute, Func<int, double>? draw = null)
    {
        if (Phase != BooggPhase.Betting) return;
        _secret = (draw ?? SechselaeutenRules.Draw)(seed);
        LitAt = now;
        _bangAt = now + _secret * secondsPerMinute;
        Phase = BooggPhase.Burning;
    }

    /// <summary>Whether it is time for the head to burst.</summary>
    public bool Due(double now) => Phase == BooggPhase.Burning && now >= _bangAt;

    /// <summary>The head bursts: shares the pool by closeness and opens the secret. Returns the minutes.</summary>
    public double Bang(double now)
    {
        if (Phase != BooggPhase.Burning) return Minutes;
        Phase = BooggPhase.Done;
        DoneAt = now;
        Shares = SechselaeutenRules.Share(Bets, _secret);
        return _secret;
    }

    /// <summary>The burn time in minutes once the head has burst, else -1: the only way out of the secret.</summary>
    public double Minutes => Phase == BooggPhase.Done ? _secret : -1;

    /// <summary>What <paramref name="key"/> is told of the round now.</summary>
    public BooggView ViewFor(string key, string msg = "")
    {
        bool done = Phase == BooggPhase.Done;
        var mine = BetOf(key);
        var board = done
            ? Bets.OrderByDescending(b => Shares.GetValueOrDefault(b.Key)).ThenBy(b => b.Name, StringComparer.Ordinal)
                .Select(b => new BooggRow(b.Name, b.Bucket, Shares.GetValueOrDefault(b.Key), b.Bot)).ToArray()
            : [];
        return new BooggView(Number, (int)Phase, Phase == BooggPhase.Betting ? 0 : LitAt, done ? DoneAt : 0, Bets.Count, Pool,
            mine?.Bucket ?? 0, SechselaeutenRules.Stake, Minutes, done && mine != null ? Shares.GetValueOrDefault(key) : 0, msg, board);
    }
}
