using Godot;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// The ball's physics on a <see cref="PinballTable"/> (#1325): pure and deterministic, stepped at a fixed
/// 1 ms by whoever plays the table (the player's client; the server when it takes over).
///
/// <para>
/// The ball rolls in the playfield's plane under 5/7 of g sin 6.5° (a solid sphere rolling). Walls, rubbers,
/// posts, bumpers and targets are met by a swept circle (the time of impact along the step, so a fast
/// ball never passes through a thin wall); flippers are driven tapered capsules, met by overlap each
/// step (they move a few millimetres a step at most) and bouncing the ball off their moving surface.
/// Pop bumpers and slingshots kick. Ramps are 1D tracks with height (a layer of their own): a ball too
/// slow rolls back out. Scoops catch a slow ball until <see cref="Release"/>. Every switch the ball closes
/// is an <see cref="Event"/> with its step, which the rules read.
/// </para>
/// </summary>
public sealed class PinballSolver
{
    public const float Dt = 0.001f;
    /// <summary>g along the playfield, and into it [m/s²].</summary>
    public static readonly float GAlong = 9.81f * Mathf.Sin(Mathf.DegToRad(PinballTable.SlopeDeg)),
        GInto = 9.81f * Mathf.Cos(Mathf.DegToRad(PinballTable.SlopeDeg));
    /// <summary>A rolling solid sphere feels 5/7 of the pull along its path.</summary>
    public const float Rolling = 5f / 7f;
    /// <summary>Rolling resistance on the playfield and on ramps [1/s, m/s²].</summary>
    public const float Drag = 0.08f, RampDrag = 0.12f;
    /// <summary>Flipper stroke: up at 2500 rad/s² to 38 rad/s (≈ 30 ms for 50°), back down slower.</summary>
    public const float FlipAccel = 15000f, FlipSpeed = 70f, FlipReturn = 22f;
    /// <summary>The bat's rubber: a ball meeting it bounces this much off the moving surface; low, so a ball on it rides it out to the tip.</summary>
    public static float FlipperBounce = 0.55f;
    /// <summary>Pop bumper and slingshot kicks: the ball leaves at least this fast along the normal [m/s].</summary>
    public const float BumperKick = 1.7f, SlingKick = 1.5f;
    /// <summary>The plunger's range [m/s]: a soft pull reaches the top lanes, a full one flies round the arch.</summary>
    public const float PlungeMin = 1.0f, PlungeMax = 2.6f, AutoLaunch = 2.4f;
    /// <summary>A scoop catches a ball slower than this [m/s].</summary>
    public const float CatchSpeed = 1.6f;
    /// <summary>The tilt bob: a nudge swings it by its size, it settles at this rate; past 1 it touches the ring.</summary>
    public const float BobSettle = 1.4f;

    public enum EventKind : byte { Switch, Drain, Launch, TiltWarn, RampFail }

    public readonly record struct Event(int Step, EventKind Kind, int Id, int Value = 1)
    {
        public double Time => Step * (double)Dt;
    }

    /// <summary>A ball: on the playfield (<see cref="Ramp"/> -1), on a ramp's track, or held in a scoop.</summary>
    public sealed class Ball
    {
        public int Id;
        public Vector2 P, V;
        public int Ramp = -1;
        /// <summary>Along the ramp's track [m], and the speed along it [m/s].</summary>
        public float S, Vs;
        public int Held = -1;
        /// <summary>When a held ball leaves its scoop (a step), or -1 until the rules say.</summary>
        public int ReleaseAt = -1;
        /// <summary>Where it is in 3D (x, y, h above the playfield), for the cabinet.</summary>
        public Vector3 Pos3;
    }

    public readonly PinballTable Table;
    public readonly List<Ball> Balls = new();
    public readonly List<Event> Events = new();
    /// <summary>Each flipper's angle and speed.</summary>
    public readonly float[] Angle, Omega;
    /// <summary>The buttons: left, right.</summary>
    public bool LeftHeld, RightHeld;
    /// <summary>The flippers answer the buttons (off once tilted).</summary>
    public bool FlippersOn = true;
    /// <summary>How far the plunger is pulled (0-1); the ball leaves as it is let go.</summary>
    public float Pull;
    public readonly bool[] Down;
    public int Step;
    public float Bob;
    /// <summary>A spinner keeps turning after the ball: its angle and speed [rad, rad/s], by sensor.</summary>
    public readonly float[] Spin, SpinSpeed;
    private float _pullWas;
    private int _nextId;
    private readonly float[] _spinRest;

