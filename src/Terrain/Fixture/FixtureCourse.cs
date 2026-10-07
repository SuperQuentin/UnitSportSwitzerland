using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// A synthetic test course built in code (#221): roads with designed heights, the trees beside
/// them, and ground that follows the roads. No file, no download, no seed: the same course every
/// run. Coordinates are metres from the start, X east, Y north; Z is the altitude.
/// <see cref="FixtureChunkSource"/> turns one into tiles. Each course targets a known failure:
/// <list type="bullet">
/// <item><c>flat</c>: no road, flat ground (UI, physics, network tests), with three fields, a paved
/// strip and a 15 % ridge east of the spawn (#494, the farm machines);</item>
/// <item><c>straight</c>: 3 km of straight 6 m road, flat: top speed, overtaking;</item>
/// <item><c>hairpin</c>: a fast downhill with six 15 m-radius hairpins, 7 % down;</item>
/// <item><c>narrow</c>: a winding 4 m road with a trunk every 5 m on both edges: no verge to use;</item>
/// <item><c>junction</c>: a 9 m road through a T junction and a crossroads with 6 m side roads;</item>
/// <item><c>verge</c>: two bends with 6 m of grass verge then a tree line on each side.</item>
/// <item><c>lake</c> (#299): a 2.6 x 2 km lake east of the start, with a beach, a 150 m shelf, a
/// drop-off to 25 m and a river coming in from the west; a slipway road runs into it.</item>
/// <item><c>airport</c> (#422): flat, a 2.7 km runway, a taxiway, a terminal apron with six stands and
/// a cargo apron with two (<see cref="FixtureAirport"/>).</item>
/// </list>
/// </summary>
public sealed class FixtureCourse
{
    public const double FlatHeight = 500;

    public required string Name { get; init; }
    public List<(RoadClass Class, List<(double X, double Y, double Z)> Points)> Roads { get; } = new();
    public List<(double X, double Y, double Z, float Height)> Trees { get; } = new();

    public static readonly string[] Names = { "flat", "straight", "hairpin", "narrow", "junction", "verge", "lake", "parking", "airport", "garage" };

    /// <summary>The ground as a function, instead of following the roads; null: <see cref="Ground"/>'s roads.</summary>
    public Func<double, double, double>? Terrain { get; init; }

    /// <summary>
    /// Still water (#299): the level at a point and the fetch there, the level NaN where dry. Null:
    /// the course has no water (its source answers no water layer).
    /// </summary>
    public Func<double, double, (double Level, double Fetch)>? Water { get; init; }

    /// <summary>Ground cover per point; null: open grass everywhere.</summary>
    public Func<double, double, CoverClass>? Cover { get; init; }

    /// <summary>
    /// A plain box building standing on the course (#558): metres from the start, its front (the
    /// wall the main door goes on) facing south, turned <see cref="Turn"/> degrees. Walls and a flat
    /// roof, nothing else, as a synthetic solid in the door checks.
    /// </summary>
    public sealed record Block(double X, double Y, float Width, float Depth, float Height, float Turn = 0f,
        BuildingKind Kind = BuildingKind.Apartment);

    /// <summary>Buildings standing on the course; none by default.</summary>
    public List<Block> Blocks { get; } = new();

    /// <summary>
    /// Every block of flats rolls an underground garage door (<see cref="Interiors.GarageRule.AlwaysRolls"/>):
    /// a live check cannot depend on a hash of the building's key.
    /// </summary>
    public bool AlwaysGarage { get; init; }

    /// <summary>Boat stops (#377): a name and where it lies, metres from the start; the source plans their piers.</summary>
    public List<(string Name, double X, double Y)> Stops { get; } = new();

    /// <summary>Harbour jetties (#377): a centreline, metres from the start, with the deck's height.</summary>
    public List<List<(double X, double Y, double Z)>> Jetties { get; } = new();

    /// <summary>
    /// Car parks (#499): a ring in metres from the start, which the source hands to
    /// <c>ParkingPlanner</c> — the same planner the network stage runs over swissTLM3D's rings, so
    /// the course exercises the real layout code rather than a stand-in.
    /// </summary>
    public List<List<(double X, double Y)>> CarParks { get; } = new();

