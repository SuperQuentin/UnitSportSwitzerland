namespace UnitSport.Movie;

/// <summary>How the camera goes from a key to the next one (#669).</summary>
public enum KeyEase
{
    /// <summary>A Catmull-Rom path through the keys, rotation and lens eased in and out.</summary>
    Smooth,
    /// <summary>Straight there at an even speed.</summary>
    Linear,
    /// <summary>Holds this key's view, then jumps to the next one: a cut.</summary>
    Cut,
}

/// <summary>
/// One keyframe of the movie's camera (#669): where it is (LV95, so origin shifts never move it),
/// which way it looks (a world-space quaternion: shifts only translate) and its lens, plus how
/// it moves on to the next key and, optionally, the actor it keeps aimed at.
/// </summary>
public sealed class CameraKey
{
    public double T { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public double Alt { get; set; }
    public float Qx { get; set; }
    public float Qy { get; set; }
    public float Qz { get; set; }
    public float Qw { get; set; } = 1;
    /// <summary>Focal length on a full-frame (36×24) sensor, mm.</summary>
    public float Lens { get; set; } = 35;
    public KeyEase Ease { get; set; } = KeyEase.Smooth;
    /// <summary>The actor lane it aims at, or -1 to look where it was keyed to look.</summary>
    public int LookAt { get; set; } = -1;

    public CameraKey Copy() => (CameraKey)MemberwiseClone();
}

/// <summary>
/// A cut in the movie's program (#675): from <see cref="T"/> on, the movie shows camera
/// <see cref="Camera"/> (an index into <see cref="MovieProject.Cameras"/>), until the next cut.
/// </summary>
public sealed class CameraCut
{
    public double T { get; set; }
    public int Camera { get; set; }
}

/// <summary>A camera at one instant: what <see cref="CameraTrack.Sample"/> fills.</summary>
public struct CameraPose
{
    public double E, N, Alt;
    public float Qx, Qy, Qz, Qw;
    public float Lens;
    /// <summary>The actor lane to aim at instead of the rotation, or -1.</summary>
    public int LookAt;
}

/// <summary>
/// The movie's camera as keyframes on the timeline (#669). Pure: the studio turns a pose into a
/// <c>Camera3D</c>, aiming it at an actor when the key says so.
/// </summary>
public sealed class CameraTrack
{
    public const float MinLens = 14, MaxLens = 200;

    /// <summary>What the timeline and the picker call it (#675): "Cam 2", or what the user renamed it to.</summary>
    public string Name { get; set; } = "Cam 1";

    public List<CameraKey> Keys { get; } = new();

    /// <summary>Vertical field of view, degrees, of a lens on a 24 mm high sensor.</summary>
    public static float Fov(float lens) => (float)(2 * Math.Atan(12.0 / Math.Clamp(lens, MinLens, MaxLens)) * 180 / Math.PI);

    /// <summary>
    /// Keeps <paramref name="key"/> at its time: it replaces a key within <paramref name="within"/>
    /// seconds (keeping that key's ease and aim unless <paramref name="replaceOptions"/>), else goes in
    /// order. The key now there.
    /// </summary>
    public CameraKey Set(CameraKey key, double within, bool replaceOptions = false)
    {
        int near = Keys.FindIndex(k => Math.Abs(k.T - key.T) <= within);
        if (near >= 0)
        {
            var old = Keys[near];
            if (!replaceOptions) { key.Ease = old.Ease; key.LookAt = old.LookAt; }
            key.T = old.T;
            Keys[near] = key;
            return key;
        }
        int i = Keys.FindIndex(k => k.T > key.T);
        Keys.Insert(i < 0 ? Keys.Count : i, key);
        return key;
    }

    /// <summary>Moves a key in time, keeping the order.</summary>
    public void Retime(CameraKey key, double t)
    {
        Keys.Remove(key);
        key.T = Math.Max(0, t);
        int i = Keys.FindIndex(k => k.T > key.T);
        Keys.Insert(i < 0 ? Keys.Count : i, key);
    }

    public bool Remove(CameraKey key) => Keys.Remove(key);