    public PinballSolver(PinballTable table)
    {
        Table = table;
        Angle = new float[table.Flippers.Count];
        Omega = new float[table.Flippers.Count];
        for (int i = 0; i < Angle.Length; i++) Angle[i] = table.Flippers[i].Rest;
        Down = new bool[table.Walls.Count];
        Spin = new float[table.Sensors.Count];
        SpinSpeed = new float[table.Sensors.Count];
        _spinRest = new float[table.Sensors.Count];
    }

    // ---- what the rules and the player do ------------------------------------------------------

    /// <summary>A new ball on the plunger, in the shooter lane.</summary>
    public Ball Serve()
    {
        var b = new Ball { Id = _nextId++, P = new Vector2(Table.ShooterX, Table.ShooterRest) };
        Balls.Add(b);
        Place3(b);
        return b;
    }

    /// <summary>A ball served and launched at once (multiball, a saved ball).</summary>
    public Ball Launch()
    {
        var b = Serve();
        b.V = new Vector2(0, AutoLaunch);
        return b;
    }

    /// <summary>The ball in scoop <paramref name="scoop"/> leaves it at step <paramref name="step"/>.</summary>
    public void Release(int scoop, int step)
    {
        foreach (var b in Balls)
            if (b.Held == scoop && b.ReleaseAt < 0) b.ReleaseAt = step;
    }

    /// <summary>A bank's drop targets come back up.</summary>
    public void ResetBank(int bank)
    {
        foreach (int w in Table.DropBanks[bank]) Down[w] = false;
    }

    /// <summary>A shove on the cabinet: every ball on the playfield gets <paramref name="dv"/> [m/s], the bob swings.</summary>
    public void Nudge(Vector2 dv)
    {
        foreach (var b in Balls)
            if (b.Ramp < 0 && b.Held < 0) b.V += dv;
        Bob += dv.Length() / 0.2f * 0.55f;
        if (Bob > 1f)
        {
            Bob = 0.4f;
            Events.Add(new Event(Step, EventKind.TiltWarn, 0));
        }
    }

    /// <summary>Whether a ball waits on the plunger.</summary>
    public bool BallOnPlunger()
    {
        foreach (var b in Balls)
            if (b.Ramp < 0 && b.Held < 0 && Mathf.Abs(b.P.X - Table.ShooterX) < 0.012f && b.P.Y < Table.ShooterRest + 0.02f && b.V.LengthSquared() < 0.04f)
                return true;
        return false;
    }

    // ---- stepping ------------------------------------------------------------------------------

    /// <summary>Steps the table by whole steps; the events go to <see cref="Events"/>.</summary>
    public void Run(int steps)
    {
        for (int i = 0; i < steps; i++) Tick();
    }

    public void Tick()
    {
        Step++;
        Bob = Mathf.Max(0, Bob - BobSettle * Dt);
        for (int f = 0; f < Table.Flippers.Count; f++) Flip(f);
        Plunger();
        for (int s = 0; s < Spin.Length; s++)
            if (SpinSpeed[s] != 0)
            {
                Spin[s] += SpinSpeed[s] * Dt;
                SpinSpeed[s] *= 1f - 1.6f * Dt;
                if (Mathf.Abs(SpinSpeed[s]) < 2f) SpinSpeed[s] = 0;
                // each whole turn closes the spinner's switch once
                if (Mathf.Abs(Spin[s] - _spinRest[s]) >= Mathf.Tau)
                {
                    _spinRest[s] += Mathf.Tau * Mathf.Sign(Spin[s] - _spinRest[s]);
                    Events.Add(new Event(Step, EventKind.Switch, Table.Sensors[s].Switch));
                }
            }
        for (int i = Balls.Count - 1; i >= 0; i--)
        {
            var b = Balls[i];
            if (b.Held >= 0) HeldBall(b);
            else if (b.Ramp >= 0) OnRamp(b);
            else Roll(b);
            if (b.Ramp < 0 && b.Held < 0 && b.P.Y < -0.03f)
            {
                Balls.RemoveAt(i);
                Events.Add(new Event(Step, EventKind.Drain, b.Id));
                continue;
            }
            Place3(b);
        }
        BallsMeet();
    }