    /// <summary>
    /// The shop a car park serves, metres from the start: its door and its floor area. A big enough
    /// retail one earns trolley shelters and a walk to the door. The course has no buildings, so
    /// this is how the fixture reaches that half of the planner.
    /// </summary>
    public (double DoorX, double DoorY, double FloorAreaM2, bool Retail)? Store { get; init; }
    /// <summary>The course's airports (#422) planned at a start in LV95; null: none.</summary>
    public Func<double, double, AirportIndex>? Airports { get; init; }

    /// <summary>The box the course needs whatever its roads, metres from the start; null: the roads' box.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? Extent { get; init; }

    /// <summary>
    /// Farm fields (#494): an id, the crop and an outline, metres from the start; the source writes
    /// them into every tile their box touches, as the preprocessor does.
    /// </summary>
    public List<(uint Id, CropKind Crop, (double X, double Y)[] Outline)> Fields { get; } = new();

    /// <summary>
    /// The flat course's three fields, clear of the spawn and inside its tiles: a wheat field east
    /// (18 x 18 cells), a potato field north of it, a meadow west. Further east, clear of all three
    /// (#494, <c>--tractorcheck</c>): a 12 m strip of paving running north-south
    /// (<see cref="PavedFrom"/>), then a ridge across the whole tile (<see cref="RidgeHeight"/>):
    /// up at <see cref="RidgeGrade"/>, a plateau, down again.
    /// </summary>
    private static FixtureCourse Flat()
    {
        var c = new FixtureCourse
        {
            Name = "flat",
            Terrain = (x, _) => FlatHeight + RidgeHeight(x),
            Cover = (x, _) => x >= PavedFrom && x < PavedFrom + PavedWidth ? CoverClass.PavedArea : CoverClass.Open,
            // the spawn's tiles plus the strip and the ridge, whatever the start
            Extent = (-100, -160, RidgeFrom + 2 * RidgeSlope + RidgePlateau + 60, 160),
        };
        c.Fields.Add((0xF1E1D001, CropKind.Wheat, Box(24, -36, 96, 36)));
        c.Fields.Add((0xF1E1D002, CropKind.Potato, Box(24, 44, 72, 92)));
        c.Fields.Add((0xF1E1D003, CropKind.Meadow, Box(-96, -36, -24, 36)));
        return c;

        static (double, double)[] Box(double x0, double y0, double x1, double y1) => [(x0, y0), (x1, y0), (x1, y1), (x0, y1)];
    }

    /// <summary>The flat course's paved strip: from this x east, metres from the start, and its width.</summary>
    public const double PavedFrom = 130, PavedWidth = 12;

    /// <summary>The flat course's ridge: where it starts rising (x, m from the start), its grade, the lengths of its slopes and plateau.</summary>
    public const double RidgeFrom = 170, RidgeGrade = 0.15, RidgeSlope = 50, RidgePlateau = 30;

    /// <summary>
    /// The ridge's height over the flat at <paramref name="x"/>: up <see cref="RidgeSlope"/> m at
    /// <see cref="RidgeGrade"/>, <see cref="RidgePlateau"/> m level, down as far; each change of
    /// grade eased over 10 m (no kink to throw a vehicle).
    /// </summary>
    public static double RidgeHeight(double x)
    {
        const double ease = 10;
        double a = RidgeFrom, b = a + RidgeSlope, c = b + RidgePlateau, d = c + RidgeSlope;
        return RidgeGrade * (Eased(x, a - ease / 2) - Eased(x, b - ease / 2) - Eased(x, c - ease / 2) + Eased(x, d - ease / 2));

        // the integral of a grade going 0 -> 1 linearly over [from, from + ease], then held
        static double Eased(double x, double from) =>
            x <= from ? 0 : x < from + ease ? (x - from) * (x - from) / (2 * ease) : ease / 2 + (x - from - ease);
    }

    public static FixtureCourse? Create(string name) => name switch
    {
        "flat" => Flat(),
        "straight" => new FixtureCourse { Name = name }.Road(RoadClass.Road, new Pen(0, 0, FlatHeight, 0, 0).Straight(3000)),
        "hairpin" => Hairpin(),
        "narrow" => Narrow(),
        "junction" => Junction(),
        "verge" => Verge(),
        "lake" => Lake.Create(),
        "parking" => Parking(),
        "airport" => FixtureAirport.Create(),
        "garage" => Garage(),
        _ => null,
    };

