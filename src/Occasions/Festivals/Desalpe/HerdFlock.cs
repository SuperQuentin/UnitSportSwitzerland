// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-desalpe.md).

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>Someone driving the herd: a body at (X, Y) with a flight zone (5 m on foot, 9 m mounted) that a cow does not like to be inside.</summary>
public readonly record struct Driver(double X, double Y, float Zone);

public enum CowState : byte { Herd, Stray, Lost }

/// <summary>
/// The herd's steering (#1269 part 7, pure, stepped by the server at 10 Hz): a Leitkuh (cow 0) follows a carrot 15 m ahead on the route
/// and the rest follow her in slots behind, kept 1.6 m apart. The Leitkuh only walks while a driver is near the herd, and waits when the
/// herd lags. Drivers push: a cow inside a driver's flight zone moves away from his shoulder, and because a cow's <i>point of balance</i>
/// is at its shoulder, a driver behind it sends it forward and one ahead of it sends it back. At a junction a cow with little herd
/// drive wanders off into the field; it comes back only when a driver flanks it from the field side; one 150 m from the Leitkuh is
/// lost for the score. O(n²) over at most 40 cows.
/// </summary>
public sealed class HerdFlock
{
    public const float LeaderSpeed = 1.1f, CarrotM = 15f, SeparationM = 1.6f, LostM = 150f, ArriveRadius = 22f, SlotGap = 2.0f;
    public const double FollowRange = 30, StrayOutM = 24, RejoinM = 6;
    /// <summary>A cow with less herd drive than this is the kind that strays at a junction.</summary>
    public const float StrayProne = 0.3f;

    public readonly DesalpeRoute Route;
    public readonly int Count;
    public readonly double[] X, Y;
    public readonly float[] Vx, Vy, Yaw, Gait, Drive;
    public readonly CowState[] State;
    private readonly (double X, double Y)[] _strayTo;
    private readonly bool[] _left;
    private readonly double[] _junctions;
    private int _nextJunction;
    private readonly int _seed;

    /// <summary>Metres along the route the Leitkuh has reached.</summary>
    public double LeaderS { get; private set; }

    /// <summary>Whether a driver was near enough for the herd to walk on the last step.</summary>
    public bool Pushed { get; private set; }

    public bool LeaderArrived => LeaderS >= Route.Length - 3;

    /// <summary>Seconds since the Leitkuh reached the fair.</summary>
    public double SinceArrival { get; private set; }

    public HerdFlock(DesalpeRoute route, int count, int seed)
    {
        Route = route;
        Count = count;
        _seed = seed;
        X = new double[count]; Y = new double[count];
        Vx = new float[count]; Vy = new float[count]; Yaw = new float[count]; Gait = new float[count]; Drive = new float[count];
        State = new CowState[count];
        _strayTo = new (double, double)[count];
        _left = new bool[count];
        var (sx, sy) = route.Start;
        var (tx, ty) = route.Tangent(0);
        for (int i = 0; i < count; i++)
        {
            // scattered over the alp's pasture round the start, the Leitkuh at its front
            // a spiral of 2.5 m spacing over the alp's pasture round the start, the Leitkuh in the middle of it
            double a = i * 2.39996, r = 2.5 * Math.Sqrt(i);
            X[i] = sx + Math.Cos(a) * r - tx * 2;
            Y[i] = sy + Math.Sin(a) * r - ty * 2;
            Yaw[i] = (float)Math.Atan2(-tx, ty);
            Drive[i] = i == 0 ? 1f : (float)DesalpeRoute.Unit((uint)seed, (uint)(300 + i));
        }
        // three junctions along the way, a little jittered
        _junctions =
        [
            route.Length * (0.28 + 0.06 * DesalpeRoute.Unit((uint)seed, 41)),
            route.Length * (0.52 + 0.06 * DesalpeRoute.Unit((uint)seed, 42)),
            route.Length * (0.76 + 0.06 * DesalpeRoute.Unit((uint)seed, 43)),
        ];
    }

