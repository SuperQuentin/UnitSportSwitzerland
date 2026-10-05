namespace UnitSport.Terrain.Format;

/// <summary>The ground under a car park, and what already stands on it. LV95 in, metres (LN02) out.</summary>
public interface IParkingGround
{
    /// <summary>The ground; NaN where nothing is known (outside the region).</summary>
    double Ground(double e, double n);

    /// <summary>
    /// True where something already occupies the ground: a building wall, a road corridor, a
    /// watercourse, a rail line. No bay, aisle or prop is placed on an occupied point.
    /// </summary>
    bool Occupied(double e, double n);
}

/// <summary>
/// Lays out a car park (#499): rows of marked bays at the lot's own angle, the aisles that serve
/// them, one entrance, and the kerbside detail that fits. Pure — no Godot, no I/O, no RoadGen — so
/// the network stage runs it over swissTLM3D's <c>Parkplatzareal</c> rings, the generated world runs
/// it over a village's lot, the fixture course runs it over a hand-built rectangle, and the unit
/// tests pin its output over a made-up polygon. Rules and values: docs/notes/tools/parking-lots.md.
///
/// <para>
/// <b>Why a planner and not a shader.</b> Before #499 a car park was <see cref="SurfacePattern.ParkingBays"/>
/// — a 2.5 x 5.0 m grid of lines painted on the terrain by <c>terrain.gdshaderinc</c>. swissTLM3D
/// records no orientation for a surface pattern and a tangent cannot be recovered from the
/// screen-space normal, so that grid runs on <b>world axes</b>: in any lot that is not square to
/// north the bays cut across the rows. Fitting an axis needs the polygon, which only exists here.
/// </para>
///
/// <para>
/// <b>The frame.</b> Everything is laid out in the lot's own (u, v): <c>u</c> along
/// <see cref="Lot.Axis"/>, <c>v</c> across it, origin at the polygon's centroid. Rows run along u,
/// bands of (row, aisle, row) stack along v from the centroid outward — outward from the middle, so
/// that moving one far vertex does not shift every bay in the lot. Output is LV95 again, because a
/// lot may cross a tile seam and the caller decides which tile each piece belongs to.
/// </para>
///
/// <para>
/// <b>Headings here are LV95 bearings</b> (east 0, north π/2), like <c>JunctionArm.OutwardHeading</c>
/// and <see cref="Border.HeadingRad"/> — NOT the Godot heading <see cref="ParkingBay.Heading"/> and
/// <see cref="RoadPointProp.Heading"/> store (about +Y, 0 = -Z = north). The caller converts with
/// <see cref="ToGodotHeading"/> when it makes a record tile-local; getting this wrong turns every
/// parked car 90°.
/// </para>
/// </summary>
public static class ParkingPlanner
{
    /// <summary>Swiss practice (VSS 640 291a, Bern Normalien). Every distance in metres.</summary>
    public sealed record Options
    {
        /// <summary>Bay 2.5 x 5.0 m, square to the aisle.</summary>
        public double BayWidth { get; init; } = ParkingBay.StandardWidth;
        public double BayDepth { get; init; } = ParkingBay.StandardDepth;

        /// <summary>A two-way aisle between two rows of 90° bays: room to turn into a bay in one go.</summary>
        public double AisleTwoWay { get; init; } = 6.0;

        /// <summary>A one-way aisle, which is all a row of 45° bays needs.</summary>
        public double AisleOneWay { get; init; } = 3.5;

        /// <summary>Depth a 45° row occupies measured across the aisle.</summary>
        public double AngledRowDepth { get; init; } = 4.2;

        /// <summary>A bay parallel to the kerb: 2.0 m out, 5.5 m long.</summary>
        public double ParallelDepth { get; init; } = 2.0;
        public double ParallelLength { get; init; } = 5.5;

        /// <summary>Kept inside the lot edge, so a bay never ends on the grass.</summary>
        public double EdgeInset { get; init; } = 0.3;

        /// <summary>A row terraces rather than stretching one plane: a bay whose ground is further than this from its band's plane is dropped.</summary>
        public double FlatTolerance { get; init; } = 0.35;

        /// <summary>Entrance throat: two-way, or <see cref="AisleOneWay"/> on a one-way lot.</summary>
        public double EntranceWidth { get; init; } = 6.0;

        /// <summary>A road must run at least this close to the lot to carry its entrance.</summary>
        public double EntranceReach { get; init; } = 35.0;

        /// <summary>The lot axis snaps to a bordering road's bearing when it lies within this of a principal axis.</summary>
        public double AxisSnapRad { get; init; } = 30.0 * Math.PI / 180.0;

        /// <summary>A row longer than this many bays gets a kerbed island with a tree at each end.</summary>
        public int IslandAfterBays { get; init; } = 8;

        /// <summary>Kerb height of an island or a walk, as #119 uses.</summary>
        public double Kerb { get; init; } = 0.12;

        /// <summary>Island and walk width.</summary>
        public double IslandWidth { get; init; } = 2.5;
        public double WalkWidth { get; init; } = 1.5;

        /// <summary>Aisle arrows this far apart, on one-way aisles only.</summary>
        public double ArrowSpacing { get; init; } = 15.0;

