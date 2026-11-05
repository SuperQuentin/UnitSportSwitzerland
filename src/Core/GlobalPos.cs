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
/// origin, so a world-space <c>Vector3</c> means nothing to the receiver. On the wire it is ECEF
/// (#1503, protocol 93): <see cref="Write"/> and <see cref="Read"/> put one in a spawn or RPC
/// dictionary; an RPC passes <see cref="ToWire"/>'s three doubles and the receiver reads
/// <see cref="FromWire"/>. Directions go as the local east-up-south (<see cref="EnuFromOffset"/>),
/// headings from true north (<see cref="TrueYaw"/>). A list of points travels as float offsets in
/// the ENU at one ECEF origin (<see cref="OriginFrame.AnchorAt"/>).
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
/// <para>
/// In a JSON save it is <c>{"x", "y", "z"}</c>, ECEF metres (<see cref="GlobalPosJson"/>, #1503 G2 A2b): saves mean the
/// same place whatever this type holds inside.
/// </para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(GlobalPosJson))]
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

    /// <summary>The compass bearing to <paramref name="other"/>, radians clockwise from north (grid north until G2's A3).</summary>
    public double BearingTo(GlobalPos other) => System.Math.Atan2(other.E - E, other.N - N);

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

    /// <summary>
    /// This place as it crosses the network (#1503, protocol 93): ECEF metres, WGS84, through <see cref="ToEcef"/>.
    /// Every peer reads it back with <see cref="FromWire"/>, whatever its origin; the rigorous chain costs ~1 µs, fine
    /// per packet.
    /// </summary>
    public (double X, double Y, double Z) ToWire()
    {
        var p = ToEcef();
        return (p.X, p.Y, p.Z);
    }

    /// <summary>The place <see cref="ToWire"/> sent.</summary>
    public static GlobalPos FromWire(double x, double y, double z) => FromEcef(new EcefPos(x, y, z));

    /// <summary>
    /// This type's own offset axes (what <c>+ Vector3</c> and <c>-</c> use: today the LV95 grid's east, up and south,
    /// which are also a flat world's axes) against the local east-up-south at this place (x east, y up, z south, as
    /// <see cref="EnuFrame.ToLocal"/>; #1503): a turn about up by the meridian convergence (0 at Bern, ~0.9° at
    /// Geneva), and the grid's scale, the physical metres a grid metre is here (1 to 5·10⁻⁴ on the ground, 1.06
    /// 400 km up), so a velocity becomes the rate this position changes. The identity once G2 A1 puts ECEF inside.
    /// A few microseconds: per packet, never per vertex.
    /// </summary>
    public EnuAxes Enu
    {
        get
        {
            // as FrameAxes.GroundAt(E, N, Alt).InEnu(the ENU here), in half the time: grid east is a metre's step
            // along the grid, laid flat in the ENU (the grid's up is the ellipsoid's); grid south is east × up
            var o = ToEcef();
            var enu = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(o));
            var (x, _, z) = enu.DirectionToLocal(SwissGeodesy.ToEcef(E + 1, N, Alt) - o);
            double len = System.Math.Sqrt(x * x + z * z);
            float c = (float)(x / len), s = (float)(z / len);
            return new EnuAxes(new Basis(new Vector3(c, 0, s), Vector3.Up, new Vector3(-s, 0, c)), (float)len);
        }
    }

    /// <summary>A velocity (or direction) in this type's offset axes as the local east-up-south here (see <see cref="Enu"/>).</summary>
    public Vector3 EnuFromOffset(Vector3 offset) => Enu.ToEnu(offset);

    /// <summary>The inverse of <see cref="EnuFromOffset"/>: a local east-up-south velocity here as the rate this position changes.</summary>
    public Vector3 OffsetFromEnu(Vector3 enu) => Enu.FromEnu(enu);

    /// <summary>
    /// The rate this position changes (its offset axes per second) for a velocity in the local east-up-south here:
    /// <see cref="OffsetFromEnu"/> measured over a hundredth of a second of that velocity, as an interpolator wants it.
    /// The same on the ground; bounded where the grid's scale runs away (LV95's singular points 90° from Bern, which a
    /// polar orbit flies over: there the local scale tends to 0 and the linear form to infinity; #1430 H5).
    /// </summary>
    public Vector3 RateOfEnu(Vector3 enu)
    {
        const double h = 0.01;
        var f = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(ToEcef()));
        var moved = FromEcef(f.ToEcef(enu.X * h, enu.Y * h, enu.Z * h));
        return (moved - this) / (float)h;
    }

    /// <summary>
    /// A heading in this type's offset axes (radians, Godot's sense: 0 faces −Z, positive turns left; grid north
    /// until A1) as a heading from true north here (#1503): what goes on the wire.
    /// </summary>
    public float TrueYaw(float offsetYaw) => Enu.TrueYaw(offsetYaw);

    /// <summary>The inverse of <see cref="TrueYaw"/>: a heading from true north here in this type's offset axes.</summary>
    public float OffsetYaw(float trueYaw) => Enu.Yaw(trueYaw);

    /// <summary>Puts this position in a network dictionary under <paramref name="key"/>, as three ECEF doubles (<see cref="ToWire"/>).</summary>
    public void Write(Godot.Collections.Dictionary d, string key = "pos")
    {
        var (x, y, z) = ToWire();
        d[key + "_x"] = x;
        d[key + "_y"] = y;
        d[key + "_z"] = z;
    }

    /// <summary>
    /// The position <see cref="Write"/> put in <paramref name="d"/>. A dictionary without the ECEF keys is a record the
    /// object containers saved before G2 A2b (LV95 <c>_e</c>, <c>_n</c>, <c>_alt</c>): read for its migration only.
    /// </summary>
    public static GlobalPos Read(Godot.Collections.Dictionary d, string key = "pos") => d.ContainsKey(key + "_x")
        ? FromWire(d[key + "_x"].AsDouble(), d[key + "_y"].AsDouble(), d[key + "_z"].AsDouble())
        : new(d[key + "_e"].AsDouble(), d[key + "_n"].AsDouble(), d[key + "_alt"].AsDouble());

    /// <summary>Whether <paramref name="d"/> holds a position as <see cref="Write"/> puts it (the wire, and saves since A2b), not an older saved record's.</summary>
    public static bool IsWire(Godot.Collections.Dictionary d, string key = "pos") => d.ContainsKey(key + "_x");

    public override string ToString() => $"LV95 {E:F2}/{N:F2} alt {Alt:F2}";
}

/// <summary>A place in LV95 notation (#1443 G2): metres east and north on the Swiss grid and an LHN95 height. For a person or a Swiss source only.</summary>
public readonly record struct Lv95Coord(double E, double N, double H)
{
    public override string ToString() => System.FormattableString.Invariant($"{E:F1}/{N:F1}");
}
