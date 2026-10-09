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
            byte variants = type == PointPropType.TicketBarrier ? (byte)2 : (byte)1;   // boom down, up
            for (byte v = 0; v < variants; v++)
            {
                byte variant = v;
                var tile = Tile(new RoadPointProp(type, variant, PropFlags.Solid, 0, 0, 0, 0f, PropHeight(type)));
                if (Arrays(tile).Vertices.Length == 0) continue;   // a type no builder draws yet
                yield return (variants > 1 ? $"{type} {variant}" : type.ToString(), () => RoadProps(tile));
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
        _ => 2.2f,
    };

    /// <summary>
    /// A crossroads' main pole: left arrow, car head, bike head, pedestrian head, lenses cycling on
    /// the clock; with a three-lens pedestrian head (Vaud) and Geneva's side-by-side one (#759).
    /// </summary>
    [Showcase("Terrain", "Traffic light")]
    private static IEnumerable<(string, Func<Node3D>)> TrafficLights() =>
    [
        ("Vaud", () => TrafficLight(pedestrianAmber: true)),
        ("Geneva", () => TrafficLight(pedestrianAmber: false)),
    ];

    private static Node3D TrafficLight(bool pedestrianAmber)
    {
        var plan = SignalPlan.Build(
        [
            new SignalArm(0, true, true, LeftPocket: true, Pedestrians: true, BikeSignal: true, Rank: 2),
            new SignalArm(Math.PI / 2, true, true, Pedestrians: true),
            new SignalArm(Math.PI, true, true, LeftPocket: true, Pedestrians: true, Rank: 2),
            new SignalArm(-Math.PI / 2, true, true, Pedestrians: true),
        ], 353, pedestrianAmber);
        var tile = Tile();
        tile.Signals.Add(new RoadSignal
        {
            Stops = new float[12],
            Plan = plan,
            Poles = { new SignalPole(0, 0, 0, 0f, Mathf.Pi / 2, 0, SignalPoleFlags.Main | SignalPoleFlags.Pedestrian) },
        });
        return RoadProps(tile);
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
        SignalBuilder.Append(tile, v, c, uv, uv2, ix, StyleKit.Detail);
        return new RoadMeshBuilder.MeshData(v.ToArray(), c.ToArray(), uv.ToArray(), uv2.ToArray(), ix.ToArray());
    }

    private static Node3D RoadProps(RoadTile tile)
    {
        var material = StyleKit.Material(MaterialRole.Road);
        FogUniforms.Apply(material);
        var node = new MeshInstance3D { Mesh = ChunkNode.ToArrayMesh(Arrays(tile), material) };
        if (SignalBuilder.BuildLamps(tile) is { } lamps) node.AddChild(SignalLamps.Create(lamps));
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
