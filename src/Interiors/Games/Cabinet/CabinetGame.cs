namespace UnitSport.Interiors.Games.Cabinet;

/// <summary>
/// An arcade cabinet (#1326), pure, one player: what the server keeps of it. A Batzen starts a game (<see cref="ActCoin"/>);
/// the player's own client runs the small game (<see cref="MiniGame"/>) and draws it into the cabinet's screen, and at
/// its end sends the score (<see cref="ActEnd"/>): the server keeps the machine's five best under the players' names and
/// pays tickets by the score. The score is the client's word, as a shop's cash is. Watchers see the attract screen with
/// who is playing, then the score.
/// </summary>
public sealed class CabinetGame : TableGame
{
    public const int ActCoin = 1, ActEnd = 2;
    /// <summary>The most a game takes before the server stops waiting for its score [s].</summary>
    public const double Longest = 600;

    public override GameKind Kind => GameKind.Cabinet;
    public override int MaxSeats => 1;
    public override bool Bots => false;

    public MiniGame.Kind Plays;
    public bool Playing;
    public double StartedAt;
    public int Seed, LastScore = -1;
    public string Call = "";
    /// <summary>The machine's best five, best first.</summary>
    public readonly List<(string Name, int Score)> Highs = new();

    public CabinetGame(MiniGame.Kind plays)
    {
        Init();
        Plays = plays;
    }

    /// <summary>Tickets for a score: a score's worth by the game (its scores run differently), at least one, at most 60.</summary>
    public static int Tickets(MiniGame.Kind kind, int score) => Math.Clamp(score / (kind switch { MiniGame.Kind.Racer => 250, MiniGame.Kind.Rhythm => 1500, _ => 100 }), 1, 60);

    public override bool Busy(double now) => Playing && now - StartedAt < Longest;

    public override string? Act(int seat, int action, int[] args, double now, Random rng)
    {
        switch (action)
        {
            case ActCoin:
                if (Busy(now)) return "A game is on.";
                Playing = true;
                StartedAt = now;
                Seed = rng.Next(1, int.MaxValue);
                Call = $"{NameAt(seat)} is playing";
                return null;
            case ActEnd when args.Length >= 1:
            {
                if (!Playing) return "No game is on.";
                Playing = false;
                int score = Math.Clamp(args[0], 0, 9_999_999);
                LastScore = score;
                string name = NameAt(seat);
                int rank = Offer(name, score);
                int tickets = Tickets(Plays, score);
                Pay(seat, tickets, MiniGame.TitleOf(Plays).ToLowerInvariant());
                Call = rank >= 0 ? $"{name}: {score}, high score #{rank + 1}!" : $"{name}: {score}";
                return null;
            }
        }
        return "?";
    }

    /// <summary>A score offered to the machine's table: its rank, or −1.</summary>
    public int Offer(string name, int score)
    {
        int at = Highs.FindIndex(h => score > h.Score);
        if (at < 0) at = Highs.Count;
        if (at >= 5) return -1;
        Highs.Insert(at, (name, score));
        if (Highs.Count > 5) Highs.RemoveAt(5);
        return at;
    }

    protected override void OnSeats()
    {
        // the player walked off mid-game: the game is lost
        if (Playing && Seats[0] == 0) { Playing = false; Call = "Game abandoned"; }
    }

    public override string Summary(Func<long, string> name)
    {
        string title = MiniGame.TitleOf(Plays);
        if (Playing) return $"{title}\n{Call}";
        return LastScore >= 0 ? $"{title}: a Batzen a game\n{Call}" : $"{title}: a Batzen a game";
    }

    protected override void WriteState(BinaryWriter w)
    {
        w.Write((byte)Plays);
        w.Write(Playing);
        w.Write(StartedAt);
        w.Write(Seed);
        w.Write(LastScore);
        w.Write(Call);
        w.Write((byte)Highs.Count);
        foreach (var (n, s) in Highs) { w.Write(n); w.Write(s); }
    }

    protected override void ReadState(BinaryReader r)
    {
        Plays = (MiniGame.Kind)r.ReadByte();
        Playing = r.ReadBoolean();
        StartedAt = r.ReadDouble();
        Seed = r.ReadInt32();
        LastScore = r.ReadInt32();
        Call = r.ReadString();
        Highs.Clear();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++) Highs.Add((r.ReadString(), r.ReadInt32()));
    }
}
