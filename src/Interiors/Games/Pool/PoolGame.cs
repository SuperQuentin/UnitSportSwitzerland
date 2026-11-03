using Godot;

namespace UnitSport.Interiors.Games.Pool;

/// <summary>
/// Eight-ball on a venue's pool table (#1324), pure: the server resolves a shot at once (<see cref="PoolSolver"/>)
/// and applies the rules; every peer replays the shot from where the balls stood and the shot's numbers
/// (<see cref="Before"/>, <see cref="Shot"/>), ending on where the server says they rest.
///
/// <para>
/// Rules (WPA-lite): the breaker places the cue ball behind the head string; the 8 on the break re-racks. The table
/// is open until a player pots a ball of a group after the break (that group is theirs). A shot is a foul when the
/// cue ball is potted, hits nothing, hits a wrong ball first (the other group, or the 8 before its group is cleared),
/// or after the contact nothing reaches a cushion and nothing is potted: the opponent then has the cue ball in hand
/// anywhere. A legal pot of your own keeps the turn. Potting the 8 wins when your group was cleared and the shot is
/// legal, and loses otherwise. Alone at the table it is practice: the turn never passes.
/// </para>
/// </summary>
public sealed class PoolGame : TableGame
{
    public enum Phase : byte { Break, Play, Over }
    public enum Group : byte { Open, Solids, Stripes }

    public const int ActShoot = 1, ActPlace = 2;
    /// <summary>A bot shoots this long after the table is still [s].</summary>
    public const double BotThink = 1.6;

    public override GameKind Kind => GameKind.Pool;
    public override int MaxSeats => 2;

    public readonly PoolSolver Table;
    /// <summary>Where the balls stood when the last shot was taken (the replay starts there).</summary>
    public readonly PoolSolver Before;
    public Phase State = Phase.Break;
    public readonly Group[] Groups = new Group[2];
    /// <summary>The player up places the cue ball first (after a foul, and to break: behind the head string).</summary>
    public bool InHand = true;
    public int Winner = -1;
    /// <summary>The last shot: its number, when it was struck (shared clock), when the table came to rest, and its numbers.</summary>
    public int ShotSeq;
    public double ShotAt, ShotEnd;
    public (int Angle, int Power, int Follow) Shot;
    /// <summary>What the last shot did, for the HUD ("Foul: scratch", "Solids for Vreni").</summary>
    public string Call = "";
    private readonly Random _rack;

    public PoolGame(float hx, float hz, int seed = 1)
    {
        Init();
        Table = new PoolSolver(hx, hz);
        Before = new PoolSolver(hx, hz);
        _rack = new Random(seed);
        Rerack();
    }

    /// <summary>The cloth of a pool table piece <paramref name="w"/> by <paramref name="d"/>: its rails 12 cm wide.</summary>
    public static (float Hx, float Hz) Cloth(float w, float d) => (w / 2 - 0.12f, d / 2 - 0.12f);

    /// <summary>The highest power: the cue ball's speed [m/s] at 1000.</summary>
    public const float MaxSpeed = 8f;

    public static float Speed(int power) => 0.25f + MaxSpeed * Mathf.Clamp(power, 0, 1000) / 1000f;

    public void Rerack()
    {
        Table.Rack(_rack);
        Before.CopyFrom(Table);
        State = Phase.Break;
        Groups[0] = Groups[1] = Group.Open;
        InHand = true;
        Winner = -1;
        Call = "Break";
    }

    public override bool Busy(double now) => now < ShotEnd;

    /// <summary>The balls of a group (1-7 or 9-15).</summary>
    public static bool InGroup(int ball, Group g) => g == Group.Solids ? ball is >= 1 and <= 7 : g == Group.Stripes && ball is >= 9 and <= 15;

    public bool Cleared(Group g)
    {
        if (g == Group.Open) return false;
        for (int b = 1; b < PoolSolver.Balls; b++)
            if (InGroup(b, g) && Table.On[b]) return false;
        return true;
    }

    /// <summary>What seat <paramref name="seat"/> may legally hit first.</summary>
    public bool Legal(int seat, int ball)
    {
        if (ball <= 0) return false;
        var g = Groups[seat];
        if (State == Phase.Break) return true;
        if (g == Group.Open) return ball != 8;
        return Cleared(g) ? ball == 8 : InGroup(ball, g);
    }

    /// <summary>Where the cue ball may go in hand: behind the head string to break, else anywhere free.</summary>
    public bool CanPlace(Vector2 p) => Table.Free(p) && (State != Phase.Break || p.Y < -Table.Hz * 0.5f);

