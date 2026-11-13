namespace UnitSport.Tools.RoadGen.Geometry;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// A test instance of <see cref="IUnits"/> (#1660, the tiling check): the plane is LV95's, and its tiles are the square
/// cells of a grid moved to <see cref="OriginX"/>, <see cref="OriginY"/>, turned by <see cref="AngleDeg"/> and
/// <see cref="CellM"/> metres on a side. A cell is named by a <see cref="TileId"/> (E, N = its column and row), and its
/// tile-local frame is a <see cref="Box"/> m square on the plane, plane-aligned and centred on the cell (its centre rounded
/// to <see cref="Snap"/> m): exactly what <see cref="FrameUnits"/> does for a leaf, so a run on these tiles goes through
/// the same seams a frame build does. Its tiles are not files: <c>TilingCheck</c> cuts the raw lines at its cells.
/// </summary>
public sealed class GridUnits : IUnits
{
    /// <summary>A cell's square on the plane (as <see cref="FrameUnits.Box"/>).</summary>
    public const double Box = FrameUnits.Box;

    /// <summary>The squares' centres are whole multiples of this (the urban field's 2 m lattice).</summary>
    public const double Snap = FrameUnits.Snap;

    public double OriginX { get; }
    public double OriginY { get; }
    public double AngleDeg { get; }
    public double CellM { get; }

    private readonly double _cos, _sin;

    public GridUnits(double originX, double originY, double angleDeg, double cellM)
    {
        OriginX = originX;
        OriginY = originY;
        AngleDeg = angleDeg;
        CellM = cellM;
        double r = angleDeg * Math.PI / 180;
        (_cos, _sin) = (Math.Cos(r), Math.Sin(r));
        if (Math.Abs(_sin) < 1e-12) _sin = 0;
        if (Math.Abs(_cos) < 1e-12) _cos = 0;
        // the cell must fit its square wherever the snap puts the square's centre
        double extent = cellM * (Math.Abs(_cos) + Math.Abs(_sin));
        if (extent > Box + 1e-9 || (extent > Box - 2 * Snap + 1e-9 && !CentresOnSnap()))
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"a {cellM} m cell turned {angleDeg}° does not fit a {Box} m square (centres off the {Snap} m lattice)"));
    }

    /// <summary>"x,y,angle,cell", e.g. "250,0,0,1000" or "0,0,28.5,610".</summary>
    public static GridUnits Parse(string spec)
    {
        var p = spec.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (p.Length != 4) throw new FormatException($"a tiling is x,y,angle,cell, not '{spec}'");
        return new GridUnits(p[0], p[1], p[2], p[3]);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{OriginX},{OriginY},{AngleDeg},{CellM}");

    /// <summary>Unturned cells whose centres all lie on the snap lattice need no slack in their square.</summary>
    private bool CentresOnSnap()
    {
        if (_sin != 0) return false;
        bool On(double v) => Math.Abs(v / Snap - Math.Round(v / Snap)) < 1e-9;
        return On(OriginX + CellM / 2) && On(OriginY + CellM / 2) && On(CellM);
    }

    /// <summary>A plane point in cell units: u along the grid's turned x axis, v along its y axis.</summary>
    public (double U, double V) ToGrid(double x, double y)
    {
        double dx = x - OriginX, dy = y - OriginY;
        return ((dx * _cos + dy * _sin) / CellM, (-dx * _sin + dy * _cos) / CellM);
    }

    /// <summary>A point in cell units on the plane.</summary>
    public (double X, double Y) FromGrid(double u, double v)
    {
        double a = u * CellM, b = v * CellM;
        return (OriginX + a * _cos - b * _sin, OriginY + a * _sin + b * _cos);
    }

    public double TileSizeM => Box;

    public TileId TileAt(double x, double y)
    {
        var (u, v) = ToGrid(x, y);
        return new TileId((int)Math.Floor(u), (int)Math.Floor(v));
    }

    private (double West, double North) BoxOf(TileId id)
    {
        var (cx, cy) = FromGrid(id.E + 0.5, id.N + 0.5);
        double x0 = Snap * Math.Round(cx / Snap), y0 = Snap * Math.Round(cy / Snap);
        return (x0 - Box / 2, y0 + Box / 2);
    }

    public double West(TileId id) => BoxOf(id).West;
    public double South(TileId id) => BoxOf(id).North - Box;
    public double North(TileId id) => BoxOf(id).North;

    public Vec2 DirectionToTile(TileId id, Vec2 at, Vec2 direction) => direction;
    public double AngleToTile(TileId id, Vec2 at, double angle) => angle;

    /// <summary>A tile-local point in the cell's own units (0..1 inside).</summary>
    private (double U, double V) Local(TileId id, double x, double z)
    {
        var (west, north) = BoxOf(id);
        var (u, v) = ToGrid(west + x, north - z);
        return (u - id.E, v - id.N);
    }

    public bool OnEdge(TileId id, double x, double z, double eps)
    {
        var (u, v) = Local(id, x, z);
        double e = eps / CellM, slack = e + 1e-9;
        if (u < -slack || v < -slack || u > 1 + slack || v > 1 + slack) return false;
        return Math.Abs(u) < e || Math.Abs(v) < e || Math.Abs(u - 1) < e || Math.Abs(v - 1) < e;
    }

    public bool Inside(TileId id, double x, double z)
    {
        var (u, v) = Local(id, x, z);
        const double slack = 1e-9;
        return u >= -slack && v >= -slack && u <= 1 + slack && v <= 1 + slack;
    }

    public bool OnAnyEdge(double x, double y, double eps)
    {
        var (u, v) = ToGrid(x, y);
        double e = eps / CellM;
        return Math.Abs(u - Math.Round(u)) < e || Math.Abs(v - Math.Round(v)) < e;
    }

    public IEnumerable<TileId> Around(TileId id, int ring)
    {
        for (int de = -ring; de <= ring; de++)
            for (int dn = -ring; dn <= ring; dn++)
                yield return new TileId(id.E + de, id.N + dn);
    }

    /// <summary>
    /// The parameters (0..1, ascending) at which the plane segment a→b crosses a grid line: where a line is cut into
    /// this grid's tiles.
    /// </summary>
    public List<double> Crossings(double ax, double ay, double bx, double by)
    {
        var (ua, va) = ToGrid(ax, ay);
        var (ub, vb) = ToGrid(bx, by);
        var ts = new List<double>();
        void Axis(double a, double b)
        {
            if (Math.Abs(b - a) < 1e-15) return;
            double lo = Math.Min(a, b), hi = Math.Max(a, b);
            for (double k = Math.Floor(lo) + 1; k < hi; k++)
            {
                double t = (k - a) / (b - a);
                if (t > 1e-12 && t < 1 - 1e-12) ts.Add(t);
            }
            // a vertex exactly on a line: no cut inside, the vertex itself is the cut
        }
        Axis(ua, ub);
        Axis(va, vb);
        ts.Sort();
        return ts;
    }
}
