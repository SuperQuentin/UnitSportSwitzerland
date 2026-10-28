using Godot;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// A table being played (#1325): the solver and the rules together, stepped at the solver's fixed 1 ms.
/// The rules' orders (serve a ball, kick a scoop, raise a bank, flippers off) reach the solver at their
/// time; the solver's events reach the rules and <see cref="Outbox"/>, which the player's client sends
/// to the server for its mirror. Pure: the same inputs give the same game.
/// </summary>
public sealed class PinballGame
{
    public readonly PinballTable Table;
    public readonly PinballSolver Solver;
    public readonly PinballRules Rules;
    /// <summary>The events since the last <see cref="TakeOutbox"/>, and the flipper presses with them.</summary>
    public readonly List<PinballSolver.Event> Outbox = new();
    private readonly List<PinballRules.Order> _pending = new();
    private double _nextLaunch;
    private bool _left, _right;

    public PinballGame(PinballTable table, int seed)
    {
        Table = table;
        Solver = new PinballSolver(table);
        Rules = new PinballRules(table, seed);
    }

    public double Time => Solver.Step * (double)PinballSolver.Dt;

    /// <summary>A flipper button pressed (side 0 left, 1 right) and its lane change, or let go.</summary>
    public void Button(int side, bool held)
    {
        bool was = side == 0 ? _left : _right;
        if (side == 0) _left = Solver.LeftHeld = held;
        else _right = Solver.RightHeld = held;
        if (held && !was)
        {
            Rules.Flipped(side, Time);
            // a press is an event too: the server's mirror moves its lanes alike
            Outbox.Add(new PinballSolver.Event(Solver.Step, PinballSolver.EventKind.Switch, side == 0 ? LeftPress : RightPress));
        }
    }

    /// <summary>The log's markers beside the switches: a flipper pressed (left, right), Start pressed, a player skipped.</summary>
    public const int LeftPress = -1, RightPress = -2, StartPress = -3, SkipMark = -4;

    /// <summary>Start pressed: a new game, or one more player during the first ball. Logged, so the mirror does the same.</summary>
    public bool Start()
    {
        bool ok = Rules.Start(Time);
        if (ok) Outbox.Add(new PinballSolver.Event(Solver.Step, PinballSolver.EventKind.Switch, StartPress));
        Pump();
        return ok;
    }

    /// <summary>The server skipped the player up (gone): applied here too, and logged.</summary>
    public void Skip()
    {
        Rules.Skip(Time);
        Outbox.Add(new PinballSolver.Event(Solver.Step, PinballSolver.EventKind.Switch, SkipMark));
        Pump();
    }

    /// <summary>
    /// Takes over a game in progress from its log (a turn handed to this peer): the rules replayed to where
    /// they stood, the clock just after, a fresh table with the balls the rules have in play on the plunger.
    /// </summary>
    public void Resume(IReadOnlyList<PinballSolver.Event> log)
    {
        if (log.Count == 0) return;
        Mirror(Rules, log, log[^1].Time);
        Solver.Step = log[^1].Step + 1;
        Rules.Commands.Clear();
        Solver.FlippersOn = !Rules.Tilted && Rules.State == PinballRules.Phase.Playing;
        for (int i = 0; i < Rules.BallsInPlay; i++) Solver.Serve();
    }

    public void Nudge(Vector2 dv) => Solver.Nudge(dv);