    private void Flip(int f)
    {
        var fl = Table.Flippers[f];
        bool held = FlippersOn && (fl.Side == 0 ? LeftHeld : RightHeld);
        float sign = fl.Sign;
        if (held)
        {
            float w = Mathf.Max(0, Omega[f] * sign);
            Omega[f] = sign * Mathf.Min(w + FlipAccel * Dt, FlipSpeed);
            Angle[f] += Omega[f] * Dt;
            if ((Angle[f] - fl.Up) * sign >= 0) { Angle[f] = fl.Up; Omega[f] = 0; }
        }
        else
        {
            Omega[f] = -sign * FlipReturn;
            Angle[f] += Omega[f] * Dt;
            if ((Angle[f] - fl.Rest) * sign <= 0) { Angle[f] = fl.Rest; Omega[f] = 0; }
        }
    }

    private void Plunger()
    {
        // let go: the plunger's tip drives the ball up the lane
        if (Pull < _pullWas - 0.05f || Pull == 0 && _pullWas > 0)
        {
            foreach (var b in Balls)
                if (b.Ramp < 0 && b.Held < 0 && Mathf.Abs(b.P.X - Table.ShooterX) < 0.015f && b.P.Y < Table.ShooterRest + 0.03f)
                {
                    b.V = new Vector2(0, PlungeMin + (PlungeMax - PlungeMin) * _pullWas);
                    Events.Add(new Event(Step, EventKind.Launch, b.Id, Mathf.RoundToInt(_pullWas * 100)));
                }
            _pullWas = Pull;
            return;
        }
        _pullWas = Pull;
    }

    private void HeldBall(Ball b)
    {
        var s = Table.Scoops[b.Held];
        if (b.ReleaseAt < 0 || Step < b.ReleaseAt) { b.P = s.C; b.V = Vector2.Zero; return; }
        b.Held = -1;
        b.ReleaseAt = -1;
        b.P = s.Exit;
        b.V = s.Kick;
    }

    // ---- on the playfield ----------------------------------------------------------------------

    private void Roll(Ball b)
    {
        b.V += new Vector2(0, -GAlong * Rolling * Dt);
        b.V *= 1f - Drag * Dt;
        // the shooter lane's plunger tip: the ball rests on it
        if (Mathf.Abs(b.P.X - Table.ShooterX) < 0.015f && b.P.Y < Table.ShooterRest && b.V.Y <= 0)
        {
            b.P = b.P with { Y = Table.ShooterRest };
            b.V = Vector2.Zero;
        }
        float remaining = 1f;
        var start = b.P;
        for (int iter = 0; iter < 4 && remaining > 1e-4f; iter++)
        {
            var d = b.V * Dt * remaining;
            if (!Earliest(b.P, d, out float t, out var n, out int hit, out bool disc))
            {
                b.P += d;
                break;
            }
            b.P += d * t;
            remaining *= 1f - t;
            Respond(b, n, hit, disc);
        }
        Flippers(b);
        Sensors(b, start, b.P);
        Scoops(b);
    }

    /// <summary>The first thing the swept ball meets along <paramref name="d"/>, as a fraction of it.</summary>
    private bool Earliest(Vector2 p, Vector2 d, out float tBest, out Vector2 nBest, out int hitBest, out bool discBest)
    {
        const float r = PinballTable.BallRadius;
        tBest = 2f;
        nBest = Vector2.Zero;
        hitBest = -1;
        discBest = false;
        var walls = Table.Walls;
        for (int i = 0; i < walls.Count; i++)
        {
            var w = walls[i];
            if (Down[i]) continue;
            if (w.Kind == PinballTable.WallKind.Gate && d.Dot(w.Pass) >= 0) continue;
            if (SweepSegment(p, d, w.A, w.B, r, out float t, out var n) && t < tBest) { tBest = t; nBest = n; hitBest = i; discBest = false; }
        }
        var discs = Table.Discs;
        for (int i = 0; i < discs.Count; i++)
            if (SweepPoint(p, d, discs[i].C, r + discs[i].R, out float t, out var n) && t < tBest)
            { tBest = t; nBest = n; hitBest = i; discBest = true; }
        return tBest <= 1f;
    }