    /// <summary>The key nearest <paramref name="t"/> within <paramref name="tolerance"/> s, or null.</summary>
    public CameraKey? Near(double t, double tolerance)
    {
        CameraKey? best = null;
        foreach (var k in Keys)
            if (Math.Abs(k.T - t) <= tolerance && (best == null || Math.Abs(k.T - t) < Math.Abs(best.T - t))) best = k;
        return best;
    }

    /// <summary>The camera at <paramref name="t"/>; false when there is no key at all.</summary>
    public bool Sample(double t, out CameraPose pose)
    {
        pose = default;
        if (Keys.Count == 0) return false;
        int i = -1;   // the last key at or before t (a loop: this runs every frame, a lambda would allocate)
        while (i + 1 < Keys.Count && Keys[i + 1].T <= t) i++;
        if (i < 0) { Take(Keys[0], ref pose); return true; }
        var a = Keys[i];
        if (i == Keys.Count - 1 || a.Ease == KeyEase.Cut) { Take(a, ref pose); return true; }
        var b = Keys[i + 1];
        double span = b.T - a.T;
        double u = span <= 1e-9 ? 1 : Math.Clamp((t - a.T) / span, 0, 1);

        if (a.Ease == KeyEase.Linear)
        {
            pose.E = Lerp(a.E, b.E, u); pose.N = Lerp(a.N, b.N, u); pose.Alt = Lerp(a.Alt, b.Alt, u);
            Slerp(a, b, (float)u, ref pose);
            pose.Lens = (float)Lerp(a.Lens, b.Lens, u);
        }
        else
        {
            // a Catmull-Rom path through the keys on either side, so the camera passes every key
            // without a corner; the turn and the lens ease in and out of each key
            var p0 = i > 0 ? Keys[i - 1] : a;
            var p3 = i + 2 < Keys.Count ? Keys[i + 2] : b;
            pose.E = CatmullRom(p0.E, a.E, b.E, p3.E, u);
            pose.N = CatmullRom(p0.N, a.N, b.N, p3.N, u);
            pose.Alt = CatmullRom(p0.Alt, a.Alt, b.Alt, p3.Alt, u);
            double s = u * u * (3 - 2 * u);
            Slerp(a, b, (float)s, ref pose);
            pose.Lens = (float)Lerp(a.Lens, b.Lens, s);
        }
        // aiming: the key being left decides; a look-at key keeps aiming until the next key takes over
        pose.LookAt = a.LookAt;
        return true;
    }

    private static void Take(CameraKey k, ref CameraPose p)
    {
        p.E = k.E; p.N = k.N; p.Alt = k.Alt;
        p.Qx = k.Qx; p.Qy = k.Qy; p.Qz = k.Qz; p.Qw = k.Qw;
        p.Lens = k.Lens;
        p.LookAt = k.LookAt;
    }

    private static double Lerp(double x, double y, double u) => x + (y - x) * u;

    private static double CatmullRom(double p0, double p1, double p2, double p3, double u)
    {
        double u2 = u * u, u3 = u2 * u;
        return 0.5 * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u2 + (3 * p1 - p0 - 3 * p2 + p3) * u3);
    }

    /// <summary>The rotation from key a to key b at <paramref name="u"/>, the short way round.</summary>
    private static void Slerp(CameraKey a, CameraKey b, float u, ref CameraPose p)
    {
        float dot = a.Qx * b.Qx + a.Qy * b.Qy + a.Qz * b.Qz + a.Qw * b.Qw;
        float sign = dot < 0 ? -1 : 1;
        dot = Math.Abs(dot);
        float wa, wb;
        if (dot > 0.9995f) { wa = 1 - u; wb = u; }
        else
        {
            float th = MathF.Acos(dot), sin = MathF.Sin(th);
            wa = MathF.Sin((1 - u) * th) / sin;
            wb = MathF.Sin(u * th) / sin;
        }
        wb *= sign;
        p.Qx = wa * a.Qx + wb * b.Qx; p.Qy = wa * a.Qy + wb * b.Qy; p.Qz = wa * a.Qz + wb * b.Qz; p.Qw = wa * a.Qw + wb * b.Qw;
        float len = MathF.Sqrt(p.Qx * p.Qx + p.Qy * p.Qy + p.Qz * p.Qz + p.Qw * p.Qw);
        if (len > 1e-6f) { p.Qx /= len; p.Qy /= len; p.Qz /= len; p.Qw /= len; }
    }
}
