using Godot;

namespace UnitSport.Movie;

/// <summary>Where a shot's camera hangs (#776).</summary>
public enum ShotRig
{
    /// <summary>Fixed in the world (a tripod, or a keyed dolly or crane move); may aim at an actor.</summary>
    World,
    /// <summary>A camera car in front of the target, moving with it and looking back at it.</summary>
    Lead,
    /// <summary>Behind or beside the target, following it with some smoothing.</summary>
    Chase,
    /// <summary>Rigid on the target or its vehicle: bumper, wing, cockpit, helmet.</summary>
    Mount,
}

/// <summary>What of an actor a rig follows (#776).</summary>
public enum TargetPart { Body, Head, Ride }

/// <summary>Shake presets (#776): how a handheld or vehicle camera trembles.</summary>
public enum ShakeStyle { Off, Handheld, Walking, Engine, Impact }

/// <summary>A shot's shake (#776). Strength 0..1 scales the preset; the result is the same at every replay.</summary>
public sealed class ShakeSettings
{
    public ShakeStyle Style { get; set; }
    public float Strength { get; set; } = 0.3f;
    /// <summary>Hz; 0 = the preset's own.</summary>
    public float Speed { get; set; }
    /// <summary>0 = all position, 1 = all turning.</summary>
    public float Turn { get; set; } = 0.7f;
    /// <summary>0..1: how much of the turning is roll.</summary>
    public float Roll { get; set; } = 0.2f;

    public ShakeSettings Copy() => (ShakeSettings)MemberwiseClone();
}

/// <summary>What a beat link moves (#776).</summary>
public enum BeatParam { Lens, Shake, Distance, Height, Roll, Orbit, Fade }

/// <summary>How a beat link moves it (#776, the user's three).</summary>
public enum BeatShape
{
    /// <summary>A smooth wave locked to the tempo, at its peak on the beat.</summary>
    Pulse,
    /// <summary>Through a list of values, one per beat (24, 50, 24, … mm).</summary>
    Step,
    /// <summary>Builds over the stretch, drops back on the next beat.</summary>
    Ramp,
}

/// <summary>
/// One camera parameter following the beat (#776). The beats come from the sequence the shot plays
/// in (its music, its game sound, or the markers), never from the shot, so a shot reused elsewhere
/// follows that rhythm.
/// </summary>
public sealed class BeatLink
{
    public BeatParam Param { get; set; }
    public BeatShape Shape { get; set; }
    /// <summary>The size of the move: mm, metres, degrees, 0..1 for shake and fade.</summary>
    public float Amount { get; set; } = 10;
    /// <summary>Every 1, 2 or 4 beats (4 = on the bar); 0 = on the markers instead.</summary>
    public int Every { get; set; } = 1;
    /// <summary>Seconds the whole pattern is moved by.</summary>
    public float Phase { get; set; }
    /// <summary>The values a Step walks through; empty = 0 and <see cref="Amount"/> in turn.</summary>
    public float[] Steps { get; set; } = Array.Empty<float>();

    public BeatLink Copy() => new() { Param = Param, Shape = Shape, Amount = Amount, Every = Every, Phase = Phase, Steps = (float[])Steps.Clone() };
}

/// <summary>What happens between two shots (#776).</summary>
public enum TransitionKind { Cut, Blend, Crossfade, FadeBlack, Wipe, WhipPan, ZoomPunch }

/// <summary>
/// The pure maths of shots (#776): the rig frames, the follow smoothing, the shake and the beat links.
/// Every result is a function of time alone, so the studio scrubs, plays backwards and exports the
/// same frames.
/// </summary>
public static class ShotMath
{
    /// <summary>Metres above an actor's feet that its parts are followed at.</summary>
    public static float PartHeight(TargetPart part) => part switch { TargetPart.Head => 1.6f, TargetPart.Body => 1.0f, _ => 0.8f };

    /// <summary>A follow step for the spring: 30 Hz, the recording's rate.</summary>
    public const double FollowStep = 1.0 / Channels.Rate;

