using System;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The fly camera's frame blended by distance (#2053, docs/plans/planets.md section 8): three weights from 1 near a body
/// to 0 away from it, each at its own rate, so nothing switches between bodies.
/// <list type="bullet">
/// <item><see cref="SpinWeight"/>: how much the camera turns with the body (the ground still under it), gone a short height
/// over the ground or the air;</item>
/// <item><see cref="UpWeight"/>: how much up is the local vertical (the view kept level, level flight, the look's yaw about
/// the vertical), gone soon after space is reached: past it the camera is fully free;</item>
/// <item><see cref="RestShare"/>: how much "standing still" is moving with a body, by its sphere of influence, nested from
/// the Sun down (<see cref="RestStep"/>), slowest.</item>
/// </list>
/// Pure: the frame tree and the bodies' registry only.
/// </summary>
public static class SpectatorFrame
{
    /// <summary>Co-rotation fades from this share of the body's radius over its air's top (or ground)...</summary>
    public const double SpinFrom = 0.002;

    /// <summary>...to none at this share.</summary>
    public const double SpinTo = 0.02;

    /// <summary>Up and level fade from this share of the body's radius over its air's top (or ground)...</summary>
    public const double UpFrom = 0.005;

    /// <summary>...to none at this share: the camera fully free.</summary>
    public const double UpTo = 0.04;

    /// <summary>A body's motion is the rest frame whole within this share of its sphere of influence...</summary>
    public const double RestFull = 0.6;

    /// <summary>...and none past this share.</summary>
    public const double RestGone = 1.4;

    /// <summary>Hermite smoothstep of <paramref name="x"/> between <paramref name="a"/> and <paramref name="b"/>: 0 to 1, flat at both ends.</summary>
    public static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Where the body's air ends over its reference surface, metres (0 without air): space starts there.</summary>
    public static double Top(CelestialBody body) => body.Air?.ThicknessM ?? 0;

    /// <summary>How much the camera turns with <paramref name="body"/> <paramref name="height"/> metres over its reference.</summary>
    public static double SpinWeight(CelestialBody body, double height) =>
        1 - Smooth(Top(body) + SpinFrom * body.Shape.A, Top(body) + SpinTo * body.Shape.A, height);

    /// <summary>
    /// How much the camera turns with <paramref name="body"/> at <paramref name="place"/> (its fixed frame, metres): by its
    /// height over the reference, and near Saturn by its distance from the rings' slab as well (#2030's surface for the
    /// speed rule; the rings and their chunks are fixed in Saturn's frame, so among them they hold still).
    /// </summary>
    public static double SpinWeight(CelestialBody body, double height, DVec3 place)
    {
        double w = SpinWeight(body, height);
        if (body.Id == BodyId.Saturn)
            w = Math.Max(w, 1 - Smooth(SpinFrom * body.Shape.A, SpinTo * body.Shape.A, CelestialBody.SaturnRingSlab.Distance(place)));
        return w;
    }

    /// <summary>How much up is <paramref name="body"/>'s vertical <paramref name="height"/> metres over its reference.</summary>
    public static double UpWeight(CelestialBody body, double height) =>
        1 - Smooth(Top(body) + UpFrom * body.Shape.A, Top(body) + UpTo * body.Shape.A, height);

    /// <summary>How much standing still is moving with <paramref name="body"/>, <paramref name="distance"/> metres from its centre (1 for the Sun).</summary>
    public static double RestShare(CelestialBody body, double distance) =>
        body.Parent == null ? 1 : 1 - Smooth(RestFull * body.SphereOfInfluence, RestGone * body.SphereOfInfluence, distance);

    /// <summary>
    /// How far the rest frame moved between two sky moments, seen from <paramref name="anchor"/>'s centre (ICRF axes,
    /// metres), for a camera at <paramref name="camera"/> from that centre (ICRF, at <paramref name="m1"/>): the Sun's
    /// inertial frame, then down the tree each child's motion as far as <see cref="RestShare"/> of the nearest child in
    /// its sphere's terms. Zero where the rest frame is the anchor's own (on the Moon, the Moon's).
    /// </summary>
    public static DVec3 RestStep(BodyId anchor, DVec3 camera, SkyMoment m0, SkyMoment m1)
    {
        DVec3 Moved(BodyId b) => b == anchor ? DVec3.Zero : Frames.Offset(anchor, b, m1) - Frames.Offset(anchor, b, m0);
        var step = Moved(BodyId.Sun);
        var at = BodyId.Sun;
        while (true)
        {
            CelestialBody? best = null;
            double share = 0;
            foreach (var child in CelestialBody.All)
            {
                if (child.Parent != at) continue;
                var centre = child.Id == anchor ? DVec3.Zero : Frames.Offset(anchor, child.Id, m1);
                double s = RestShare(child, (camera - centre).Length);
                if (s > share)
                {
                    share = s;
                    best = child;
                }
            }
            if (best == null) break;
            step = step * (1 - share) + Moved(best.Id) * share;
            at = best.Id;
        }
        return step;
    }

    /// <summary>
    /// The turn that keeps a body-fixed vector still against the stars from <paramref name="m0"/> to <paramref name="m1"/>:
    /// the body's axes then and now (<see cref="Frames.Orientation"/>, the local day included) as an axis in its fixed
    /// frame and an angle, radians (right-handed). A body turning east gives a turn west about its pole.
    /// </summary>
    public static (DVec3 Axis, double Angle) Unspin(BodyId body, SkyMoment m0, SkyMoment m1)
    {
        var r0 = Frames.Orientation(body, m0);
        var r1 = Frames.Orientation(body, m1);
        // M = R1ᵀ R0: a vector fixed against the stars, in the body's frame now
        var c0 = r1.ApplyInverse(r0.C0);
        var c1 = r1.ApplyInverse(r0.C1);
        var c2 = r1.ApplyInverse(r0.C2);
        double cos = (c0.X + c1.Y + c2.Z - 1) / 2;
        var axis = new DVec3(c1.Z - c2.Y, c2.X - c0.Z, c0.Y - c1.X);
        double sin2 = axis.Length;
        if (!(sin2 > 1e-15)) return (new DVec3(0, 0, 1), 0);
        return (axis / sin2, Math.Atan2(sin2 / 2, cos));
    }

    /// <summary><paramref name="v"/> turned by <paramref name="angle"/> about the unit <paramref name="axis"/> (Rodrigues, right-handed).</summary>
    public static DVec3 Turn(DVec3 v, DVec3 axis, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + axis.Cross(v) * s + axis * (axis.Dot(v) * (1 - c));
    }
}
