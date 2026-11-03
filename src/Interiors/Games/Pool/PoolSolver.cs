using Godot;

namespace UnitSport.Interiors.Games.Pool;

/// <summary>
/// A pool table's balls (#1324), pure: sixteen balls on the cloth as discs in the table's plane (x across, z along,
/// metres from the table's centre, as the piece's frame), stepped at 0.5 ms until all are still.
///
/// <para>
/// Each ball has its velocity <c>V</c> and the velocity its spin gives its contact with the cloth (<c>W</c>): a ball
/// struck in the middle slides with no spin (W = 0) and the cloth's friction (0.2) brings W and V together until it
/// rolls (W = V, then only the rolling resistance, 0.01); a ball struck high (follow) or low (draw) starts with W ahead
/// of or behind its V. Balls hit as smooth equal spheres (restitution 0.95): the velocity along the line of centres
/// passes over, the spin stays, so a drawn cue ball comes back. Cushions are straight lines broken at the six
/// pockets' mouths; a ball whose centre goes into a mouth falls into the pocket.
/// </para>
/// </summary>
public sealed class PoolSolver
{
    public const float R = 0.02855f, Dt = 0.0005f, G = 9.81f;
    public const float Slide = 0.2f, Roll = 0.011f, Restitution = 0.95f, CushionKeep = 0.72f;
    /// <summary>Half the opening of a corner pocket and of a side pocket, along the rail [m].</summary>
    public const float CornerMouth = 0.062f, SideMouth = 0.058f;
    public const int Balls = 16;
    /// <summary>A shot never runs longer than this [s].</summary>
    public const float MaxTime = 30f;

    public enum EventKind : byte { Contact, Cushion, Pocket }

    public readonly record struct Event(float Time, EventKind Kind, int Ball, int Other);

    /// <summary>Half the cloth's width (x) and length (z) between the cushions' noses [m].</summary>
    public readonly float Hx, Hz;
    public readonly Vector2[] P = new Vector2[Balls], V = new Vector2[Balls], W = new Vector2[Balls];
    /// <summary>Whether each ball is on the table (a potted one is not).</summary>
    public readonly bool[] On = new bool[Balls];
    public readonly List<Event> Events = new();
    public float Time;

    public PoolSolver(float hx, float hz)
    {
        Hx = hx;
        Hz = hz;
    }

    /// <summary>The six pockets' points: the corners and the middles of the long sides.</summary>
    public IEnumerable<(Vector2 At, bool Side)> Pockets()
    {
        foreach (float sx in new[] { -1f, 1f })
        {
            yield return (new Vector2(sx * Hx, -Hz), false);
            yield return (new Vector2(sx * Hx, 0), true);
            yield return (new Vector2(sx * Hx, Hz), false);
        }
    }

    public void CopyFrom(PoolSolver o)
    {
        Array.Copy(o.P, P, Balls);
        Array.Copy(o.On, On, Balls);
        Array.Clear(V);
        Array.Clear(W);
        Events.Clear();
        Time = 0;
    }

    public bool Moving
    {
        get
        {
            for (int i = 0; i < Balls; i++)
                if (On[i] && V[i].LengthSquared() > 1e-8f) return true;
            return false;
        }
    }

    /// <summary>
    /// The cue strikes the cue ball (ball 0) towards <paramref name="angle"/> (radians, 0 = +z, turning towards +x) at
    /// <paramref name="speed"/> m/s; <paramref name="follow"/> from -1 (full draw) to 1 (full follow).
    /// </summary>
    public void Strike(float angle, float speed, float follow)
    {
        // a new shot: its own events and clock
        Events.Clear();
        Time = 0;
        var dir = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
        V[0] = dir * speed;
        // a tip 0.5 R off centre spins the ball to about 1.25x (follow) or -1.25x (draw) its speed at the cloth
        W[0] = dir * speed * Mathf.Clamp(follow, -1, 1) * 1.25f;
    }

    /// <summary>Runs the shot to its end (every ball still, or <see cref="MaxTime"/>).</summary>
    public void Run()
    {
        while (Moving && Time < MaxTime) Step();
        for (int i = 0; i < Balls; i++) { V[i] = Vector2.Zero; W[i] = Vector2.Zero; }
    }

    /// <summary>Runs the shot until shot time <paramref name="t"/> (a replay shown as it plays).</summary>
    public void RunTo(float t)
    {
        while (Time < t && Moving && Time < MaxTime) Step();
    }

    public void Step()
    {
        Time += Dt;
        for (int i = 0; i < Balls; i++)
        {
            if (!On[i]) continue;
            Friction(i);
            P[i] += V[i] * Dt;
        }
        for (int i = 0; i < Balls; i++)
        {
            if (!On[i]) continue;
            for (int j = i + 1; j < Balls; j++)
                if (On[j]) Collide(i, j);
        }
        for (int i = 0; i < Balls; i++)
            if (On[i]) Rails(i);
    }

