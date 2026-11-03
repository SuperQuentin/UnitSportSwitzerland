using Godot;

namespace UnitSport.Interiors.Games.Darts;

/// <summary>
/// 501, double out, on a venue's dartboard (#1324), pure. Up to four players in turn, three darts a visit; a visit
/// that takes the score below zero, to one, or to zero not on a double is bust: the score goes back to where it
/// began. A dart is an aim on the board (millimetres from the bull) that the server scatters a little (the hand's
/// unsteadiness, <see cref="HandSpread"/>) before scoring where it lands. Alone, it is practice from 501.
/// </summary>
public sealed class DartsGame : TableGame
{
    public const int ActThrow = 1;
    public const int Start = 501, Darts = 3;
    /// <summary>How far a human's dart lands from where it was aimed, one sigma [mm]: the reticle's sway is the rest of the skill.</summary>
    public const float HandSpread = 5f;
    /// <summary>A dart's flight [s], and the darts left in the board after a visit before they are pulled [s].</summary>
    public const double Flight = 0.4, Pull = 1.6;
    /// <summary>A bot throws this long after the board is free [s].</summary>
    public const double BotThink = 1.1;

    public override GameKind Kind => GameKind.Darts;
    public override int MaxSeats => 4;

    public readonly int[] Scores = { Start, Start, Start, Start };
    /// <summary>The score this visit began at (a bust goes back to it).</summary>
    public int VisitFrom = Start;
    /// <summary>This visit's darts in the board: where each landed and what it scored.</summary>
    public readonly List<(Vector2 At, DartBoard.Hit Hit)> Board = new();
    public int Winner = -1;
    public int ThrowSeq;
    public double ThrowAt, FreeAt;
    public string Call = "";

    public DartsGame() => Init();

    public override bool Busy(double now) => now < FreeAt;

    protected override void OnSeats()
    {
        if (Seats[Up] == 0) { Up = NextSeated(Up); VisitFrom = Scores[Up]; Board.Clear(); }
    }

    public override string? Act(int seat, int action, int[] args, double now, Random rng)
    {
        if (action != ActThrow || args.Length < 2) return "?";
        if (Busy(now)) return "Wait for the darts.";
        if (Winner >= 0)
        {
            // a throw after the leg's end starts the next; the loser of the last throws first
            for (int i = 0; i < Scores.Length; i++) Scores[i] = Start;
            Up = NextSeated(Winner);
            Winner = -1;
            Board.Clear();
            VisitFrom = Start;
            Call = "Game on";
            return null;
        }
        if (seat != Up) return "Not your turn.";
        // a new visit: last visit's darts are out of the board
        if (Board.Count >= Darts) Board.Clear();
        if (Board.Count == 0) VisitFrom = Scores[seat];
        var aim = new Vector2(args[0] / 10f, args[1] / 10f);
        var at = aim + Gauss(rng) * (IsBot(Seats[seat]) ? 0 : HandSpread);
        var hit = DartBoard.Score(at);
        Board.Add((at, hit));
        ThrowSeq++;
        ThrowAt = now;
        FreeAt = now + Flight;
        string who = NameAt(seat);
        int left = Scores[seat] - hit.Score;
        if (left < 0 || left == 1 || left == 0 && !hit.Double)
        {
            Scores[seat] = VisitFrom;
            Call = $"{who}: {hit.Name}, bust";
            EndVisit(seat, now);
            return null;
        }
        Scores[seat] = left;
        if (left == 0)
        {
            Winner = seat;
            Call = $"{who} checks out on {hit.Name}";
            FreeAt = now + Pull;
            return null;
        }
        int visit = VisitFrom - left;
        Call = Board.Count < Darts ? $"{who}: {hit.Name}" : visit == 180 ? $"{who}: one hundred and eighty!" : $"{who}: {visit}";
        if (Board.Count >= Darts) EndVisit(seat, now);
        return null;
    }

    private void EndVisit(int seat, double now)
    {
        FreeAt = now + Pull;
        // a bust leaves the darts in the board for the pull too
        while (Board.Count < Darts) Board.Add((new Vector2(9999, 9999), new DartBoard.Hit(0, 0)));
        Up = NextSeated(seat);
    }

    /// <summary>Darts that are in the board (not the placeholders a bust leaves).</summary>
    public IEnumerable<(Vector2 At, DartBoard.Hit Hit)> InBoard() => Board.Where(d => d.At.X < 9000);

    private static Vector2 Gauss(Random rng)
    {
        double u = 1 - rng.NextDouble(), v = rng.NextDouble();
        double r = Math.Sqrt(-2 * Math.Log(u));
        return new Vector2((float)(r * Math.Cos(Math.Tau * v)), (float)(r * Math.Sin(Math.Tau * v)));
    }

