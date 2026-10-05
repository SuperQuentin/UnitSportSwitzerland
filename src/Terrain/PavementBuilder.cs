using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Flush paved surfaces beside the carriageways: the <see cref="AreaPropType.Pavement"/> strips the
/// network stage writes where an approach widens for a left-turn pocket (#123), and a car park's
/// bays and aisles as one <see cref="AreaPropType.ParkingPad"/> surface (#499). Drawn at the
/// surface's own height. Their collision is the heightfield's: the road blend holds the ground
/// under them (<c>TerrainMeshBuilder.HoldUnderPavement</c>), as under a ribbon.
///
/// <para>
/// A car park is a shade lighter and greyer than a carriageway — worn, unsealed by traffic polish,
/// and read from the air as a lot rather than as a very wide road. The bays painted on it are
/// <c>PNT2</c> lines drawn by <see cref="RoadPaintBuilder"/> over this surface.
/// </para>
/// </summary>
public static class PavementBuilder
{
    private static readonly Color Asphalt = RoadMeshBuilder.ColorFor(
        new RoadSegment { Class = RoadClass.Road, Surface = RoadSurface.Paved, Points = [] });

    /// <summary>A car park's own tarmac: the carriageway's, lifted and desaturated a little.</summary>
    private static readonly Color LotAsphalt = new(0.355f, 0.350f, 0.340f);

    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var road = Asphalt.SrgbToLinear();
        var lot = LotAsphalt.SrgbToLinear();
        foreach (var p in tile.AreaProps)
        {
            if (p.Type is not (AreaPropType.Pavement or AreaPropType.ParkingPad) || p.Vertices.Length < 9) continue;
            var colour = p.Type == AreaPropType.ParkingPad ? lot : road;
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                int i0 = vertices.Count;
                for (int k = 0; k < 3; k++)
                {
                    int v = p.Indices[t + k] * 3;
                    vertices.Add(new Vector3(p.Vertices[v], p.Vertices[v + 1], p.Vertices[v + 2]));
                    colors.Add(colour);
                    uvs.Add(Vector2.Zero);
                    uv2s.Add(Vector2.Zero);
                }
                // the road material is cull_disabled, so winding only needs to be consistent
                indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
            }
        }
    }
}
