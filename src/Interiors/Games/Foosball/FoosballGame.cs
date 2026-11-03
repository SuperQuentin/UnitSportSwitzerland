using Godot;

namespace UnitSport.Interiors.Games.Foosball;

/// <summary>
/// Table football (#1324), pure and live: the server steps it at 120 Hz from its players' controls. The pitch in the
/// piece's frame (x along the table, z across, metres from its centre): red defends the goal at −x and attacks +x,
/// blue the other way. Eight rods across it (red's goalie, defence, blue's attack, red's midfield, blue's
/// midfield, red's attack, blue's defence, blue's goalie: 1, 2, 3, 5, 5, 3, 2, 1 figures), red's handles at −z where
/// red's players stand, blue's at +z.
///
/// <para>
/// A rod slides (its figures' z) and turns (θ: 0 hanging straight down, positive towards the rod's own attack). A
/// figure blocks the ball while it hangs within <see cref="Lifted"/> of straight down; turning fast it kicks it.
/// Two players: each runs their team's four rods; four: the back seats (0 red, 1 blue) the goalie and defence, the
/// front seats (2 red, 3 blue) the midfield and attack. A player's stick slides all their rods at once, Jump kicks
/// (a fast turn and back), Sprint held lifts them (the ball passes under). First to <see cref="Win"/>.
/// </para>
/// </summary>
public sealed class FoosballGame : TableGame
{
    public const float HalfX = 0.555f, HalfZ = 0.315f, Ball = 0.017f, GoalHalf = 0.095f;
    public const float FootDepth = 0.075f, FootHalfX = 0.012f, FootHalfZ = 0.016f, Lifted = 0.55f;
    public const float SlideSpeed = 1.4f, KickSpeed = 28f, ReturnSpeed = 9f, LiftSpeed = 30f;
    /// <summary>A ball this slow this long out of every figure's reach is dead: served again [m/s, s].</summary>
    public const float DeadSpeed = 0.04f, DeadTime = 2.5f;
    public const int Win = 10;
    public const int ActRestart = 1;
    public static readonly float[] RodX = { -0.49f, -0.35f, -0.21f, -0.07f, 0.07f, 0.21f, 0.35f, 0.49f };
    public static readonly int[] Figures = { 1, 2, 3, 5, 5, 3, 2, 1 };
    /// <summary>The team of each rod: 0 red, 1 blue.</summary>
    public static readonly int[] Team = { 0, 0, 1, 0, 1, 0, 1, 1 };
    /// <summary>Whether a rod is its team's back pair (goalie, defence) or front (midfield, attack).</summary>
    public static readonly bool[] Back = { true, true, false, false, false, false, true, true };

    public override GameKind Kind => GameKind.Foosball;
    public override int MaxSeats => 4;
    public override bool Live => true;

    public readonly float[] Slide = new float[8], Angle = new float[8], Spin = new float[8];
    public Vector2 BallAt, BallV;
    public readonly int[] Goals = new int[2];
    public int Winner = -1;
    /// <summary>The ball is in play (else waiting for the serve, <see cref="ServeAt"/>).</summary>
    public bool InPlay;
    public double ServeAt;
    public string Call = "";
    private readonly (sbyte Slide, byte Flags)[] _input = new (sbyte, byte)[4];
    private readonly bool[] _kicking = new bool[8];
    private int _serveTo;
    private float _still;
    private readonly Random _rng = new(1324);

    public FoosballGame() => Init();

    /// <summary>A figure's z on its rod at slide 0.</summary>
    public static float FigureZ(int rod, int k)
    {
        int n = Figures[rod];
        if (n == 1) return 0;
        float gap = n switch { 2 => 0.24f, 3 => 0.2f, _ => 0.12f };
        return (k - (n - 1) / 2f) * gap;
    }

    /// <summary>How far a rod slides each way: its outer figures stay off the side walls.</summary>
    public static float SlideRange(int rod) => Figures[rod] == 1 ? GoalHalf + 0.03f : HalfZ - FigureZ(rod, Figures[rod] - 1) - FootHalfZ - 0.004f;

    /// <summary>Which seat runs rod <paramref name="rod"/> now: a team's front seat if someone holds it, else its back seat.</summary>
    public int SeatOfRod(int rod)
    {
        int team = Team[rod];
        int front = 2 + team, back = team;
        if (!Back[rod] && Seats[front] != 0) return front;
        return back;
    }

    public static int TeamOf(int seat) => seat % 2;

    public override string? Sit(long peer, int seat)
    {
        Seats[seat] = peer;
        if (!InPlay && Winner < 0 && ServeAt == 0) ServeAt = -1;
        return null;
    }

    public override bool Awake => AnyHuman || InPlay;

    public override void Input(int seat, byte[] input)
    {
        if (input.Length >= 2 && seat < _input.Length) _input[seat] = ((sbyte)input[0], input[1]);
    }

    /// <summary>A seat's controls packed for the wire: the slide (−1 to 1, +z), kick, lift.</summary>
    public static byte[] Pack(float slide, bool kick, bool lift) =>
        new[] { (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(slide * 127), -127, 127), (byte)((kick ? 1 : 0) | (lift ? 2 : 0)) };

