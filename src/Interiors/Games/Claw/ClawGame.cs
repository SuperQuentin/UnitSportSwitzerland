using Godot;

namespace UnitSport.Interiors.Games.Claw;

/// <summary>
/// The claw machine (#1326), pure and live, one player: a Batzen in, <see cref="Time"/> seconds to steer the claw over
/// the plush (x across, z front to back, in the piece's frame), then it drops (by Jump or when the time runs out),
/// closes, and lifts. Whether it holds is the server's luck: a good chance with the claw's middle over a prize's, a
/// poor one off it, and a prize can slip on the way up or back. Over the chute in the front-left corner the claw
/// opens: the prize drops out to the player (<see cref="TableGame.Give"/>). The pile restocks when it runs low.
/// </summary>
public sealed class ClawGame : TableGame
{
    public enum Phase : byte { Idle, Steer, Drop, Close, Lift, Carry, Open }

    public const int ActCoin = 1, ActDrop = 2;
    /// <summary>Seconds to steer, the claw's speed [m/s], the drop's and lift's times [s].</summary>
    public const float Time = 30f, Speed = 0.22f, DropTime = 1.4f, CloseTime = 0.6f, LiftTime = 1.2f, OpenTime = 0.8f;
    /// <summary>A grab's chance with the claw's middle within <see cref="Good"/> of a prize's, within <see cref="Fair"/>, and a slip on the way.</summary>
    public const float Good = 0.04f, Fair = 0.08f, GoodChance = 0.6f, FairChance = 0.2f, SlipChance = 0.2f;
    public const int Stock = 12, Restock = 6;

    public static readonly Items.ItemId[] Kinds = { Items.ItemId.PlushCow, Items.ItemId.PlushMarmot, Items.ItemId.PlushBernese, Items.ItemId.PlushCow, Items.ItemId.PlushMarmot };

    public override GameKind Kind => GameKind.Claw;
    public override int MaxSeats => 1;
    public override bool Live => true;
    public override bool Bots => false;

    /// <summary>Half the box's floor (x, z) inside its posts; the chute's corner (−x, +z) is kept clear of plush.</summary>
    public readonly float Hx, Hz;
    public Phase State;
    /// <summary>The claw over the floor, how far down it is (0 at the top, 1 on the plush), how open (1 open, 0 closed).</summary>
    public Vector2 ClawAt;
    public float Down, Open = 1;
    public double PhaseAt, SteerUntil;
    /// <summary>The prizes in the box: where each lies and what it is.</summary>
    public readonly List<(Vector2 At, Items.ItemId Item)> Pile = new();
    /// <summary>The prize in the claw (an index into <see cref="Pile"/>), or −1.</summary>
    public int Held = -1;
    public string Call = "";
    public int Won;
    private Vector2 _input;
    private readonly Random _rng;

    public ClawGame(float w = 1.0f, float d = 1.0f, int seed = 1326)
    {
        Init();
        Hx = w / 2 - 0.08f;
        Hz = d / 2 - 0.08f;
        _rng = new Random(seed);
        ClawAt = Chute;
        Fill();
    }

    /// <summary>Over the chute: the front-left corner.</summary>
    public Vector2 Chute => new(-Hx + 0.11f, Hz - 0.11f);

    private bool InChute(Vector2 p) => p.X < -Hx + 0.24f && p.Y > Hz - 0.24f;

    private void Fill()
    {
        for (int guard = 0; Pile.Count < Stock && guard < 400; guard++)
        {
            var p = new Vector2((float)(_rng.NextDouble() * 2 - 1) * (Hx - 0.06f), (float)(_rng.NextDouble() * 2 - 1) * (Hz - 0.06f));
            if (InChute(p) || Pile.Exists(q => q.At.DistanceTo(p) < 0.11f)) continue;
            Pile.Add((p, Kinds[_rng.Next(Kinds.Length)]));
        }
    }

    public override bool Awake => State != Phase.Idle;

    public override void Input(int seat, byte[] input)
    {
        if (input.Length >= 2) _input = new Vector2((sbyte)input[0], (sbyte)input[1]) / 127f;
    }

