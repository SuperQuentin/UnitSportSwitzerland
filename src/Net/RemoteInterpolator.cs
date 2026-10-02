using System;
using Godot;

namespace UnitSport.Net;

/// <summary>
/// Where to draw another player this frame, from the states their client sent.
///
/// <para>
/// Snapping a remote body to each update is what made a car at 150 km/h hop 0.7-2 m per packet
/// and freeze-then-jump whenever one was late. This keeps the last second of timestamped states
/// (the sender's own clock, so arrival jitter does not bend the timeline) and renders a little
/// behind the newest one, where there is always a state on either side to interpolate between.
/// Position is Hermite-interpolated with the sent velocities, so a car follows the curve it
/// actually drove, not the chords between samples. Past the newest state it extrapolates on
/// velocity for a short while; when the truth arrives the gap is eased away instead of snapped.
/// </para>
/// </summary>
public sealed class RemoteInterpolator
{
    private struct State { public double T; public Vector3 P, V; public float Yaw; }

    private const int Capacity = 32;
    private readonly State[] _buf = new State[Capacity];
    private int _count, _head;   // _head = index of the newest

    /// <summary>Longest a body is carried on its last velocity when states stop arriving.</summary>
    public const float MaxExtrapolation = 0.25f;

    /// <summary>This body's own limit (<see cref="MaxExtrapolation"/> unless set): a race NPC's is longer (#159).</summary>
    public float MaxAhead = MaxExtrapolation;

    /// <summary>A jump beyond this is a teleport: snap, do not ease.</summary>
    public const float TeleportMetres = 25f;

    private double _offset = double.NaN;   // local clock − sender clock, as low as it has been seen
    private double _interval = 1.0 / 30.0;
    private Vector3 _error;                 // rendered − true, eased to zero after a correction
    private float _yawError;
    private Vector3 _lastOut;
    private float _lastYaw;
    private bool _hasOut;

    /// <summary>How far behind the newest state it renders: enough to always have one ahead.</summary>
    public double Delay => Math.Clamp(1.6 * _interval + 0.02, 0.06, 0.4);

    public bool HasData => _count > 0;

    /// <summary>Records a state the owner sent, stamped with the owner's clock.</summary>
    public void Push(double senderTime, double localTime, Vector3 position, Vector3 velocity, float yaw)
    {
        if (_count > 0)
        {
            ref var newest = ref _buf[_head];
            if (senderTime <= newest.T) return;            // duplicate or out of order: unreliable channel
            double gap = senderTime - newest.T;
            if (gap < 1.0) _interval += (gap - _interval) * 0.1;
            if (position.DistanceTo(newest.P) > TeleportMetres) Clear();
        }
        // The lowest (local − sender) seen is the path with no queueing on it; letting it creep up
        // slowly follows a clock that drifts, or a route that got permanently slower.
        double offset = localTime - senderTime;
        _offset = double.IsNaN(_offset) ? offset : Math.Min(offset, _offset + 0.0002);

        _head = (_head + 1) % Capacity;
        _buf[_head] = new State { T = senderTime, P = position, V = velocity, Yaw = yaw };
        if (_count < Capacity) _count++;
    }

    public void Clear()
    {
        _count = 0;
        _hasOut = false;
        _lag = double.NaN;
        _error = Vector3.Zero;
        _yawError = 0;
    }

    /// <summary>
    /// Another peer sends this body from now on (a race NPC handed to another client, #50): its
    /// clock has nothing to do with the last one, so the states and the clock estimate start
    /// over. The picture does not: the first new sample eases from where it was drawn.
    /// </summary>
    public void NewSender()
    {
        _count = 0;
        _offset = double.NaN;
        _lag = double.NaN;
        _rebase = _hasOut;
    }

    private bool _rebase;

    /// <summary>The position and yaw to draw at <paramref name="localTime"/>.</summary>
    public (Vector3 Position, float Yaw) Sample(double localTime, float dt)
    {
        if (_count == 0) return (_lastOut, _lastYaw);
        double t = RenderTime(localTime, dt);
        var (p, yaw) = Raw(t);
        if (_rebase && _lastOut.DistanceTo(p) < TeleportMetres)
        {
            _error = _lastOut - p;
            _yawError = Mathf.AngleDifference(yaw, _lastYaw);
        }
        _rebase = false;
        _hasOut = true;
        // the correction a late or surprising state caused (Begin/EndCorrection) bleeds away
        // over ~100 ms instead of showing as a jump
        float k = Mathf.Exp(-12f * Mathf.Max(dt, 0f));
        _error *= k;
        _yawError *= k;

        var outP = p + _error;
        float outYaw = yaw + _yawError;
        _lastOut = outP;
        _lastYaw = outYaw;
        return (outP, outYaw);
    }

    /// <summary>Called when a new state arrives, before it is pushed: keeps the picture continuous.</summary>
    public void BeginCorrection(double localTime)
    {
        if (!_hasOut || _count == 0) return;
        double t = localTime - _lag;
        var (before, yawBefore) = Raw(t);
        _pendingT = t;
        _pendingP = before;
        _pendingYaw = yawBefore;
        _pending = true;
    }