    /// <summary>
    /// Where a smoothed rig's frame is at <paramref name="t"/>: the target's frame followed by a critically
    /// damped spring of time constant <paramref name="smoothing"/> s. Integrated from six time constants
    /// back (where the spring has forgotten where it started), at a fixed step: a pure function of time.
    /// <paramref name="target"/> gives the target's (E, N, Alt, yaw) at a time, or false where it is absent.
    /// </summary>
    public static bool Follow(double t, double smoothing, TargetSampler target,
        out double e, out double n, out double alt, out float yaw)
    {
        if (!target(t, out e, out n, out alt, out yaw)) return false;
        if (smoothing <= 1e-3) return true;
        // start where the target was first present in the window
        double s = t - 6 * smoothing, xe = 0, xn = 0, xa = 0;
        float y0 = 0;
        while (s < t && !target(s, out xe, out xn, out xa, out y0)) s += FollowStep;
        if (s >= t) return true;
        double xy = y0, ve = 0, vn = 0, va = 0, vy = 0;
        double omega = 2.0 / smoothing;
        while (s < t)
        {
            double dt = Math.Min(FollowStep, t - s);
            s += dt;
            if (!target(s, out var te, out var tn, out var ta, out var ty)) continue;
            // the yaw unwrapped next to where the spring is, so it turns the short way
            double tyu = xy + Wrap(ty - xy);
            Spring(ref xe, ref ve, te, omega, dt);
            Spring(ref xn, ref vn, tn, omega, dt);
            Spring(ref xa, ref va, ta, omega, dt);
            Spring(ref xy, ref vy, tyu, omega, dt);
        }
        e = xe; n = xn; alt = xa; yaw = (float)Wrap(xy);
        return true;
    }

    /// <summary>The target's frame at a time: LV95 feet and the yaw it faces; false where it is absent.</summary>
    public delegate bool TargetSampler(double t, out double e, out double n, out double alt, out float yaw);

    // the critically damped spring step (Game Programming Gems 4, 1.10): exact for any dt, never overshoots
    private static void Spring(ref double x, ref double v, double target, double omega, double dt)
    {
        double k = omega * dt;
        double exp = Math.Exp(-k);
        double change = x - target;
        double temp = (v + omega * change) * dt;
        v = (v - omega * temp) * exp;
        x = target + (change + temp) * exp;
    }

    private static double Wrap(double a)
    {
        a %= Math.Tau;
        if (a > Math.PI) a -= Math.Tau;
        else if (a < -Math.PI) a += Math.Tau;
        return a;
    }

