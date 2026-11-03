using UnitSport.Terrain.Format;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// The fun world (#1396, <c>docs/notes/world/fun-world.md</c>): a 2 km course with the game's activities a short walk or
/// drive apart, played from the world picker like the test worlds. Metres from the start (the spawn, on the plaza south
/// of the venues), X east, Y north, flat at <see cref="FixtureCourse.FlatHeight"/> but for the snow hill, the lake and
/// the stunt park. The neon look is the client's (<c>World/Fun</c>); this is the ground and what stands on it:
/// <list type="bullet">
/// <item>the neon boulevard (Y = <see cref="BoulevardY"/>): a bowling centre, a cinema, an arcade, a nightclub, a café
/// and a restaurant with terraces, all fronts on it;</item>
/// <item>west: the race track (an oval of <see cref="TrackWidth"/> m, pit cars on its north side) with a kart loop in its infield;</item>
/// <item>all round: the grand circuit (<see cref="CircuitCorners"/>), about 9 km of chicanes, esses, a sweeper, crests and a dip, its grid on the south straight;</item>
/// <item>north: a runway with small planes and a helicopter on its apron; the air rings (<see cref="Rings"/>) start over it;</item>
/// <item>north-west: the snow hill, a groomed slope and a T-bar;</item>
/// <item>north-east: the neon arena (<see cref="Arena"/>), its stage always playing;</item>
/// <item>east: the PvP block (<see cref="PvpBox"/>), the only place weapons hurt;</item>
/// <item>south-east: the stunt park (kickers, a gap jump, a half-pipe, a bowl);</item>
/// <item>south: the truck yard and its trailer-parking puzzles; south-west: the lake, its beach, a jetty, a slipway;</item>
/// <item>south-centre: the festival field (<see cref="Festival"/>): the Älplerfest's games all year.</item>
/// </list>
/// </summary>
public static class FunCourse
{
    public const double Flat = FixtureCourse.FlatHeight;
    public const double BoulevardY = -20, BoulevardHalf = 330, EastRoadEnd = 560;
    public const double AvenueNorthEnd = 560, AvenueSouthEnd = -700;

    // ---- the race track and the kart loop ----
    public const double TrackX0 = -800, TrackX1 = -500, TrackY0 = -260, TrackY1 = -60, TrackR = 100;
    public const float TrackWidth = 14f;
    public const double KartX0 = -700, KartX1 = -600, KartY0 = -200, KartY1 = -120, KartR = 40;
    public const float KartWidth = 8f;

    // ---- the airfield ----
    public const double RunwayY = 690, RunwayX0 = -300, RunwayX1 = 1100, RunwayWidth = 40;
    public const double ApronX0 = -20, ApronX1 = 420, ApronY0 = 540, ApronY1 = RunwayY - RunwayWidth / 2;

    // ---- the snow hill: rising north from SlopeY0 to SlopeY1, a plateau, then down its back ----
    public const double HillX0 = -880, HillX1 = -520, SlopeY0 = 250, SlopeY1 = 650, PlateauY1 = 710, BackY1 = 880, HillH = 110;
    public const double LiftX = -545;

    // ---- the lake ----
    public const double LakeX = -690, LakeY = -720, LakeR = 220, BeachM = 45;
    public const double Level = Flat - 1.0;
    public const double SlipwayY = -700;

    // ---- the stunt park ----
    public const double StuntX0 = 480, StuntX1 = 920, StuntY0 = -700, StuntY1 = -260;
    public const double PipeY = -470, PipeHalf = 13, PipeFlat = 4, PipeH = 5.5, PipeX0 = 560, PipeX1 = 700, MesaHalf = 18;
    public const double BowlX = 820, BowlY = -470, BowlR = 26, BowlDepth = 5;

    // ---- the truck yard ----
    public const double YardX0 = 10, YardX1 = 400, YardY0 = -580, YardY1 = -320;

    /// <summary>The PvP block, metres from the start: weapons hurt players only when both stand inside it.</summary>
    public static readonly (double MinX, double MinY, double MaxX, double MaxY) PvpBox = (570, -210, 900, 120);

    /// <summary>The festival field's middle and radius (the Älplerfest held here all year).</summary>
    public static readonly (double X, double Y, double R) Festival = (-200, -440, 140);

