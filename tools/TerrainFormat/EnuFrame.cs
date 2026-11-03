namespace UnitSport.Terrain.Format;

/// <summary>
/// The east-north-up frame at a point of the WGS84 ellipsoid (#1443): its origin in ECEF and its
/// three unit axes, all doubles. A cube tile's anchor (<see cref="CubeKey.Anchor"/>) is one, and so is
/// the game's anchor frame later (G2). Local coordinates come in the game's axes: x east, y up,
/// z south (−north), as <see cref="TileId.Local"/> already has them.
/// </summary>
public readonly struct EnuFrame
{
    public EcefPos Origin { get; }

    /// <summary>Unit vectors in ECEF.</summary>
    public EcefPos East { get; }

    public EcefPos North { get; }

    public EcefPos Up { get; }

    /// <summary>The frame at a latitude and longitude (degrees) and an ellipsoidal height.</summary>
    public EnuFrame(Geodetic at)
    {
        double lat = at.LatDeg * Math.PI / 180.0, lon = at.LonDeg * Math.PI / 180.0;
        double sl = Math.Sin(lat), cl = Math.Cos(lat), so = Math.Sin(lon), co = Math.Cos(lon);
        Origin = Ellipsoid.Wgs84.ToEcef(lat, lon, at.H);
        East = new(-so, co, 0);
        North = new(-sl * co, -sl * so, cl);
        Up = new(cl * co, cl * so, sl);
    }

    /// <summary>An ECEF point in this frame: x east, y up, z south, metres.</summary>
    public (double X, double Y, double Z) ToLocal(EcefPos p)
    {
        double dx = p.X - Origin.X, dy = p.Y - Origin.Y, dz = p.Z - Origin.Z;
        return (Dot(East, dx, dy, dz), Dot(Up, dx, dy, dz), -Dot(North, dx, dy, dz));
    }

    /// <summary>The inverse of <see cref="ToLocal"/>.</summary>
    public EcefPos ToEcef(double x, double y, double z) => new(
        Origin.X + East.X * x + Up.X * y - North.X * z,
        Origin.Y + East.Y * x + Up.Y * y - North.Y * z,
        Origin.Z + East.Z * x + Up.Z * y - North.Z * z);

    /// <summary>An ECEF direction (no origin) in this frame's axes.</summary>
    public (double X, double Y, double Z) DirectionToLocal(EcefPos d) =>
        (Dot(East, d.X, d.Y, d.Z), Dot(Up, d.X, d.Y, d.Z), -Dot(North, d.X, d.Y, d.Z));

    private static double Dot(EcefPos a, double x, double y, double z) => a.X * x + a.Y * y + a.Z * z;
}