    /// <summary>The distances along the route at which the herd reaches a junction (cows with little drive may wander off there).</summary>
    public IReadOnlyList<double> Junctions => _junctions;

    public double Distance(int i, double x, double y) => Math.Sqrt((X[i] - x) * (X[i] - x) + (Y[i] - y) * (Y[i] - y));

    public (double X, double Y) Centroid()
    {
        double sx = 0, sy = 0;
        int n = 0;
        for (int i = 0; i < Count; i++)
            if (State[i] == CowState.Herd) { sx += X[i]; sy += Y[i]; n++; }
        return n == 0 ? (X[0], Y[0]) : (sx / n, sy / n);
    }

    public bool Arrived(int i) => State[i] != CowState.Lost && Distance(i, Route.End.X, Route.End.Y) <= ArriveRadius;

    public int ArrivedCount()
    {
        int n = 0;
        for (int i = 0; i < Count; i++) if (Arrived(i)) n++;
        return n;
    }

    /// <summary>The cow's wish for the herd's slot: behind the Leitkuh, alternating sides, a row every two cows.</summary>
    private (double X, double Y) Slot(int i)
    {
        var (tx, ty) = Route.Tangent(LeaderS);
        double back = SlotGap * (1 + (i - 1) / 2), side = (i % 2 == 0 ? 1 : -1) * (1.6 + ((i - 1) / 4) * 0.5);
        return (X[0] - tx * back - ty * side, Y[0] - ty * back + tx * side);
    }