    private void Friction(int i)
    {
        var v = V[i];
        var slip = v - W[i];
        float s = slip.Length();
        if (s > 1e-4f)
        {
            // sliding: the cloth pulls the contact towards rest: V loses μg, the slip 7/2 μg
            var dir = slip / s;
            float dv = Slide * G * Dt;
            if (3.5f * dv >= s)
            {
                // the slip ends within this step: rolling from here
                V[i] = v - dir * (s / 3.5f);
                W[i] = V[i];
            }
            else
            {
                V[i] = v - dir * dv;
                W[i] = W[i] + dir * (2.5f * dv);
            }
            return;
        }
        float speed = v.Length();
        if (speed < 1e-4f) { V[i] = Vector2.Zero; W[i] = Vector2.Zero; return; }
        float left = Math.Max(0, speed - Roll * G * Dt);
        V[i] = v * (left / speed);
        W[i] = V[i];
    }

    private void Collide(int i, int j)
    {
        var d = P[j] - P[i];
        float dist2 = d.LengthSquared();
        if (dist2 >= 4 * R * R || dist2 < 1e-12f) return;
        float dist = Mathf.Sqrt(dist2);
        var n = d / dist;
        // apart, as far as they overlap
        float push = (2 * R - dist) * 0.5f;
        P[i] -= n * push;
        P[j] += n * push;
        float closing = (V[i] - V[j]).Dot(n);
        if (closing <= 0) return;
        float impulse = closing * (1 + Restitution) * 0.5f;
        V[i] -= n * impulse;
        V[j] += n * impulse;
        Events.Add(new Event(Time, EventKind.Contact, i, j));
    }

    private void Rails(int i)
    {
        var p = P[i];
        // into a pocket: near a pocket's point, inside its mouth
        foreach (var (at, side) in Pockets())
        {
            float mouth = side ? SideMouth : CornerMouth;
            if (p.DistanceTo(at) < mouth + (side ? 0.0f : 0.012f))
            {
                On[i] = false;
                V[i] = Vector2.Zero;
                W[i] = Vector2.Zero;
                Events.Add(new Event(Time, EventKind.Pocket, i, -1));
                return;
            }
        }
        float lx = Hx - R, lz = Hz - R;
        if (Mathf.Abs(p.X) > lx && !InMouth(p, true))
        {
            P[i] = p with { X = Mathf.Sign(p.X) * lx };
            if (V[i].X * Mathf.Sign(p.X) > 0)
            {
                V[i] = new Vector2(-V[i].X * CushionKeep, V[i].Y * 0.95f);
                W[i] = W[i] with { X = -W[i].X * 0.5f };
                Events.Add(new Event(Time, EventKind.Cushion, i, -1));
            }
        }
        p = P[i];
        if (Mathf.Abs(p.Y) > lz && !InMouth(p, false))
        {
            P[i] = p with { Y = Mathf.Sign(p.Y) * lz };
            if (V[i].Y * Mathf.Sign(p.Y) > 0)
            {
                V[i] = new Vector2(V[i].X * 0.95f, -V[i].Y * CushionKeep);
                W[i] = W[i] with { Y = -W[i].Y * 0.5f };
                Events.Add(new Event(Time, EventKind.Cushion, i, -1));
            }
        }
    }

    /// <summary>Whether a ball past a rail's line is in a pocket's mouth there (no cushion to meet).</summary>
    private bool InMouth(Vector2 p, bool longRail)
    {
        if (longRail)
        {
            // a long rail (x = ±Hx) has the corners and the side pocket
            float z = Mathf.Abs(p.Y);
            return z < SideMouth || z > Hz - CornerMouth * 1.2f;
        }
        return Mathf.Abs(p.X) > Hx - CornerMouth * 1.2f;
    }

    /// <summary>The fifteen balls racked at the foot spot, the cue ball behind the head string (8 in the middle, a solid and a stripe in the back corners).</summary>
    public void Rack(Random rng)
    {
        Array.Clear(V);
        Array.Clear(W);
        Events.Clear();
        Time = 0;
        for (int i = 0; i < Balls; i++) On[i] = true;
        var foot = new Vector2(0, Hz * 0.5f);
        P[0] = new Vector2(0, -Hz * 0.5f);
        // rows 1..5 from the apex, away from the breaker (+z)
        var order = new List<int>();
        for (int b = 1; b < Balls; b++) if (b != 8) order.Add(b);
        for (int k = order.Count - 1; k > 0; k--)
        {
            int j = rng.Next(k + 1);
            (order[k], order[j]) = (order[j], order[k]);
        }
        // a stripe and a solid in the back corners
        int solid = order.FindIndex(b => b < 8), stripe = order.FindIndex(b => b > 8);
        int s1 = order[solid], s2 = order[stripe];
        order.Remove(s1);
        order.Remove(s2);
        int next = 0;
        float dz = R * 2 * Mathf.Sqrt(3) / 2 + 0.0002f;
        for (int row = 0; row < 5; row++)
            for (int k = 0; k <= row; k++)
            {
                var at = foot + new Vector2((k - row * 0.5f) * (2 * R + 0.0002f), row * dz);
                int ball = row == 2 && k == 1 ? 8 : row == 4 && k == 0 ? s1 : row == 4 && k == 4 ? s2 : order[next++];
                P[ball] = at;
            }
    }

    /// <summary>Whether the cue ball may stand at <paramref name="p"/>: on the cloth, touching no ball.</summary>
    public bool Free(Vector2 p, int except = 0)
    {
        if (Mathf.Abs(p.X) > Hx - R || Mathf.Abs(p.Y) > Hz - R) return false;
        for (int i = 0; i < Balls; i++)
            if (i != except && On[i] && P[i].DistanceTo(p) < 2 * R + 0.001f) return false;
        return true;
    }
}