    /// <summary>
    /// Time of impact (0-1 along <paramref name="d"/>) of a circle of radius <paramref name="r"/> from
    /// <paramref name="p"/> with segment AB, and the normal at it; only when moving into it.
    /// </summary>
    public static bool SweepSegment(Vector2 p, Vector2 d, Vector2 a, Vector2 b, float r, out float t, out Vector2 n)
    {
        t = 2f;
        n = Vector2.Zero;
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-10f) return SweepPoint(p, d, a, r, out t, out n);
        var normal = new Vector2(-ab.Y, ab.X) / Mathf.Sqrt(len2);
        float s0 = (p - a).Dot(normal);
        if (s0 < 0) { normal = -normal; s0 = -s0; }
        float s1 = (p + d - a).Dot(normal);
        bool found = false;
        if (s1 < r && s0 - s1 > 1e-9f)
        {
            // the face: where the centre comes within r of the line, if that point is along the segment
            float tf = Mathf.Max(0, (s0 - r) / (s0 - s1));
            var q = p + d * tf;
            float u = (q - a).Dot(ab) / len2;
            if (u >= 0 && u <= 1 && tf <= 1) { t = tf; n = normal; found = true; }
        }
        if (SweepPoint(p, d, a, r, out float ta, out var na) && ta < t) { t = ta; n = na; found = true; }
        if (SweepPoint(p, d, b, r, out float tb, out var nb) && tb < t) { t = tb; n = nb; found = true; }
        return found;
    }

    /// <summary>Time of impact of a moving point with a circle of radius <paramref name="r"/> round <paramref name="c"/>.</summary>
    public static bool SweepPoint(Vector2 p, Vector2 d, Vector2 c, float r, out float t, out Vector2 n)
    {
        t = 2f;
        n = Vector2.Zero;
        var m = p - c;
        float bq = m.Dot(d);
        if (bq >= 0) return false;   // moving away
        float cq = m.LengthSquared() - r * r;
        float aq = d.LengthSquared();
        if (cq <= 0)
        {
            // already touching (resting on it, or pushed in): a contact now
            t = 0;
            n = m.LengthSquared() > 1e-12f ? m.Normalized() : -d.Normalized();
            return true;
        }
        float disc = bq * bq - aq * cq;
        if (disc < 0 || aq < 1e-14f) return false;
        t = (-bq - Mathf.Sqrt(disc)) / aq;
        if (t < 0 || t > 1) return false;
        n = (m + d * t).Normalized();
        return true;
    }

    private void Respond(Ball b, Vector2 n, int hit, bool disc)
    {
        float vn = b.V.Dot(n);
        if (disc)
        {
            var dsc = Table.Discs[hit];
            if (dsc.Kind == PinballTable.DiscKind.Bumper)
            {
                // the skirt closes the switch and the ring slams the ball away
                b.V -= vn * n;
                b.V += n * Mathf.Max(BumperKick, -vn * 0.6f + BumperKick * 0.6f);
                Close(dsc.Switch);
                return;
            }
            Bounce(b, n, vn, 0.65f, 0.99f);
            return;
        }
        var w = Table.Walls[hit];
        switch (w.Kind)
        {
            case PinballTable.WallKind.Sling:
                // the kicking rubber fires on a real hit, not on a ball rolling along it
                if (-vn > 0.12f && n.Dot(w.Pass) > 0.5f)
                {
                    b.V -= vn * n;
                    b.V += n * Mathf.Max(SlingKick, -vn * 0.5f + SlingKick * 0.7f);
                    Close(w.Switch);
                }
                else Bounce(b, n, vn, 0.6f, 0.995f);
                break;
            case PinballTable.WallKind.Rubber:
                Bounce(b, n, vn, 0.6f, 0.995f);
                break;
            case PinballTable.WallKind.Target:
                Bounce(b, n, vn, 0.35f, 0.99f);
                if (-vn > 0.08f) Close(w.Switch);
                break;
            case PinballTable.WallKind.Drop:
                Bounce(b, n, vn, 0.3f, 0.99f);
                if (-vn > 0.08f)
                {
                    Down[hit] = true;
                    Close(w.Switch);
                }
                break;
            default:
                Bounce(b, n, vn, 0.4f, 0.998f);
                break;
        }
    }

    /// <summary>Restitution <paramref name="e"/> on the normal (dulled for a slow touch, so a ball rests), friction on the tangent.</summary>
    private static void Bounce(Ball b, Vector2 n, float vn, float e, float keep)
    {
        if (vn >= 0) return;
        if (-vn < 0.08f) e = 0;
        var vt = b.V - vn * n;
        b.V = vt * keep - vn * e * n;
    }

    private void Close(int sw)
    {
        if (sw >= 0) Events.Add(new Event(Step, EventKind.Switch, sw));
    }

    /// <summary>The flippers as they stand now: overlap, push out, bounce off the moving surface.</summary>
    private void Flippers(Ball b)
    {
        const float r = PinballTable.BallRadius;
        for (int f = 0; f < Table.Flippers.Count; f++)
        {
            var fl = Table.Flippers[f];
            var dir = Vector2.FromAngle(Angle[f]);
            var rel = b.P - fl.Pivot;
            float u = Mathf.Clamp(rel.Dot(dir) / fl.Length, 0, 1);
            var q = fl.Pivot + dir * (u * fl.Length);
            float rad = fl.RBase + (fl.RTip - fl.RBase) * u;
            var m = b.P - q;
            float dist = m.Length();
            if (dist >= r + rad || dist < 1e-6f) continue;
            var n = m / dist;
            b.P = q + n * (r + rad);
            // the bat's surface where the ball touches it moves at ω × r
            var contact = q + n * rad - fl.Pivot;
            var surf = new Vector2(-contact.Y, contact.X) * Omega[f];
            var vrel = b.V - surf;
            float vn = vrel.Dot(n);
            if (vn >= 0) continue;
            float e = -vn > 0.25f ? FlipperBounce : 0f;
            var vt = vrel - vn * n;
            b.V = surf + vt * 0.997f - vn * e * n;
        }
    }

    // ---- switches the ball rolls over ----------------------------------------------------------

    private void Sensors(Ball b, Vector2 from, Vector2 to)
    {
        var sensors = Table.Sensors;
        for (int i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (!Crosses(from, to, s.A, s.B)) continue;
            switch (s.Kind)
            {
                case PinballTable.SensorKind.Rollover:
                    Close(s.Switch);
                    break;
                case PinballTable.SensorKind.Spinner:
                {
                    // the flag takes the ball's speed across it and turns on by itself
                    var ab = s.B - s.A;
                    var across = new Vector2(-ab.Y, ab.X).Normalized();
                    float v = b.V.Dot(across);
                    SpinSpeed[i] = Mathf.Clamp(v * 60f, -260f, 260f);
                    break;
                }
                case PinballTable.SensorKind.RampEntry:
                    if (b.V.Dot(s.Dir) > 0) Enter(b, s.Ramp);
                    break;
            }
            if (b.Ramp >= 0) return;
        }
    }

    public static bool Crosses(Vector2 p, Vector2 q, Vector2 a, Vector2 b)
    {
        static float Cross(Vector2 o, Vector2 x, Vector2 y) => (x.X - o.X) * (y.Y - o.Y) - (x.Y - o.Y) * (y.X - o.X);
        float d1 = Cross(a, b, p), d2 = Cross(a, b, q), d3 = Cross(p, q, a), d4 = Cross(p, q, b);
        return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0);
    }

    private void Scoops(Ball b)
    {
        for (int i = 0; i < Table.Scoops.Count; i++)
        {
            var s = Table.Scoops[i];
            if ((b.P - s.C).LengthSquared() > s.R * s.R || b.V.Length() > CatchSpeed) continue;
            b.Held = i;
            b.ReleaseAt = -1;
            b.P = s.C;
            b.V = Vector2.Zero;
            Close(s.Switch);
            return;
        }
    }

    // ---- ramps ---------------------------------------------------------------------------------

    private void Enter(Ball b, int ramp)
    {
        var tr = Table.Ramps[ramp].Track;
        var first = tr[1] - tr[0];
        b.Ramp = ramp;
        b.S = 0;
        b.Vs = Mathf.Max(0.05f, b.V.Dot(new Vector2(first.X, first.Y).Normalized()));
    }

    private void OnRamp(Ball b)
    {
        var ramp = Table.Ramps[b.Ramp];
        var (_, dir) = Along(ramp.Track, b.S);
        // the pull along the track: the playfield's slope in y, the ramp's own climb in h
        float a = -Rolling * (GAlong * dir.Y + GInto * dir.Z) - RampDrag * Mathf.Sign(b.Vs);
        b.Vs += a * Dt;
        b.S += b.Vs * Dt;
        float len = Length(ramp.Track);
        if (b.S >= len)
        {
            // made: back onto the playfield at the far end, along the track's last direction
            var end = ramp.Track[^1];
            var last = ramp.Track[^1] - ramp.Track[^2];
            var flat = new Vector2(last.X, last.Y);
            b.Ramp = -1;
            b.P = new Vector2(end.X, end.Y);
            b.V = (flat.LengthSquared() > 1e-8f ? flat.Normalized() : new Vector2(0, -1)) * Mathf.Abs(b.Vs);
            Close(ramp.Switch);
            return;
        }
        if (b.S <= 0 && b.Vs < 0)
        {
            // too slow: it rolled back out of the mouth
            int index = b.Ramp;
            var mouth = ramp.Track[0];
            var first = ramp.Track[1] - ramp.Track[0];
            b.Ramp = -1;
            b.P = new Vector2(mouth.X, mouth.Y);
            b.V = -new Vector2(first.X, first.Y).Normalized() * Mathf.Max(0.1f, -b.Vs);
            Events.Add(new Event(Step, EventKind.RampFail, index));
        }
    }

    public static float Length(Vector3[] track)
    {
        float l = 0;
        for (int i = 1; i < track.Length; i++) l += (track[i] - track[i - 1]).Length();
        return l;
    }

    /// <summary>The point <paramref name="s"/> along a track and the track's direction there.</summary>
    public static (Vector3 At, Vector3 Dir) Along(Vector3[] track, float s)
    {
        for (int i = 1; i < track.Length; i++)
        {
            var seg = track[i] - track[i - 1];
            float l = seg.Length();
            if (s <= l || i == track.Length - 1)
            {
                var dir = l > 1e-6f ? seg / l : Vector3.Up;
                return (track[i - 1] + dir * Mathf.Clamp(s, 0, l), dir);
            }
            s -= l;
        }
        return (track[0], Vector3.Up);
    }

    private void Place3(Ball b)
    {
        if (b.Ramp >= 0)
        {
            var (at, _) = Along(Table.Ramps[b.Ramp].Track, b.S);
            b.Pos3 = at + new Vector3(0, 0, PinballTable.BallRadius);
        }
        else b.Pos3 = new Vector3(b.P.X, b.P.Y, b.Held >= 0 && Table.Scoops[b.Held].Subway ? -0.03f : PinballTable.BallRadius);
    }

    /// <summary>Two balls on the playfield bounce off each other (equal masses).</summary>
    private void BallsMeet()
    {
        const float d2 = 2 * PinballTable.BallRadius;
        for (int i = 0; i < Balls.Count; i++)
            for (int j = i + 1; j < Balls.Count; j++)
            {
                Ball a = Balls[i], c = Balls[j];
                if (a.Ramp >= 0 || c.Ramp >= 0 || a.Held >= 0 || c.Held >= 0) continue;
                var m = c.P - a.P;
                float dist = m.Length();
                if (dist >= d2 || dist < 1e-6f) continue;
                var n = m / dist;
                float push = (d2 - dist) / 2;
                a.P -= n * push;
                c.P += n * push;
                float vn = (c.V - a.V).Dot(n);
                if (vn >= 0) continue;
                var imp = n * vn * 0.95f;
                a.V += imp;
                c.V -= imp;
            }
    }
}