        /// <summary>Divider line width painted between bays.</summary>
        public double LineWidth { get; init; } = 0.12;

        /// <summary>One trolley shelter per this many bays at a big-box store.</summary>
        public int BaysPerShelter { get; init; } = 40;

        /// <summary>A lot smaller than this keeps the old shader pattern: laying out three bays is worse than a grid.</summary>
        public int MinBays { get; init; } = 6;
    }

    /// <summary>A road running past the lot. The entrance goes on the best of them.</summary>
    public readonly record struct Border(
        // The point on the carriageway centreline nearest the lot.
        double E, double N,
        // The centreline's bearing there, radians (LV95: east 0, north π/2).
        double HeadingRad,
        // `priority` as ATTR carries it: higher is a more important road.
        int Priority,
        // Half the paved width, so the throat starts at the carriageway edge.
        double HalfWidth,
        bool OneWay);

    /// <summary>A building that faces the lot: its door draws the walk, its size decides a trolley shelter.</summary>
    public readonly record struct Frontage(double DoorE, double DoorN, double FloorAreaM2, bool Retail);

    public enum AreaKind { Pad, Island, Walk }

    /// <summary>A flush or kerbed surface of the lot, as an LV95 ring (counter-clockwise) at one height.</summary>
    public sealed record Area(AreaKind Kind, double[] Ring, double Y, double Height);

    /// <summary>A painted marking. <see cref="Line"/> is an LV95 polyline; a glyph carries a single point and a heading.</summary>
    public sealed record Mark(PaintType Type, double[] Line, double Y, double Width, double HeadingRad = 0, byte Variant = 0);

    public enum FixtureKind { Barrier, Kiosk, Shelter, Sign, Tree }

    /// <summary>Something standing in the lot, at LV95 with a heading (radians about +Y, 0 = north).</summary>
    public sealed record Fixture(FixtureKind Kind, double E, double N, double Y, double HeadingRad, byte Variant = 0);

    /// <summary>A bay before it is made tile-local: LV95 centre, heading as a compass bearing.</summary>
    public sealed record Bay(double E, double N, double Y, double HeadingRad, ParkingBayKind Kind, ParkingBayFlags Flags)
    {
        /// <summary>True where a vehicle may be left standing; the rule itself lives on <see cref="ParkingBay"/>.</summary>
        public bool Occupiable => new ParkingBay(0, 0, 0, 0, Kind, Flags).Occupiable;
    }

    public sealed record Lot
    {
        /// <summary>The fitted axis, radians (LV95: east 0, north π/2), normalised to [0, π).</summary>
        public double Axis { get; init; }

        /// <summary>Whether the layout took its axis from a road rather than the polygon.</summary>
        public bool AxisFromRoad { get; init; }

        public Module Layout { get; init; }

        /// <summary>Paid: the entrance has a boom and a kiosk. Hashed from the lot key.</summary>
        public bool Paid { get; init; }

        public required List<Bay> Bays { get; init; }
        public required List<Area> Areas { get; init; }
        public required List<Mark> Marks { get; init; }
        public required List<Fixture> Fixtures { get; init; }

        /// <summary>Why nothing was laid out, for the stage's stats; null when the lot was planned.</summary>
        public string? Rejected { get; init; }

        public static Lot Reject(string why) => new()
            { Bays = [], Areas = [], Marks = [], Fixtures = [], Rejected = why };
    }

    /// <summary>How the rows are arranged across the lot: the widest that fits its depth.</summary>
    public enum Module { None = 0, TwoSided = 1, OneSided = 2, Angled = 3, Parallel = 4 }

    // ---- entry point -------------------------------------------------------------------------

    /// <summary>
    /// Lays out the lot inside <paramref name="ring"/> (an LV95 polygon, closed or not). Returns a
    /// <see cref="Lot"/> whose <see cref="Lot.Rejected"/> says why when nothing was laid out —
    /// the caller then leaves the old surface pattern alone, which is still better than bare asphalt.
    /// </summary>
    public static Lot Plan(
        IReadOnlyList<(double E, double N)> ring,
        IReadOnlyList<Border> borders,
        IParkingGround ground,
        Frontage? frontage = null,
        Options? options = null)
    {
        var o = options ?? new Options();
        var poly = Normalise(ring);
        if (poly.Length < 3) return Lot.Reject("degenerate ring");

        double area = Math.Abs(SignedArea(poly));
        if (area < 120) return Lot.Reject("under 120 m2");

        var centroid = Centroid(poly);
        var (axis, fromRoad) = FitAxis(poly, borders, centroid, o);
        var frame = new Frame(centroid, axis);

        // the lot's extent in its own frame
        var local = new (double U, double V)[poly.Length / 2];
        for (int i = 0; i < local.Length; i++) local[i] = frame.ToLocal(poly[i * 2], poly[i * 2 + 1]);
        double vMin = local.Min(p => p.V), vMax = local.Max(p => p.V);
        double uMin = local.Min(p => p.U), uMax = local.Max(p => p.U);

        var module = PickModule(vMax - vMin - o.EdgeInset * 2, uMax - uMin - o.EdgeInset * 2, o);
        if (module == Module.None) return Lot.Reject("too small for a row");

        ulong seed = Key(centroid);
        var lot = new Build(poly, frame, ground, o, seed, module, borders, frontage);
        lot.LayRows(uMin, uMax, vMin, vMax);
        if (lot.Bays.Count < o.MinBays) return Lot.Reject($"only {lot.Bays.Count} bays fit");

        lot.Entrance();
        lot.Walks();
        lot.Shelters();
        lot.MarkBays();

        return new Lot
        {
            Axis = axis, AxisFromRoad = fromRoad, Layout = module, Paid = lot.Paid,
            Bays = lot.Bays, Areas = lot.Areas, Marks = lot.Marks, Fixtures = lot.Fixtures,
        };
    }