    /// <summary>The air rings, metres from the start and above the flat, in flying order; the first and last over the runway.</summary>
    public static readonly (double X, double Y, double H)[] Rings =
    {
        (-150, RunwayY, 35), (250, RunwayY + 20, 55), (650, 740, 85), (1000, 450, 120), (1150, 0, 150),
        (950, -480, 110), (450, -850, 85), (-150, -900, 65), (-560, -720, 40), (-1000, -350, 95),
        (-1000, 100, 140), (-700, 500, 175), (-300, 850, 150), (150, 350, 110), (-40, RunwayY, 45),
    };

    /// <summary>A ring's radius, m: room for a small plane without threading a needle.</summary>
    public const float RingRadius = 16f;

    /// <summary>Where the arena stands, metres from the start, and which way its long axis runs.</summary>
    public const double ArenaX = 420, ArenaY = 230;

    /// <summary>The neon arena at the origin, for the model viewer.</summary>
    public static readonly StadiumSpec Arena = ArenaAt(-ArenaX, -ArenaY);

    /// <summary>
    /// The neon arena (#1396) for a course started at <paramref name="startE"/> / <paramref name="startN"/>: a one-tier bowl
    /// round a pitch, its concert stage always playing in the fun world (<c>Concert.ConcertDirector.WorldSeed</c>). A
    /// stadium row like the catalogue's, but only listed while the fun world is played (<see cref="StadiumCatalog.UseWorld"/>).
    /// </summary>
    public static StadiumSpec ArenaAt(double startE, double startN) => new()
    {
        Name = "Neon Arena", Town = "Fun world",
        E = startE + ArenaX, N = startN + ArenaY, AzimuthDeg = 0f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 37f, FrontHalfV = 53f, FrontCorner = 6f,
        Tiers = new[] { new StadiumTier(Rows: 16, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1f) },
        DepthNorth = 14f, DepthEast = 14f, DepthSouth = 14f, DepthWest = 14f,
        RoofOuter = 14f, RoofInner = 12f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 5 },
        Seat = new(0.95f, 0.15f, 0.65f), SeatAlt = new(0.1f, 0.85f, 0.95f), Facade = new(0.16f, 0.1f, 0.26f), Roof = new(0.22f, 0.12f, 0.32f),
        ConcertCapacity = 9000,
    };

    /// <summary>The set the arena plays (a seed of <c>ConcertPlanner.Plan</c>).</summary>
    public const int ArenaSeed = 1984;

    public static FixtureCourse Create()
    {
        var c = new FixtureCourse
        {
            Name = "fun",
            Terrain = Ground,
            Water = Water,
            Cover = Cover,
            Extent = (-1000, -1000, 1150, 1000),
            StadiumAt = ArenaAt,
        };
        Roads(c);
        Venues(c);
        Pvp(c);
        Yard(c);
        Fleet(c);
        c.Slopes.Add(new FixtureCourse.Slope("Neon run", PisteDifficulty.Intermediate,
            [(HillX0 + 40, SlopeY0 + 10), (LiftX - 25, SlopeY0 + 10), (LiftX - 25, SlopeY1 + 30), (HillX0 + 40, SlopeY1 + 30)]));
        // the T-bar up the slope's east edge, valley station at the foot, mountain station on the plateau
        c.Road(RoadClass.SkiLift, [(LiftX, SlopeY0 - 15, Ground(LiftX, SlopeY0 - 15)), (LiftX, SlopeY1 + 25, Ground(LiftX, SlopeY1 + 25))]);
        // a jetty off the beach for the boats
        c.Jetties.Add([(LakeX + LakeR + 20, LakeY + 60, Level + 0.4), (LakeX + LakeR - 70, LakeY + 60, Level + 0.4)]);
        return c;
    }

    private static void Roads(FixtureCourse c)
    {
        // the boulevard, split where the avenue meets it (a lane graph links ends only), on to the PvP block
        c.Road(RoadClass.Road, Line(-BoulevardHalf, BoulevardY, 0, BoulevardY));
        c.Road(RoadClass.Road, Line(0, BoulevardY, EastRoadEnd, BoulevardY));
        c.Road(RoadClass.Road, Line(0, BoulevardY, 0, AvenueNorthEnd));
        c.Road(RoadClass.Road, Line(0, BoulevardY, 0, AvenueSouthEnd));
        // the slipway: west from the avenue's end, down the beach and a few metres into the lake; east to the stunt park
        var slip = new List<(double X, double Y, double Z)>();
        for (double x = 0; x >= LakeX + LakeR - 18; x -= 2) slip.Add((x, SlipwayY, Ground(x, SlipwayY)));
        c.Road(RoadClass.Road, slip);
        c.Road(RoadClass.Road, Line(0, AvenueSouthEnd, StuntX0, AvenueSouthEnd));

        // the race track: two halves meeting at the ends of the south straight, so it is one loop of two edges
        int track = c.Roads.Count;
        c.Road(RoadClass.Major, new FixtureCourse.Pen(TrackX0, TrackY0, Flat, 0, 0).Straight(TrackX1 - TrackX0).Arc(TrackR, 180).Points);
        c.Road(RoadClass.Major, new FixtureCourse.Pen(TrackX1, TrackY1, Flat, 180, 0).Straight(TrackX1 - TrackX0).Arc(TrackR, 180).Points);
        c.RoadWidths[track] = c.RoadWidths[track + 1] = TrackWidth;
        int kart = c.Roads.Count;
        c.Road(RoadClass.Minor, new FixtureCourse.Pen(KartX0, KartY0, Flat, 0, 0).Straight(KartX1 - KartX0).Arc(KartR, 180).Points);
        c.Road(RoadClass.Minor, new FixtureCourse.Pen(KartX1, KartY1, Flat, 180, 0).Straight(KartX1 - KartX0).Arc(KartR, 180).Points);
        c.RoadWidths[kart] = c.RoadWidths[kart + 1] = KartWidth;
        // the grand circuit round everything, one closed edge of its own
        c.RoadWidths[c.Roads.Count] = CircuitWidth;
        c.Road(RoadClass.Major, Circuit());
    }

    private static List<(double X, double Y, double Z)> Line(double x0, double y0, double x1, double y1)
    {
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        return new FixtureCourse.Pen(x0, y0, Flat, Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI, 0).Straight(len).Points;
    }

    /// <summary>The venues on the boulevard's north side, their fronts (south walls) on it.</summary>
    private static void Venues(FixtureCourse c)
    {
        void Venue(VenueType type, string name, double x, float width, float depth, float height, VenueFlags flags = VenueFlags.None, byte floors = 1)
        {
            double y = FrontY + depth / 2;
            c.Blocks.Add(new FixtureCourse.Block(x, y, width, depth, height, Kind: BuildingKind.Commercial, Floors: floors));
            c.Venues.Add(new FixtureCourse.Venue(type, x, y, name, flags));
        }
        Venue(VenueType.Bowling, "Strike City", -230, 30, 48, 8.5f);
        Venue(VenueType.Cinema, "Laser Palace", -160, 36, 50, 12f);
        Venue(VenueType.Arcade, "Pixel Paradise", -100, 26, 36, 6f, VenueFlags.Pinball);
        Venue(VenueType.Cafe, "Café Miami", -55, 16, 12, 4.5f, VenueFlags.Outdoor);
        Venue(VenueType.Nightclub, "Club Turbo", 60, 26, 30, 7f);
        Venue(VenueType.Restaurant, "Sunset Diner", 115, 18, 14, 4.5f, VenueFlags.Outdoor);
    }

    /// <summary>The venues' fronts, metres north of the boulevard's centreline: room for a terrace before the kerb.</summary>
    public const double FrontY = BoulevardY + 24;

    /// <summary>The PvP block: two buildings, a tower, sheds, crates and low walls to fight round.</summary>
    private static void Pvp(FixtureCourse c)
    {
        var (x0, y0, x1, y1) = PvpBox;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        void Box(double x, double y, float w, float d, float h, float turn = 0, BuildingKind kind = BuildingKind.Other) =>
            c.Blocks.Add(new FixtureCourse.Block(x, y, w, d, h, turn, kind, (byte)Math.Max(1, (int)(h / 3.2f))));
        Box(cx, cy, 7, 7, 14);                                                   // the tower in the middle
        Box(cx - 95, cy + 85, 16, 12, 7, 0, BuildingKind.Industrial);            // two sheds of a works
        Box(cx + 95, cy - 85, 16, 12, 7, 90, BuildingKind.Industrial);
        foreach (var (x, y, t) in new[] { (-50.0, 40.0, 0f), (50.0, -40.0, 0f), (-40.0, -60.0, 45f), (40.0, 60.0, 45f), (-110.0, -30.0, 90f), (110.0, 30.0, 90f), (0.0, 110.0, 0f), (0.0, -110.0, 0f) })
            Box(cx + x, cy + y, 3, 3, 2.5f, t);                                  // crates to duck behind
        foreach (var (x, y, t) in new[] { (-25.0, 0.0, 90f), (25.0, 0.0, 90f), (0.0, 30.0, 0f), (0.0, -30.0, 0f), (-75.0, 75.0, 30f), (75.0, -75.0, 30f), (-80.0, -100.0, 0f), (80.0, 100.0, 0f), (-130.0, 50.0, 90f), (130.0, -50.0, 90f) })
            Box(cx + x, cy + y, 12, 0.8f, 1.3f, t);                              // low walls: crouch behind
    }

    /// <summary>
    /// The truck yard's puzzles: a dock row to reverse a semi into between parked trailers, an L-shaped alley to back a
    /// trailer through, trailers to back under between containers, and a box to park the drawbar train in.
    /// </summary>
    private static void Yard(FixtureCourse c)
    {
        // the warehouse along the north edge, its loading front on the yard; a bay every 5 m in front of it
        c.Blocks.Add(new FixtureCourse.Block(225, YardY1 - 18, 150, 30, 9, Kind: BuildingKind.Industrial, Floors: 1));
        double dockY = YardY1 - 33;
        for (int i = 0; i <= 12; i++) c.Lines.Add((165 + 5 * i, dockY, 165 + 5 * i, dockY - 17));
        // the alley: two walls 5 m apart, 60 m north then 40 m east (an L), entered from the south
        c.Blocks.Add(new FixtureCourse.Block(40, -482.5, 3, 65, 4, Kind: BuildingKind.Other));      // outer: x 38.5-41.5, y -515 to -450
        c.Blocks.Add(new FixtureCourse.Block(48, -486.5, 3, 57, 4, Kind: BuildingKind.Other));      // inner: x 46.5-49.5, y -515 to -458
        c.Blocks.Add(new FixtureCourse.Block(69.25, -448.5, 61.5f, 3, 4, Kind: BuildingKind.Other)); // outer: y -450 to -447
        c.Blocks.Add(new FixtureCourse.Block(73.25, -456.5, 53.5f, 3, 4, Kind: BuildingKind.Other)); // inner: y -458 to -455
        // containers in pairs: a trailer stands in the 3.6 m gap between each pair, to back under
        foreach (double x in new[] { 170.0, 215, 260, 305 })
        {
            c.Blocks.Add(new FixtureCourse.Block(x - 3.05, -505, 2.5f, 12, 2.6f, Kind: BuildingKind.Other));
            c.Blocks.Add(new FixtureCourse.Block(x + 3.05, -505, 2.5f, 12, 2.6f, Kind: BuildingKind.Other));
        }
        // the drawbar train's box: 22 x 4 m along the yard's south edge, kerbed by low blocks at its ends
        c.Lines.Add((290, -568, 312, -568)); c.Lines.Add((290, -564, 312, -564));
        c.Lines.Add((290, -568, 290, -564)); c.Lines.Add((312, -568, 312, -564));
        c.Blocks.Add(new FixtureCourse.Block(287, -566, 1.2f, 4, 0.5f, Kind: BuildingKind.Other));
        c.Blocks.Add(new FixtureCourse.Block(315, -566, 1.2f, 4, 0.5f, Kind: BuildingKind.Other));
    }

    /// <summary>The vehicles standing ready: pit cars, karts, planes, trucks with trailers, a boat on its trailer.</summary>
    private static void Fleet(FixtureCourse c)
    {
        // the pit row north of the track's north straight: the 80s and 90s heroes, nose to the track
        int[] heroes = [8, 9, 12, 22, 27, 11, 14, 19, 15, 31, 24, 28];
        for (int i = 0; i < heroes.Length; i++) c.Fleet.Add(new(heroes[i], TrackX0 + 15 + 24 * i, TrackY1 + 22, 180));
        // the grand circuit's grid: two staggered columns behind the start line on the main straight, facing east
        int[] grid = [27, 31, 22, 9, 12, 33, 32, 30, 23, 8];
        for (int i = 0; i < grid.Length; i++) c.Fleet.Add(new(grid[i], CircuitGridX - 14 * i, CircuitCorners[0].Y + (i % 2 == 0 ? 3 : -3), 90));
        // karts in the kart loop's infield
        for (int i = 0; i < 8; i++) c.Fleet.Add(new(36, KartX0 + 8 + 12 * i, (KartY0 + KartY1) / 2, 90));
        // small planes and a helicopter on the apron, facing the runway
        for (int i = 0; i < 5; i++) c.Fleet.Add(new(7, ApronX0 + 60 + 55 * i, ApronY0 + 50, 0));
        c.Fleet.Add(new(6, ApronX1 - 40, ApronY0 + 35, 0));
        // a saucer behind the boulevard, for whoever finds it
        c.Fleet.Add(new(201, 180, 90, 0));
        // motorbikes on the plaza
        for (int i = 0; i < 4; i++) c.Fleet.Add(new(64 + i, 25 + 4 * i, BoulevardY - 14, 0));

        int curtain = Trailer(0, 0.55f), tanker = Trailer(1, 0.8f), timber = Trailer(2, 1f);
        // a semi ready to go, and lone trailers in the dock row to reverse between
        c.Fleet.Add(new(96, 140, -440, 90, curtain));
        foreach (int bay in new[] { 0, 2, 3, 6, 8, 9, 11 })
            c.Fleet.Add(new(120, 167.5 + 5 * bay, YardY1 - 33 - 9, 180, bay % 3 == 0 ? tanker : curtain));
        // trailers waiting between the containers, noses out: back the tractor under
        foreach (double x in new[] { 170.0, 260 }) c.Fleet.Add(new(120, x, -505, 180, timber));
        c.Fleet.Add(new(96, 215, -535, 0));
        // the rigid with its drawbar trailer, for the box
        c.Fleet.Add(new(97, 340, -430, 270, Trailer(3, 0.4f)));
        // a pickup with a speedboat on its trailer at the top of the slipway: back it into the lake
        c.Fleet.Add(new(101, -330, SlipwayY, 90, Trailer(5, 1f)));
    }

    /// <summary><c>TrailerCatalog.Code</c> (index + 1, load percent in the second byte), which this plain file cannot reach.</summary>
    private static int Trailer(int index, float load) => (index + 1) | (int)Math.Round(load * 100) << 8;

    // ---- the grand circuit ----------------------------------------------------------------------------

    /// <summary>The grand circuit's width, m.</summary>
    public const float CircuitWidth = 12f;

    /// <summary>
    /// The grand circuit's corners, metres from the start, anticlockwise from the start line, each with the radius its
    /// corner is rounded to: round the whole course, outside every other area. The main straight along the south with
    /// a chicane, a fast sweeper into the east side's esses, over the east crest, a tight corner onto the back straight
    /// with its own chicane and crest, the bus stop half-way down the west side.
    /// </summary>
    public static readonly (double X, double Y, double R)[] CircuitCorners =
    {
        (-1150, -1130, 90),                                                      // turn 1: onto the main straight
        (120, -1130, 30), (165, -1100, 30), (245, -1100, 30), (290, -1130, 30),   // the chicane
        (1240, -1130, 160),                                                      // the sweeper
        (1240, -700, 70), (1160, -480, 70), (1320, -250, 80), (1250, 80, 120),   // the esses, toward the crest
        (1330, 1000, 60),                                                        // onto the back straight
        (420, 1000, 35), (380, 955, 35), (280, 955, 35), (240, 1000, 35),       // the back chicane
        (-1150, 1000, 110),                                                      // the north-west corner
        (-1150, 160, 40), (-1075, 110, 40), (-1075, -60, 40), (-1150, -110, 40),  // the bus stop
    };

    /// <summary>
    /// The grand circuit's centreline, a point every <see cref="FixtureCourse.Pen.Step"/> m, closed (the last point is the
    /// first) and on the ground: each corner of <see cref="CircuitCorners"/> rounded to its radius, straights between.
    /// </summary>
    public static List<(double X, double Y, double Z)> Circuit()
    {
        var c = CircuitCorners;
        int n = c.Length;
        var line = new List<(double X, double Y)>();
        for (int i = 0; i < n; i++)
        {
            var (px, py, r) = c[i];
            var (ax, ay, _) = c[(i + n - 1) % n];
            var (bx, by, _) = c[(i + 1) % n];
            double inX = px - ax, inY = py - ay, inL = Math.Sqrt(inX * inX + inY * inY);
            double outX = bx - px, outY = by - py, outL = Math.Sqrt(outX * outX + outY * outY);
            (inX, inY, outX, outY) = (inX / inL, inY / inL, outX / outL, outY / outL);
            double turn = Math.Atan2(inX * outY - inY * outX, inX * outX + inY * outY);   // signed, left positive
            double t = r * Math.Tan(Math.Abs(turn) / 2);
            // the arc from the tangent point on the way in to the one on the way out
            double sx = px - inX * t, sy = py - inY * t;
            double side = Math.Sign(turn);
            double cx = sx - inY * r * side, cy = sy + inX * r * side;
            double a0 = Math.Atan2(sy - cy, sx - cx);
            int steps = Math.Max(1, (int)Math.Ceiling(r * Math.Abs(turn) / FixtureCourse.Pen.Step));
            for (int k = 0; k <= steps; k++)
            {
                double a = a0 + turn * k / steps;
                line.Add((cx + Math.Cos(a) * r, cy + Math.Sin(a) * r));
            }
        }
        line.Add(line[0]);
        // straights between the arcs, a point every step
        var pts = new List<(double X, double Y, double Z)>();
        for (int i = 1; i < line.Count; i++)
        {
            var (x0, y0) = line[i - 1];
            var (x1, y1) = line[i];
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int steps = Math.Max(1, (int)Math.Ceiling(len / FixtureCourse.Pen.Step));
            for (int k = i == 1 ? 0 : 1; k <= steps; k++)
            {
                double x = x0 + (x1 - x0) * k / steps, y = y0 + (y1 - y0) * k / steps;
                pts.Add((x, y, Ground(x, y)));
            }
        }
        return pts;
    }

    /// <summary>The circuit's crests and dip: broad, gentle swells of the ground it runs over (nothing else stands there).</summary>
    private static double Swells(double x, double y)
    {
        double h = 0;
        foreach (var (sx, sy, r, height) in new[] { (1290.0, 300.0, 230.0, 14.0), (-420.0, 1000.0, 190.0, 10.0), (420.0, -1130.0, 160.0, -5.0) })
        {
            double d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
            if (d < r) h += height * (0.5 + 0.5 * Math.Cos(Math.PI * d / r));
        }
        return h;
    }

    /// <summary>The grid's front car, metres from the start: the start line just ahead of it on the main straight.</summary>
    public const double CircuitGridX = -850;

    // ---- the ground ------------------------------------------------------------------------------

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static double Ground(double x, double y) => Flat + Hill(x, y) + Stunts(x, y) + Lake(x, y) + Swells(x, y);

    /// <summary>The snow hill: 110 m up over 400 m north (15°, eased at both ends), a plateau, a steeper back; steep flanks.</summary>
    private static double Hill(double x, double y)
    {
        if (x < HillX0 - 60 || x > HillX1 + 60 || y < SlopeY0 || y > BackY1) return 0;
        double rise = y < SlopeY1 ? (y - SlopeY0) / (SlopeY1 - SlopeY0) : y < PlateauY1 ? 1 : 1 - (y - PlateauY1) / (BackY1 - PlateauY1);
        // eased so the foot and the brow have no kink to throw a skier
        double h = HillH * Smooth(rise * 1.08 - 0.04);
        double flank = Math.Min(Smooth((x - (HillX0 - 60)) / 60), Smooth((HillX1 + 60 - x) / 60));
        return h * flank;
    }

    /// <summary>The lake: a round bowl, a gravel beach falling to the water, a shelf, then 12 m deep.</summary>
    private static double Lake(double x, double y)
    {
        double d = Math.Sqrt((x - LakeX) * (x - LakeX) + (y - LakeY) * (y - LakeY));
        if (d > LakeR + BeachM) return 0;
        if (d > LakeR) return -1.5 * (1 - Smooth((d - LakeR) / BeachM));   // the beach: 1.5 m down to the lake's rim over 45 m
        double s = LakeR - d;   // metres in from the waterline
        return -1.5 - (s < 60 ? 2.5 * s / 60 : 2.5 + 9.5 * Smooth((s - 60) / 50));
    }

    /// <summary>Still water over the lake, a little past the waterline so its edge hides under the beach.</summary>
    public static (double Level, double Fetch) Water(double x, double y)
    {
        double d = Math.Sqrt((x - LakeX) * (x - LakeX) + (y - LakeY) * (y - LakeY));
        return d < LakeR + 12 && Ground(x, y) < Level + 1 ? (Level, 2 * LakeR) : (double.NaN, 0);
    }

    /// <summary>The stunt park's ground over the flat: kickers, the gap jump, the half-pipe through its mesa, the bowl.</summary>
    private static double Stunts(double x, double y)
    {
        if (x < StuntX0 || x > StuntX1 || y < StuntY0 || y > StuntY1) return 0;
        double h = 0;
        // three kickers along the north lane (heading east): 1.2, 2 and 3 m lips, each a wedge then a drop
        foreach (var (k, lip, run) in new[] { (540.0, 1.2, 8.0), (620.0, 2.0, 12.0), (720.0, 3.0, 16.0) })
            if (Math.Abs(y + 300) < 6 && x > k - run && x <= k) h = Math.Max(h, lip * Smooth((x - (k - run)) / run * 1.2 - 0.2));
        // the gap jump (south lane): up 5 m over 40 m, a 30 m gap, a 50 m landing down
        if (Math.Abs(y + 630) < 8)
        {
            if (x > 540 && x <= 580) h = Math.Max(h, 5 * Smooth((x - 540) / 40 * 1.15 - 0.15));
            if (x >= 610 && x < 660) h = Math.Max(h, 5 * (1 - Smooth((x - 610) / 50)));
        }
        // the half-pipe: a mesa PipeH tall cut through east-west by a U channel, open at both ends
        if (x >= PipeX0 && x <= PipeX1 && Math.Abs(y - PipeY) < PipeHalf + MesaHalf)
        {
            double w = Math.Abs(y - PipeY);
            double u = w <= PipeFlat ? 0
                : w < PipeHalf ? PipeH * (1 - Math.Sqrt(Math.Max(0, 1 - Math.Pow((w - PipeFlat) / (PipeHalf - PipeFlat), 2))))
                : PipeH;
            // the deck's outer edge slopes down 1:2 so it can be ridden off; the ends taper over 10 m
            if (w > PipeHalf + MesaHalf - 2 * PipeH) u = Math.Min(u, (PipeHalf + MesaHalf - w) / 2);
            double end = Math.Min(x - PipeX0, PipeX1 - x);
            h = Math.Max(h, Math.Min(u, PipeH * Smooth(end / 10)));
        }
        // the bowl: a dish, its lip flush with the ground
        double b = Math.Sqrt((x - BowlX) * (x - BowlX) + (y - BowlY) * (y - BowlY));
        if (b < BowlR) h -= BowlDepth * (1 - Smooth(b / BowlR));
        return h;
    }

    /// <summary>Paving where vehicles play, gravel on the beach and the lake's shelf, grass elsewhere.</summary>
    public static CoverClass Cover(double x, double y)
    {
        var (level, _) = Water(x, y);
        if (!double.IsNaN(level)) return level - Ground(x, y) > 3 ? CoverClass.Water : CoverClass.LooseScree;
        double d = Math.Sqrt((x - LakeX) * (x - LakeX) + (y - LakeY) * (y - LakeY));
        if (d < LakeR + BeachM) return CoverClass.LooseScree;
        if (x >= StuntX0 && x <= StuntX1 && y >= StuntY0 && y <= StuntY1) return CoverClass.PavedArea;
        if (x >= YardX0 && x <= YardX1 && y >= YardY0 && y <= YardY1) return CoverClass.PavedArea;
        if (x >= ApronX0 && x <= ApronX1 && y >= ApronY0 && y <= ApronY1) return CoverClass.PavedArea;
        if (x >= RunwayX0 && x <= RunwayX1 && Math.Abs(y - RunwayY) <= RunwayWidth / 2) return CoverClass.PavedArea;
        // the plaza south of the boulevard and the pavement before the venues
        if (Math.Abs(x) <= BoulevardHalf && y >= BoulevardY - 30 && y <= FrontY) return CoverClass.PavedArea;
        var (px0, py0, px1, py1) = PvpBox;
        if (x >= px0 && x <= px1 && y >= py0 && y <= py1) return CoverClass.LooseScree;
        return CoverClass.Open;
    }
}
