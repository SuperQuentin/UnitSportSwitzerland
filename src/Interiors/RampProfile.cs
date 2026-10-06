namespace UnitSport.Interiors;

/// <summary>
/// The shape of an underground garage's ramp (#558): a straight run of one storey's drop from
/// the ground floor behind the door to the basement's floor, with a vertical curve (a bevel at
/// half the grade) at each end so a car's hull box does not catch on the crest or the foot.
/// Distances are measured along the run from its <b>top</b> (where the flat apron behind the door
/// ends and the descent begins). Pure arithmetic with no Godot: tier 0 (<c>GarageTests</c>), read
/// by the generator (how long the hole in the floor is), the mesh builder (the surface and its
/// collision) and the validator.
/// </summary>
public static class RampProfile
{
    /// <summary>
    /// The grade of the straight part: 27 %, about 15 degrees. Steeper than a real ramp (Swiss
    /// standards say 15 to 18 %), but a gentler one does not fit the 17 to 20 m deep blocks that make
    /// up most of the real blocks with a car park: the foot would have no room to turn.
    /// </summary>
    public const float Slope = 0.27f;

    /// <summary>The length of each vertical curve, m.</summary>
    public const float Bevel = 1.2f;

    /// <summary>Headroom a car needs under the floor slab the ramp passes beneath, m.</summary>
    public const float Headroom = 2.15f;

    /// <summary>The length of a run that drops <paramref name="drop"/> metres: top to foot.</summary>
    public static float Length(float drop) => drop / Slope + Bevel;

    /// <summary>How far down the run has gone <paramref name="t"/> metres from its top, m.</summary>
    public static float Drop(float t, float drop)
    {
        float len = Length(drop);
        t = Math.Clamp(t, 0f, len);
        // the grade climbs from 0 to Slope over the first bevel, holds, and falls back over the last
        if (t <= Bevel) return Slope * t * t / (2 * Bevel);
        if (t <= len - Bevel) return Slope * Bevel / 2 + Slope * (t - Bevel);
        float r = len - t;
        return drop - Slope * r * r / (2 * Bevel);
    }

    /// <summary>The surface's height above the basement floor, <paramref name="t"/> metres from the top.</summary>
    public static float Height(float t, float drop) => drop - Drop(t, drop);

    /// <summary>
    /// Where the floor slab over the ramp must stop (metres from the top): from there on the surface
    /// is low enough for a car to pass under it. <paramref name="clear"/> is the basement's
    /// headroom (its storey less the slab).
    /// </summary>
    public static float HoleLength(float drop, float clear)
    {
        float need = Math.Max(0f, clear - Headroom);   // the highest the surface may be under the slab
        float lo = 0f, hi = Length(drop);
        for (int i = 0; i < 40; i++)
        {
            float mid = (lo + hi) / 2;
            if (Height(mid, drop) > need) lo = mid; else hi = mid;
        }
        return hi;
    }

    /// <summary>The surface as a polyline of (distance from the top, height above the foot's floor), at most <paramref name="step"/> apart.</summary>
    public static List<(float T, float Y)> Polyline(float drop, float step = 0.5f)
    {
        float len = Length(drop);
        int n = Math.Max(4, (int)MathF.Ceiling(len / step));
        var pts = new List<(float, float)>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            float t = len * i / n;
            pts.Add((t, Height(t, drop)));
        }
        return pts;
    }
}
