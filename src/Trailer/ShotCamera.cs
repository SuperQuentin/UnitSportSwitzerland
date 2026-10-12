using System;
using System.Collections.Generic;
using Godot;

namespace UnitSport.Trailer;

/// <summary>
/// Where a shot's camera is at a key time: each key's eye and look worked out in this frame's world
/// space (actor-relative keys move with their actor, world keys follow the floating origin), then a
/// cubic through them with Catmull-Rom slopes per second, so the move keeps its speed through every
/// key. The lens eases from key to key; the result is damped by <see cref="Shot.Smooth"/> and never
/// goes under the ground.
/// </summary>
public sealed class ShotCamera
{
    /// <summary>A world spot here and now (its height above the surface found), or null while the ground is not in.</summary>
    public required Func<Spot, Vector3?> Place { get; init; }
    /// <summary>An actor's position and its flat travel direction, or null when it is not there.</summary>
    public required Func<int, (Vector3 At, Vector3 Ahead)?> Actor { get; init; }
    /// <summary>An actor's first-person eye and the way its body faces, or null when it rides nothing.</summary>
    public Func<int, (Vector3 Eye, Vector3 Ahead)?> Seat { get; init; } = _ => null;

    /// <summary>A point on a shot's road: (route, arc, right, up), or null while it is not built.</summary>
    public Func<string, Vector3, Vector3?> Road { get; init; } = (_, _) => null;
    /// <summary>A point of the shot's set, in world space (<see cref="Pt.Set"/>); null with no set.</summary>
    public Func<Vector3, Vector3?> SetPoint { get; init; } = _ => null;

    /// <summary>The surface under a point, for keeping the eye above it.</summary>
    public required Func<Vector3, float?> Surface { get; init; }

    /// <summary>The lowest the eye may go over the surface, m.</summary>
    public float Clearance { get; set; } = 0.5f;

    private readonly Shot _shot;
    private readonly Vector3[] _eyes, _looks;
    private Vector3 _eye, _look;
    private bool _primed;

    public ShotCamera(Shot shot)
    {
        _shot = shot;
        _eyes = new Vector3[shot.Keys.Count];
        _looks = new Vector3[shot.Keys.Count];
    }

    /// <summary>Full-frame lens (mm) to a vertical field of view (degrees): 24 mm of film height.</summary>
    public static float Fov(float lens) => Mathf.RadToDeg(2f * Mathf.Atan(12f / lens));

    /// <summary>The next damped frame resets to where the path is (the first frame of a shot).</summary>
    public void Reset() => _primed = false;

