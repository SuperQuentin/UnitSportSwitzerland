using Godot;

namespace UnitSport.Interiors.Games.AirHockey;

/// <summary>
/// Air hockey for two (#1326), pure and live: the server steps it at 120 Hz from both players' mallets. The field in the
/// piece's frame (x across, z along, metres from its centre): seat 0 defends the goal at −z, seat 1 the one at +z. The
/// puck glides on air (a little drag), keeps 0.9 of its speed off the rails, and takes the mallet's own speed when hit;
/// each mallet stays in its own half. A goal serves the puck, still, in the half of who conceded. First to <see cref="Win"/>.
/// </summary>
public sealed class AirHockeyGame : TableGame
{
    public const float Puck = 0.031f, Mallet = 0.048f, GoalHalf = 0.14f, MalletSpeed = 3.2f, MaxPuck = 7f;
    public const int Win = 7;
    /// <summary>A coin in the slot (a Batzen the client paid; a bot pays none): a game starts when none is on.</summary>
    public const int ActCoin = 1;
    /// <summary>Tickets a game pays each player: so many a goal, so many more to the winner.</summary>
    public const int TicketsPerGoal = 2, TicketsToWin = 8;

    public override GameKind Kind => GameKind.AirHockey;
    public override int MaxSeats => 2;
    public override bool Live => true;

    /// <summary>Half the field's width (x) and length (z) inside the rails.</summary>
    public readonly float Hx, Hz;
    public Vector2 PuckAt, PuckV;
    public readonly Vector2[] MalletAt = new Vector2[2], MalletV = new Vector2[2];
    public readonly int[] Goals = new int[2];
    public int Winner = -1;
    public bool InPlay;
    public double ServeAt;
    public string Call = "";
    private readonly Vector2[] _input = new Vector2[2];
    private int _serveTo;

    public AirHockeyGame(float hx = 0.52f, float hz = 0.97f)
    {
        Init();
        Hx = hx;
        Hz = hz;
        Home();
    }

    /// <summary>The field of an air hockey piece <paramref name="w"/> by <paramref name="d"/>: its rails 8 cm.</summary>
    public static (float Hx, float Hz) Field(float w, float d) => (w / 2 - 0.08f, d / 2 - 0.08f);

    private void Home()
    {
        MalletAt[0] = new Vector2(0, -Hz + 0.2f);
        MalletAt[1] = new Vector2(0, Hz - 0.2f);
    }

    public override bool Awake => AnyHuman || InPlay;

    /// <summary>A game is on: a coin went in and nobody has won it yet.</summary>
    public bool Running => ServeAt != 0 && Winner < 0;

    /// <summary>A seat's mallet velocity for the wire: each axis −1 to 1 of <see cref="MalletSpeed"/>, x across and z along the table.</summary>
    public static byte[] Pack(Vector2 v) =>
        new[] { (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(v.X * 127), -127, 127), (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(v.Y * 127), -127, 127) };

    public override void Input(int seat, byte[] input)
    {
        if (input.Length >= 2 && seat < 2) _input[seat] = new Vector2((sbyte)input[0], (sbyte)input[1]) / 127f;
    }

    public override string? Act(int seat, int action, int[] args, double now, Random rng)
    {
        if (action != ActCoin) return "?";
        if (Running) return "A game is on.";
        Goals[0] = Goals[1] = 0;
        Winner = -1;
        InPlay = false;
        ServeAt = now + 1;
        Call = "Game on";
        return null;
    }

    public override bool Step(double dt, double now)
    {
        float t = (float)dt;
        bool changed = false;
        if (!InPlay)
        {
            if (!Running || Seated == 0) return false;
            if (now >= ServeAt)
            {
                InPlay = true;
                PuckAt = new Vector2(0, _serveTo == 0 ? -Hz * 0.4f : Hz * 0.4f);
                PuckV = Vector2.Zero;
                Call = Goals[0] + Goals[1] == 0 ? "Face-off" : Call;
                changed = true;
            }
        }
        for (int s = 0; s < 2; s++) MoveMallet(s, t);
        if (!InPlay) return changed;
        PuckV *= 1 - 0.15f * t;
        PuckAt += PuckV * t;
        // the rails: the sides, and the ends beside the goals
        if (Mathf.Abs(PuckAt.X) > Hx - Puck)
        {
            PuckAt.X = Mathf.Sign(PuckAt.X) * (Hx - Puck);
            if (PuckV.X * PuckAt.X > 0) PuckV.X = -PuckV.X * 0.9f;
        }
        if (Mathf.Abs(PuckAt.Y) > Hz - Puck && Mathf.Abs(PuckAt.X) > GoalHalf - Puck * 0.3f)
        {
            PuckAt.Y = Mathf.Sign(PuckAt.Y) * (Hz - Puck);
            if (PuckV.Y * PuckAt.Y > 0) PuckV.Y = -PuckV.Y * 0.9f;
        }
        for (int s = 0; s < 2; s++) Hit(s);
        if (Mathf.Abs(PuckAt.Y) > Hz + Puck)
        {
            int scorer = PuckAt.Y > 0 ? 0 : 1;
            Goals[scorer]++;
            InPlay = false;
            _serveTo = 1 - scorer;
            Call = $"Goal! {Goals[0]} : {Goals[1]}";
            if (Goals[scorer] >= Win)
            {
                Winner = scorer;
                Call = $"{NameAt(scorer)} wins {Goals[0]} : {Goals[1]}";
                for (int s = 0; s < 2; s++) Pay(s, Goals[s] * TicketsPerGoal + (s == scorer ? TicketsToWin : 0), "air hockey");
            }
            else ServeAt = now + 1.5;
            return true;
        }
        return changed;
    }

