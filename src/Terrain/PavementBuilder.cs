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

    /// <summary>The tint of the nearest junction cap of the tile within 80 m of the patch's first vertex, null when none (sRGB).</summary>
    private static Color? CapColour(RoadTile tile, RoadAreaProp p)
    {
        float best = 80f * 80f;
        Color? found = null;
        foreach (var junction in tile.Junctions)
        {
            if (junction.Vertices.Length < 3) continue;
            // the cap's first vertex is its centre
            float dx = junction.Vertices[0] - p.Vertices[0], dz = junction.Vertices[2] - p.Vertices[2];
            float d = dx * dx + dz * dz;
            if (d >= best) continue;
            best = d;
            found = RoadMeshBuilder.CapColour(junction);
        }
        return found;
    }

    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var road = Asphalt.SrgbToLinear();
        var lot = LotAsphalt.SrgbToLinear();
        foreach (var p in tile.AreaProps)
        {
            if (p.Type is not (AreaPropType.Pavement or AreaPropType.ParkingPad) || p.Vertices.Length < 9) continue;
            // a widening or a corner's fill is the asphalt of the junction it belongs to: its cap's tint, so they read as one surface (#682)
            var colour = p.Type == AreaPropType.ParkingPad ? lot : CapColour(tile, p)?.SrgbToLinear() ?? road;
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