    /// <summary>
    /// A big-box store's car park off a main road (#499): 34 m deep, so it takes two-sided bands,
    /// and 72 m of frontage, so its rows are long enough for end planters. The road runs 14° off
    /// east so the lot is NOT square to the world — which is the whole point: the old shader grid
    /// cut across rows like these, and the planner's rows have to follow the lot instead.
    /// </summary>
    private static FixtureCourse Parking()
    {
        const double turn = 14 * Math.PI / 180;
        var road = new Pen(0, 0, FlatHeight, 14, 0).Straight(600);

        // the lot sits 10 m north of the road, turned with it
        var ring = new List<(double X, double Y)>();
        foreach (var (u, v) in (( double, double)[])[(60, 10), (132, 10), (132, 44), (60, 44)])
            ring.Add((u * Math.Cos(turn) - v * Math.Sin(turn), u * Math.Sin(turn) + v * Math.Cos(turn)));

        // the store's door on the far side of the lot, facing it
        double doorU = 96, doorV = 48;
        var course = new FixtureCourse
        {
            Name = "parking",
            Terrain = (_, _) => FlatHeight,
            Store = (doorU * Math.Cos(turn) - doorV * Math.Sin(turn),
                     doorU * Math.Sin(turn) + doorV * Math.Cos(turn), 2400, true),
            Extent = (-20, -60, 320, 160),
        }.Road(RoadClass.Major, road);
        course.CarParks.Add(ring);
        return course;
    }

    /// <summary>
    /// Three blocks of flats 80 x 18 m (a car park strip, just the depth a ramp takes) and a fourth 80 x 14.5 m (the ramp along the facade) along a
    /// minor street, their fronts 6 m from its kerb, on flat ground: each rolls an underground garage
    /// door at one end of the front wall, with a pavement to the street (#558, #694). The street and the
    /// blocks stand within one tile of the default spawn.
    /// </summary>
    private static FixtureCourse Garage()
    {
        var course = new FixtureCourse
        {
            Name = "garage",
            Terrain = (_, _) => FlatHeight,
            AlwaysGarage = true,
            Extent = (-150, -150, 700, 200),
        }.Road(RoadClass.Minor, new Pen(-120, 0, FlatHeight, 0, 0).Straight(780));
        foreach (double x in new[] { 100.0, 220, 340 })
            course.Blocks.Add(new Block(x, 17, 80, 18, 15));
        // a fourth, too shallow for a ramp square to the front wall (#694): its ramp runs along the facade
        course.Blocks.Add(new Block(460, 15.25, 80, 14.5f, 15));
        return course;
    }

    private static FixtureCourse Hairpin()
    {
        // legs 8 degrees off east-west, so they fan apart away from each hairpin as on a real slope
        var pen = new Pen(0, 0, 1200, -8, -0.07);
        for (int leg = 0; leg < 6; leg++)
        {
            pen.Straight(450);
            if (leg < 5) pen.Arc(15, leg % 2 == 0 ? -164 : 164);
        }
        return new FixtureCourse { Name = "hairpin" }.Road(RoadClass.Road, pen);
    }

    private static FixtureCourse Narrow()
    {
        var pen = new Pen(0, 0, 700, 0, -0.03).Straight(150).Arc(60, 45).Straight(100).Arc(50, -90)
            .Straight(120).Arc(80, 70).Straight(200).Arc(40, -60).Straight(150).Arc(70, 35).Straight(200);
        var c = new FixtureCourse { Name = "narrow" }.Road(RoadClass.Minor, pen);
        // the edges are blocked: a trunk every 5 m, just off each edge of the 4 m tarmac
        return c.TreesAlong(pen, RoadFormat.DefaultWidth(RoadClass.Minor) / 2 + 0.8, 5, 14f);
    }

    private static FixtureCourse Junction()
    {
        // the main road is split where the side roads meet it: a lane graph links ends only
        var c = new FixtureCourse { Name = "junction" }
            .Road(RoadClass.Major, new Pen(0, 0, FlatHeight, 0, 0).Straight(700))
            .Road(RoadClass.Major, new Pen(700, 0, FlatHeight, 0, 0).Straight(700))
            .Road(RoadClass.Major, new Pen(1400, 0, FlatHeight, 0, 0).Straight(800));
        c.Road(RoadClass.Road, new Pen(700, 0, FlatHeight, 90, 0).Straight(600));      // T: north
        c.Road(RoadClass.Road, new Pen(1400, 0, FlatHeight, 90, 0).Straight(500));     // crossroads: north
        c.Road(RoadClass.Road, new Pen(1400, 0, FlatHeight, -90, 0).Straight(500));    // and south
        return c;
    }

