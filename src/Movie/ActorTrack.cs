namespace UnitSport.Movie;

/// <summary>
/// The float channels of one recorded frame of a player (#638), in the order a frame stores them.
/// They are the player's replicated visual state (<c>FootPlayer</c>'s <c>NetVel</c>, <c>NetYaw</c>,
/// <c>Anim</c>, <c>DeckPos</c>, <c>DeckYaw</c>, <c>NetPose</c>): what another peer draws a player
/// from, so a puppet fed them draws the same thing. The position is kept apart, in doubles (LV95).
/// </summary>
public static class Channels
{
    public const int Vel = 0;        // 3
    public const int Yaw = 3;
    public const int Anim = 4;       // 4
    public const int DeckPos = 8;    // 3
    public const int DeckYaw = 11;
    public const int PoseLen = 12;   // how many of the pose floats are used
    public const int Pose = 13;      // up to MaxPose
    public const int MaxPose = 17;   // a body pose (8), a train's joints (3), VR hands (6)
    public const int Stride = Pose + MaxPose;

    /// <summary>Frames a second: the network's own rate, which every channel is already smooth at.</summary>
    public const double Rate = 30.0;
}

/// <summary>
/// One player at one instant: what a recorder fills from a live player and a sampler fills for a
/// puppet. Reused, never allocated per frame. The discrete values are the change-only properties
/// (ride, seat, held item, clothes, …): numbers in <see cref="Num"/>, strings in <see cref="Str"/>,
/// by index into the project's property names.
/// </summary>
public sealed class ActorState
{
    public double E, N, Alt;
    public readonly float[] F = new float[Channels.Stride];
    public readonly long[] Num;
    public readonly string[] Str;

    public ActorState(int discrete)
    {
        Num = new long[discrete];
        Str = new string[discrete];
        Array.Fill(Str, "");
    }

    public int PoseLength => (int)F[Channels.PoseLen];
}

/// <summary>A change-only property taking a new value at <see cref="T"/>.</summary>
public readonly record struct DiscreteEvent(double T, int Prop, long Num, string Str);

/// <summary>
/// One recorded stretch of one player: frames at <see cref="Channels.Rate"/> plus the change-only
/// properties as events. Times are local, from 0 at the first frame. Sampling is a pure function of
/// time, so playing backwards and scrubbing cost the same as playing.
/// </summary>
public sealed class ActorTrack
{
    public readonly double[] Times;
    public readonly double[] Pos;      // E, N, Alt per frame
    public readonly float[] F;         // Channels.Stride per frame
    public readonly long[] BaseNum;    // the change-only values at time 0
    public readonly string[] BaseStr;
    public readonly DiscreteEvent[] Events;   // sorted by time

    public ActorTrack(double[] times, double[] pos, float[] f, long[] baseNum, string[] baseStr, DiscreteEvent[] events)
    {
        if (times.Length == 0) throw new ArgumentException("a track needs a frame");
        if (pos.Length != times.Length * 3 || f.Length != times.Length * Channels.Stride)
            throw new ArgumentException("channel arrays do not match the frame count");
        Times = times; Pos = pos; F = f; BaseNum = baseNum; BaseStr = baseStr; Events = events;
    }

    public int Frames => Times.Length;
    public double Duration => Times[^1];
    public int Discrete => BaseNum.Length;

    /// <summary>The last frame at or before <paramref name="t"/> (clamped to the track).</summary>
    public int FrameAt(double t)
    {
        if (t <= Times[0]) return 0;
        if (t >= Times[^1]) return Times.Length - 1;
        int i = Array.BinarySearch(Times, t);
        return i >= 0 ? i : ~i - 1;
    }