    protected override void OnSeats()
    {
        if (Seats[Up] == 0) Up = NextSeated(Up);
    }

    public override string? Act(int seat, int action, int[] args, double now, Random rng)
    {
        if (Busy(now)) return "Wait for the balls to stop.";
        if (State == Phase.Over)
        {
            // a shot after the game's end racks the next one; the loser breaks
            Rerack();
            if (Winner >= 0) Up = NextSeated(Winner);
            ShotEnd = now;
            return null;
        }
        if (seat != Up) return "Not your turn.";
        switch (action)
        {
            case ActPlace when args.Length >= 2:
            {
                if (!InHand) return "The cue ball is not in hand.";
                var p = new Vector2(args[0] / 1000f, args[1] / 1000f);
                if (!CanPlace(p)) return State == Phase.Break ? "Behind the head string, clear of the balls." : "Clear of the balls, on the cloth.";
                Table.P[0] = p;
                Table.On[0] = true;
                InHand = false;
                return null;
            }
            case ActShoot when args.Length >= 3:
                if (InHand) return "Place the cue ball first.";
                Take(seat, args[0], args[1], args[2], now);
                return null;
        }
        return "?";
    }

    /// <summary>The shot: run it whole, then the rules.</summary>
    private void Take(int seat, int angle, int power, int follow, double now)
    {
        Before.CopyFrom(Table);
        Shot = (angle, Mathf.Clamp(power, 0, 1000), Mathf.Clamp(follow, -1000, 1000));
        ShotSeq++;
        ShotAt = now;
        Strike(Table, Shot);
        Table.Run();
        ShotEnd = now + Table.Time + 0.4;
        Judge(seat);
    }

    /// <summary>A shot's numbers onto a solver: a peer replaying it does the same.</summary>
    public static void Strike(PoolSolver s, (int Angle, int Power, int Follow) shot) =>
        s.Strike(shot.Angle / 1000f * Mathf.Pi / 180f, Speed(shot.Power), shot.Follow / 1000f);

    private void Judge(int seat)
    {
        int first = -1;
        float firstAt = -1;
        bool rail = false;
        var potted = new List<int>();
        foreach (var e in Table.Events)
        {
            if (e.Kind == PoolSolver.EventKind.Contact && first < 0 && (e.Ball == 0 || e.Other == 0))
            {
                first = e.Ball == 0 ? e.Other : e.Ball;
                firstAt = e.Time;
            }
            else if (e.Kind == PoolSolver.EventKind.Cushion && first >= 0 && e.Time >= firstAt) rail = true;
            else if (e.Kind == PoolSolver.EventKind.Pocket) potted.Add(e.Ball);
        }
        bool breakShot = State == Phase.Break;
        var mine = Groups[seat];
        // the 8 is legal only once the group was all off before this shot
        bool clearedBefore = mine != Group.Open && ClearedBut(mine, potted);
        string? foul = potted.Contains(0) ? "scratch" : first < 0 ? "no ball hit" : !Legal(seat, first) ? $"the {first} first" : !rail && potted.Count == 0 ? "no cushion" : null;
        string who = Name(seat);
        if (potted.Contains(8))
        {
            if (breakShot)
            {
                Rerack();
                Call = "The 8 on the break: racked again";
                return;
            }
            bool win = foul == null && clearedBefore;
            Winner = win ? seat : Opponent(seat);
            State = Phase.Over;
            Call = win ? $"{who} sinks the 8 and wins" : $"The 8 too soon: {Name(Winner)} wins";
            return;
        }
        if (breakShot) State = Phase.Play;
        // the open table: the first ball of a group potted (not on the break) makes it the shooter's
        if (!breakShot && foul == null && mine == Group.Open)
            foreach (int b in potted)
                if (b != 0 && b != 8)
                {
                    Groups[seat] = b < 8 ? Group.Solids : Group.Stripes;
                    int o = Opponent(seat);
                    if (o != seat) Groups[o] = b < 8 ? Group.Stripes : Group.Solids;
                    break;
                }
        mine = Groups[seat];
        bool own = potted.Exists(b => b != 0 && (mine == Group.Open ? b != 8 : InGroup(b, mine)));
        if (foul != null)
        {
            Call = $"Foul: {foul}. Ball in hand";
            Pass(seat);
            InHand = true;
            if (!Table.On[0]) { Table.On[0] = true; Table.P[0] = Spot(); }
            return;
        }
        if (own || breakShot && potted.Count > 0)
        {
            Call = $"{who} pots {string.Join(", ", potted)}";
            return;
        }
        Call = potted.Count > 0 ? $"{who} pots {string.Join(", ", potted)}, not theirs" : $"{who} misses";
        Pass(seat);
    }