    /// <summary>Runs <paramref name="steps"/> ms of the table.</summary>
    public void Run(int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            Pump();
            Solver.Tick();
            if (Solver.Events.Count == 0) continue;
            foreach (var e in Solver.Events)
            {
                Rules.Apply(e);
                Outbox.Add(e);
            }
            Solver.Events.Clear();
        }
        Rules.Advance(Time);
    }

    /// <summary>The rules' due orders onto the table.</summary>
    private void Pump()
    {
        if (Rules.Commands.Count > 0)
        {
            _pending.AddRange(Rules.Commands);
            Rules.Commands.Clear();
        }
        for (int i = 0; i < _pending.Count; i++)
        {
            var o = _pending[i];
            if (o.At > Time) continue;
            if (o.Command is PinballRules.Command.LaunchBall or PinballRules.Command.ServeBall && Time < _nextLaunch) continue;
            _pending.RemoveAt(i--);
            switch (o.Command)
            {
                case PinballRules.Command.ServeBall:
                    Solver.Serve();
                    _nextLaunch = Time + 0.3;
                    break;
                case PinballRules.Command.LaunchBall:
                    Solver.Launch();
                    _nextLaunch = Time + 1.0;
                    break;
                case PinballRules.Command.ReleaseScoop:
                    Solver.Release(o.Arg, Solver.Step);
                    break;
                case PinballRules.Command.ResetBank:
                    Solver.ResetBank(o.Arg);
                    break;
                case PinballRules.Command.FlippersOff:
                    Solver.FlippersOn = false;
                    break;
                case PinballRules.Command.FlippersOn:
                    Solver.FlippersOn = true;
                    break;
            }
        }
    }

    public List<PinballSolver.Event> TakeOutbox()
    {
        var list = new List<PinballSolver.Event>(Outbox);
        Outbox.Clear();
        return list;
    }

    /// <summary>
    /// The server's mirror: the player's events applied to a rules engine with no table (the flipper
    /// presses as lane changes, everything else as it came), then the clock to <paramref name="until"/>.
    /// </summary>
    public static void Mirror(PinballRules rules, IEnumerable<PinballSolver.Event> events, double until)
    {
        foreach (var e in events)
        {
            if (e.Kind == PinballSolver.EventKind.Switch && e.Id == StartPress) rules.Start(e.Time);
            else if (e.Kind == PinballSolver.EventKind.Switch && e.Id == SkipMark) rules.Skip(e.Time);
            else if (e.Kind == PinballSolver.EventKind.Switch && e.Id < 0) rules.Flipped(-1 - e.Id, e.Time);
            else rules.Apply(e);
        }
        rules.Advance(until);
        rules.Commands.Clear();
    }

    // ---- the wire: a table's balls and flippers in a few bytes ---------------------------------

    /// <summary>The balls (x, y, h in tenths of a millimetre) and the flippers' angles, for the watchers.</summary>
    public static byte[] PackPoses(PinballSolver s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)s.Balls.Count);
        foreach (var b in s.Balls)
        {
            w.Write((short)Mathf.Clamp(Mathf.RoundToInt(b.Pos3.X * 10000), short.MinValue, short.MaxValue));
            w.Write((short)Mathf.Clamp(Mathf.RoundToInt(b.Pos3.Y * 10000 - 5000), short.MinValue, short.MaxValue));
            w.Write((short)Mathf.Clamp(Mathf.RoundToInt(b.Pos3.Z * 10000), short.MinValue, short.MaxValue));
        }
        w.Write((byte)s.Angle.Length);
        foreach (float a in s.Angle) w.Write((short)Mathf.RoundToInt(a * 10000));
        int down = 0;
        for (int i = 0; i < s.Down.Length && i < 31; i++)
            if (s.Down[i]) down |= 1 << i;
        w.Write(down);
        return ms.ToArray();
    }

    public readonly record struct Poses(Vector3[] Balls, float[] Flippers, int Down);

    public static Poses UnpackPoses(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var balls = new Vector3[r.ReadByte()];
        for (int i = 0; i < balls.Length; i++)
            balls[i] = new Vector3(r.ReadInt16() / 10000f, (r.ReadInt16() + 5000) / 10000f, r.ReadInt16() / 10000f);
        var flips = new float[r.ReadByte()];
        for (int i = 0; i < flips.Length; i++) flips[i] = r.ReadInt16() / 10000f;
        return new Poses(balls, flips, r.ReadInt32());
    }

    /// <summary>Events as bytes: step, kind, id.</summary>
    public static byte[] PackEvents(IReadOnlyList<PinballSolver.Event> events)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)events.Count);
        foreach (var e in events)
        {
            w.Write(e.Step);
            w.Write((byte)e.Kind);
            w.Write((short)e.Id);
        }
        return ms.ToArray();
    }

    public static List<PinballSolver.Event> UnpackEvents(byte[] data)
    {
        if (data.Length < 2) return new List<PinballSolver.Event>();
        using var r = new BinaryReader(new MemoryStream(data));
        int n = r.ReadInt16();
        var list = new List<PinballSolver.Event>(n);
        for (int i = 0; i < n; i++) list.Add(new PinballSolver.Event(r.ReadInt32(), (PinballSolver.EventKind)r.ReadByte(), r.ReadInt16()));
        return list;
    }
}
