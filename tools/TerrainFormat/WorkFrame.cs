namespace UnitSport.Terrain.Format;

/// <summary>
/// How a work frame lays the curved Earth flat (#1443 G2, B2 #1506): the ellipsoid seen straight down onto
/// the tangent plane at the frame's centre. A place's plane point is its point on the ellipsoid,
/// (s − O)·east and (s − O)·north, so it is the place's position in the centre's east-north-up frame seen
/// from above; the inverse is closed form (the line along the centre's up meets the ellipsoid). Over a cell
/// and a 2 km margin the scale is within 3 ppm and a right angle within 1.3″ (<c>WorkFrameTests</c>).
/// Chosen with the user over the oblique stereographic (exact angles, an iterated inverse, the same scale
/// error) and the cube lattice itself (rasters on the plane's grid, but its scale drifts by 2-3 m a km near
/// face edges and corners); PR #1523 has the comparison.
/// </summary>
public sealed class WorkPlane(EnuFrame centre)
{

    public (double X, double Y) ToPlane(double latDeg, double lonDeg)
    {
        var s = Ellipsoid.Wgs84.ToEcef(latDeg * Math.PI / 180, lonDeg * Math.PI / 180, 0);
        double dx = s.X - centre.Origin.X, dy = s.Y - centre.Origin.Y, dz = s.Z - centre.Origin.Z;
        return (dx * centre.East.X + dy * centre.East.Y + dz * centre.East.Z,
            dx * centre.North.X + dy * centre.North.Y + dz * centre.North.Z);
    }

    /// <summary>The plane point of a point on the ellipsoid given in ECEF (#1507): <see cref="ToPlane(double, double)"/> without the trip through latitude and longitude.</summary>
    public (double X, double Y) ToPlane(EcefPos s)
    {
        double dx = s.X - centre.Origin.X, dy = s.Y - centre.Origin.Y, dz = s.Z - centre.Origin.Z;
        return (dx * centre.East.X + dy * centre.East.Y + dz * centre.East.Z,
            dx * centre.North.X + dy * centre.North.Y + dz * centre.North.Z);
    }

    /// <summary>The geodetic normal (unit, ECEF) under a plane point (#1507): <see cref="FromPlane"/> without the angles.</summary>
    public EcefPos NormalAt(double x, double y)
    {
        var (o, e, n, u) = (centre.Origin, centre.East, centre.North, centre.Up);
        double px = o.X + e.X * x + n.X * y, py = o.Y + e.Y * x + n.Y * y, pz = o.Z + e.Z * x + n.Z * y;
        var ell = Ellipsoid.Wgs84;
        double ia = 1 / (ell.A * ell.A), ib = 1 / (ell.B * ell.B);
        double qa = (u.X * u.X + u.Y * u.Y) * ia + u.Z * u.Z * ib;
        double qb = 2 * ((px * u.X + py * u.Y) * ia + pz * u.Z * ib);
        double qc = (px * px + py * py) * ia + pz * pz * ib - 1;
        double t = -2 * qc / (qb + Math.Sqrt(qb * qb - 4 * qa * qc));
        double nx = (px + u.X * t) * ia, ny = (py + u.Y * t) * ia, nz = (pz + u.Z * t) * ib;
        double inv = 1 / Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return new(nx * inv, ny * inv, nz * inv);
    }

    public (double LatDeg, double LonDeg) FromPlane(double x, double y)
    {
        var (o, e, n, u) = (centre.Origin, centre.East, centre.North, centre.Up);
        double px = o.X + e.X * x + n.X * y, py = o.Y + e.Y * x + n.Y * y, pz = o.Z + e.Z * x + n.Z * y;
        // (p + t·up) on the ellipsoid: a·t² + b·t + c = 0 in the metric diag(1/a², 1/a², 1/b²); the small root
        var ell = Ellipsoid.Wgs84;
        double ia = 1 / (ell.A * ell.A), ib = 1 / (ell.B * ell.B);
        double qa = (u.X * u.X + u.Y * u.Y) * ia + u.Z * u.Z * ib;
        double qb = 2 * ((px * u.X + py * u.Y) * ia + pz * u.Z * ib);
        double qc = (px * px + py * py) * ia + pz * pz * ib - 1;
        double t = -2 * qc / (qb + Math.Sqrt(qb * qb - 4 * qa * qc));
        double sx = px + u.X * t, sy = py + u.Y * t, sz = pz + u.Z * t;
        // the geodetic normal there
        double nx = sx * ia, ny = sy * ia, nz = sz * ib;
        return (Math.Atan2(nz, Math.Sqrt(nx * nx + ny * ny)) * 180 / Math.PI, Math.Atan2(ny, nx) * 180 / Math.PI);
    }
}

/// <summary>
/// The flat frame a build stage works in (#1443 G2, B2 #1506): one level-9 cube cell (~20 km), metres east
/// and north on its <see cref="WorkPlane"/>, heights above its <see cref="ISeaLevel"/>. Every source enters
/// through <see cref="FromSource"/>, and every result leaves through <see cref="ToEcef"/> into the leaves.
/// </summary>
public sealed class WorkFrame
{
    /// <summary>The level of the cube cells work frames are made for: 32 × 32 leaves, ~20 km.</summary>
    public const int Level = 9;

    public CubeKey Cell { get; }

    public WorkPlane Plane { get; }

    public ISeaLevel SeaLevel { get; }

    /// <summary>The frame of a level-<see cref="Level"/> cell, its plane tangent at the cell's centre.</summary>
    public WorkFrame(CubeKey cell, ISeaLevel seaLevel)
    {
        Cell = cell;
        Plane = new WorkPlane(cell.Anchor);
        SeaLevel = seaLevel;
    }

    /// <summary>A plane point and its height above sea level as ECEF.</summary>
    public EcefPos ToEcef(double x, double y, double hSea)
    {
        var (lat, lon) = Plane.FromPlane(x, y);
        return Ellipsoid.Wgs84.ToEcef(lat * Math.PI / 180, lon * Math.PI / 180, hSea + SeaLevel.Undulation(lat, lon));
    }

    /// <summary>The inverse of <see cref="ToEcef"/>.</summary>
    public (double X, double Y, double HSea) FromEcef(EcefPos p)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        var (x, y) = Plane.ToPlane(g.LatDeg, g.LonDeg);
        return (x, y, g.H - SeaLevel.Undulation(g.LatDeg, g.LonDeg));
    }

    /// <summary>A source's point and its height above the source's sea level, in this frame.</summary>
    public (double X, double Y, double HSea) FromSource(ISourceProjection source, double a, double b, double h)
    {
        if (source is Lv95Source) return FromEcef(SwissGeodesy.ToEcef(a, b, h));
        var (lat, lon) = source.ToLatLon(a, b);
        double hEll = h + source.SeaLevel.Undulation(lat, lon);
        return FromEcef(Ellipsoid.Wgs84.ToEcef(lat * Math.PI / 180, lon * Math.PI / 180, hEll));
    }
}
