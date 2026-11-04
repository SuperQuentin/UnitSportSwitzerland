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
///
/// <para>
/// <b>Becoming an Earth position</b> (#1443 G2, A1 #1502, <c>docs/plans/global-tiling-g2.md</c>): it will hold
/// ECEF doubles inside, and <see cref="E"/> / <see cref="N"/> go. Code reads it through what stays true on the
/// whole Earth: the distances, <see cref="OffsetTo"/> and <see cref="Moved"/> (metres east, north and up at the
/// place), <see cref="WithAlt"/>, <see cref="BearingTo"/>, <see cref="Tile"/>; LV95 only as notation, for a
/// person or a Swiss source: <see cref="FromLv95"/> in, <see cref="Lv95"/> out. LV95 is still inside until
/// A1's last step, so moving a site onto these is bit-identical.
/// </para>
/// </summary>
public readonly record struct GlobalPos(double E, double N, double Alt)
{
    /// <summary>A place given in LV95 notation (a Swiss source, a literal, a typed coordinate), its height LHN95.</summary>
    public static GlobalPos FromLv95(double e, double n, double alt) => new(e, n, alt);

    /// <summary>This place in LV95 notation, for a person or a Swiss file: never for arithmetic between places.</summary>
    public Lv95Coord Lv95 => new(E, N, Alt);

    /// <summary>
    /// Where <paramref name="other"/> is from here, in metres east, north and up of the local horizon at this
    /// place. Today the LV95 grid's differences; once inside is ECEF, the local east-north-up offset, the same to
    /// the millimetre within a few km (and grid against true north by the meridian convergence, 0.9° at Geneva).
    /// </summary>
    public (double East, double North, double Up) OffsetTo(GlobalPos other) => (other.E - E, other.N - N, other.Alt - Alt);

    /// <summary>This place moved by metres east, north and up of its local horizon (see <see cref="OffsetTo"/>).</summary>
    public GlobalPos Moved(double east, double north, double up = 0) => new(E + east, N + north, Alt + up);

    /// <summary>This place at another altitude (above sea level), straight up or down.</summary>
    public GlobalPos WithAlt(double alt) => this with { Alt = alt };

    /// <summary>The compass bearing to <paramref name="other"/>, radians clockwise from true north (#1504).</summary>
    public double BearingTo(GlobalPos other) => System.Math.Atan2(other.E - E, other.N - N) - Convergence;

    /// <summary>
    /// The bearing of true north in this type's offset axes here (<see cref="OffsetTo"/>, <see cref="Moved"/>,
    /// <c>+ Vector3</c>), radians clockwise: the meridian convergence while they are the LV95 grid's
    /// (<see cref="TrueNorth.GridConvergence"/>, 0 at Bern, ~0.9° at Geneva), 0 once A1 puts ECEF inside (#1504).
    /// A few microseconds: per object, never per vertex.
    /// </summary>
    public double Convergence => TrueNorth.GridConvergence(E, N, Alt);

    /// <summary>
    /// The LV95 grid's meridian convergence here, whatever this type holds inside: what a heading stored in grid north
    /// (a Swiss source's, a literal catalogue's) is turned by at load (<see cref="TrueNorth.GridYawToTrue(GlobalPos, float)"/>).
    /// </summary>
    public double GridConvergence => TrueNorth.GridConvergence(E, N, Alt);

    /// <summary>The terrain tile under this place (the LV95 kilometre today, the cube leaf in G2).</summary>
    public TileId Tile => TileId.FromLv95(E, N);

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

/// <summary>A place in LV95 notation (#1443 G2): metres east and north on the Swiss grid and an LHN95 height. For a person or a Swiss source only.</summary>
public readonly record struct Lv95Coord(double E, double N, double H)
{
    public override string ToString() => System.FormattableString.Invariant($"{E:F1}/{N:F1}");
}
