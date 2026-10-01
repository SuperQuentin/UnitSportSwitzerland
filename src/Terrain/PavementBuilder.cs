using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Flush carriageway beside a segment (#123): the <see cref="AreaPropType.Pavement"/> strips the
/// network stage writes where an approach widens for a left-turn pocket, drawn in the road's
/// asphalt at the road's height. Their collision is the heightfield's: the road blend holds the
/// ground under them at the road (<c>TerrainMeshBuilder.HoldUnderPavement</c>), as under a ribbon.
/// </summary>
public static class PavementBuilder
{
    private static readonly Color Asphalt = RoadMeshBuilder.ColorFor(
        new RoadSegment { Class = RoadClass.Road, Surface = RoadSurface.Paved, Points = [] });

    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var colour = Asphalt.SrgbToLinear();
        foreach (var p in tile.AreaProps)
        {
            if (p.Type != AreaPropType.Pavement || p.Vertices.Length < 9) continue;
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