    /// <summary>
    /// A pose keyed in a rig's frame put into the world: the key's x right, y up, z back of the frame
    /// facing <paramref name="yaw"/> at LV95 (<paramref name="e"/>, <paramref name="n"/>, <paramref name="alt"/>),
    /// its rotation turned with the frame. With <paramref name="frame"/> (a mount) the frame's full rotation is used.
    /// </summary>
    public static void InFrame(ref CameraPose pose, double e, double n, double alt, float yaw, Quaternion? frame = null)
    {
        var q = frame ?? new Quaternion(Vector3.Up, yaw);
        // keys of a relative rig keep x in E, y in Alt and z in N (see CameraKey)
        var local = new Vector3((float)pose.E, (float)pose.Alt, (float)pose.N);
        var world = q * local;
        pose.E = e + world.X;
        pose.N = n - world.Z;
        pose.Alt = alt + world.Y;
        var r = (q * new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw)).Normalized();
        pose.Qx = r.X; pose.Qy = r.Y; pose.Qz = r.Z; pose.Qw = r.W;
    }

    /// <summary>The inverse of <see cref="InFrame"/>: a world pose put into the rig's frame, for "Frame from view".</summary>
    public static void ToFrame(ref CameraPose pose, double e, double n, double alt, float yaw, Quaternion? frame = null)
    {
        var q = frame ?? new Quaternion(Vector3.Up, yaw);
        var inv = q.Inverse();
        var world = new Vector3((float)(pose.E - e), (float)(pose.Alt - alt), (float)-(pose.N - n));
        var local = inv * world;
        pose.E = local.X;
        pose.Alt = local.Y;
        pose.N = local.Z;
        var r = (inv * new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw)).Normalized();
        pose.Qx = r.X; pose.Qy = r.Y; pose.Qz = r.Z; pose.Qw = r.W;
    }

    // ---- shake ---------------------------------------------------------------------------------

    /// <summary>
    /// The shake at <paramref name="t"/>: a position offset (metres, camera frame) and a turn (radians:
    /// pitch, yaw, roll). Seeded smooth noise: the same shot shakes the same way at every replay.
    /// </summary>
    public static (Vector3 Move, Vector3 Turn) Shake(ShakeSettings s, int seed, double t, float extraStrength = 0)
    {
        if (s.Style == ShakeStyle.Off && extraStrength <= 0) return (Vector3.Zero, Vector3.Zero);
        var (hz, metres, degrees) = s.Style switch
        {
            ShakeStyle.Walking => (1.9f, 0.05f, 1.2f),
            ShakeStyle.Engine => (17f, 0.008f, 0.35f),
            ShakeStyle.Impact => (5f, 0.12f, 3.5f),
            _ => (0.7f, 0.03f, 1.6f),   // handheld, and the shake a beat link adds to an Off shot
        };
        if (s.Speed > 0) hz = s.Speed;
        float strength = Math.Clamp(s.Strength + extraStrength, 0, 2);
        if (s.Style == ShakeStyle.Off) strength = Math.Clamp(extraStrength, 0, 2);
        float move = metres * strength * (1 - s.Turn), turn = Mathf.DegToRad(degrees) * strength * s.Turn;
        double u = t * hz;
        var m = new Vector3(Noise(seed, 0, u), Noise(seed, 1, u), Noise(seed, 2, u)) * move;
        // walking bobs: more up and down than sideways
        if (s.Style == ShakeStyle.Walking) m.Y *= 1.8f;
        var r = new Vector3(Noise(seed, 3, u), Noise(seed, 4, u), Noise(seed, 5, u) * s.Roll * 2) * turn;
        return (m, r);
    }

    /// <summary>Smooth noise in -1..1: three octaves of sines at seeded frequencies and phases.</summary>
    private static float Noise(int seed, int axis, double u)
    {
        double sum = 0, weight = 0;
        for (int o = 0; o < 3; o++)
        {
            uint h = Hash((uint)seed * 73856093u ^ (uint)(axis * 19349663) ^ (uint)(o * 83492791));
            double f = (1 << o) * (0.8 + 0.4 * (h & 0xffff) / 65535.0);
            double phase = (h >> 16) / 65535.0 * Math.Tau;
            double w = 1.0 / (1 << o);
            sum += Math.Sin(u * Math.Tau * f + phase) * w;
            weight += w;
        }
        return (float)(sum / weight);
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    // ---- beat links ------------------------------------------------------------------------------

    /// <summary>
    /// What <paramref name="link"/> adds at <paramref name="t"/>, given the sequence's beats (sorted, downbeat
    /// flag on every fourth) and markers. Zero before the first beat or after the last.
    /// </summary>
    public static float Beat(BeatLink link, double t, IReadOnlyList<(double T, bool Downbeat)> beats, IReadOnlyList<double> markers)
    {
        t -= link.Phase;
        // the beats this link moves on: every Nth (every 4th = the bar's downbeats), or the markers
        double from = double.NaN, to = double.NaN;
        int index = -1;
        if (link.Every == 0)
        {
            for (int i = 0; i < markers.Count; i++)
            {
                if (markers[i] <= t) { from = markers[i]; index = i; }
                else { to = markers[i]; break; }
            }
        }
        else
        {
            int every = Math.Max(1, link.Every);
            for (int i = 0; i < beats.Count; i += every)
            {
                if (beats[i].T <= t) { from = beats[i].T; index = i / every; }
                else { to = beats[i].T; break; }
            }
        }
        // before the first beat or after the last there is no beat length to move on
        if (double.IsNaN(from) || double.IsNaN(to)) return 0;
        double u = Math.Clamp((t - from) / Math.Max(1e-3, to - from), 0, 1);
        return link.Shape switch
        {
            BeatShape.Pulse => link.Amount * (float)(0.5 * (1 + Math.Cos(Math.Tau * u))),
            BeatShape.Ramp => link.Amount * (float)u,
            _ => link.Steps.Length > 0 ? link.Steps[index % link.Steps.Length] : index % 2 == 0 ? 0 : link.Amount,
        };
    }
}