    public override string? Act(int seat, int action, int[] args, double now, Random rng)
    {
        if (action != ActRestart) return "?";
        if (Winner < 0 && (Goals[0] > 0 || Goals[1] > 0)) return "The game is on.";
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
            if (Winner >= 0 || Seated == 0) return false;
            if (ServeAt < 0) ServeAt = now + 1.5;
            if (now >= ServeAt) { Serve(); changed = true; }
        }
        for (int rod = 0; rod < 8; rod++) Rod(rod, t);
        if (!InPlay) return changed;
        MoveBall(t);
        // a dead ball (stopped where no figure reaches) is served again
        _still = BallV.Length() < DeadSpeed ? _still + t : 0;
        if (_still > DeadTime)
        {
            _still = 0;
            _serveTo = BallAt.X < 0 ? 0 : 1;
            InPlay = false;
            ServeAt = now + 0.8;
            Call = "Dead ball: served again";
            return true;
        }
        // goals
        if (Mathf.Abs(BallAt.X) > HalfX && Mathf.Abs(BallAt.Y) < GoalHalf)
        {
            int scorer = BallAt.X > 0 ? 0 : 1;
            Goals[scorer]++;
            InPlay = false;
            _serveTo = 1 - scorer;
            Call = $"Goal for {(scorer == 0 ? "red" : "blue")}! {Goals[0]} : {Goals[1]}";
            if (Goals[scorer] >= Win) { Winner = scorer; Call = $"{(scorer == 0 ? "Red" : "Blue")} wins {Goals[0]} : {Goals[1]}"; }
            else ServeAt = now + 2;
            return true;
        }
        return changed;
    }

    /// <summary>The ball dropped in at the middle from the side, rolling a little towards the team that conceded.</summary>
    private void Serve()
    {
        InPlay = true;
        BallAt = new Vector2(0, -HalfZ + 0.04f);
        float towards = _serveTo == 0 ? -1 : 1;
        BallV = new Vector2(towards * (0.05f + 0.1f * (float)_rng.NextDouble()), 0.35f + 0.25f * (float)_rng.NextDouble());
        Call = Goals[0] + Goals[1] == 0 ? "Kick-off" : Call;
    }

    private void Rod(int rod, float t)
    {
        int seat = SeatOfRod(rod);
        var (slide, flags) = Seats[seat] != 0 ? _input[seat] : ((sbyte)0, (byte)0);
        // red faces +z from the −z side: its stick's "right" is the table's +x; the slide is the table's z for both
        float range = SlideRange(rod);
        Slide[rod] = Mathf.Clamp(Slide[rod] + slide / 127f * SlideSpeed * t, -range, range);
        float dir = Team[rod] == 0 ? 1 : -1;
        bool kick = (flags & 1) != 0, lift = (flags & 2) != 0;
        float was = Angle[rod];
        if (kick && !_kicking[rod] && Mathf.Abs(Angle[rod]) < 0.3f) _kicking[rod] = true;
        if (_kicking[rod])
        {
            // the swing through: from behind the ball to well past straight down, then back
            Angle[rod] += dir * KickSpeed * t;
            if (Mathf.Abs(Angle[rod]) > 1.3f) _kicking[rod] = false;
        }
        else
        {
            float target = lift ? -dir * 1.45f : 0;
            Angle[rod] = Mathf.MoveToward(Angle[rod], target, (lift ? LiftSpeed : ReturnSpeed) * t);
        }
        Spin[rod] = (Angle[rod] - was) / Mathf.Max(t, 1e-4f);
    }

    private void MoveBall(float t)
    {
        // the pitch's slight dish brings a dead ball back into play: a pull towards the long axis
        BallV *= 1 - 0.35f * t;
        BallV.Y -= BallAt.Y * 0.25f * t;
        var from = BallAt;
        BallAt += BallV * t;
        // the side walls and the ends beside the goals
        if (Mathf.Abs(BallAt.Y) > HalfZ - Ball)
        {
            BallAt.Y = Mathf.Sign(BallAt.Y) * (HalfZ - Ball);
            if (BallV.Y * BallAt.Y > 0) BallV.Y = -BallV.Y * 0.75f;
        }
        if (Mathf.Abs(BallAt.X) > HalfX - Ball && Mathf.Abs(BallAt.Y) > GoalHalf - Ball * 0.5f)
        {
            BallAt.X = Mathf.Sign(BallAt.X) * (HalfX - Ball);
            if (BallV.X * BallAt.X > 0) BallV.X = -BallV.X * 0.75f;
        }
        for (int rod = 0; rod < 8; rod++)
        {
            if (Mathf.Abs(Angle[rod]) > Lifted) continue;
            float x = RodX[rod];
            // the foot's reach along x as it turns: sin θ times its depth
            float footX = x + Mathf.Sin(Angle[rod]) * FootDepth;
            if (Mathf.Abs(BallAt.X - footX) > FootHalfX + Ball && Mathf.Abs(from.X - footX) > FootHalfX + Ball
                && Mathf.Sign(BallAt.X - footX) == Mathf.Sign(from.X - footX)) continue;
            for (int k = 0; k < Figures[rod]; k++)
            {
                float z = FigureZ(rod, k) + Slide[rod];
                if (Mathf.Abs(BallAt.Y - z) > FootHalfZ + Ball) continue;
                // which side of the foot the ball came from
                float side = Mathf.Sign(from.X - footX);
                if (side == 0) side = Team[rod] == 0 ? 1 : -1;
                BallAt.X = footX + side * (FootHalfX + Ball + 0.0005f);
                // the foot's own speed at the ball: the rod's spin times the foot's depth
                float footV = Spin[rod] * FootDepth * Mathf.Cos(Angle[rod]);
                float rel = BallV.X - footV;
                if (rel * side < 0) BallV.X = footV - rel * 0.55f;
                if (Mathf.Abs(footV) > 0.3f) BallV.X = footV * 1.25f + (BallV.X - footV) * 0.2f;
                // a glancing touch off the foot's side steers it
                BallV.Y += (BallAt.Y - z) / (FootHalfZ + Ball) * 0.4f;
                break;
            }
        }
        float speed = BallV.Length();
        if (speed > 6f) BallV *= 6f / speed;
    }

    public override string Summary(Func<long, string> name)
    {
        string Who(int team)
        {
            var names = new List<string>();
            if (Seats[team] != 0) names.Add(name(Seats[team]));
            if (Seats[team + 2] != 0) names.Add(name(Seats[team + 2]));
            return names.Count == 0 ? (team == 0 ? "Red" : "Blue") : string.Join(" & ", names);
        }
        if (Seated == 0) return "Table football";
        string score = $"{Who(0)} (red) {Goals[0]} : {Goals[1]} {Who(1)} (blue)";
        return Winner >= 0 ? $"{score}\n{Call}. Jump for a new game." : $"{score}\n{Call}";
    }

    // ---- a bot on the rods ----------------------------------------------------------------------

    /// <summary>A bot's reaction: it follows where the ball was this long ago [s], and how far off it lines a figure up [m].</summary>
    public const float BotLag = 0.12f, BotError = 0.012f;
    private readonly Vector2[] _seen = new Vector2[16];
    private int _seenAt;

    public override (int Action, int[] Args)? Bot(int seat, double now, Random rng)
    {
        if (Winner >= 0) return Seats.Any(s => s > 0) ? null : (ActRestart, Array.Empty<int>());
        // the ball as the bot saw it a moment ago (ten housekeeping passes a second)
        _seen[_seenAt++ % _seen.Length] = BallAt;
        var seen = _seen[(_seenAt - 2 + _seen.Length) % _seen.Length];
        int team = TeamOf(seat);
        float dir = team == 0 ? 1 : -1;
        // the rod of its own nearest the ball, behind or at it: the one it plays with
        int best = -1;
        float near = float.MaxValue;
        for (int rod = 0; rod < 8; rod++)
        {
            if (Team[rod] != team || SeatOfRod(rod) != seat) continue;
            float d = Mathf.Abs(seen.X - RodX[rod]);
            if (d < near) { near = d; best = rod; }
        }
        if (best < 0) return null;
        // line up the figure nearest the ball
        int fig = 0;
        float gap = float.MaxValue;
        for (int k = 0; k < Figures[best]; k++)
        {
            float d = Mathf.Abs(FigureZ(best, k) + Slide[best] - seen.Y);
            if (d < gap) { gap = d; fig = k; }
        }
        float want = seen.Y - FigureZ(best, fig) + (float)(rng.NextDouble() * 2 - 1) * BotError;
        float slide = Mathf.Clamp((want - Slide[best]) / 0.03f, -1, 1);
        float ahead = (BallAt.X - RodX[best]) * dir;
        bool kick = ahead > -0.005f && ahead < 0.05f && Mathf.Abs(BallAt.Y - FigureZ(best, fig) - Slide[best]) < 0.03f;
        // the ball behind its rod, rolling to its own goal: lift the rod out of the way of the ones behind
        bool lift = ahead < -0.03f && BallV.X * dir < 0 && !Back[best];
        Input(seat, Pack(slide, kick, lift));
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
        w.Write(BallAt.X);
        w.Write(BallAt.Y);
        w.Write(BallV.X);
        w.Write(BallV.Y);
        for (int i = 0; i < 8; i++)
        {
            w.Write((short)Mathf.RoundToInt(Slide[i] * 10000));
            w.Write((short)Mathf.RoundToInt(Angle[i] * 5000));
        }
    }

    public override void ReadPoses(BinaryReader r)
    {
        BallAt = new Vector2(r.ReadSingle(), r.ReadSingle());
        BallV = new Vector2(r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < 8; i++)
        {
            Slide[i] = r.ReadInt16() / 10000f;
            Angle[i] = r.ReadInt16() / 5000f;
        }
    }
}