    public void Step(float dt, IReadOnlyList<Driver> drivers)
    {
        // does anyone drive the herd? the Leitkuh only walks then
        var (cx, cy) = Centroid();
        Pushed = false;
        foreach (var d in drivers)
            if (Math.Sqrt((d.X - cx) * (d.X - cx) + (d.Y - cy) * (d.Y - cy)) <= FollowRange
                || Math.Sqrt((d.X - X[0]) * (d.X - X[0]) + (d.Y - Y[0]) * (d.Y - Y[0])) <= FollowRange)
            { Pushed = true; break; }

        // the Leitkuh: after the carrot, waiting for a herd that lags
        double back = 0;
        for (int i = 1; i < Count; i++)
            if (State[i] == CowState.Herd) back = Math.Max(back, Distance(i, X[0], Y[0]));
        float wait = (float)Math.Clamp((40 - back) / 20, 0, 1);
        float want = Pushed && !LeaderArrived ? LeaderSpeed * wait : 0f;
        double carrotS = Math.Min(Route.Length, Route.Project(X[0], Y[0], out _) + CarrotM);
        var (kx, ky) = Route.At(carrotS);
        double dx = kx - X[0], dy = ky - Y[0], dl = Math.Sqrt(dx * dx + dy * dy);
        float lvx = dl > 0.2 ? (float)(dx / dl) * want : 0, lvy = dl > 0.2 ? (float)(dy / dl) * want : 0;
        Blend(0, lvx, lvy, dt);
        double s = Route.Project(X[0], Y[0], out _);
        if (s > LeaderS) LeaderS = s;
        if (LeaderArrived) SinceArrival += dt;

        // a junction passed: the cows with little herd drive wander into the field
        while (_nextJunction < _junctions.Length && LeaderS >= _junctions[_nextJunction])
        {
            double js = _junctions[_nextJunction];
            var (jx, jy) = Route.At(js);
            var (tx, ty) = Route.Tangent(js);
            double sign = DesalpeRoute.Unit((uint)_seed, (uint)(50 + _nextJunction)) < 0.5 ? 1 : -1;
            // at most two cows leave at a junction: the ones with the least herd drive that are close enough to see the gap
            var leaving = Enumerable.Range(1, Count - 1).Where(i => State[i] == CowState.Herd && Drive[i] < StrayProne && Distance(i, jx, jy) <= 40)
                .OrderBy(i => Drive[i]).Take(2);
            foreach (int i in leaving)
            {
                State[i] = CowState.Stray;
                _left[i] = false;
                double outward = StrayOutM * (0.8 + 0.5 * DesalpeRoute.Unit((uint)_seed, (uint)(60 + i)));
                _strayTo[i] = (jx - ty * sign * outward + tx * 6, jy + tx * sign * outward + ty * 6);
            }
            _nextJunction++;
        }

        for (int i = 1; i < Count; i++)
        {
            if (State[i] == CowState.Lost) { Blend(i, 0, 0, dt); continue; }
            float vx = 0, vy = 0;
            if (Distance(i, X[0], Y[0]) > LostM) { State[i] = CowState.Lost; continue; }
            if (State[i] == CowState.Herd)
            {
                var (sx, sy) = Slot(i);
                double ex = sx - X[i], ey = sy - Y[i], el = Math.Sqrt(ex * ex + ey * ey);
                if (el > 1.0)
                {
                    float sp = (float)Math.Min(LeaderSpeed * 1.2, el * 0.6);
                    vx = (float)(ex / el) * sp; vy = (float)(ey / el) * sp;
                }
            }
            else
            {
                // a stray walks to its field spot, then grazes
                double ex = _strayTo[i].X - X[i], ey = _strayTo[i].Y - Y[i], el = Math.Sqrt(ex * ex + ey * ey);
                if (el > 1.5) { vx = (float)(ex / el) * 0.6f; vy = (float)(ey / el) * 0.6f; }
            }
            // separation from every other cow too close
            for (int j = 0; j < Count; j++)
            {
                if (j == i) continue;
                double sx = X[i] - X[j], sy = Y[i] - Y[j], sd = Math.Sqrt(sx * sx + sy * sy);
                if (sd >= SeparationM || sd < 1e-6) continue;
                float push = (float)((SeparationM - sd) / SeparationM * 1.2);
                vx += (float)(sx / sd) * push; vy += (float)(sy / sd) * push;
            }
            // drivers: away from the driver, measured at the cow's shoulder, so one behind sends it forward and one ahead sends it back
            float hx = Vx[i], hy = Vy[i], hl = MathF.Sqrt(hx * hx + hy * hy);
            if (hl < 0.05f) { hx = -MathF.Sin(Yaw[i]); hy = MathF.Cos(Yaw[i]); } else { hx /= hl; hy /= hl; }
            double shx = X[i] + hx * 0.4, shy = Y[i] + hy * 0.4;
            foreach (var d in drivers)
            {
                double ax = shx - d.X, ay = shy - d.Y, ad = Math.Sqrt(ax * ax + ay * ay);
                if (ad >= d.Zone || ad < 1e-6) continue;
                float strength = (float)((1 - ad / d.Zone) * 2.2);
                vx += (float)(ax / ad) * strength; vy += (float)(ay / ad) * strength;
            }
            Blend(i, vx, vy, dt);
            // a stray led back to the road is part of the herd again
            if (State[i] == CowState.Stray)
            {
                Route.Project(X[i], Y[i], out double dist);
                if (dist > RejoinM + 3) _left[i] = true;
                else if (dist <= RejoinM && _left[i] && Distance(i, X[0], Y[0]) < 60) State[i] = CowState.Herd;
            }
        }
    }

    private void Blend(int i, float vx, float vy, float dt)
    {
        float k = Math.Min(1f, 3f * dt);
        Vx[i] += (vx - Vx[i]) * k;
        Vy[i] += (vy - Vy[i]) * k;
        X[i] += Vx[i] * dt;
        Y[i] += Vy[i] * dt;
        float sp = MathF.Sqrt(Vx[i] * Vx[i] + Vy[i] * Vy[i]);
        Gait[i] = Math.Clamp(sp / 0.6f, 0f, 1f);
        if (sp > 0.15f) Yaw[i] = MathF.Atan2(-Vx[i], Vy[i]);
    }
}