    public static byte[] Pack(Vector2 v) =>
        new[] { (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(v.X * 127), -127, 127), (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(v.Y * 127), -127, 127) };

    public override bool Busy(double now) => State != Phase.Idle && State != Phase.Steer;

    public override string? Act(int seat, int action, int[] args, double now, Random rng)
    {
        switch (action)
        {
            case ActCoin:
                if (State != Phase.Idle) return "The claw is busy.";
                State = Phase.Steer;
                PhaseAt = now;
                SteerUntil = now + Time;
                Call = $"{Time:F0} seconds: steer, then drop";
                return null;
            case ActDrop:
                if (State != Phase.Steer) return "Put a Batzen in first.";
                Go(Phase.Drop, now);
                return null;
        }
        return "?";
    }

    private void Go(Phase p, double now)
    {
        State = p;
        PhaseAt = now;
    }

    public override bool Step(double dt, double now)
    {
        float t = (float)dt;
        float into = (float)(now - PhaseAt);
        switch (State)
        {
            case Phase.Steer:
            {
                var v = _input.Length() > 1 ? _input.Normalized() : _input;
                ClawAt += v * Speed * t;
                ClawAt = new Vector2(Mathf.Clamp(ClawAt.X, -Hx, Hx), Mathf.Clamp(ClawAt.Y, -Hz, Hz));
                if (now >= SteerUntil) { Go(Phase.Drop, now); Call = "Time! It drops"; return true; }
                return false;
            }
            case Phase.Drop:
                Down = Mathf.Clamp(into / DropTime, 0, 1);
                if (into >= DropTime) Go(Phase.Close, now);
                return false;
            case Phase.Close:
                Open = Mathf.Clamp(1 - into / CloseTime, 0, 1);
                if (into < CloseTime) return false;
                Grab();
                Go(Phase.Lift, now);
                return true;
            case Phase.Lift:
                Down = Mathf.Clamp(1 - into / LiftTime, 0, 1);
                if (Held >= 0 && into > LiftTime * 0.6f && _slip) { Slip(); return true; }
                if (into >= LiftTime) Go(Phase.Carry, now);
                return false;
            case Phase.Carry:
            {
                var to = Chute - ClawAt;
                float step = Speed * 1.5f * t;
                ClawAt = to.Length() <= step ? Chute : ClawAt + to.Normalized() * step;
                if (Held >= 0) Pile[Held] = (ClawAt, Pile[Held].Item);
                if (ClawAt == Chute) Go(Phase.Open, now);
                return false;
            }
            case Phase.Open:
                Open = Mathf.Clamp(into / OpenTime, 0, 1);
                if (into < OpenTime) return false;
                bool won = Held >= 0;
                if (won)
                {
                    var item = Pile[Held].Item;
                    Pile.RemoveAt(Held);
                    Held = -1;
                    Won++;
                    Give(0, item, "the claw");
                    Call = $"Won a {NameOf(item)}!";
                }
                else if (!Call.StartsWith("It slipped")) Call = "Nothing this time";
                if (Pile.Count < Restock) Fill();
                Open = 1;
                Down = 0;
                Go(Phase.Idle, now);
                return true;
        }
        return false;
    }

    private bool _slip;

    /// <summary>A prize's name for the call (plain C#: the item table is the game's, not the tests').</summary>
    public static string NameOf(Items.ItemId item) => item switch
    {
        Items.ItemId.PlushCow => "plush cow",
        Items.ItemId.PlushMarmot => "plush marmot",
        Items.ItemId.PlushBernese => "plush Bernese",
        _ => "prize",
    };

    /// <summary>The claw closes: the prize nearest its middle, by the luck its place gives.</summary>
    private void Grab()
    {
        int best = -1;
        float near = float.MaxValue;
        for (int i = 0; i < Pile.Count; i++)
        {
            float d = Pile[i].At.DistanceTo(ClawAt);
            if (d < near) { near = d; best = i; }
        }
        float chance = near < Good ? GoodChance : near < Fair ? FairChance : 0;
        if (best >= 0 && _rng.NextDouble() < chance)
        {
            Held = best;
            _slip = _rng.NextDouble() < SlipChance;
            Call = "Got one...";
        }
        else Call = near < Fair ? "It brushed it" : "Nothing there";
    }

