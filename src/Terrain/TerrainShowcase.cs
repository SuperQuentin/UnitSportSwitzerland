using Godot;
using UnitSport.Core;
using UnitSport.Styles;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Samples of what the terrain pipeline places from map data, built from one hand-made record each
/// (a tree, a sign, a traffic light, a pier) with the game's own builders and materials, for the
/// model viewer (--models, docs/notes/avatar/model-viewer.md). Every enum value is one entry.
/// </summary>
public static class TerrainShowcase
{
    [Showcase("Terrain", "Tree")]
    private static IEnumerable<(string, Func<Node3D>)> Trees()
    {
        // TreeFormat's kinds: 0 conifer, 1 shrub, 2 fruit, 3 broadleaf
        (byte Kind, string Name, float Height)[] kinds = { (0, "conifer", 22f), (1, "shrub", 3f), (2, "fruit", 6f), (3, "broadleaf", 12f) };
        foreach (var detail in Enum.GetValues<MeshDetail>())
            foreach (var (kind, name, height) in kinds)
                yield return ($"{name}, {detail}", () => Tree(kind, height, detail));
    }

    private static Node3D Tree(byte kind, float height, MeshDetail detail)
    {
        var material = StyleKit.Material(MaterialRole.Tree);
        FogUniforms.Apply(material);
        var buffers = ChunkNode.BuildTreeBuffers(new[] { new TreeInstance(0, 0, 0, height, kind) });
        var meshes = ChunkNode.BuildTreeMeshes(buffers, material, new Aabb(new Vector3(-20, 0, -20), new Vector3(40, 40, 40)), detail);
        return new MultiMeshInstance3D { Multimesh = kind is 0 or 1 ? meshes.Conifers : meshes.Broadleaves };
    }

    [Showcase("Terrain", "Road prop")]
    private static IEnumerable<(string, Func<Node3D>)> PointProps()
    {
        foreach (var type in Enum.GetValues<PointPropType>().Where(t => t != PointPropType.None))
        {
            byte[] variants = type switch
            {
                PointPropType.TicketBarrier => [0, 1],   // boom down, up
                // the test region's zones (#1038): the default one, Geneva, Vaud
                PointPropType.CantonSign => [0, RoadSigns.CantonVariant("GE"), RoadSigns.CantonVariant("VD")],
                _ => [0],
            };
            foreach (byte variant in variants)
            {
                var tile = Tile(new RoadPointProp(type, variant, PropFlags.Solid, 0, 0, 0, 0f, PropHeight(type)));
                if (Arrays(tile).Vertices.Length == 0) continue;   // a type no builder draws yet
                string name = type == PointPropType.CantonSign ? $"{type} {RoadSigns.CantonCode(variant) ?? "default"}"
                    : variants.Length > 1 ? $"{type} {variant}" : type.ToString();
                yield return (name, () => RoadProps(tile));
            }
        }
    }

    // the heights the preprocessor gives them (FormatCheck, FixtureChunkSource); a new type gets a sign's
    private static float PropHeight(PointPropType type) => type switch
    {
        PointPropType.ParkingSign => RoadSigns.LowerEdge + RoadSigns.PlateHeight(type, RoadSigns.Normal),
        PointPropType.TicketBarrier => 1.1f,
        PointPropType.TicketKiosk => 1.5f,
        PointPropType.CartShelter => 2.4f,
        PointPropType.CantonSign => RoadSigns.LowerEdge + RoadSigns.CantonSignHeight,
        PointPropType.TramStop => 2.6f,
        _ => 2.2f,
    };