    /// <summary>After the push: whatever the new state changed about "now" becomes easing error.</summary>
    public void EndCorrection()
    {
        if (!_pending) return;
        _pending = false;
        var (after, yawAfter) = Raw(_pendingT);
        var jump = _pendingP - after;
        if (jump.Length() < TeleportMetres)
        {
            _error += jump;
            _yawError += Mathf.AngleDifference(yawAfter, _pendingYaw);
        }
    }

    private bool _pending;
    private double _pendingT;
    private Vector3 _pendingP;
    private float _pendingYaw;

    private double _lag = double.NaN;   // local − render time actually used, eased

    /// <summary>
    /// The render clock never jumps: when the clock estimate or the delay changes, playback runs
    /// at most 5% fast or slow until it has caught up. A jump in render time is a jump on screen.
    /// </summary>
    private double RenderTime(double localTime, float dt)
    {
        double want = _offset + Delay;
        if (double.IsNaN(_lag) || Math.Abs(want - _lag) > 0.5) _lag = want;
        else _lag += Math.Clamp(want - _lag, -0.05 * dt, 0.05 * dt);
        return localTime - _lag;
    }

    private (Vector3, float) Raw(double t)
    {
        ref var newest = ref _buf[_head];
        if (t >= newest.T)
        {
            float ahead = (float)Math.Min(t - newest.T, MaxAhead);
            // a long carry (a race NPC waiting for its handoff) eases off like a lift, to half its speed by the
            // end of it, instead of rolling on flat out and then stopping dead (#159)
            if (MaxAhead > MaxExtrapolation) ahead -= ahead * ahead / (4f * MaxAhead);
            return (newest.P + newest.V * ahead, newest.Yaw);
        }
        // walk back to the pair straddling t
        int i = _head;
        for (int n = 1; n < _count; n++)
        {
            int prev = (i - 1 + Capacity) % Capacity;
            ref var a = ref _buf[prev];
            ref var b = ref _buf[i];
            if (a.T <= t)
            {
                float span = (float)(b.T - a.T);
                float u = span > 1e-6f ? (float)((t - a.T) / span) : 1f;
                return (Hermite(a.P, a.V * span, b.P, b.V * span, u), Mathf.LerpAngle(a.Yaw, b.Yaw, u));
            }
            i = prev;
        }
        ref var oldest = ref _buf[i];
        return (oldest.P, oldest.Yaw);
    }

    private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return p0 * (2 * u3 - 3 * u2 + 1) + m0 * (u3 - 2 * u2 + u) + p1 * (-2 * u3 + 3 * u2) + m1 * (u3 - u2);
    }

    /// <summary>
    /// Self-check (<c>--interestcheck</c>): a car at 150 and 300 km/h sent at 30 Hz with 60 ms of
    /// arrival jitter and 5% (then 20%) loss must be drawn with no step over 1.5 × v·dt at 60 fps
    /// and no freeze. Then a wingsuit or the cargo plane at 80 m/s whose sender hitches, a third of a
    /// second of states lost at once now and then (#207): no snap, the catch-up eased (no step over 3.5 × v·dt).
    /// </summary>
    public static bool SelfCheck() => Run(42f, 0.05) & Run(83f, 0.05) & Run(42f, 0.2) & Run(80f, 0.03, 10, 3.5);

    private static bool Run(float v, double loss, int burst = 1, double maxRatio = 1.5)
    {
        int dropping = 0;
        var rng = new Random(7);
        var ip = new RemoteInterpolator();
        double send = 0, sendDt = 1.0 / 30.0, frameDt = 1.0 / 60.0;
        var inFlight = new System.Collections.Generic.List<(double Arrive, double T)>();
        double worstRatio = 0; int freezes = 0; Vector3? prev = null;
        for (double now = 0; now < 10; now += frameDt)
        {
            while (send <= now)
            {
                if (dropping == 0 && rng.NextDouble() < loss) dropping = burst;
                if (dropping > 0) dropping--;
                else inFlight.Add((send + 0.05 + rng.NextDouble() * 0.06, send));
                send += sendDt;
            }
            inFlight.Sort((x, y) => x.Arrive.CompareTo(y.Arrive));
            while (inFlight.Count > 0 && inFlight[0].Arrive <= now)
            {
                double t = inFlight[0].T;
                inFlight.RemoveAt(0);
                ip.BeginCorrection(now);
                ip.Push(t, now, new Vector3((float)(v * t), 0, 0), new Vector3(v, 0, 0), 0f);
                ip.EndCorrection();
            }
            if (!ip.HasData) continue;
            var (p, _) = ip.Sample(now, (float)frameDt);
            if (prev is { } q && now > 1)
            {
                float step = p.DistanceTo(q);
                worstRatio = Math.Max(worstRatio, step / (v * frameDt));
                if (step < 0.05f * v * frameDt) freezes++;
            }
            prev = p;
        }
        bool ok = worstRatio < maxRatio && (freezes == 0 || burst > 1);
        string lost = burst > 1 ? $"{loss:P0} chance of losing {burst} states at once" : $"{loss:P0} loss";
        GD.Print($"[interp] {(ok ? "ok  " : "FAIL")} {v:F0} m/s, 30 Hz, jitter 60 ms, {lost}: worst step {worstRatio:F2}x v·dt, {freezes} freeze frames");
        return ok;
    }
}