    private static FixtureCourse Verge()
    {
        var pen = new Pen(0, 0, 600, 0, -0.02).Straight(400).Arc(120, 90).Straight(300).Arc(200, -60).Straight(400);
        var c = new FixtureCourse { Name = "verge" }.Road(RoadClass.Road, pen);
        // 6 m of open grass on each side, then a line of trunks
        return c.TreesAlong(pen, RoadFormat.DefaultWidth(RoadClass.Road) / 2 + 6, 10, 18f);
    }

    /// <summary>A road through given points (heights included), for a course whose ground is a function.</summary>
    public FixtureCourse Road(RoadClass cls, List<(double X, double Y, double Z)> points)
    {
        Roads.Add((cls, points));
        return this;
    }

    private FixtureCourse Road(RoadClass cls, Pen pen)
    {
        Roads.Add((cls, pen.Points));
        return this;
    }

    private FixtureCourse TreesAlong(Pen pen, double offset, double spacing, float height)
    {
        var p = pen.Points;
        int every = Math.Max(1, (int)Math.Round(spacing / Pen.Step));
        for (int i = every; i + 1 < p.Count; i += every)
        {
            double dx = p[i + 1].X - p[i - 1].X, dy = p[i + 1].Y - p[i - 1].Y, len = Math.Sqrt(dx * dx + dy * dy);
            double nx = -dy / len, ny = dx / len;   // left of the direction of travel
            foreach (int side in new[] { 1, -1 })
                Trees.Add((p[i].X + nx * offset * side, p[i].Y + ny * offset * side, p[i].Z, height));
        }
        return this;
    }

    /// <summary>
    /// The ground: inverse-distance weighting (power 4) of the road heights, so it is the road's
    /// own height beside it and a smooth slope between two legs. Flat with no road.
    /// </summary>
    public double Ground(double x, double y)
    {
        if (Terrain != null) return Terrain(x, y);
        if (Roads.Count == 0) return FlatHeight;
        double sum = 0, weights = 0;
        foreach (var (_, pts) in Roads)
            for (int i = 0; i < pts.Count; i += GroundSampleEvery)
            {
                double dx = pts[i].X - x, dy = pts[i].Y - y;
                double d2 = dx * dx + dy * dy + 1;
                double w = 1 / (d2 * d2);
                sum += w * pts[i].Z;
                weights += w;
            }
        return sum / weights;
    }

    /// <summary>Road points used for the ground, every 10 m: plenty for a 10 m height lattice.</summary>
    private const int GroundSampleEvery = 5;

    /// <summary>The box the course covers, in metres from the start, with <paramref name="margin"/> round it.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds(double margin)
    {
        if (Extent is { } box) return box;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var (_, pts) in Roads)
            foreach (var p in pts)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        return (minX - margin, minY - margin, maxX + margin, maxY + margin);
    }

    /// <summary>Draws a road like a turtle: straights and arcs at a fixed grade, a point every 2 m.</summary>
    public sealed class Pen
    {
        public const double Step = 2;
        public readonly List<(double X, double Y, double Z)> Points = new();
        private double _x, _y, _z, _heading;
        private readonly double _grade;

        /// <param name="headingDeg">0 east, 90 north.</param>
        /// <param name="grade">Height change per metre along the road (negative: downhill).</param>
        public Pen(double x, double y, double z, double headingDeg, double grade)
        {
            (_x, _y, _z, _heading, _grade) = (x, y, z, headingDeg * Math.PI / 180, grade);
            Points.Add((x, y, z));
        }

        public Pen Straight(double length) => Walk(length, 0);

        /// <summary>An arc of <paramref name="radius"/> m turning <paramref name="degrees"/> (positive: left).</summary>
        public Pen Arc(double radius, double degrees) =>
            Walk(radius * Math.Abs(degrees) * Math.PI / 180, degrees * Math.PI / 180 / (radius * Math.Abs(degrees) * Math.PI / 180));

        private Pen Walk(double length, double turnPerMetre)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(length / Step));
            double ds = length / steps;
            for (int k = 0; k < steps; k++)
            {
                // the midpoint heading of the step, so an arc closes on its true end point
                double h = _heading + turnPerMetre * ds / 2;
                _x += Math.Cos(h) * ds;
                _y += Math.Sin(h) * ds;
                _z += _grade * ds;
                _heading += turnPerMetre * ds;
                Points.Add((_x, _y, _z));
            }
            return this;
        }
    }
}