    /// <summary>
    /// The widest row module whose depth fits, as real lots are built. <paramref name="depth"/> and
    /// <paramref name="length"/> are what is left after the edge insets, not the raw extent.
    /// </summary>
    private static Module PickModule(double depth, double length, Options o)
    {
        if (length < o.BayWidth * 2) return Module.None;
        if (depth >= o.BayDepth * 2 + o.AisleTwoWay) return Module.TwoSided;
        if (depth >= o.BayDepth + o.AisleTwoWay) return Module.OneSided;
        if (depth >= o.AngledRowDepth + o.AisleOneWay) return Module.Angled;
        if (depth >= o.ParallelDepth + o.AisleOneWay) return Module.Parallel;
        return Module.None;
    }

    // ---- the lot's own frame -----------------------------------------------------------------

    /// <summary>LV95 &lt;-&gt; the lot's (u along the axis, v across it) about its centroid.</summary>
    private readonly struct Frame(( double E, double N) origin, double axis)
    {
        private readonly double _ce = Math.Cos(axis), _se = Math.Sin(axis);
        private readonly double _oe = origin.E, _on = origin.N;

        public (double U, double V) ToLocal(double e, double n)
        {
            double de = e - _oe, dn = n - _on;
            return (de * _ce + dn * _se, -de * _se + dn * _ce);
        }

        public (double E, double N) ToWorld(double u, double v) =>
            (_oe + u * _ce - v * _se, _on + u * _se + v * _ce);

        /// <summary>The compass bearing of +v (across the lot), which is where a 90° bay faces.</summary>
        public double AcrossHeading => Math.Atan2(_ce, -_se);
    }

