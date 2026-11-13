using UnitSport.Terrain.Format;

// Plain C#, no test framework: the sample records CubeLeafTests encodes. The SHA-256 of each one's
// LV95 file encoding is pinned there from the codecs before the body split (#1505), so this file
// must keep building the same records.

namespace UnitSport.Tests;

public static class CubeLeafSamples
{
    public static readonly TileId Id = new(2579, 1109);

    public static RoadTile Road()
    {
        var main = new RoadSegment { Class = RoadClass.Road, Surface = RoadSurface.Paved, Width = 6, Points = [0, 400, 0, 80, 400, 0, 160.5f, 401.25f, -3.5f] };
        var lane = new RoadSegment { Class = RoadClass.Lane, Width = 3, Points = [10, 399.5f, 10, 10, 399f, 90] };
        var tile = new RoadTile
        {
            Id = Id,
            Flags = RoadTileFlags.Osm | RoadTileFlags.Network | RoadTileFlags.Bikes,
            Segments = [main, lane],
            Junctions = [new RoadJunction { Class = RoadClass.Road, Layer = 0, Vertices = [0, 400, 0, 5, 400, 0, 5, 400, 5, 0, 400, 5], Indices = [0, 1, 2, 0, 2, 3] }],
            PointProps = [new RoadPointProp(PointPropType.YieldSign, 0, PropFlags.Solid, 12.5f, 400.1f, -4, 1.25f, 2.4f)],
            LinearProps = [new RoadLinearProp { Type = LinearPropType.Guardrail, Flags = PropFlags.Solid, Thickness = 0.3f, Param = 4, Points = [0, 400, 3, 0.8f, 40, 400.2f, 3, 0.8f] }],
            AreaProps = [new RoadAreaProp { Type = AreaPropType.Sidewalk, Flags = PropFlags.Solid, Height = 0.12f, Vertices = [0, 400, 4, 10, 400, 4, 10, 400, 6], Indices = [0, 1, 2] }],
            Signals = [new RoadSignal
            {
                X = 90, Y = 400, Z = 0, Stops = [75, 400, 1.5f, float.NaN, float.NaN, float.NaN],
                Plan = SignalPlan.Build([new SignalArm(Math.PI, true, true, LeftPocket: true), new SignalArm(0, false, true)]),
            }],
            Approaches =
            [
                new RoadApproach
                {
                    X = 75, Y = 400, Z = 1.5f, Heading = (float)Math.PI, Signal = 0, SignalArm = 0, Banned = SignalMoves.Right, LaneCentre = 1.5f,
                    Lanes =
                    [
                        new ApproachLane(0f, 15.5f, 15.5f, SignalMoves.Left, ApproachLaneKind.Car, 4f),
                        new ApproachLane(4.5f, 15.5f, 35.5f, SignalMoves.Through | SignalMoves.Right, ApproachLaneKind.Car),
                    ],
                },
            ],
        };
        tile.Paint.Add(RoadPaint.AlongSegment(main, PaintType.WhiteSolid, 0xFFFFFFFF, 0.15f, 0, 0, 2.8));
        return tile;
    }

    public static BuildingTile Buildings() => new()
    {
        Id = Id,
        Buildings =
        [
            new Building
            {
                Kind = BuildingKind.Apartment, Floors = 3, YearBuilt = 1972, Egid = 190123456, MinY = 450.5f, MaxY = 462f,
                Triangles = Enumerable.Range(0, 18).Select(i => i * 1.5f - 7).ToArray(),
            },
            new Building { Kind = BuildingKind.Garage, Triangles = [] },
        ],
    };

    public static List<TreeInstance> Trees() => [new(0, 512.25f, 999.9f, 18.5f, 0), new(-0.5f, 2000f, 1f, 3f, 255), new(300, 450, 12.75f, 9, 3)];

    /// <summary>A piste tile with its rasters of <paramref name="side"/>² (1001 for the LV95 file).</summary>
    public static PisteTile Pistes(int side)
    {
        var slot = new byte[side * side];
        var aux = new ushort[side * side];
        for (int i = 0; i < slot.Length; i += 7)
        {
            slot[i] = (byte)(i % 3);
            aux[i] = PisteFormat.PackAux((i % 11) / 10f, i * 0.37);
        }
        return new PisteTile(Id,
        [
            new PisteRecord(123456789, PisteType.Downhill, PisteDifficulty.Intermediate, PisteGrooming.Classic, false, 30, "Lauberhorn", "1",
                [10, 20, 400.5f, 620.25f, 900, 980], 1800, 2300, 0.4f, 22),
            new PisteRecord(1UL << 63 | 42, PisteType.Nordic, PisteDifficulty.Easy, PisteGrooming.Skating, true, 0, "Loipe", "",
                [0, 0, 100, 0, 100, 100, 0, 100]),
        ], slot, aux);
    }

    public static List<FieldPolygon> Fields() =>
    [
        new FieldPolygon(0xDEADBEEF, CropKind.Wheat, FieldSource.Lwb, 513, [[10, 10, 200, 10, 200, 150, 10, 150], [50, 50, 60, 50, 60, 60]]),
        new FieldPolygon(7, CropKind.Pasture, FieldSource.Osm, 0, [[-20, 900, 40, 1010, 300, 980]]),
    ];
}