    /// <summary>The group was all off but for what this shot potted (so the 8 was the next ball).</summary>
    private bool ClearedBut(Group g, List<int> potted)
    {
        for (int b = 1; b < PoolSolver.Balls; b++)
            if (InGroup(b, g) && (Table.On[b] || potted.Contains(b))) return false;
        return true;
    }

    private void Pass(int seat) => Up = NextSeated(seat);

    private int Opponent(int seat) => NextSeated(seat);

    /// <summary>A free place for a cue ball back on the table (a foul's ball in hand starts there).</summary>
    private Vector2 Spot()
    {
        for (int k = 0; k < 40; k++)
        {
            var p = new Vector2(0, -Table.Hz * 0.5f + k * 0.03f * (k % 2 == 0 ? 1 : -1));
            if (Table.Free(p)) return p;
        }
        return Vector2.Zero;
    }

    private string Name(int seat) => NameAt(seat);

    public override string Summary(Func<long, string> name)
    {
        string Who(int s) => Seats[s] == 0 ? "—" : name(Seats[s]);
        string G(int s) => Groups[s] switch { Group.Solids => " (solids)", Group.Stripes => " (stripes)", _ => "" };
        if (Seated == 0) return "Eight-ball";
        if (State == Phase.Over) return $"{Call}. Shoot to rack again.";
        string turn = Seated > 1 ? $"   {Who(Up)} to shoot{(InHand ? ", ball in hand" : "")}" : InHand ? "   ball in hand" : "";
        return $"{Who(0)}{G(0)} v {Who(1)}{G(1)}{turn}\n{Call}";
    }

    // ---- a bot at the table ----------------------------------------------------------------------

    public override (int Action, int[] Args)? Bot(int seat, double now, Random rng)
    {
        if (seat != Up || now < ShotEnd + BotThink) return null;
        if (State == Phase.Over) return (ActShoot, new[] { 0, 0, 0 });
        if (InHand)
        {
            var at = BotPlace(seat, rng);
            return (ActPlace, new[] { (int)(at.X * 1000), (int)(at.Y * 1000) });
        }
        var (angle, power, follow) = BotShot(seat, rng);
        return (ActShoot, new[] { angle, power, follow });
    }

    /// <summary>The best pot it sees (the easiest cut on a clear line), or a safe hit on its nearest ball; aimed with a skill spread.</summary>
    public (int Angle, int Power, int Follow) BotShot(int seat, Random rng, float skill = 0.6f)
    {
        var cue = Table.P[0];
        if (State == Phase.Break)
            return (AngleTo(cue, new Vector2((float)(rng.NextDouble() - 0.5) * 0.02f, Table.Hz * 0.5f)), 900 + rng.Next(100), 0);
        float bestScore = -1;
        (Vector2 Ghost, float Distance) best = default;
        for (int b = 1; b < PoolSolver.Balls; b++)
        {
            if (!Table.On[b] || !Legal(seat, b)) continue;
            foreach (var (pocket, _) in Table.Pockets())
            {
                var toPocket = pocket - Table.P[b];
                var ghost = Table.P[b] - toPocket.Normalized() * 2 * PoolSolver.R;
                var aim = ghost - cue;
                float cut = aim.Normalized().Dot(toPocket.Normalized());
                if (cut < 0.3f || !Clear(cue, ghost, b) || !Clear(Table.P[b], pocket, b)) continue;
                float d = aim.Length() + toPocket.Length();
                float score = cut * cut / (0.4f + d);
                if (score > bestScore) { bestScore = score; best = (ghost, d); }
            }
        }
        float spread = (1.2f - skill) * 2.2f;
        float noise = (float)(rng.NextDouble() * 2 - 1) * spread;
        if (bestScore > 0)
        {
            int power = Mathf.Clamp((int)((0.6f + best.Distance * 1.1f) / MaxSpeed * 1000), 120, 800);
            return (AngleTo(cue, best.Ghost) + (int)(noise * 1000), power, rng.Next(-300, 300));
        }
        // nothing to pot: hit the nearest of its own full, firmly enough to reach a cushion
        int target = -1;
        float near = float.MaxValue;
        for (int b = 1; b < PoolSolver.Balls; b++)
            if (Table.On[b] && Legal(seat, b) && cue.DistanceTo(Table.P[b]) < near) { near = cue.DistanceTo(Table.P[b]); target = b; }
        if (target < 0) target = 8;
        return (AngleTo(cue, Table.P[target]) + (int)(noise * 1000), 450, 0);
    }