    /// <summary>
    /// Puts <paramref name="camera"/> where the path is at shot time <paramref name="t"/> (s from the
    /// first frame). False while a key's point cannot be placed yet (its ground or its actor not in).
    /// </summary>
    public bool Apply(Camera3D camera, double t, double dt)
    {
        var keys = _shot.Keys;
        double kt = _shot.KeysFrom + t;
        for (int i = 0; i < keys.Count; i++)
        {
            if (Resolve(keys[i].Eye, null) is not { } eye) return false;
            if (Resolve(keys[i].Look, eye) is not { } look) return false;
            _eyes[i] = eye;
            _looks[i] = look;
        }

        var (seg, s) = Segment(keys, kt);
        var wantEye = Hermite(keys, _eyes, seg, s);
        var wantLook = Hermite(keys, _looks, seg, s);
        float lens = keys.Count == 1 ? keys[0].Lens : Mathf.Lerp(keys[seg].Lens, keys[seg + 1].Lens, Ease(s));
        float roll = keys.Count == 1 ? keys[0].Roll : Mathf.Lerp(keys[seg].Roll, keys[seg + 1].Roll, Ease(s));

        if (!_primed || _shot.Smooth <= 0f)
        {
            _eye = wantEye;
            _look = wantLook;
            _primed = true;
        }
        else
        {
            float a = 1f - Mathf.Exp(-(float)dt / _shot.Smooth);
            _eye = _eye.Lerp(wantEye, a);
            _look = _look.Lerp(wantLook, a);
        }

        var at = _eye;
        if (Surface(at) is { } ground && at.Y < ground + Clearance) at.Y = ground + Clearance;
        var forward = _look - at;
        if (forward.LengthSquared() < 1e-6f) forward = Vector3.Forward;
        var basis = Basis.LookingAt(forward.Normalized(), Mathf.Abs(forward.Normalized().Y) > 0.999f ? Vector3.Forward : Vector3.Up);
        if (_shot.Shake > 0f)
        {
            // a slow handheld drift: three incommensurate sines per axis, never a jitter
            float yaw = Mathf.DegToRad(_shot.Shake) * (Mathf.Sin((float)t * 1.3f) * 0.6f + Mathf.Sin((float)t * 2.9f + 1f) * 0.3f + Mathf.Sin((float)t * 7.1f + 2f) * 0.1f);
            float pitch = Mathf.DegToRad(_shot.Shake) * (Mathf.Sin((float)t * 1.7f + 3f) * 0.6f + Mathf.Sin((float)t * 3.7f) * 0.3f + Mathf.Sin((float)t * 6.3f + 1f) * 0.1f);
            basis = basis * new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, pitch);
        }
        if (roll != 0f) basis = basis * new Basis(Vector3.Back, Mathf.DegToRad(roll));
        camera.GlobalTransform = new Transform3D(basis.Orthonormalized(), at);
        camera.Fov = Fov(lens);
        return true;
    }

    /// <summary>Where a key's point is now; a direction needs the eye it starts from.</summary>
    private Vector3? Resolve(Pt p, Vector3? eye)
    {
        if (p.IsDirection)
        {
            if (eye is not { } from) return null;
            float b = Mathf.DegToRad(p.Bearing), pitch = Mathf.DegToRad(p.Pitch);
            // a compass bearing: north is −Z, east +X
            var dir = new Vector3(Mathf.Sin(b) * Mathf.Cos(pitch), Mathf.Sin(pitch), -Mathf.Cos(b) * Mathf.Cos(pitch));
            return from + dir * 2000f;
        }
        if (p.InSet is { } local) return SetPoint(local);
        if (p.World is { } spot) return Place(spot);
        if (p.Route is { } route) return Road(route, p.Offset);
        if (p.Seat) return Seat(p.Actor) is { } seat ? seat.Eye + seat.Ahead * p.Offset.Z + Vector3.Up * p.Offset.Y : null;
        if (Actor(p.Actor) is not { } frame) return null;
        var right = frame.Ahead.Cross(Vector3.Up).Normalized();
        return frame.At + right * p.Offset.X + Vector3.Up * p.Offset.Y - frame.Ahead * p.Offset.Z;
    }

    /// <summary>Which pair of keys <paramref name="t"/> falls between, and how far along (0..1).</summary>
    private static (int Seg, float S) Segment(IReadOnlyList<Key> keys, double t)
    {
        if (keys.Count == 1 || t <= keys[0].T) return (0, 0f);
        for (int i = 0; i < keys.Count - 1; i++)
            if (t <= keys[i + 1].T)
                return (i, (float)((t - keys[i].T) / Math.Max(1e-6, keys[i + 1].T - keys[i].T)));
        return (keys.Count - 2, 1f);
    }

    /// <summary>A cubic Hermite through the points with Catmull-Rom slopes measured per second.</summary>
    private static Vector3 Hermite(IReadOnlyList<Key> keys, Vector3[] p, int i, float s)
    {
        if (keys.Count == 1) return p[0];
        float h = (float)(keys[i + 1].T - keys[i].T);
        var m0 = Slope(keys, p, i);
        var m1 = Slope(keys, p, i + 1);
        float s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * p[i] + (s3 - 2 * s2 + s) * h * m0 + (-2 * s3 + 3 * s2) * p[i + 1] + (s3 - s2) * h * m1;
    }

    private static Vector3 Slope(IReadOnlyList<Key> keys, Vector3[] p, int i)
    {
        int a = Math.Max(0, i - 1), b = Math.Min(keys.Count - 1, i + 1);
        float span = (float)(keys[b].T - keys[a].T);
        return span > 1e-6f ? (p[b] - p[a]) / span : Vector3.Zero;
    }

    private static float Ease(float s) => s * s * (3f - 2f * s);
}
