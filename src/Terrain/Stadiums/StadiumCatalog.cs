using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// The stadiums we build, one row each (#885). A row is placed by its centre spot, so like the
/// IKEA landmarks it is a pure function of the tile on every peer: the tile holding the centre spot
/// builds the whole bowl, mesh and collision, and nothing is sent.
///
/// <para>
/// Adding a stadium: take the centre spot and axis from the pitch's <c>SportsField</c> cells in its
/// cover tile (the centroid and the principal axis), the seating front, depths and roof from the
/// swissimage aerial (WMS <c>ch.swisstopo.swissimage</c>, 3 px a metre is plenty), and the tiers
/// from photos and the published capacity; then check it with <c>--stadiumcheck</c> and look at
/// it in the model viewer. How each figure of a row was found: <c>docs/notes/terrain/stadiums.md</c>.
/// </para>
/// </summary>
public static class StadiumCatalog
{
    /// <summary>
    /// Stade de Genève (La Praille, Lancy), 2003, Bernard Mocellin with RSA: 30,084 seats, all
    /// covered, garnet since the 2019-2020 renovation. Pitch from cover_2498_1114 (centroid and
    /// principal axis of its SportsField cells: 104.4 x 67.8 m, 1.35 degrees west of north); roof
    /// 153 x 206 m outside, opening 83 x 133 m, from the aerial; 39 m overall with the roof at 32 m
    /// (pss-archi data sheet); 48 trusses on 48 pillars with a translucent polycarbonate ring along
    /// the inner edge (builder's dossier).
    /// </summary>
    public static readonly StadiumSpec Geneva = new()
    {
        Name = "Stade de Genève", Town = "Lancy",
        E = 2498751.2, N = 1114890.3, AzimuthDeg = -1.35f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 40f, FrontHalfV = 62f, FrontCorner = 4f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 17, RowDepth: 0.8f, Riser: 0.38f, Gap: 0f, Step: 1.2f),
            // the boxes' and clubs' glazing under the upper tier
            new StadiumTier(Rows: 17, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 3.4f),
        },
        DepthNorth = 41f, DepthEast = 32.5f, DepthSouth = 39f, DepthWest = 38.3f,
        RoofOuter = 34f, RoofInner = 30f, RoofInset = 2.5f, RoofRing = 9f, Solar = true,
        Columns = 48, ColumnTop = 39f,
        // a gate in every side, the players' tunnel in the main (west) stand; a vomitory into each
        // seat block of both tiers, the upper ones from the club level behind the boxes
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 6, 3 },
    };

    /// <summary>Stadion Wankdorf (Bern), 2005: 31,500 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Wankdorf = new()
    {
        Name = "Stadion Wankdorf", Town = "Bern",
        E = 2601996.7, N = 1201338.3, AzimuthDeg = -63.5f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 42.5f, FrontHalfV = 63.5f, FrontCorner = 6f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 15, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 19, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 3f),
        },
        DepthNorth = 39.5f, DepthEast = 42f, DepthSouth = 40.5f, DepthWest = 45f,
        RoofOuter = 37f, RoofInner = 31f, RoofInset = 8f, RoofRing = 0f, Solar = true,
        Columns = 40, ColumnTop = 39f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 5, 3 },
        Seat = new(0.95f, 0.8f, 0.1f), SeatAlt = new(0.1f, 0.1f, 0.1f), Facade = new(0.7f, 0.72f, 0.75f), Roof = new(0.62f, 0.64f, 0.68f),
        ConcertCapacity = 44500,
    };

    /// <summary>St. Jakob-Park (Basel), 2001: 38,512 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec StJakobPark = new()
    {
        Name = "St. Jakob-Park", Town = "Basel",
        E = 2613666.2, N = 1265671.3, AzimuthDeg = -68f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 41f, FrontHalfV = 63.5f, FrontCorner = 10f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 12, RowDepth: 0.74f, Riser: 0.4f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 12, RowDepth: 0.74f, Riser: 0.48f, Gap: 2f, Step: 3f),
            new StadiumTier(Rows: 13, RowDepth: 0.74f, Riser: 0.55f, Gap: 2f, Step: 3f),
        },
        DepthNorth = 32.5f, DepthEast = 33.5f, DepthSouth = 27.5f, DepthWest = 30f,
        RoofOuter = 37f, RoofInner = 30f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 4, 3, 3 },
        Seat = new(0.8f, 0.12f, 0.15f), SeatAlt = new(0.15f, 0.25f, 0.65f), Facade = new(0.85f, 0.86f, 0.88f), Roof = new(0.6f, 0.62f, 0.66f),
        ConcertCapacity = 60000,
    };

    /// <summary>Stadion Letzigrund (Zürich), 2007: 26,104 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Letzigrund = new()
    {
        Name = "Stadion Letzigrund", Town = "Zürich",
        E = 2680429.7, N = 1248535.3, AzimuthDeg = 38.7f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 52f, FrontHalfV = 90f, FrontCorner = 48f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 12, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 12, RowDepth: 0.8f, Riser: 0.45f, Gap: 2f, Step: 2f),
        },
        DepthNorth = 33f, DepthEast = 33.5f, DepthSouth = 34.5f, DepthWest = 38f,
        RoofOuter = 28f, RoofInner = 24f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 62, ColumnTop = 30f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 4, 3 },
        Seat = new(0.18f, 0.35f, 0.7f), SeatAlt = new(0.9f, 0.9f, 0.92f), Facade = new(0.55f, 0.35f, 0.25f), Roof = new(0.45f, 0.48f, 0.3f),
        ConcertCapacity = 50044,
    };

    /// <summary>kybunpark (Berit Sitterstadion) (St. Gallen), 2008: 20,660 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Kybunpark = new()
    {
        Name = "kybunpark (Berit Sitterstadion)", Town = "St. Gallen",
        E = 2740904.3, N = 1252512, AzimuthDeg = 67f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 40f, FrontHalfV = 58.5f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 13, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 13, RowDepth: 0.8f, Riser: 0.48f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 26.5f, DepthEast = 21f, DepthSouth = 25.5f, DepthWest = 29f,
        RoofOuter = 24f, RoofInner = 21f, RoofInset = 1.5f, RoofRing = 0f, Solar = true,
        Columns = 30, ColumnTop = 26f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 4, 3 },
        Seat = new(0.1f, 0.55f, 0.25f), SeatAlt = new(0.95f, 0.95f, 0.95f), Facade = new(0.65f, 0.68f, 0.72f), Roof = new(0.55f, 0.58f, 0.64f),
        ConcertCapacity = 20660,
    };

    /// <summary>swissporarena (Luzern), 2011: 16,800 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Swissporarena = new()
    {
        Name = "swissporarena", Town = "Luzern",
        E = 2665861.7, N = 1209462, AzimuthDeg = -10.5f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 39f, FrontHalfV = 58.5f, FrontCorner = 14f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 11, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 11, RowDepth: 0.8f, Riser: 0.45f, Gap: 2f, Step: 2f),
        },
        DepthNorth = 30.5f, DepthEast = 28f, DepthSouth = 28f, DepthWest = 30f,
        RoofOuter = 22f, RoofInner = 19f, RoofInset = 0.5f, RoofRing = 0f, Solar = true,
        Columns = 24, ColumnTop = 24f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 3, 3 },
        Seat = new(0.15f, 0.35f, 0.75f), SeatAlt = new(0.95f, 0.95f, 0.95f), Facade = new(0.88f, 0.82f, 0.62f), Roof = new(0.89f, 0.84f, 0.66f),
        ConcertCapacity = 20000,
    };

    /// <summary>Stockhorn Arena (Visana Stadion) (Thun), 2011: 10,398 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec StockhornArena = new()
    {
        Name = "Stockhorn Arena (Visana Stadion)", Town = "Thun",
        E = 2612836, N = 1177056.7, AzimuthDeg = -22.5f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 40f, FrontHalfV = 60f, FrontCorner = 4f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 7, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1f),
            new StadiumTier(Rows: 6, RowDepth: 0.8f, Riser: 0.45f, Gap: 2f, Step: 1.5f),
        },
        DepthNorth = 17.5f, DepthEast = 17f, DepthSouth = 18f, DepthWest = 23f,
        RoofOuter = 19f, RoofInner = 17f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 16, ColumnTop = 21f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 2, 3 },
        Seat = new(0.8f, 0.12f, 0.15f), SeatAlt = new(0.95f, 0.95f, 0.95f), Facade = new(0.75f, 0.76f, 0.78f), Roof = new(0.72f, 0.73f, 0.76f),
        ConcertCapacity = 20000,
    };

    /// <summary>Stade de Tourbillon (Sion), 1968: 14,283 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Tourbillon = new()
    {
        Name = "Stade de Tourbillon", Town = "Sion",
        E = 2595164.7, N = 1120212.3, AzimuthDeg = -58f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 43.5f, FrontHalfV = 59f, FrontCorner = 12f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 18, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1f),
        },
        DepthNorth = 21f, DepthEast = 18.5f, DepthSouth = 21f, DepthWest = 22.5f,
        RoofOuter = 15f, RoofInner = 13f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.East, StadiumSide.West, StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 6 },
        Seat = new(0.8f, 0.12f, 0.15f), SeatAlt = new(0.95f, 0.95f, 0.95f), Facade = new(0.85f, 0.85f, 0.88f), Roof = new(0.9f, 0.9f, 0.92f),
        ConcertCapacity = 14283,
    };

    /// <summary>Stade de la Tuilière (Lausanne), 2020: 12,544 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Tuiliere = new()
    {
        Name = "Stade de la Tuilière", Town = "Lausanne",
        E = 2537360.3, N = 1155029.3, AzimuthDeg = -27.2f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 37.5f, FrontHalfV = 57f, FrontCorner = 4f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1f),
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.48f, Gap: 2f, Step: 1.6f),
        },
        DepthNorth = 23f, DepthEast = 23.5f, DepthSouth = 25f, DepthWest = 29.5f,
        RoofOuter = 22f, RoofInner = 20f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 2, 3 },
        Seat = new(0.12f, 0.3f, 0.65f), SeatAlt = new(0.95f, 0.95f, 0.95f), Facade = new(0.7f, 0.72f, 0.74f), Roof = new(0.4f, 0.46f, 0.35f),
        ConcertCapacity = 12544,
    };

    /// <summary>Stade de la Maladière (Neuchâtel), 2007: 12,500 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Maladiere = new()
    {
        Name = "Stade de la Maladière", Town = "Neuchâtel",
        E = 2562388, N = 1205021, AzimuthDeg = 63f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 37f, FrontHalfV = 57.5f, FrontCorner = 4f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.4f, Gap: 0f, Step: 1f),
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.48f, Gap: 2f, Step: 1.8f),
        },
        DepthNorth = 52.5f, DepthEast = 26f, DepthSouth = 24f, DepthWest = 25f,
        RoofOuter = 25f, RoofInner = 22f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.East, StadiumSide.South, StadiumSide.West },
        VomitoryRows = new[] { 2, 3 },
        Seat = new(0.8f, 0.12f, 0.15f), SeatAlt = new(0.1f, 0.1f, 0.1f), Facade = new(0.7f, 0.72f, 0.75f), Roof = new(0.6f, 0.62f, 0.66f),
        ConcertCapacity = 12500,
    };

    /// <summary>Stadion Brügglifeld (Aarau), 1924: 8,450 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Brugglifeld = new()
    {
        Name = "Stadion Brügglifeld", Town = "Aarau",
        E = 2646907, N = 1248242, AzimuthDeg = 41.5f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 37f, FrontHalfV = 53f, FrontCorner = 4f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 15, RowDepth: 0.8f, Riser: 0.35f, Gap: 0f, Step: 0.8f),
        },
        DepthNorth = 8f, DepthEast = 13f, DepthSouth = 8f, DepthWest = 17f,
        RoofOuter = 10f, RoofInner = 10f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.West },
        VomitoryRows = new[] { 5 },
        Seat = new(0.8f, 0.12f, 0.15f), SeatAlt = new(0.95f, 0.95f, 0.95f), Facade = new(0.55f, 0.5f, 0.45f), Roof = new(0.35f, 0.35f, 0.35f),
        ConcertCapacity = 8450,
    };

    /// <summary>Stadion Schützenwiese (Winterthur), 1922: 8,700 seats. Centre, axis and outline from the swissimage aerial; heights, tiers and colours estimated (#948 research).</summary>
    public static readonly StadiumSpec Schutzenwiese = new()
    {
        Name = "Stadion Schützenwiese", Town = "Winterthur",
        E = 2696352.7, N = 1261898.3, AzimuthDeg = 23f,
        PitchWidth = 68f, PitchLength = 105f,
        FrontHalfU = 41f, FrontHalfV = 56.5f, FrontCorner = 4f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 12, RowDepth: 0.8f, Riser: 0.35f, Gap: 0f, Step: 0.8f),
        },
        DepthNorth = 20f, DepthEast = 12f, DepthSouth = 26f, DepthWest = 25f,
        RoofOuter = 10f, RoofInner = 10f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.West },
        VomitoryRows = new[] { 4 },
        Seat = new(0.8f, 0.12f, 0.15f), SeatAlt = new(0.95f, 0.95f, 0.95f), Facade = new(0.55f, 0.52f, 0.5f), Roof = new(0.8f, 0.8f, 0.82f),
        ConcertCapacity = 8700,
    };

    /// <summary>Hallenstadion (Zürich-Oerlikon), arena: 11,200 seats, 13,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec Hallenstadion = new()
    {
        Name = "Hallenstadion", Town = "Zürich",
        Kind = VenueKind.Arena,
        E = 2684005, N = 1251786, AzimuthDeg = 0f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 12, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 12, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 26.5f, DepthEast = 36.5f, DepthSouth = 26.5f, DepthWest = 36.5f,
        RoofOuter = 17.72f, RoofInner = 27f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 4, 3 },
        Seat = new(0.55f, 0.12f, 0.12f), SeatAlt = new(0.44f, 0.096f, 0.096f), Facade = new(0.8f, 0.76f, 0.68f), Roof = new(0.8f, 0.8f, 0.78f),
        ConcertCapacity = 13000,
    };

    /// <summary>PostFinance Arena (Bern), arena: 17,031 seats, 17,031 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec PostFinanceArena = new()
    {
        Name = "PostFinance Arena", Town = "Bern",
        Kind = VenueKind.Arena,
        E = 2602296, N = 1200841, AzimuthDeg = -42f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 17, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 18, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 32f, DepthEast = 23f, DepthSouth = 32f, DepthWest = 23f,
        RoofOuter = 22.82f, RoofInner = 28f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 5, 3 },
        Seat = new(0.75f, 0.1f, 0.1f), SeatAlt = new(0.6f, 0.08f, 0.08f), Facade = new(0.78f, 0.78f, 0.78f), Roof = new(0.82f, 0.82f, 0.84f),
        ConcertCapacity = 17031,
        SeatWidth = 0.42f,   // standing terraces: people closer than seats
    };

    /// <summary>St. Jakobshalle (Basel (Münchenstein BL, Basel border)), arena: 9,000 seats, 12,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec StJakobshalle = new()
    {
        Name = "St. Jakobshalle", Town = "Basel",
        Kind = VenueKind.Arena,
        E = 2613529, N = 1265481, AzimuthDeg = -4f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 10, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 10, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 18.5f, DepthEast = 33.5f, DepthSouth = 18.5f, DepthWest = 33.5f,
        RoofOuter = 18f, RoofInner = 22f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 3, 3 },
        Seat = new(0.1f, 0.25f, 0.6f), SeatAlt = new(0.08f, 0.2f, 0.48f), Facade = new(0.35f, 0.36f, 0.38f), Roof = new(0.8f, 0.8f, 0.82f),
        ConcertCapacity = 12000,
    };

    /// <summary>Swiss Life Arena (Zürich-Altstetten), arena: 11,157 seats, 12,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec SwissLifeArena = new()
    {
        Name = "Swiss Life Arena", Town = "Zürich",
        Kind = VenueKind.Arena,
        E = 2678619, N = 1249913, AzimuthDeg = 21f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 13, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 13, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 18f, DepthEast = 33f, DepthSouth = 18f, DepthWest = 33f,
        RoofOuter = 22f, RoofInner = 26f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 4, 3 },
        Seat = new(0.1f, 0.3f, 0.65f), SeatAlt = new(0.08f, 0.24f, 0.52f), Facade = new(0.72f, 0.72f, 0.7f), Roof = new(0.45f, 0.36f, 0.28f),
        ConcertCapacity = 12000,
    };

    /// <summary>Vaudoise Aréna (Lausanne (Prilly/Renens, Malley)), arena: 10,000 seats, 9,600 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec VaudoiseArena = new()
    {
        Name = "Vaudoise Aréna", Town = "Lausanne",
        Kind = VenueKind.Arena,
        E = 2535600, N = 1153410, AzimuthDeg = 25f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 11, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 10, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 24f, DepthEast = 26f, DepthSouth = 24f, DepthWest = 26f,
        RoofOuter = 20f, RoofInner = 24f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 3, 3 },
        Seat = new(0.7f, 0.1f, 0.1f), SeatAlt = new(0.56f, 0.08f, 0.08f), Facade = new(0.8f, 0.8f, 0.8f), Roof = new(0.55f, 0.38f, 0.28f),
        ConcertCapacity = 9600,
    };

    /// <summary>Patinoire des Vernets (Genève), arena: 7,100 seats, 8,500 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec VernetsArena = new()
    {
        Name = "Patinoire des Vernets", Town = "Genève",
        Kind = VenueKind.Arena,
        E = 2499209, N = 1116735, AzimuthDeg = 39f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 9, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 15.5f, DepthEast = 24f, DepthSouth = 15.5f, DepthWest = 24f,
        RoofOuter = 14.46f, RoofInner = 18f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 3, 3 },
        Seat = new(0.55f, 0.1f, 0.12f), SeatAlt = new(0.44f, 0.08f, 0.096f), Facade = new(0.75f, 0.72f, 0.68f), Roof = new(0.65f, 0.65f, 0.67f),
        ConcertCapacity = 8500,
    };

    /// <summary>Palexpo Arena Genève (Geneva Arena) (Le Grand-Saconnex), arena: 9,500 seats, 9,500 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec GenevaArena = new()
    {
        Name = "Palexpo Arena Genève (Geneva Arena)", Town = "Genève",
        Kind = VenueKind.Arena,
        E = 2497680, N = 1121110, AzimuthDeg = 40f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 12, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 11, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 15.5f, DepthEast = 25.5f, DepthSouth = 15.5f, DepthWest = 25.5f,
        RoofOuter = 18f, RoofInner = 22f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 4, 3 },
        Seat = new(0.2f, 0.3f, 0.55f), SeatAlt = new(0.16f, 0.24f, 0.44f), Facade = new(0.7f, 0.7f, 0.7f), Roof = new(0.72f, 0.72f, 0.72f),
        ConcertCapacity = 9500,
    };

    /// <summary>BCF Arena (Fribourg (St-Léonard)), arena: 6,900 seats, 9,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec BcfArena = new()
    {
        Name = "BCF Arena", Town = "Fribourg",
        Kind = VenueKind.Arena,
        E = 2578388, N = 1185176, AzimuthDeg = 74f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 10, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 10, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 10f, DepthEast = 22f, DepthSouth = 10f, DepthWest = 22f,
        RoofOuter = 16f, RoofInner = 20f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 3, 3 },
        Seat = new(0.12f, 0.12f, 0.14f), SeatAlt = new(0.096f, 0.096f, 0.112f), Facade = new(0.58f, 0.6f, 0.64f), Roof = new(0.35f, 0.38f, 0.46f),
        ConcertCapacity = 9000,
    };

    /// <summary>Tissot Arena (ice halls) (Biel/Bienne), arena: 6,521 seats, 7,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec TissotArena = new()
    {
        Name = "Tissot Arena (ice halls)", Town = "Biel/Bienne",
        Kind = VenueKind.Arena,
        E = 2588178, N = 1222825, AzimuthDeg = 56f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 7, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 20.5f, DepthEast = 33f, DepthSouth = 20.5f, DepthWest = 33f,
        RoofOuter = 16f, RoofInner = 20f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 2, 3 },
        Seat = new(0.15f, 0.25f, 0.55f), SeatAlt = new(0.12f, 0.2f, 0.44f), Facade = new(0.55f, 0.55f, 0.58f), Roof = new(0.3f, 0.32f, 0.45f),
        ConcertCapacity = 7000,
    };

    /// <summary>Bossard Arena (Zug), arena: 7,015 seats, 8,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec BossardArena = new()
    {
        Name = "Bossard Arena", Town = "Zug",
        Kind = VenueKind.Arena,
        E = 2681044, N = 1225660, AzimuthDeg = -78f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 9, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 14f, DepthEast = 20.5f, DepthSouth = 14f, DepthWest = 20.5f,
        RoofOuter = 14.46f, RoofInner = 18f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 3, 3 },
        Seat = new(0.15f, 0.25f, 0.55f), SeatAlt = new(0.12f, 0.2f, 0.44f), Facade = new(0.65f, 0.66f, 0.68f), Roof = new(0.78f, 0.78f, 0.8f),
        ConcertCapacity = 8000,
    };

    /// <summary>Cornèr Arena (Lugano (Porza)), arena: 7,200 seats, 8,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec CornerArena = new()
    {
        Name = "Cornèr Arena", Town = "Lugano",
        Kind = VenueKind.Arena,
        E = 2718047, N = 1098354, AzimuthDeg = 4f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 10, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 11, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 9f, DepthEast = 20.5f, DepthSouth = 9f, DepthWest = 20.5f,
        RoofOuter = 16.38f, RoofInner = 18f, RoofInset = 0f, RoofRing = 0f, Solar = true,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 3, 3 },
        Seat = new(0.6f, 0.1f, 0.1f), SeatAlt = new(0.48f, 0.08f, 0.08f), Facade = new(0.65f, 0.67f, 0.7f), Roof = new(0.35f, 0.38f, 0.46f),
        ConcertCapacity = 8000,
    };

    /// <summary>THE HALL (ex Samsung Hall) (Dübendorf), arena: 3,000 seats, 5,062 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec TheHall = new()
    {
        Name = "THE HALL (ex Samsung Hall)", Town = "Dübendorf",
        Kind = VenueKind.Arena,
        E = 2687522, N = 1250085, AzimuthDeg = 0f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
        },
        DepthNorth = 10.5f, DepthEast = 13f, DepthSouth = 10.5f, DepthWest = 13f,
        RoofOuter = 12f, RoofInner = 14f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 2 },
        Seat = new(0.15f, 0.15f, 0.2f), SeatAlt = new(0.12f, 0.12f, 0.16f), Facade = new(0.55f, 0.55f, 0.55f), Roof = new(0.5f, 0.5f, 0.52f),
        ConcertCapacity = 5062,
    };

    /// <summary>SWISS Arena (Kloten) (Kloten), arena: 7,624 seats, 7,000 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec SwissArenaKloten = new()
    {
        Name = "SWISS Arena (Kloten)", Town = "Kloten",
        Kind = VenueKind.Arena,
        E = 2686386, N = 1255203, AzimuthDeg = -30f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 8, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 18f, DepthEast = 18f, DepthSouth = 18f, DepthWest = 18f,
        RoofOuter = 14.04f, RoofInner = 18f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 2, 3 },
        Seat = new(0.6f, 0.1f, 0.1f), SeatAlt = new(0.48f, 0.08f, 0.08f), Facade = new(0.65f, 0.65f, 0.68f), Roof = new(0.7f, 0.7f, 0.72f),
        ConcertCapacity = 7000,
    };

    /// <summary>Zondacrypto Arena (Vaillant Arena) (Davos), arena: 7,080 seats, 6,500 for a concert. Footprint from the swissimage aerial; the inside estimated (#948 research).</summary>
    public static readonly StadiumSpec DavosArena = new()
    {
        Name = "Zondacrypto Arena (Vaillant Arena)", Town = "Davos",
        Kind = VenueKind.Arena,
        E = 2782260, N = 1185812, AzimuthDeg = -80f,
        PitchWidth = 30f, PitchLength = 60f,
        FrontHalfU = 17f, FrontHalfV = 32f, FrontCorner = 8f,
        Tiers = new[]
        {
            new StadiumTier(Rows: 6, RowDepth: 0.8f, Riser: 0.42f, Gap: 0f, Step: 1.2f),
            new StadiumTier(Rows: 6, RowDepth: 0.8f, Riser: 0.5f, Gap: 2f, Step: 2.4f),
        },
        DepthNorth = 13.5f, DepthEast = 13f, DepthSouth = 13.5f, DepthWest = 13f,
        RoofOuter = 14f, RoofInner = 18f, RoofInset = 0f, RoofRing = 0f, Solar = false,
        Columns = 0, ColumnTop = 0f,
        Gates = new[] { StadiumSide.North, StadiumSide.South },
        VomitoryRows = new[] { 2, 3 },
        Seat = new(0.8f, 0.8f, 0.1f), SeatAlt = new(0.64f, 0.64f, 0.08f), Facade = new(0.6f, 0.6f, 0.62f), Roof = new(0.7f, 0.7f, 0.72f),
        ConcertCapacity = 6500,
        SeatWidth = 0.42f,   // standing terraces: people closer than seats
    };

    /// <summary>Paléo Festival, Grande Scène (Nyon), open air: about 35,000 a day. The main stage placed from the 2026 site plan fitted to the aerial; the 2013 festival flight shows the stage roof (high) (#948 research).</summary>
    public static readonly StadiumSpec PaleoOpenAir = new()
    {
        Name = "Paléo Festival, Grande Scène", Town = "Nyon",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2505256.1, N = 1139822.8, AzimuthDeg = 176f,
        FrontHalfU = 95f, FrontHalfV = 80f, FrontCorner = 6f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        Kiosks = false,
        ConcertCapacity = 35000,
    };

    /// <summary>Gurtenfestival, Hauptbühne (Bern (Gurten)), open air: about 27,000 a day. The main stage placed from the 2026 Geländeplan fitted on three landmarks (5 m); the crowd's meadow rises away from the stage (#948 research).</summary>
    public static readonly StadiumSpec GurtenOpenAir = new()
    {
        Name = "Gurtenfestival, Hauptbühne", Town = "Bern",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2600188.6, N = 1196352.6, AzimuthDeg = -25f,
        FrontHalfU = 55f, FrontHalfV = 80f, FrontCorner = 6f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        Kiosks = false,
        ConcertCapacity = 27000,
    };

    /// <summary>Openair Frauenfeld, Arena Stage (Frauenfeld), open air: about 35,000 a day. The main stage placed from the worn crowd rectangle in the racecourse infield on the 2025 aerial (low: the stage may face the other way) (#948 research).</summary>
    public static readonly StadiumSpec FrauenfeldOpenAir = new()
    {
        Name = "Openair Frauenfeld, Arena Stage", Town = "Frauenfeld",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2710209.4, N = 1270159.3, AzimuthDeg = 35f,
        FrontHalfU = 67.5f, FrontHalfV = 100f, FrontCorner = 6f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        Kiosks = false,
        ConcertCapacity = 35000,
    };

    /// <summary>OpenAir St. Gallen, Sitterbühne (St. Gallen (Sittertobel)), open air: about 30,000 a day. The main stage placed from the 2026 Geländeplan's illustration matched to the Sitter-loop meadow (low-medium) (#948 research).</summary>
    public static readonly StadiumSpec StGallenOpenAir = new()
    {
        Name = "OpenAir St. Gallen, Sitterbühne", Town = "St. Gallen",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2742507.9, N = 1253134.6, AzimuthDeg = 50f,
        FrontHalfU = 55f, FrontHalfV = 65f, FrontCorner = 6f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        Kiosks = false,
        ConcertCapacity = 30000,
    };

    /// <summary>Greenfield Festival, Jungfrau Stage (Interlaken), open air: about 25,000 a day. The main stage placed from the 2018 build-up flight's platform on the old airfield apron (medium-low) (#948 research).</summary>
    public static readonly StadiumSpec GreenfieldOpenAir = new()
    {
        Name = "Greenfield Festival, Jungfrau Stage", Town = "Interlaken",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2633958.7, N = 1169472.0, AzimuthDeg = 120f,
        FrontHalfU = 55f, FrontHalfV = 90f, FrontCorner = 6f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        Kiosks = false,
        ConcertCapacity = 25000,
    };

    /// <summary>Heitere Open Air (Zofingen), open air: about 10,000 a day. The main stage placed from the main stage and the arena read off a swissimage tile shot during the festival (medium) (#948 research).</summary>
    public static readonly StadiumSpec HeitereOpenAir = new()
    {
        Name = "Heitere Open Air", Town = "Zofingen",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2639062.5, N = 1237134.7, AzimuthDeg = -30f,
        FrontHalfU = 35f, FrontHalfV = 75f, FrontCorner = 6f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        Kiosks = false,
        ConcertCapacity = 10000,
    };

    /// <summary>Moon&Stars, Piazza Grande (Locarno), open air: about 8,000 a day. The main stage placed from the piazza from the building outlines, the stage assumed at its west end (low) (#948 research).</summary>
    public static readonly StadiumSpec MoonAndStarsOpenAir = new()
    {
        Name = "Moon&Stars, Piazza Grande", Town = "Locarno",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2704818.4, N = 1114001.6, AzimuthDeg = -102f,
        FrontHalfU = 35f, FrontHalfV = 85f, FrontCorner = 6f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        Kiosks = false,
        ConcertCapacity = 8000,
    };

    /// <summary>
    /// Montreux Jazz Festival, the lake stage (Montreux), open air: a small stage and a seated lawn on the Place du Marché by the quay (#1394). Fitted on the swissimage
    /// aerial: the sand square 60 x 32 m between the market hall and the hotels, the stage at its lake (west) end, the audience facing the water behind the band
    /// (low-medium: the real stages move each year). It stands only in the festival's window (<see cref="StadiumSpec.Season"/>), the lake side is open.
    /// </summary>
    public static readonly StadiumSpec MontreuxJazzOpenAir = new()
    {
        Name = "Montreux Jazz, Lake Stage", Town = "Montreux",
        Kind = VenueKind.OpenAir, StageAt = StadiumSide.North,
        E = 2559335, N = 1142425, AzimuthDeg = 270f,
        FrontHalfU = 16f, FrontHalfV = 30f, FrontCorner = 3f,
        Tiers = Array.Empty<StadiumTier>(),
        DepthNorth = 1f, DepthEast = 1f, DepthSouth = 1f, DepthWest = 1f,
        Columns = 0,
        Gates = new[] { StadiumSide.South, StadiumSide.East, StadiumSide.West },
        OpenSides = new[] { StadiumSide.North },
        Kiosks = false,
        ConcertCapacity = 1200,
        Season = "07-fri#1+15",
    };

    public static readonly StadiumSpec[] All =
    {
        Geneva, Wankdorf, StJakobPark, Letzigrund, Kybunpark, Swissporarena, StockhornArena, Tourbillon, Tuiliere, Maladiere, Brugglifeld, Schutzenwiese, Hallenstadion, PostFinanceArena, StJakobshalle, SwissLifeArena, VaudoiseArena, VernetsArena, GenevaArena, BcfArena, TissotArena, BossardArena, CornerArena, TheHall, SwissArenaKloten, DavosArena, PaleoOpenAir, GurtenOpenAir, FrauenfeldOpenAir, StGallenOpenAir, GreenfieldOpenAir, HeitereOpenAir, MoonAndStarsOpenAir, MontreuxJazzOpenAir,
    };

    /// <summary>How far past its own tile a data building may reach, m: tiles this far from a stadium's outline are clipped (<see cref="StadiumClip"/>).</summary>
    public const float Reach = 300f;

    /// <summary>The tile that builds a stadium: the one holding its centre spot.</summary>
    public static TileId Owner(StadiumSpec s) => TileId.FromLv95(s.E, s.N);

    /// <summary>The stadiums this tile builds, usually none.</summary>
    public static IEnumerable<StadiumSpec> OwnedBy(TileId id)
    {
        foreach (var s in All)
            if (Owner(s) == id) yield return s;
    }
}