    public override string Summary(Func<long, string> name)
    {
        if (Seated == 0) return "501, double out";
        var parts = new List<string>();
        for (int i = 0; i < Seats.Length; i++)
            if (Seats[i] != 0) parts.Add($"{(i == Up && Winner < 0 ? "▸ " : "")}{name(Seats[i])} {Scores[i]}");
        string line = string.Join("   ", parts);
        if (Winner >= 0) return $"{line}\n{Call}. Throw to play again.";
        string hint = Checkout(Scores[Up]) is { } c ? $"   (out: {c})" : "";
        return $"{line}{hint}\n{Call}";
    }

    // ---- a bot at the oche -----------------------------------------------------------------------

    /// <summary>A bot's sway, one sigma [mm]: a pub player's.</summary>
    public const float BotSpread = 17f;

    public override (int Action, int[] Args)? Bot(int seat, double now, Random rng)
    {
        if (now < FreeAt + BotThink) return null;
        if (Winner >= 0) return (ActThrow, new[] { 0, 0 });
        if (seat != Up) return null;
        var aim = Target(Scores[seat]) + Gauss(rng) * BotSpread;
        return (ActThrow, new[] { (int)(aim.X * 10), (int)(aim.Y * 10) });
    }

    /// <summary>Where a player aims from <paramref name="score"/>: the treble 20 while far, else the double that finishes or a single that leaves one.</summary>
    public static Vector2 Target(int score)
    {
        if (score == 50) return DartBoard.Aim(25, 2);
        if (score <= 40 && score % 2 == 0) return DartBoard.Aim(score / 2, 2);
        if (score <= 60)
        {
            // a single that leaves a good double (32, 40, 16, then any even)
            foreach (int leave in new[] { 32, 40, 16, 24, 20, 36, 8, 12, 28, 4, 2 })
                if (score - leave is >= 1 and <= 20) return DartBoard.Aim(score - leave, 1);
            return DartBoard.Aim(Math.Min(20, score - 2), 1);
        }
        if (score <= 100)
            for (int t = 20; t >= 10; t--)
            {
                int leave = score - 3 * t;
                if (leave is >= 2 and <= 40 && leave % 2 == 0) return DartBoard.Aim(t, 3);
            }
        return DartBoard.Aim(20, 3);
    }

    /// <summary>A checkout the HUD suggests (up to three darts), or null.</summary>
    public static string? Checkout(int score)
    {
        if (score > 170 || score < 2) return null;
        if (score == 50) return "bull";
        if (score <= 40 && score % 2 == 0) return $"D{score / 2}";
        static IEnumerable<(string, int)> Throws()
        {
            for (int v = 20; v >= 1; v--)
            {
                yield return ($"T{v}", 3 * v);
                yield return ($"{v}", v);
            }
            yield return ("bull", 50);
            yield return ("25", 25);
        }
        foreach (var (a, sa) in Throws())
        {
            int rest = score - sa;
            if (rest is >= 2 and <= 40 && rest % 2 == 0) return $"{a} D{rest / 2}";
            if (rest == 50) return $"{a} bull";
        }
        foreach (var (a, sa) in Throws())
            foreach (var (b, sb) in Throws())
            {
                int rest = score - sa - sb;
                if (rest is >= 2 and <= 40 && rest % 2 == 0) return $"{a} {b} D{rest / 2}";
                if (rest == 50) return $"{a} {b} bull";
            }
        return null;
    }

    // ---- the wire ------------------------------------------------------------------------------

    protected override void WriteState(BinaryWriter w)
    {
        foreach (int s in Scores) w.Write((short)s);
        w.Write((short)VisitFrom);
        w.Write((sbyte)Winner);
        w.Write(ThrowSeq);
        w.Write(ThrowAt);
        w.Write(FreeAt);
        w.Write(Call);
        w.Write((byte)Board.Count);
        foreach (var (at, hit) in Board)
        {
            w.Write(at.X);
            w.Write(at.Y);
            w.Write((byte)hit.Value);
            w.Write((byte)hit.Multiplier);
        }
    }

    protected override void ReadState(BinaryReader r)
    {
        for (int i = 0; i < Scores.Length; i++) Scores[i] = r.ReadInt16();
        VisitFrom = r.ReadInt16();
        Winner = r.ReadSByte();
        ThrowSeq = r.ReadInt32();
        ThrowAt = r.ReadDouble();
        FreeAt = r.ReadDouble();
        Call = r.ReadString();
        Board.Clear();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            var at = new Vector2(r.ReadSingle(), r.ReadSingle());
            Board.Add((at, new DartBoard.Hit(r.ReadByte(), r.ReadByte())));
        }
    }
}