    /// <summary>
    /// A crossroads' main pole: left arrow, car head, bike head, pedestrian head, lenses cycling on
    /// the clock; with a three-lens pedestrian head (Vaud) and Geneva's side-by-side one (#759).
    /// </summary>
    [Showcase("Terrain", "Traffic light")]
    private static IEnumerable<(string, Func<Node3D>)> TrafficLights() =>
    [
        // Vaud: three-lens pedestrian heads, arrows on the plates; Geneva: neither (#759)
        ("Vaud", () => TrafficLight(pedestrianAmber: true, arrowPlates: true)),
        ("Geneva", () => TrafficLight(pedestrianAmber: false)),
        // riders may turn right on red (#711, signal 5.18): beside the bike head's red lamp, else the car head's
        ("Bike right on red", () => TrafficLight(pedestrianAmber: true, rightOnRed: true)),
        ("Bike right on red, no bike head", () => TrafficLight(pedestrianAmber: true, rightOnRed: true, bikeHead: false)),
        // the canton's pole colour (#1037): Basel-Landschaft's anthracite, Bern's RAL 7042
        ("Basel-Landschaft", () => TrafficLight(pedestrianAmber: true, colour: SignalPoleColour.Anthracite)),
        ("Bern", () => TrafficLight(pedestrianAmber: true, colour: SignalPoleColour.TrafficGrey)),
        // heads over the lanes (#1037): a mast arm over two lanes (a tie rod past 7 m), a portal over three, a span wire between two
        // facades where a tram street is too wide for an arm
        ("Mast arm", () => TrafficLight(pedestrianAmber: true, overhead: OverheadKind.Arm)),
        ("Portal", () => TrafficLight(pedestrianAmber: true, overhead: OverheadKind.Portal)),
        ("Span wire", () => TrafficLight(pedestrianAmber: true, overhead: OverheadKind.Span)),
        // a two-stage crossing's island pole (#1030): the left repeater over two pedestrian heads back to back, one per half
        ("Two-stage island", () => TrafficLight(pedestrianAmber: true, island: true)),
        ("Two-stage island, Geneva", () => TrafficLight(pedestrianAmber: false, island: true)),
        // the trams' bar head (#1057) beside the car heads: the bar across, the dot, the bar upright, lit on the clock
        ("Tram", () => TrafficLight(pedestrianAmber: true, tram: true)),
        // Romandy's head (#1105, the user's photos): a one-lens car head module, a solid white T lit in its lens, packed against the car heads
        ("Tram, Romandy", () => TrafficLight(pedestrianAmber: false, tram: true, tramT: true)),
        // trams going two ways (#1057, the user): a head per direction, its arrow on its plate; Romandy's in a row on top of the car heads
        ("Tram, two ways", () => TrafficLight(pedestrianAmber: true, tram: true, tramTwo: true)),
        ("Tram, Romandy, two ways", () => TrafficLight(pedestrianAmber: false, tram: true, tramT: true, tramTwo: true)),
    ];