    private void MoveMallet(int s, float t)
    {
        var v = Seats[s] != 0 ? _input[s] : Vector2.Zero;
        if (v.Length() > 1) v = v.Normalized();
        var was = MalletAt[s];
        var at = was + v * MalletSpeed * t;
        float side = s == 0 ? -1 : 1;
        // its own half, inside the rails
        at.X = Mathf.Clamp(at.X, -Hx + Mallet, Hx - Mallet);
        at.Y = side < 0 ? Mathf.Clamp(at.Y, -Hz + Mallet, -Mallet) : Mathf.Clamp(at.Y, Mallet, Hz - Mallet);
        MalletAt[s] = at;
        MalletV[s] = (at - was) / Mathf.Max(t, 1e-4f);
    }

    private void Hit(int s)
    {
        var d = PuckAt - MalletAt[s];
        float dist = d.Length();
        float reach = Puck + Mallet;
        if (dist >= reach || dist < 1e-5f) return;
        var n = d / dist;
        PuckAt = MalletAt[s] + n * (reach + 0.0005f);
        float closing = (PuckV - MalletV[s]).Dot(n);
        if (closing < 0) PuckV -= n * closing * 1.9f;
        float speed = PuckV.Length();
        if (speed > MaxPuck) PuckV *= MaxPuck / speed;
    }

    public override string Summary(Func<long, string> name)
    {
        if (Seated == 0) return "Air hockey: a Batzen a game";
        string Who(int s) => Seats[s] == 0 ? "—" : name(Seats[s]);
        string score = $"{Who(0)} {Goals[0]} : {Goals[1]} {Who(1)}";
        if (!Running) return $"{score}\n{(Winner >= 0 ? Call + ". " : "")}A Batzen for a game.";
        return $"{score}\n{Call}";
    }

    // ---- a bot at the table ---------------------------------------------------------------------

    /// <summary>How fast a bot's mallet goes at most (of the full speed), and how far off it aims [m].</summary>
    public const float BotPace = 0.8f, BotError = 0.03f;

    public override (int Action, int[] Args)? Bot(int seat, double now, Random rng)
    {
        // bots alone at the table play on; with a human, the human puts the coin in
        if (!Running) return Seats.Any(x => x > 0) ? null : (ActCoin, Array.Empty<int>());
        float side = seat == 0 ? -1 : 1;
        var goal = new Vector2(0, side * Hz);
        var me = MalletAt[seat];
        Vector2 want;
        bool mine = PuckAt.Y * side > 0;
        if (mine && PuckV.Y * side < 1.2f)
        {
            // the puck on its side, not racing at its goal: come at it from behind, towards the other goal
            var through = (PuckAt - new Vector2(0, -side * Hz)).Normalized();
            want = PuckAt + through * (Puck + Mallet) * 0.5f;
            if ((me - goal).Length() > (PuckAt - goal).Length()) want = PuckAt + through * (Puck + Mallet + 0.08f);
        }
        else
        {
            // defend: between the puck and its goal, a third of the way out
            want = goal + (PuckAt - goal) * 0.25f;
        }
        want += new Vector2((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)) * BotError;
        var dir = want - me;
        var v = dir.Length() > 0.01f ? dir.Normalized() * Mathf.Clamp(dir.Length() / 0.06f, 0, 1) * BotPace : Vector2.Zero;
        Input(seat, Pack(v));
        return null;
    }

    // ---- the wire ------------------------------------------------------------------------------

    protected override void WriteState(BinaryWriter w)
    {
        w.Write((byte)Goals[0]);
        w.Write((byte)Goals[1]);
        w.Write((sbyte)Winner);
        w.Write(InPlay);
        w.Write(ServeAt);
        w.Write(Call);
        WritePoses(w);
    }

    protected override void ReadState(BinaryReader r)
    {
        Goals[0] = r.ReadByte();
        Goals[1] = r.ReadByte();
        Winner = r.ReadSByte();
        InPlay = r.ReadBoolean();
        ServeAt = r.ReadDouble();
        Call = r.ReadString();
        ReadPoses(r);
    }

    public override void WritePoses(BinaryWriter w)
    {
        w.Write(PuckAt.X); w.Write(PuckAt.Y); w.Write(PuckV.X); w.Write(PuckV.Y);
        for (int s = 0; s < 2; s++) { w.Write(MalletAt[s].X); w.Write(MalletAt[s].Y); }
    }

    public override void ReadPoses(BinaryReader r)
    {
        PuckAt = new Vector2(r.ReadSingle(), r.ReadSingle());
        PuckV = new Vector2(r.ReadSingle(), r.ReadSingle());
        for (int s = 0; s < 2; s++) MalletAt[s] = new Vector2(r.ReadSingle(), r.ReadSingle());
    }
}