    /// <summary>
    /// The player at local time <paramref name="t"/>, into <paramref name="o"/>: positions and
    /// velocities interpolated, the yaw the short way round, the pose's rotation slerped. A channel
    /// that jumps between two frames (a gait phase or crank angle wrapping, a pose changing shape)
    /// takes the nearer frame instead of sweeping through everything in between.
    /// </summary>
    public void Sample(double t, ActorState o)
    {
        int i = FrameAt(t);
        int j = Math.Min(i + 1, Times.Length - 1);
        float a = j == i ? 0f : (float)Math.Clamp((t - Times[i]) / (Times[j] - Times[i]), 0, 1);
        int near = a < 0.5f ? i : j;

        o.E = Lerp(Pos[i * 3], Pos[j * 3], a);
        o.N = Lerp(Pos[i * 3 + 1], Pos[j * 3 + 1], a);
        o.Alt = Lerp(Pos[i * 3 + 2], Pos[j * 3 + 2], a);

        int fi = i * Channels.Stride, fj = j * Channels.Stride, fn = near * Channels.Stride;
        for (int c = 0; c < 3; c++)
        {
            o.F[Channels.Vel + c] = Lerp(F[fi + Channels.Vel + c], F[fj + Channels.Vel + c], a);
            o.F[Channels.DeckPos + c] = Lerp(F[fi + Channels.DeckPos + c], F[fj + Channels.DeckPos + c], a);
        }
        o.F[Channels.Yaw] = LerpAngle(F[fi + Channels.Yaw], F[fj + Channels.Yaw], a);
        o.F[Channels.DeckYaw] = LerpAngle(F[fi + Channels.DeckYaw], F[fj + Channels.DeckYaw], a);
        for (int c = 0; c < 4; c++)
        {
            float x = F[fi + Channels.Anim + c], y = F[fj + Channels.Anim + c];
            o.F[Channels.Anim + c] = Math.Abs(y - x) > JumpLimit(x, y) ? F[fn + Channels.Anim + c] : Lerp(x, y, a);
        }

        int len = (int)F[fi + Channels.PoseLen];
        if (len != (int)F[fj + Channels.PoseLen] || len < 8)
        {
            Array.Copy(F, fn + Channels.PoseLen, o.F, Channels.PoseLen, 1 + Channels.MaxPose);
        }
        else
        {
            o.F[Channels.PoseLen] = len;
            int pi = fi + Channels.Pose, pj = fj + Channels.Pose, po = Channels.Pose;
            Nlerp(F, pi, pj, a, o.F, po);
            for (int c = 4; c < len; c++) o.F[po + c] = Lerp(F[pi + c], F[pj + c], a);
        }

        ValuesAt(t, o.Num, o.Str);
    }

    /// <summary>The change-only properties at local time <paramref name="t"/>.</summary>
    public void ValuesAt(double t, long[] num, string[] str)
    {
        Array.Copy(BaseNum, num, BaseNum.Length);
        Array.Copy(BaseStr, str, BaseStr.Length);
        foreach (var e in Events)
        {
            if (e.T > t) break;
            num[e.Prop] = e.Num;
            str[e.Prop] = e.Str;
        }
    }

    /// <summary>The frames from <paramref name="from"/> to <paramref name="to"/> (local times) as a track of their own.</summary>
    public ActorTrack Cut(double from, double to)
    {
        int a = FrameAt(from), b = Math.Max(a, FrameAt(to));
        int n = b - a + 1;
        double t0 = Times[a];
        var times = new double[n];
        for (int k = 0; k < n; k++) times[k] = Times[a + k] - t0;
        var pos = new double[n * 3];
        Array.Copy(Pos, a * 3, pos, 0, n * 3);
        var f = new float[n * Channels.Stride];
        Array.Copy(F, a * Channels.Stride, f, 0, n * Channels.Stride);
        var num = new long[Discrete];
        var str = new string[Discrete];
        ValuesAt(t0, num, str);
        var events = Events.Where(e => e.T > t0 && e.T <= Times[b]).Select(e => e with { T = e.T - t0 }).ToArray();
        return new ActorTrack(times, pos, f, num, str, events);
    }

    // a phase in [0, 1) wraps by ~1, a crank angle by ~2π: anything that moves by more than half its
    // own size in one frame (1/30 s) jumped rather than moved
    private static float JumpLimit(float x, float y) => Math.Max(0.5f, 0.5f * Math.Max(Math.Abs(x), Math.Abs(y)));

    private static double Lerp(double x, double y, float a) => x + (y - x) * a;
    private static float Lerp(float x, float y, float a) => x + (y - x) * a;

    private static float LerpAngle(float x, float y, float a)
    {
        float d = (y - x) % MathF.Tau;
        if (d > MathF.PI) d -= MathF.Tau;
        else if (d < -MathF.PI) d += MathF.Tau;
        return x + d * a;
    }

    /// <summary>Normalised lerp of the quaternions at <paramref name="i"/> and <paramref name="j"/>, the short way round.</summary>
    private static void Nlerp(float[] src, int i, int j, float a, float[] dst, int o)
    {
        float dot = src[i] * src[j] + src[i + 1] * src[j + 1] + src[i + 2] * src[j + 2] + src[i + 3] * src[j + 3];
        float s = dot < 0 ? -1f : 1f, len = 0;
        for (int c = 0; c < 4; c++)
        {
            float v = Lerp(src[i + c], s * src[j + c], a);
            dst[o + c] = v;
            len += v * v;
        }
        len = MathF.Sqrt(len);
        if (len < 1e-6f) { dst[o] = 0; dst[o + 1] = 0; dst[o + 2] = 0; dst[o + 3] = 1; return; }
        for (int c = 0; c < 4; c++) dst[o + c] /= len;
    }
}