    /// <summary>In hand: behind the best pot it sees, else behind the head string's middle.</summary>
    public Vector2 BotPlace(int seat, Random rng)
    {
        if (State == Phase.Break) return new Vector2(((float)rng.NextDouble() - 0.5f) * 0.3f, -Table.Hz * 0.55f);
        for (int b = 1; b < PoolSolver.Balls; b++)
        {
            if (!Table.On[b] || !Legal(seat, b)) continue;
            foreach (var (pocket, _) in Table.Pockets())
            {
                var dir = (pocket - Table.P[b]).Normalized();
                var at = Table.P[b] - dir * 0.3f;
                if (CanPlace(at) && Clear(Table.P[b], pocket, b) && Clear(at, Table.P[b] - dir * 2 * PoolSolver.R, b)) return at;
            }
        }
        for (int k = 0; k < 60; k++)
        {
            var at = new Vector2(((float)rng.NextDouble() - 0.5f) * Table.Hx, ((float)rng.NextDouble() - 0.5f) * Table.Hz * 1.6f);
            if (CanPlace(at)) return at;
        }
        return Vector2.Zero;
    }

    /// <summary>No ball but the cue ball and <paramref name="skip"/> within two radii of the line from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private bool Clear(Vector2 a, Vector2 b, int skip)
    {
        var ab = b - a;
        float len = ab.Length();
        if (len < 1e-4f) return true;
        var dir = ab / len;
        for (int i = 1; i < PoolSolver.Balls; i++)
        {
            if (i == skip || !Table.On[i]) continue;
            var ap = Table.P[i] - a;
            float t = Mathf.Clamp(ap.Dot(dir), 0, len);
            if ((a + dir * t).DistanceTo(Table.P[i]) < 2 * PoolSolver.R) return false;
        }
        return true;
    }

    /// <summary>The shot's angle towards <paramref name="to"/> in thousandths of a degree (0 = +z, towards +x).</summary>
    public static int AngleTo(Vector2 from, Vector2 to)
    {
        var d = to - from;
        float deg = Mathf.RadToDeg(Mathf.Atan2(d.X, d.Y));
        return (int)Math.Round((deg + 360) % 360 * 1000);
    }

    // ---- the wire ------------------------------------------------------------------------------

    protected override void WriteState(BinaryWriter w)
    {
        w.Write((byte)State);
        w.Write((byte)Groups[0]);
        w.Write((byte)Groups[1]);
        w.Write(InHand);
        w.Write((sbyte)Winner);
        w.Write(ShotSeq);
        w.Write(ShotAt);
        w.Write(ShotEnd);
        w.Write(Shot.Angle);
        w.Write((short)Shot.Power);
        w.Write((short)Shot.Follow);
        w.Write(Call);
        WriteBalls(w, Table);
        WriteBalls(w, Before);
    }

    protected override void ReadState(BinaryReader r)
    {
        State = (Phase)r.ReadByte();
        Groups[0] = (Group)r.ReadByte();
        Groups[1] = (Group)r.ReadByte();
        InHand = r.ReadBoolean();
        Winner = r.ReadSByte();
        ShotSeq = r.ReadInt32();
        ShotAt = r.ReadDouble();
        ShotEnd = r.ReadDouble();
        Shot = (r.ReadInt32(), r.ReadInt16(), r.ReadInt16());
        Call = r.ReadString();
        ReadBalls(r, Table);
        ReadBalls(r, Before);
    }

    private static void WriteBalls(BinaryWriter w, PoolSolver s)
    {
        int on = 0;
        for (int i = 0; i < PoolSolver.Balls; i++) if (s.On[i]) on |= 1 << i;
        w.Write((ushort)on);
        for (int i = 0; i < PoolSolver.Balls; i++)
        {
            w.Write(s.P[i].X);
            w.Write(s.P[i].Y);
        }
    }

    private static void ReadBalls(BinaryReader r, PoolSolver s)
    {
        int on = r.ReadUInt16();
        for (int i = 0; i < PoolSolver.Balls; i++)
        {
            s.On[i] = (on & (1 << i)) != 0;
            s.P[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        }
    }
}
