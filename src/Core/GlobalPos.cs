using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// A position that means the same place whatever the origin is (#185): LV95 metres and the
/// altitude, in doubles. Anything that keeps a position across frames should keep one of these
/// rather than a world-space <c>Vector3</c>, which changes meaning at every origin shift.
/// Converted with <see cref="WorldOrigin.ToWorld(GlobalPos)"/> and <see cref="WorldOrigin.ToGlobal"/>.
///
/// <para>
/// It is also the only form a single position takes on the network: every peer has its own
/// origin, so a world-space <c>Vector3</c> means nothing to the receiver. <see cref="Write"/> and
/// <see cref="Read"/> put one in a spawn or RPC dictionary; an RPC passes <see cref="E"/>,
/// <see cref="N"/> and <see cref="Alt"/> as three doubles. A list of points travels as float
/// offsets from one <see cref="OriginFrame.Anchor"/>.
/// </para>
/// </summary>
public readonly record struct GlobalPos(double E, double N, double Alt)
{
    public double HorizontalDistanceTo(GlobalPos other)
    {
        double de = E - other.E, dn = N - other.N;
        return System.Math.Sqrt(de * de + dn * dn);
    }

    public double DistanceTo(GlobalPos other)
    {
        double de = E - other.E, dn = N - other.N, da = Alt - other.Alt;
        return System.Math.Sqrt(de * de + dn * dn + da * da);
    }

    /// <summary>The square of <see cref="HorizontalDistanceTo"/>, for comparisons that skip the root.</summary>
    public double HorizontalDistanceSquaredTo(GlobalPos other) => HorizontalDistanceSquared(E, N, other.E, other.N);

    /// <summary>
    /// The horizontal distance between two LV95 points held as raw eastings and northings rather than
    /// <see cref="GlobalPos"/> values (#1443). The same metric as <see cref="HorizontalDistanceTo"/>, kept here
    /// so that both become Earth distances together.
    /// </summary>
    public static double HorizontalDistance(double e1, double n1, double e2, double n2)
    {
        double de = e1 - e2, dn = n1 - n2;
        return System.Math.Sqrt(de * de + dn * dn);
    }

    /// <summary>The square of <see cref="HorizontalDistance"/>, for comparisons that skip the root.</summary>
    public static double HorizontalDistanceSquared(double e1, double n1, double e2, double n2)
    {
        double de = e1 - e2, dn = n1 - n2;
        return de * de + dn * dn;
    }

    /// <summary>
    /// This place moved by <paramref name="d"/>, an offset in the local axes at <paramref name="p"/>
    /// (x east, y up, z south). Today those are the world's axes in every frame. With the global
    /// tiling (#1443) they stay the local east-up-south at <paramref name="p"/>, which the anchor
    /// frame's axes match to 0.03° within a few km: the metre-scale callers (interpolation, docking)
    /// need no change, only this body.
    /// </summary>
    public static GlobalPos operator +(GlobalPos p, Vector3 d) => new(p.E + d.X, p.N - d.Z, p.Alt + d.Y);

    /// <summary>The world-space offset from <paramref name="b"/> to <paramref name="a"/>: small and precise when they are near.</summary>
    public static Vector3 operator -(GlobalPos a, GlobalPos b) =>
        new((float)(a.E - b.E), (float)(a.Alt - b.Alt), (float)-(a.N - b.N));

    public bool IsFinite => double.IsFinite(E) && double.IsFinite(N) && double.IsFinite(Alt);

    /// <summary>
    /// This place on the Earth (#1430), <see cref="Alt"/> taken as an LHN95 height: the rigorous
    /// <see cref="SwissGeodesy"/> chain, so <see cref="FromEcef"/> brings it back to a tenth of a millimetre.
    /// </summary>
    public EcefPos ToEcef() => SwissGeodesy.ToEcef(E, N, Alt);

    /// <summary>The inverse of <see cref="ToEcef"/>.</summary>
    public static GlobalPos FromEcef(EcefPos p)
    {
        var (e, n, h) = SwissGeodesy.FromEcef(p);
        return new(e, n, h);
    }

    /// <summary>Puts this position in a network dictionary under <paramref name="key"/>, as three doubles.</summary>
    public void Write(Godot.Collections.Dictionary d, string key = "pos")
    {
        d[key + "_e"] = E;
        d[key + "_n"] = N;
        d[key + "_alt"] = Alt;
    }

    /// <summary>The position <see cref="Write"/> put in <paramref name="d"/>.</summary>
    public static GlobalPos Read(Godot.Collections.Dictionary d, string key = "pos") =>
        new(d[key + "_e"].AsDouble(), d[key + "_n"].AsDouble(), d[key + "_alt"].AsDouble());

    public override string ToString() => $"LV95 {E:F2}/{N:F2} alt {Alt:F2}";
}