    /// <summary>
    /// The lot's axis: a bordering road's bearing when one runs within <see cref="Options.AxisSnapRad"/>
    /// of a principal axis of the polygon, else the long side of the minimum-area rectangle. The
    /// minimum-area rectangle always has a side collinear with a hull edge, so the candidates are the
    /// hull's own bearings. Ties break on the lower bearing, so the result never depends on ring
    /// winding or on which vertex the ring starts at.
    /// </summary>
    private static (double Axis, bool FromRoad) FitAxis(
        double[] poly, IReadOnlyList<Border> borders, (double E, double N) centroid, Options o)
    {
        var hull = ConvexHull(poly);
        double best = double.MaxValue, bestAxis = 0;
        for (int i = 0; i < hull.Length / 2; i++)
        {
            int j = (i + 1) % (hull.Length / 2);
            double ex = hull[j * 2] - hull[i * 2], ny = hull[j * 2 + 1] - hull[i * 2 + 1];
            double len = Math.Sqrt(ex * ex + ny * ny);
            if (len < 1e-6) continue;
            double a = Norm(Math.Atan2(ny, ex));
            double c = ex / len, s = ny / len;
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            for (int k = 0; k < hull.Length / 2; k++)
            {
                double u = hull[k * 2] * c + hull[k * 2 + 1] * s;
                double v = -hull[k * 2] * s + hull[k * 2 + 1] * c;
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
                v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            double boxArea = (u1 - u0) * (v1 - v0);
            // a strictly smaller box wins; an equal one only on a lower bearing, so the result is stable
            if (boxArea < best - 1e-9 || (boxArea < best + 1e-9 && a < bestAxis))
            {
                // rows run along the longer side
                best = Math.Min(best, boxArea);
                bestAxis = (u1 - u0) >= (v1 - v0) ? a : Norm(a + Math.PI / 2);
            }
        }

        // a road alongside wins when it is close to one of the two principal axes: a lot is built
        // square to the street it is entered from, not to its own slightly skew boundary
        var alt = Norm(bestAxis + Math.PI / 2);
        double bestDelta = double.MaxValue, roadAxis = 0;
        foreach (var b in borders)
        {
            double d = Dist(centroid.E, centroid.N, b.E, b.N);
            if (d > o.EntranceReach) continue;
            double h = Norm(b.HeadingRad);
            foreach (double cand in (double[])[bestAxis, alt])
            {
                double delta = Delta(h, cand);
                if (delta <= o.AxisSnapRad && delta < bestDelta) { bestDelta = delta; roadAxis = h; }
            }
        }
        return bestDelta < double.MaxValue ? (roadAxis, true) : (bestAxis, false);
    }

    // ---- the layout --------------------------------------------------------------------------

    /// <summary>Mutable state while one lot is laid out. Short-lived, one per <see cref="Plan"/>.</summary>
    private sealed class Build(
        double[] poly, Frame frame, IParkingGround ground,
        Options o, ulong seed, Module module, IReadOnlyList<Border> borders, Frontage? frontage)
    {
        public readonly List<Bay> Bays = [];
        public readonly List<Area> Areas = [];
        public readonly List<Mark> Marks = [];
        public readonly List<Fixture> Fixtures = [];
        public bool Paid;

        /// <summary>Row bands as laid, for the islands, the walks and the arrows.</summary>
        private readonly List<Row> _rows = [];
        private readonly List<(double V, double Y, double U0, double U1, bool OneWay)> _aisles = [];

        private sealed record Row(double V, double Y, double Depth, int Sign, List<int> BayIndices);

        public void LayRows(double uMin, double uMax, double vMin, double vMax)
        {
            double rowDepth = module switch
            {
                Module.Angled => o.AngledRowDepth,
                Module.Parallel => o.ParallelDepth,
                _ => o.BayDepth,
            };
            double aisle = module is Module.TwoSided or Module.OneSided ? o.AisleTwoWay : o.AisleOneWay;
            bool twoSided = module == Module.TwoSided;
            double band = twoSided ? rowDepth * 2 + aisle : rowDepth + aisle;

            // Bands are anchored to the lot's lower v edge and stacked up from it, and a band is only
            // laid when the WHOLE of it fits: a row whose aisle fell outside the lot would be a row
            // of bays nothing can reach, which is how the 9 m lot first came out. The anchor is a real
            // boundary of the polygon, so a vertex moved on the far side does not shift a single bay.
            double v0 = vMin + o.EdgeInset;
            for (int step = 0; v0 + band <= vMax - o.EdgeInset + 1e-9; step++, v0 += band)
            {
                LayBand(v0, rowDepth, aisle, twoSided, uMin, uMax);
                if (step > 200) break;   // a 3 km lot does not exist; a guard against a bad ring
            }

            // A leftover strip deeper than a one-sided module takes one more row off the last aisle;
            // a shallower one is left as plain pad, which is what the end of a real lot looks like.
            double left = vMax - o.EdgeInset - v0;
            if (left >= rowDepth && _aisles.Count > 0) LayRow(v0, rowDepth, -1, uMin, uMax);
        }

        /// <summary>One band: a row, its aisle, and on a two-sided band a second row.</summary>
        private void LayBand(double v0, double rowDepth, double aisle, bool twoSided, double uMin, double uMax)
        {
            double aisleV = v0 + rowDepth;
            bool placed = LayRow(v0, rowDepth, +1, uMin, uMax);
            if (twoSided) placed |= LayRow(aisleV + aisle, rowDepth, -1, uMin, uMax);
            if (placed) LayAisle(aisleV, aisle, uMin, uMax);
        }

        /// <summary>
        /// A row of bays between v and v + depth. <paramref name="sign"/> says which way a car in it
        /// faces: +1 means it noses toward +v (its aisle is behind it at the higher v).
        /// </summary>
        private bool LayRow(double v, double depth, int sign, double uMin, double uMax)
        {
            // a parallel bay lies ALONG the aisle, so its cell is a bay length, not a bay width
            double cell = module == Module.Parallel ? o.ParallelLength : o.BayWidth;

            // Pass one: which cells of the row are usable at all, and at what height. Two passes
            // because the end planters REPLACE the end bays rather than needing spare ground past
            // them — a lot laid out to its own boundary has no spare ground, and hunting for some
            // made planters appear only when the bay grid happened to leave slack.
            var clear = new List<(double U, double Y)>();
            double plane = double.NaN;
            for (double u = Math.Ceiling((uMin + o.EdgeInset) / cell) * cell;
                 u + cell <= uMax - o.EdgeInset + 1e-9; u += cell)
            {
                if (!CellClear(u, u + cell, v, v + depth, out double y)) continue;
                if (!double.IsNaN(plane) && Math.Abs(y - plane) > o.FlatTolerance) continue;
                if (double.IsNaN(plane)) plane = y;
                clear.Add((u, plane));
            }
            if (clear.Count == 0) return false;

            // Pass two: the ends become planters on a row long enough to spare them, and the rest bays.
            bool planters = clear.Count >= o.IslandAfterBays + 2;
            int first = planters ? 1 : 0, last = planters ? clear.Count - 2 : clear.Count - 1;
            var indices = new List<int>();
            var runs = new List<(double U0, double U1)>();
            double runStart = double.NaN, runEnd = double.NaN;

            for (int k = 0; k < clear.Count; k++)
            {
                double u = clear[k].U;
                bool contiguous = !double.IsNaN(runEnd) && Math.Abs(u - runEnd) < 1e-6;

                if (planters && (k == 0 || k == clear.Count - 1))
                {
                    // the planter is paved under its kerb like any other raised bed, so the pad run
                    // carries on through it and the kerb face has tarmac to stand on
                    Areas.Add(new Area(AreaKind.Island, Rect(u, u + cell, v, v + depth), clear[k].Y, o.Kerb));
                    var (te, tn) = frame.ToWorld(u + cell * 0.5, v + depth * 0.5);
                    Fixtures.Add(new Fixture(FixtureKind.Tree, te, tn, clear[k].Y + o.Kerb, 0));
                }
                else if (k >= first && k <= last)
                {
                    var (e, n) = frame.ToWorld(u + cell * 0.5, v + depth * 0.5);
                    // a bay next to a planter is the shaded one
                    var flags = BayFlags();
                    if (planters && (k == first || k == last)) flags |= ParkingBayFlags.Shaded;
                    indices.Add(Bays.Count);
                    Bays.Add(new Bay(e, n, clear[k].Y, Heading(sign), BayKind(), flags));
                }

                if (!contiguous)
                {
                    if (!double.IsNaN(runStart)) runs.Add((runStart, runEnd));
                    runStart = u;
                }
                runEnd = u + cell;
            }
            if (!double.IsNaN(runStart)) runs.Add((runStart, runEnd));
            if (indices.Count == 0) return false;

            foreach (var (u0, u1) in runs) Areas.Add(Pad(u0, u1, v, v + depth, plane));
            _rows.Add(new Row(v, plane, depth, sign, indices));
            return true;
        }

        private double Heading(int sign)
        {
            double across = frame.AcrossHeading;             // compass bearing of +v
            double h = sign > 0 ? across : across + Math.PI;
            if (module == Module.Angled) h -= sign * 45.0 * Math.PI / 180.0;   // noses into a 45° bay
            if (module == Module.Parallel) h = Norm(frame.AcrossHeading + Math.PI / 2);   // along the kerb
            return Norm2(h);
        }

        private ParkingBayKind BayKind() => module == Module.Parallel ? ParkingBayKind.Car : ParkingBayKind.Car;

        private ParkingBayFlags BayFlags() => module switch
        {
            Module.Angled => ParkingBayFlags.Angled,
            Module.Parallel => ParkingBayFlags.Parallel,
            _ => ParkingBayFlags.None,
        };

        /// <summary>The aisle strip, as pad and (one-way only) arrows.</summary>
        private void LayAisle(double v, double width, double uMin, double uMax)
        {
            double cell = o.BayWidth;
            double u = Math.Ceiling((uMin + o.EdgeInset) / cell) * cell;
            double runStart = double.NaN, runEnd = double.NaN, plane = double.NaN;
            var runs = new List<(double U0, double U1, double Y)>();

            while (u + cell <= uMax - o.EdgeInset + 1e-9)
            {
                if (CellClear(u, u + cell, v, v + width, out double y))
                {
                    if (double.IsNaN(plane)) plane = y;
                    if (double.IsNaN(runStart)) runStart = u;
                    runEnd = u + cell;
                }
                else if (!double.IsNaN(runStart))
                {
                    runs.Add((runStart, runEnd, plane));
                    runStart = runEnd = plane = double.NaN;
                }
                u += cell;
            }
            if (!double.IsNaN(runStart)) runs.Add((runStart, runEnd, plane));

            bool oneWay = width < o.AisleTwoWay - 1e-9;
            foreach (var (u0, u1, y) in runs)
            {
                Areas.Add(Pad(u0, u1, v, v + width, y));
                _aisles.Add((v + width * 0.5, y, u0, u1, oneWay));
                if (!oneWay) continue;
                // arrows along a one-way aisle; two-way aisles carry none, which is also correct here
                for (double a = u0 + o.ArrowSpacing * 0.5; a < u1; a += o.ArrowSpacing)
                {
                    var (e, n) = frame.ToWorld(a, v + width * 0.5);
                    Marks.Add(new Mark(PaintType.Arrow, [e, n], y, 0,
                        Norm2(frame.AcrossHeading + Math.PI / 2), (byte)PaintArrow.Straight));
                }
            }
        }

        // ---- tests and emitters --------------------------------------------------------------

        /// <summary>
        /// A cell is clear when all four corners and its centre are inside the polygon, nothing
        /// occupies them, and the ground is known and level across it.
        /// </summary>
        private bool CellClear(double u0, double u1, double v0, double v1, out double y)
        {
            y = double.NaN;
            double lo = double.MaxValue, hi = double.MinValue;
            Span<(double U, double V)> pts =
            [
                (u0, v0), (u1, v0), (u1, v1), (u0, v1), ((u0 + u1) * 0.5, (v0 + v1) * 0.5),
            ];
            foreach (var (u, v) in pts)
            {
                var (e, n) = frame.ToWorld(u, v);
                if (!Inside(poly, e, n)) return false;
                if (ground.Occupied(e, n)) return false;
                double g = ground.Ground(e, n);
                if (double.IsNaN(g)) return false;
                lo = Math.Min(lo, g); hi = Math.Max(hi, g);
            }
            if (hi - lo > o.FlatTolerance) return false;
            y = (lo + hi) * 0.5;
            return true;
        }

        private Area Pad(double u0, double u1, double v0, double v1, double y) =>
            new(AreaKind.Pad, Rect(u0, u1, v0, v1), y, 0);

        private double[] Rect(double u0, double u1, double v0, double v1)
        {
            var a = frame.ToWorld(u0, v0);
            var b = frame.ToWorld(u1, v0);
            var c = frame.ToWorld(u1, v1);
            var d = frame.ToWorld(u0, v1);
            return [a.E, a.N, b.E, b.N, c.E, c.N, d.E, d.N];
        }

        // ---- bay markings --------------------------------------------------------------------

        /// <summary>
        /// The divider between neighbouring bays, and the open end at the aisle: a real bay is drawn
        /// with a line down each side and across its far end, never across the aisle end.
        /// </summary>
        public void MarkBays()
        {
            foreach (var row in _rows)
            {
                double vNear = row.Sign > 0 ? row.V + row.Depth : row.V;      // the aisle side
                double vFar = row.Sign > 0 ? row.V : row.V + row.Depth;
                var byU = row.BayIndices
                    .Select(i => (Index: i, U: frame.ToLocal(Bays[i].E, Bays[i].N).U))
                    .OrderBy(t => t.U).ToList();

                double cell = module == Module.Parallel ? o.ParallelLength : o.BayWidth;
                foreach (var (_, u) in byU)
                {
                    double u0 = u - cell * 0.5, u1 = u + cell * 0.5;
                    Marks.Add(Line(u0, vFar, u0, vNear, row.Y));          // the divider
                    Marks.Add(Line(u0, vFar, u1, vFar, row.Y));           // the closed end
                }
                var last = byU[^1];
                double ue = last.U + cell * 0.5;
                Marks.Add(Line(ue, vFar, ue, vNear, row.Y));              // the row's last divider
            }
        }

        private Mark Line(double u0, double v0, double u1, double v1, double y)
        {
            var a = frame.ToWorld(u0, v0);
            var b = frame.ToWorld(u1, v1);
            return new Mark(PaintType.WhiteSolid, [a.E, a.N, b.E, b.N], y, o.LineWidth);
        }

        // ---- the entrance --------------------------------------------------------------------

        /// <summary>
        /// The throat, on the bordering road with the highest priority: a give-way line into the road,
        /// a stop bar inside, and on a paid lot a boom with its kiosk and the blue P.
        /// </summary>
        public void Entrance()
        {
            Border? pick = null;
            double bestScore = double.MinValue;
            var c = Centroid(poly);
            foreach (var b in borders)
            {
                double d = Dist(c.E, c.N, b.E, b.N);
                if (d > o.EntranceReach) continue;
                double score = b.Priority * 100 - d;   // the important road first, the near one next
                if (score > bestScore) { bestScore = score; pick = b; }
            }
            if (pick is not { } road) return;   // TLM maps car parks with no mapped access: rows, no throat

            // Paid or free: hashed, because the data does not say. A lot off a busy road is the one
            // that is metered in practice, so priority tips the odds rather than deciding alone.
            Paid = (long)(Hash(seed, 0x9E3779B9) & 0xFF) < (road.Priority >= 2 ? 150 : 60);

            // into the lot, from the road toward the centroid
            double inE = c.E - road.E, inN = c.N - road.N;
            double len = Math.Sqrt(inE * inE + inN * inN);
            if (len < 1e-6) return;
            inE /= len; inN /= len;

            double half = (road.OneWay ? o.AisleOneWay : o.EntranceWidth) * 0.5;
            double startE = road.E + inE * road.HalfWidth, startN = road.N + inN * road.HalfWidth;

            // the throat bridges the gap between the carriageway edge and the lot: it ends where the
            // pad starts, so there is never a strip of grass across the way in
            var (endE, endN) = FirstPadPoint(startE, startN, inE, inN, len);
            double gate = Dist(startE, startN, endE, endN);
            if (gate > 1e-3)
            {
                double py = -inN, pn = inE;   // across the throat
                double y = GroundAt((startE + endE) * 0.5, (startN + endN) * 0.5);
                if (!double.IsNaN(y))
                    Areas.Add(new Area(AreaKind.Pad,
                    [
                        startE + py * half, startN + pn * half,
                        endE + py * half, endN + pn * half,
                        endE - py * half, endN - pn * half,
                        startE - py * half, startN - pn * half,
                    ], y, 0));
            }

            double gy = GroundAt(startE, startN);
            if (double.IsNaN(gy)) return;
            double across = Math.Atan2(inN, inE) + Math.PI / 2;
            double pe = Math.Cos(across), pnn = Math.Sin(across);

            // give way where it meets the road, as a car park's exit always must
            Marks.Add(new Mark(PaintType.GiveWayLine,
                [startE + pe * half, startN + pnn * half, startE - pe * half, startN - pnn * half], gy, 0.5));

            if (Paid)
            {
                // the boom stands 4 m in, the kiosk at the driver's window on the right
                double be = startE + inE * 4.0, bn = startN + inN * 4.0;
                double by = GroundAt(be, bn);
                double heading = Norm2(Math.Atan2(inE, inN));   // compass: the way in
                if (!double.IsNaN(by))
                {
                    Marks.Add(new Mark(PaintType.StopLine,
                        [be + pe * half, bn + pnn * half, be - pe * half, bn - pnn * half], by, 0.4));
                    Fixtures.Add(new Fixture(FixtureKind.Barrier, be, bn, by, heading));
                    Fixtures.Add(new Fixture(FixtureKind.Kiosk,
                        be - pe * (half + 0.8), bn - pnn * (half + 0.8), by, Norm2(heading + Math.PI / 2)));
                }
                double sy = GroundAt(startE + pe * (half + 0.6), startN + pnn * (half + 0.6));
                if (!double.IsNaN(sy))
                    Fixtures.Add(new Fixture(FixtureKind.Sign,
                        startE + pe * (half + 0.6), startN + pnn * (half + 0.6), sy, Norm2(heading + Math.PI)));
            }

            // the bay nearest the way in is left free, so an arriving player has somewhere to put their car
            int nearest = -1; double best = double.MaxValue;
            for (int i = 0; i < Bays.Count; i++)
            {
                double d = Dist(Bays[i].E, Bays[i].N, startE, startN);
                if (d < best) { best = d; nearest = i; }
            }
            if (nearest >= 0)
                Bays[nearest] = Bays[nearest] with { Flags = Bays[nearest].Flags | ParkingBayFlags.NearEntrance };

            // disabled bays: the two nearest the door if there is one, else the way in
            var target = frontage is { } f ? (f.DoorE, f.DoorN) : (startE, startN);
            foreach (int i in Bays
                .Select((b, i) => (b, i))
                .Where(t => t.b.Occupiable)
                .OrderBy(t => Dist(t.b.E, t.b.N, target.Item1, target.Item2))
                .Take(2).Select(t => t.i).ToList())
            {
                Bays[i] = Bays[i] with { Flags = Bays[i].Flags | ParkingBayFlags.Disabled };
                Marks.Add(new Mark(PaintType.DisabledBay, [Bays[i].E, Bays[i].N], Bays[i].Y, 1.2, Bays[i].HeadingRad));
            }
        }

        /// <summary>Walks in from the road until it is over a planned pad, so the throat stops there.</summary>
        private (double E, double N) FirstPadPoint(double e, double n, double de, double dn, double max)
        {
            for (double t = 0; t <= max; t += 0.5)
            {
                double pe = e + de * t, pn = n + dn * t;
                if (Inside(poly, pe, pn)) return (pe, pn);
            }
            return (e, n);
        }

        // ---- the extras, where there is room -------------------------------------------------

        /// <summary>A kerbed walk from the row ends to the building door, when one faces the lot.</summary>
        public void Walks()
        {
            if (frontage is not { } f) return;
            // the row whose end is nearest the door carries the walk along its far side
            double doorV = frame.ToLocal(f.DoorE, f.DoorN).V;
            Row? pick = null; double best = double.MaxValue;
            foreach (var row in _rows)
            {
                double d = Math.Abs(doorV - row.V);
                if (d < best) { best = d; pick = row; }
            }
            if (pick is not { } chosen || best > 40) return;

            var us = chosen.BayIndices.Select(i => frame.ToLocal(Bays[i].E, Bays[i].N).U).ToList();
            double v = chosen.Sign > 0 ? chosen.V - o.WalkWidth : chosen.V + chosen.Depth;
            double u0 = us.Min() - o.BayWidth * 0.5, u1 = us.Max() + o.BayWidth * 0.5;
            if (!CellClear(u0, u1, v, v + o.WalkWidth, out double y)) return;
            Areas.Add(new Area(AreaKind.Walk, Rect(u0, u1, v, v + o.WalkWidth), y, o.Kerb));
        }

        /// <summary>A trolley shelter at a big-box store, one per <see cref="Options.BaysPerShelter"/> bays.</summary>
        public void Shelters()
        {
            if (frontage is not { Retail: true } f || f.FloorAreaM2 < 600) return;
            int want = Math.Max(1, Bays.Count / o.BaysPerShelter);
            var placed = new List<(double E, double N)>();

            // A shelter stands IN a bay, as it does in every real supermarket car park: trying to
            // find 3 m of spare ground past the end of a row only works in a lot with slack, and a
            // lot laid out to its own boundary has none. The bay is kept painted and flagged
            // Blocked, so it is drawn but never filled with a car.
            foreach (int i in Bays
                .Select((b, i) => (b, i))
                .Where(t => t.b.Occupiable && (t.b.Flags & ParkingBayFlags.Disabled) == 0)
                .OrderBy(t => Dist(t.b.E, t.b.N, f.DoorE, f.DoorN))
                .Select(t => t.i))
            {
                if (placed.Count >= want) break;
                var bay = Bays[i];
                if (Dist(bay.E, bay.N, f.DoorE, f.DoorN) > 60) continue;
                // spread them: trolleys are no use all in one corner of a big lot
                if (placed.Any(p => Dist(p.E, p.N, bay.E, bay.N) < 25)) continue;
                Bays[i] = bay with { Flags = bay.Flags | ParkingBayFlags.Blocked };
                Fixtures.Add(new Fixture(FixtureKind.Shelter, bay.E, bay.N, bay.Y, bay.HeadingRad));
                placed.Add((bay.E, bay.N));
            }
        }

        private double GroundAt(double e, double n) => ground.Ground(e, n);
    }

    // ---- small geometry ----------------------------------------------------------------------

    /// <summary>Flattens a ring to a closed-free x,y array, dropping a duplicated last point.</summary>
    private static double[] Normalise(IReadOnlyList<(double E, double N)> ring)
    {
        int n = ring.Count;
        if (n >= 2 && Math.Abs(ring[0].E - ring[n - 1].E) < 1e-9 && Math.Abs(ring[0].N - ring[n - 1].N) < 1e-9) n--;
        var a = new double[n * 2];
        for (int i = 0; i < n; i++) { a[i * 2] = ring[i].E; a[i * 2 + 1] = ring[i].N; }
        return a;
    }

    private static double SignedArea(double[] p)
    {
        double s = 0;
        for (int i = 0, n = p.Length / 2; i < n; i++)
        {
            int j = (i + 1) % n;
            s += p[i * 2] * p[j * 2 + 1] - p[j * 2] * p[i * 2 + 1];
        }
        return s * 0.5;
    }

    /// <summary>The area centroid, which is stable against a dense run of vertices on one side.</summary>
    private static (double E, double N) Centroid(double[] p)
    {
        double a = SignedArea(p);
        if (Math.Abs(a) < 1e-9)
        {
            double se = 0, sn = 0;
            for (int i = 0; i < p.Length / 2; i++) { se += p[i * 2]; sn += p[i * 2 + 1]; }
            return (se / (p.Length / 2), sn / (p.Length / 2));
        }
        double ce = 0, cn = 0;
        for (int i = 0, n = p.Length / 2; i < n; i++)
        {
            int j = (i + 1) % n;
            double cross = p[i * 2] * p[j * 2 + 1] - p[j * 2] * p[i * 2 + 1];
            ce += (p[i * 2] + p[j * 2]) * cross;
            cn += (p[i * 2 + 1] + p[j * 2 + 1]) * cross;
        }
        return (ce / (6 * a), cn / (6 * a));
    }

    /// <summary>Crossing test; a point exactly on an edge may fall either way, which the 0.3 m inset covers.</summary>
    public static bool Inside(double[] p, double e, double n)
    {
        bool inside = false;
        for (int i = 0, c = p.Length / 2, j = c - 1; i < c; j = i++)
        {
            double ei = p[i * 2], ni = p[i * 2 + 1], ej = p[j * 2], nj = p[j * 2 + 1];
            if (ni > n != nj > n && e < (ej - ei) * (n - ni) / (nj - ni) + ei) inside = !inside;
        }
        return inside;
    }

    /// <summary>Andrew's monotone chain, counter-clockwise.</summary>
    private static double[] ConvexHull(double[] p)
    {
        int n = p.Length / 2;
        if (n < 3) return p;
        var idx = Enumerable.Range(0, n)
            .OrderBy(i => p[i * 2]).ThenBy(i => p[i * 2 + 1]).ToArray();
        var hull = new List<int>(n * 2);

        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            IEnumerable<int> order = pass == 0 ? idx : Enumerable.Reverse(idx);
            foreach (int i in order)
            {
                while (hull.Count - start >= 2 &&
                       Cross(hull[^2], hull[^1], i) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(i);
            }
            hull.RemoveAt(hull.Count - 1);
        }

        var a = new double[hull.Count * 2];
        for (int k = 0; k < hull.Count; k++) { a[k * 2] = p[hull[k] * 2]; a[k * 2 + 1] = p[hull[k] * 2 + 1]; }
        return a;

        double Cross(int ai, int bi, int ci) =>
            (p[bi * 2] - p[ai * 2]) * (p[ci * 2 + 1] - p[ai * 2 + 1]) -
            (p[bi * 2 + 1] - p[ai * 2 + 1]) * (p[ci * 2] - p[ai * 2]);
    }

    private static double Dist(double e0, double n0, double e1, double n1) =>
        Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0));

    /// <summary>A bearing folded into [0, π): an axis has no direction.</summary>
    private static double Norm(double a)
    {
        a %= Math.PI;
        return a < 0 ? a + Math.PI : a;
    }

    /// <summary>A heading folded into [0, 2π).</summary>
    private static double Norm2(double a)
    {
        a %= Math.Tau;
        return a < 0 ? a + Math.Tau : a;
    }

    /// <summary>The acute angle between two axes.</summary>
    private static double Delta(double a, double b)
    {
        double d = Math.Abs(Norm(a) - Norm(b));
        return Math.Min(d, Math.PI - d);
    }

    /// <summary>
    /// An LV95 bearing (east 0, north π/2) as the Godot heading the tile records store (about +Y,
    /// 0 = -Z = north): a quarter turn, and the opposite sense.
    /// </summary>
    public static double ToGodotHeading(double bearingRad) => Norm2(Math.PI / 2 - bearingRad);

    /// <summary>
    /// The lot's key: its centroid to the decimetre, FNV-1a. Stable across rebuilds and independent
    /// of which tile writes the lot, so paid/free and every other hashed choice never flips.
    /// <see cref="string.GetHashCode()"/> is randomised per process and must never be used here.
    /// </summary>
    public static ulong Key((double E, double N) centroid)
    {
        ulong h = 14695981039346656037UL;
        foreach (long v in (long[])[(long)Math.Round(centroid.E * 10), (long)Math.Round(centroid.N * 10)])
            for (int b = 0; b < 8; b++)
            {
                h ^= (byte)(v >> (b * 8));
                h *= 1099511628211UL;
            }
        return h;
    }

    private static ulong Hash(ulong seed, ulong salt)
    {
        ulong h = seed ^ salt;
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL;
        return h ^ (h >> 33);
    }
}