    private static Node3D TrafficLight(bool pedestrianAmber, bool arrowPlates = false, bool rightOnRed = false, bool bikeHead = true,
        SignalPoleColour colour = SignalPoleColour.SilverGrey, OverheadKind? overhead = null, bool island = false, bool tram = false,
        bool tramT = false, bool tramTwo = false)
    {
        var plan = SignalPlan.Build(
        [
            new SignalArm(0, true, true, LeftPocket: true, Pedestrians: true, BikeSignal: bikeHead, Rank: 2, BikeRightOnRed: rightOnRed,
                HalfInM: island ? 4 : 0, HalfOutM: island ? 3.5f : 0, TramMoves: !tram ? SignalMoves.None : tramTwo ? SignalMoves.Left | SignalMoves.Through : SignalMoves.Through),
            new SignalArm(Math.PI / 2, true, true, Pedestrians: true),
            new SignalArm(Math.PI, true, true, LeftPocket: true, Pedestrians: true, Rank: 2),
            new SignalArm(-Math.PI / 2, true, true, Pedestrians: true),
        ], 353, pedestrianAmber, tramT);
        plan.ArrowPlates = arrowPlates;
        plan.PoleColour = colour;
        var tile = Tile();
        var signal = new RoadSignal
        {
            Stops = new float[12],
            Plan = plan,
            Poles =
            {
                island
                    ? new SignalPole(0, 0, 0, 0f, Mathf.Pi / 2, 0, SignalPoleFlags.Second | SignalPoleFlags.Pedestrian | SignalPoleFlags.ArrivingHalf | SignalPoleFlags.DepartingHalf)
                    : new SignalPole(0, 0, 0, 0f, Mathf.Pi / 2, 0, SignalPoleFlags.Main | SignalPoleFlags.Pedestrian | (tram ? SignalPoleFlags.Tram : 0)),
            },
        };
        // the heads face -Z, the driver's left is +X: the lanes to the pole's left
        // (an arm's or portal's post on the pole, carrying its heads; a span wire from the facade 3 m outside it to one across the street)
        float from = overhead == OverheadKind.Span ? -3f : 0f;
        const float P = SignalOverhead.ClusterPitch;
        if (overhead is { } kind)
            signal.Overheads.Add(new SignalOverhead
            {
                Kind = kind, Arm = 0, X = from, EndX = kind switch { OverheadKind.Arm => 4.4f, OverheadKind.Span => 15f, _ => 12f }, Bottom = 5f,
                // one cluster over the middle of the lanes, a head per direction (#1110): over two lanes the main head and the left
                // arrow (an arm), over three the right arrow, the main head and the left arrow (a portal, a span wire)
                Heads = kind == OverheadKind.Arm
                    ? [new OverheadHead(3.5f - from - P * 0.5f, SignalMoves.Through | SignalMoves.Right), new OverheadHead(3.5f - from + P * 0.5f, SignalMoves.Left)]
                    : [new OverheadHead(6.0f - from - P, SignalMoves.Right), new OverheadHead(6.0f - from, SignalMoves.Through),
                       new OverheadHead(6.0f - from + P, SignalMoves.Left)],
            });
        tile.Signals.Add(signal);
        var node = RoadProps(tile);
        if (overhead == OverheadKind.Span)
            foreach (float x in (ReadOnlySpan<float>)[from - 0.5f, 15.5f])
                node.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(1f, 9f, 8f) }, Position = new Vector3(x, 4.5f, 0f),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.74f, 0.66f) },
                });
        return node;
    }

    private static RoadTile Tile(params RoadPointProp[] props)
    {
        var tile = new RoadTile { Id = new TileId(0, 0), Segments = new() };
        tile.PointProps.AddRange(props);
        return tile;
    }

    // what ChunkNode does with a tile's props, without the roads
    private static RoadMeshBuilder.MeshData Arrays(RoadTile tile)
    {
        var v = new List<Vector3>(); var c = new List<Color>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var ix = new List<int>();
        RoadSignBuilder.Append(tile, v, c, uv, uv2, ix);
        ParkingBuilder.Append(tile, v, c, uv, uv2, ix);
        SignalBuilder.Append(tile, v, c, uv, uv2, ix, rounded: StyleKit.Detail == MeshDetail.High);
        CatenaryBuilder.Append(tile, v, c, uv, uv2, ix);
        return new RoadMeshBuilder.MeshData(v.ToArray(), c.ToArray(), uv.ToArray(), uv2.ToArray(), ix.ToArray());
    }

    private static Node3D RoadProps(RoadTile tile)
    {
        var material = StyleKit.Material(MaterialRole.Road);
        FogUniforms.Apply(material);
        var node = new MeshInstance3D { Mesh = ChunkNode.ToArrayMesh(Arrays(tile), material) };
        if (SignalBuilder.BuildLamps(tile) is { } lamps)
        {
            var lit = SignalLamps.Create(lamps);
            // --modelscycle <s>: the viewer runs a whole signal cycle in that long, for a clip of every lamp's states (#1105)
            if (CmdArgs.Float("--modelscycle") is { } cycle && cycle > 0 && lit.LongestCycle > 0)
            {
                lit.PlanSpeed = lit.LongestCycle / cycle;
                GD.Print($"[models] signal cycle {lit.LongestCycle:0.#} s run in {cycle:0.#} s ({lit.PlanSpeed:0.##}x)");
            }
            // --modelscyclefrom <s>: the plans start at that second of their time (a clip of a chosen part of the cycle, #1105)
            if (CmdArgs.Float("--modelscyclefrom") is { } from)
                lit.PlanShift = from - SignalLamps.Clock() * lit.PlanSpeed;
            node.AddChild(lit);
        }
        foreach (var label in RoadSignBuilder.Labels(tile) ?? []) node.AddChild(RoadSignBuilder.Make(label));
        return node;
    }

    /// <summary>Overhead lines (#1052): a tram's span between facades and between masts, its cantilever, its centre mast, a main line's pair.</summary>
    [Showcase("Terrain", "Overhead line")]
    private static IEnumerable<(string, Func<Node3D>)> OverheadLines() =>
    [
        ("Tram span on facades", () => OverheadLine(CatenaryLine.Tram, span: true, walls: true)),
        ("Tram span on masts", () => OverheadLine(CatenaryLine.Tram, span: true, walls: false)),
        ("Tram cantilever", () => OverheadLine(CatenaryLine.Tram, span: false, walls: false)),
        ("Tram centre mast", () => OverheadLine(CatenaryLine.Tram, span: false, walls: false, centre: true)),
        ("Main line cantilevers", () => OverheadLine(CatenaryLine.Main, span: false, walls: false)),
    ];

    // a double track along X (tracks at z = -2.2 and +2.2, the rails' top at y 0), 40 m of wire, its supports at x = 0
    private static Node3D OverheadLine(CatenaryLine line, bool span, bool walls, bool centre = false)
    {
        float wireY = (line == CatenaryLine.Tram ? 5.8f : 5.5f) + 0.18f;
        var tile = Tile();
        foreach (float z in (ReadOnlySpan<float>)[-2.2f, 2.2f])
        {
            tile.CatenaryWires.Add(new CatenaryWire { Line = line, Points = [-20f, wireY, z, 0f, wireY, z, 20f, wireY, z] });
            if (!span && !centre)
                tile.CatenarySupports.Add(new CatenarySupport
                {
                    Kind = CatenarySupportKind.Cantilever, Line = line, AZ = z + MathF.Sign(z) * 3f, BZ = z, WireY = wireY, Over = [0f, z],
                });
        }
        if (centre)
            tile.CatenarySupports.Add(new CatenarySupport
            {
                Kind = CatenarySupportKind.CentreMast, Line = line, BZ = -2.2f, WireY = wireY, Over = [0f, -2.2f, 0f, 2.2f],
            });
        if (span)
            tile.CatenarySupports.Add(new CatenarySupport
            {
                Kind = CatenarySupportKind.Span, Line = line, AZ = -9f, AWall = walls, BZ = 9f, BWall = walls, WireY = wireY, Over = [0f, -2.2f, 0f, 2.2f],
            });
        var node = RoadProps(tile);
        if (walls)
            foreach (float z in (ReadOnlySpan<float>)[-9.5f, 9.5f])
                node.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(14f, 10f, 1f) }, Position = new Vector3(0f, 5f, z),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.74f, 0.66f) },
                });
        return node;
    }

    [Showcase("Terrain", "Landing")]
    private static IEnumerable<(string, Func<Node3D>)> Piers() =>
        Enum.GetValues<PierKind>().Select(kind => (kind.ToString(), (Func<Node3D>)(() => Pier(kind))));

    // 30 m out from the shore, in tile (0, 0)'s LV95 frame: the camera finds it wherever it lands
    private static Node3D Pier(PierKind kind)
    {
        var index = new LandingIndex();
        var landing = new Landing { Name = "Showcase" };
        landing.Ribbons.Add(new PierRibbon
        {
            Kind = kind, Width = kind == PierKind.Pier ? 4 : 2, Rails = kind == PierKind.Pier,
            Points = { new[] { 500.0, 500, 1.5 }, new[] { 500.0, 530, 1.5 } },
        });
        landing.Bollards.Add(new[] { 501.5, 528, 1.5 });
        index.Landings.Add(landing);
        var data = PierMeshBuilder.Build(index, new TileId(0, 0), null, mesh: true, collision: false)!.Value.Mesh!;
        var material = StyleKit.Material(MaterialRole.Prop);
        material.SetShaderParameter("flicker", 0f);
        FogUniforms.Apply(material);
        return new MeshInstance3D { Mesh = ChunkNode.WithPiers(null, data, material) };
    }
}
