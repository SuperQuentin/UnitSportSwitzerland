using Godot;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// One tier of a bowl, swept all round it: a walkway, a front wall, then rows. Heights and depths
/// are metres; a row is <see cref="RowDepth"/> deep and <see cref="Riser"/> above the one in front.
/// </summary>
/// <param name="Gap">Flat walkway before the tier's front wall (the concourse between two tiers).</param>
/// <param name="Step">Height of the tier's front wall above the walkway: the pitch wall, or the boxes' glazing.</param>
public sealed record StadiumTier(int Rows, float RowDepth, float Riser, float Gap, float Step);

/// <summary>Which straight side of the bowl, named as they lie when <see cref="StadiumSpec.AzimuthDeg"/> is 0.</summary>
public enum StadiumSide { North, East, South, West }

/// <summary>
/// A stadium, as a catalog row (<see cref="StadiumCatalog"/>): everything the builder needs, and
/// nothing it could read off the data. swissTLM3D gives the pitch (a <c>Sportplatz</c> polygon,
/// <c>CoverClass.SportsField</c> in our tiles) but no stand, roof or tier, and swissBUILDINGS3D
/// draws a stadium as anything from one filled solid (Wankdorf) to nothing at all (Stade de
/// Genève, an "Offenes Gebaeude" the export never had). So the bowl is described here, measured
/// off the aerial photo and the published figures, and built the same way for every stadium.
///
/// <para>
/// The frame: u across the pitch (east when the azimuth is 0), v along it (south), metres from the
/// centre spot, y up from the pitch. The seating front is a rounded rectangle of half extents
/// <see cref="FrontHalfU"/> x <see cref="FrontHalfV"/>; every tier, the roof and the facade are
/// swept outwards from it, so the bowl closes round its corners by construction.
/// </para>
/// </summary>
public sealed record StadiumSpec
{
    public required string Name { get; init; }
    /// <summary>The centre spot, LV95 (from the pitch polygon's centroid).</summary>
    public required double E { get; init; }
    public required double N { get; init; }
    /// <summary>The pitch's long axis, degrees clockwise from north.</summary>
    public float AzimuthDeg { get; init; }

    /// <summary>The pitch's lines, for the record and the check: 105 x 68 is the UEFA size.</summary>
    public float PitchWidth { get; init; } = 68f;
    public float PitchLength { get; init; } = 105f;

    /// <summary>Half extents of the seating front (the pitch plus its run-off) and its corner radius.</summary>
    public required float FrontHalfU { get; init; }
    public required float FrontHalfV { get; init; }
    public float FrontCorner { get; init; } = 4f;

    public required StadiumTier[] Tiers { get; init; }

    /// <summary>
    /// How far the facade stands out from the seating front, per side; the corners blend between
    /// the two sides they join. Anything between the last row and the facade is the concourse.
    /// </summary>
    public required float DepthNorth { get; init; }
    public required float DepthEast { get; init; }
    public required float DepthSouth { get; init; }
    public required float DepthWest { get; init; }

    /// <summary>Roof surface height at the facade and at its inner edge, above the pitch.</summary>
    public float RoofOuter { get; init; } = 34f;
    public float RoofInner { get; init; } = 30f;
    /// <summary>The roof's inner edge, outwards from the seating front (negative overhangs the pitch).</summary>
    public float RoofInset { get; init; } = 2f;
    public float RoofThickness { get; init; } = 1.6f;
    /// <summary>Width of the translucent band along the roof's inner edge, 0 for none.</summary>
    public float RoofRing { get; init; } = 8f;
    /// <summary>Solar panels on the straight sides of the roof.</summary>
    public bool Solar { get; init; }

    /// <summary>Pillars round the facade, carrying one roof truss each.</summary>
    public int Columns { get; init; } = 48;
    public float ColumnTop { get; init; } = 36f;
    public Vector2 ColumnSize { get; init; } = new(1.6f, 0.8f);

    /// <summary>The sides with a gate: a passage through the facade and a tunnel under the stand to the pitch.</summary>
    public StadiumSide[] Gates { get; init; } = { StadiumSide.North, StadiumSide.South };
    public float GateWidth { get; init; } = 7f;
    public float GateHeight { get; init; } = 4.2f;

    /// <summary>Seat blocks are about this long, with an aisle of stairs between two.</summary>
    public float BlockLength { get; init; } = 14f;
    public float AisleWidth { get; init; } = 1.2f;
    /// <summary>A seat's width, for the capacity the check reports.</summary>
    public float SeatWidth { get; init; } = 0.5f;

    // colours, sRGB as on the photos
    public Color Seat { get; init; } = new(0.45f, 0.1f, 0.15f);
    public Color SeatAlt { get; init; } = new(0.36f, 0.08f, 0.12f);
    public Color Concrete { get; init; } = new(0.53f, 0.53f, 0.52f);
    public Color Glazing { get; init; } = new(0.16f, 0.2f, 0.24f);
    public Color Roof { get; init; } = new(0.84f, 0.85f, 0.86f);
    public Color RoofRingColor { get; init; } = new(0.68f, 0.74f, 0.78f);
    public Color Steel { get; init; } = new(0.62f, 0.64f, 0.66f);
    public Color Facade { get; init; } = new(0.7f, 0.72f, 0.74f);
    public Color Plinth { get; init; } = new(0.42f, 0.42f, 0.43f);
    public Color SolarColor { get; init; } = new(0.12f, 0.14f, 0.2f);

    public float Depth(StadiumSide side) => side switch
    {
        StadiumSide.North => DepthNorth,
        StadiumSide.East => DepthEast,
        StadiumSide.South => DepthSouth,
        _ => DepthWest,
    };
}