    /// <summary>The prize slips out on the way up: back into the pile, near where it fell.</summary>
    private void Slip()
    {
        var at = ClawAt + new Vector2((float)(_rng.NextDouble() - 0.5) * 0.1f, (float)(_rng.NextDouble() - 0.5) * 0.1f);
        at = new Vector2(Mathf.Clamp(at.X, -Hx + 0.05f, Hx - 0.05f), Mathf.Clamp(at.Y, -Hz + 0.05f, Hz - 0.05f));
        if (InChute(at)) at += new Vector2(0.2f, -0.2f);
        Pile[Held] = (at, Pile[Held].Item);
        Held = -1;
        _slip = false;
        Call = "It slipped!";
    }

    public override string Summary(Func<long, string> name)
    {
        string left = State == Phase.Steer ? $"   {Math.Max(0, (int)Math.Ceiling(SteerUntil - _clockSeen))} s" : "";
        return State == Phase.Idle ? $"The claw: a Batzen a go\n{Call}" : $"The claw{left}\n{Call}";
    }

    private double _clockSeen;

    /// <summary>The time the HUD's countdown counts from (the shared clock, set by whoever shows it).</summary>
    public void Seen(double now) => _clockSeen = now;

    // ---- a bot's hands (the showcase and the check: no bot plays it in a venue) ------------------

    public override (int Action, int[] Args)? Bot(int seat, double now, Random rng)
    {
        if (State == Phase.Idle) return (ActCoin, Array.Empty<int>());
        if (State != Phase.Steer || Pile.Count == 0) return null;
        // the nearest prize out of the chute's corner, aimed at with a little error
        int best = 0;
        for (int i = 1; i < Pile.Count; i++)
            if (Pile[i].At.DistanceTo(ClawAt) < Pile[best].At.DistanceTo(ClawAt)) best = i;
        var to = Pile[best].At - ClawAt;
        if (to.Length() < 0.015f + (float)rng.NextDouble() * 0.03f) return (ActDrop, Array.Empty<int>());
        Input(seat, Pack(to.Normalized() * Mathf.Clamp(to.Length() / 0.05f, 0.2f, 1)));
        return null;
    }

    // ---- the wire ------------------------------------------------------------------------------

    protected override void WriteState(BinaryWriter w)
    {
        w.Write((byte)State);
        w.Write(PhaseAt);
        w.Write(SteerUntil);
        w.Write((sbyte)Held);
        w.Write(Call);
        w.Write((short)Won);
        w.Write((byte)Pile.Count);
        foreach (var (at, item) in Pile)
        {
            w.Write(at.X);
            w.Write(at.Y);
            w.Write((short)item);
        }
        WritePoses(w);
    }

    protected override void ReadState(BinaryReader r)
    {
        State = (Phase)r.ReadByte();
        PhaseAt = r.ReadDouble();
        SteerUntil = r.ReadDouble();
        Held = r.ReadSByte();
        Call = r.ReadString();
        Won = r.ReadInt16();
        Pile.Clear();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++) Pile.Add((new Vector2(r.ReadSingle(), r.ReadSingle()), (Items.ItemId)r.ReadInt16()));
        ReadPoses(r);
    }

    public override void WritePoses(BinaryWriter w)
    {
        w.Write(ClawAt.X);
        w.Write(ClawAt.Y);
        w.Write((byte)Mathf.RoundToInt(Down * 255));
        w.Write((byte)Mathf.RoundToInt(Open * 255));
        w.Write((sbyte)Held);
    }

    public override void ReadPoses(BinaryReader r)
    {
        ClawAt = new Vector2(r.ReadSingle(), r.ReadSingle());
        Down = r.ReadByte() / 255f;
        Open = r.ReadByte() / 255f;
        Held = r.ReadSByte();
        if (Held >= 0 && Held < Pile.Count) Pile[Held] = (ClawAt, Pile[Held].Item);
    }
}
