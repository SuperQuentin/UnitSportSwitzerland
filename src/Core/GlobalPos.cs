using Godot;

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

    /// <summary>
    /// This place moved by <paramref name="d"/>, a world-space offset (x east, y up, z south). The
    /// world axes are the same in every frame while a shift has no rotation; the round world
    /// (#187) makes them depend on where the frame is, and this is the place to change then.
    /// </summary>
    public static GlobalPos operator +(GlobalPos p, Vector3 d) => new(p.E + d.X, p.N - d.Z, p.Alt + d.Y);

    /// <summary>The world-space offset from <paramref name="b"/> to <paramref name="a"/>: small and precise when they are near.</summary>
    public static Vector3 operator -(GlobalPos a, GlobalPos b) =>
        new((float)(a.E - b.E), (float)(a.Alt - b.Alt), (float)-(a.N - b.N));

    public bool IsFinite => double.IsFinite(E) && double.IsFinite(N) && double.IsFinite(Alt);

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
