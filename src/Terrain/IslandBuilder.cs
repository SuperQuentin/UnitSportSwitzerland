using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Roundabout central islands (#122): the <see cref="AreaPropType.Island"/> props the network
/// stage wrote, a fan whose rim vertices sit at the ring road's height. Variant 0 is raised by its
/// <c>Height</c> behind a kerb, grassed, and solid; variant 1 (a mini-roundabout) is a flush white
/// disc a lorry drives over. The ground under a raised island is held below its top by the road
/// blend (<c>TerrainMeshBuilder.HoldUnderIsland</c>), so a mound in the terrain does not poke
/// through it.
/// </summary>
public static class IslandBuilder
{
    private static readonly Color GrassColor = new(0.36f, 0.47f, 0.24f);
    private static readonly Color KerbColor = new(0.70f, 0.69f, 0.66f);
    private static readonly Color PaintColor = new(0.92f, 0.92f, 0.90f);

    /// <summary>A mini-roundabout's paint stands this far over the road, against z-fighting.</summary>
    private const float PaintLift = 0.02f;

    public static bool IsIsland(RoadAreaProp p) => p.Type == AreaPropType.Island && p.Vertices.Length >= 9;

    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var grass = GrassColor.SrgbToLinear();
        var kerb = KerbColor.SrgbToLinear();
        var paint = PaintColor.SrgbToLinear();
        foreach (var p in tile.AreaProps)
        {
            if (!IsIsland(p)) continue;
            bool mini = p.Variant == 1;
            float lift = mini ? PaintLift : p.Height;
            var top = mini ? paint : grass;
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
                Tri(vertices, colors, uvs, uv2s, indices, top,
                    At(p, p.Indices[t], lift), At(p, p.Indices[t + 1], lift), At(p, p.Indices[t + 2], lift));
            if (mini) continue;
            foreach (var (a, b) in Rim(p))
                Quad(vertices, colors, uvs, uv2s, indices, kerb,
                    At(p, a, 0f), At(p, b, 0f), At(p, b, p.Height), At(p, a, p.Height));
        }
    }

    /// <summary>Collision for every raised island: its top and its kerb.</summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var p in tile.AreaProps)
        {
            if (!IsIsland(p) || (p.Flags & PropFlags.Solid) == 0) continue;
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                faces.Add(At(p, p.Indices[t], p.Height));
                faces.Add(At(p, p.Indices[t + 1], p.Height));
                faces.Add(At(p, p.Indices[t + 2], p.Height));
            }
            foreach (var (a, b) in Rim(p))
            {
                faces.Add(At(p, a, 0f)); faces.Add(At(p, b, 0f)); faces.Add(At(p, b, p.Height));
                faces.Add(At(p, a, 0f)); faces.Add(At(p, b, p.Height)); faces.Add(At(p, a, p.Height));
            }
        }
        return faces.ToArray();
    }

    /// <summary>The polygon's outline: every triangle edge used by one triangle only.</summary>
    public static List<(int A, int B)> Rim(RoadAreaProp p)
    {
        var count = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = p.Indices[t + k], b = p.Indices[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        return count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
    }

    private static Vector3 At(RoadAreaProp p, int i, float lift) =>
        new(p.Vertices[i * 3], p.Vertices[i * 3 + 1] + lift, p.Vertices[i * 3 + 2]);

    private static void Tri(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices, Color color, Vector3 a, Vector3 b, Vector3 c)
    {
        int i0 = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c);
        for (int k = 0; k < 3; k++) { colors.Add(color); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero); }
        // the road material is cull_disabled, so winding only needs to be consistent
        indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
    }

    private static void Quad(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices, Color color, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int i0 = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        for (int k = 0; k < 4; k++) { colors.Add(color); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero); }
        indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
        indices.Add(i0); indices.Add(i0 + 2); indices.Add(i0 + 3);
    }
}
