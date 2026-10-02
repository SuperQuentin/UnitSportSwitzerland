using Godot;
using UnitSport.Avatar;

namespace UnitSport.Build;

/// <summary>
/// The low-poly look of every piece in every material (#274), built once from boxes and cached:
/// wood as planks, stone as coursed blocks over mortar, metal as corrugated sheet, sandbags as
/// staggered bags. Each piece is authored in its own frame (<see cref="Structures.LocalTransform"/>
/// puts it in place): a floor's top at y 0, an edge piece standing from y 0 along X at z 0, stairs
/// climbing toward +Z, a gable roof with its ridge along X.
///
/// <para>
/// <see cref="MeshScratch.Build()"/> turns what it builds half round (x, z → −x, −z). Every piece here
/// is symmetric under that except the stairs, which are authored climbing toward −Z so they end up
/// climbing toward +Z. Colliders are made directly in the final frame.
/// </para>
/// </summary>
public static class StructureMeshes
{
    private const float S = BuildGrid.Cell, H = BuildGrid.Storey;
    private const float FloorT = 0.2f;

    private static readonly Dictionary<(PieceKind, BuildMaterial), ArrayMesh> Cache = new();
    private static readonly Dictionary<BuildMaterial, ArrayMesh> Stilts = new();

    public static float Thickness(BuildMaterial m) => m switch
    {
        BuildMaterial.Stone => 0.3f,
        BuildMaterial.Metal => 0.12f,
        BuildMaterial.Sandbag => 0.5f,
        _ => 0.16f,
    };

    /// <summary>An edge piece's solid parts as (x0, x1, y0, y1) rectangles in its wall plane: the window and door are holes.</summary>
    private static (float X0, float X1, float Y0, float Y1)[] Rects(PieceKind k) => k switch
    {
        PieceKind.WindowWall => new[] { (-S / 2, -0.5f, 0f, H), (0.5f, S / 2, 0f, H), (-0.5f, 0.5f, 0f, 0.9f), (-0.5f, 0.5f, 1.8f, H) },
        PieceKind.DoorWall => new[] { (-S / 2, -0.55f, 0f, H), (0.55f, S / 2, 0f, H), (-0.55f, 0.55f, 2.05f, H) },
        PieceKind.HalfWall => new[] { (-S / 2, S / 2, 0f, H / 2) },
        _ => new[] { (-S / 2, S / 2, 0f, H) },
    };

    private static float RoofAngle => Mathf.Atan2(H / 2, S / 2);

    public static ArrayMesh Mesh(PieceKind kind, BuildMaterial m)
    {
        if (Cache.TryGetValue((kind, m), out var cached)) return cached;
        var s = new MeshScratch();
        switch (kind)
        {
            case PieceKind.Floor:
                Fill(s, new Vector3(-S / 2, -FloorT, -S / 2), new Vector3(S / 2, 0, S / 2), m, thin: 1);
                break;
            case PieceKind.Pillar:
                Fill(s, new Vector3(-0.18f, 0, -0.18f), new Vector3(0.18f, H, 0.18f), m, thin: 2);
                break;
            case PieceKind.Stairs:
            {
                // authored climbing toward −Z (Build turns it to +Z): step i rises to (i+1)/N of the storey
                const int n = 8;
                for (int i = 0; i < n; i++)
                {
                    float z1 = S / 2 - i * S / n, z0 = z1 - S / n;
                    Fill(s, new Vector3(-S / 2 + 0.05f, 0, z0), new Vector3(S / 2 - 0.05f, (i + 1) * H / n, z1), m, thin: 1);
                }
                break;
            }
            case PieceKind.Roof:
                AppendRoof(s, m);
                break;
            default:
            {
                float t = Thickness(m);
                foreach (var (x0, x1, y0, y1) in Rects(kind))
                    Fill(s, new Vector3(x0, y0, -t / 2), new Vector3(x1, y1, t / 2), m, thin: 2);
                break;
            }
        }
        return Cache[(kind, m)] = s.Build();
    }

    /// <summary>Two slopes from the eaves (z ±S/2, a little overhang) to a ridge at z 0, H/2 up, in strips of tiles.</summary>
    private static void AppendRoof(MeshScratch s, BuildMaterial m)
    {
        var (a, b) = RoofColours(m);
        float angle = RoofAngle, len = Mathf.Sqrt(S * S / 4 + H * H / 4) + 0.3f;
        const int strips = 6;
        foreach (int side in new[] { -1, 1 })
        {
            var basis = new Basis(Vector3.Right, side * angle);
            for (int i = 0; i < strips; i++)
            {
                // along the slope, from the eave (i = 0) to the ridge
                float along = -len / 2 + (i + 0.5f) * len / strips;
                var mid = new Vector3(0, H / 4, side * S / 4);
                var down = basis * new Vector3(0, 0, side);
                var c = mid + down * -along + basis * new Vector3(0, 0.06f, 0);
                s.Box(c, new Vector3(S + 0.3f, 0.1f, len / strips + 0.01f), i % 2 == 0 ? a : b, basis);
            }
        }
        s.Box(new Vector3(0, H / 2 + 0.05f, 0), new Vector3(S + 0.35f, 0.16f, 0.18f), b.Darkened(0.2f));   // ridge
    }

    private static (Color A, Color B) RoofColours(BuildMaterial m) => m switch
    {
        BuildMaterial.Stone => (new Color(0.36f, 0.37f, 0.41f), new Color(0.31f, 0.32f, 0.36f)),   // slate
        BuildMaterial.Metal => (new Color(0.56f, 0.20f, 0.17f), new Color(0.48f, 0.17f, 0.15f)),   // red tin
        _ => (new Color(0.46f, 0.31f, 0.19f), new Color(0.40f, 0.26f, 0.16f)),                    // shingles
    };

    /// <summary>
    /// Fills a box (<paramref name="min"/> to <paramref name="max"/>) with the material's pattern.
    /// <paramref name="thin"/> is the axis across the slab (1 for a floor, 2 for a wall): courses run
    /// along X, stacked along the remaining axis.
    /// </summary>
    private static void Fill(MeshScratch s, Vector3 min, Vector3 max, BuildMaterial m, int thin)
    {
        int up = thin == 1 ? 2 : 1;   // the axis courses stack along
        float width = max.X - min.X, height = max[up] - min[up];
        if (width <= 0.001f || height <= 0.001f) return;

        void Block(float x0, float x1, float v0, float v1, float inset, float bulge, Color c)
        {
            var lo = min; var hi = max;
            lo.X = x0 + inset; hi.X = x1 - inset;
            lo[up] = v0 + inset; hi[up] = v1 - inset;
            lo[thin] -= bulge; hi[thin] += bulge;
            if (hi.X - lo.X < 0.005f || hi[up] - lo[up] < 0.005f) return;
            s.Box((lo + hi) / 2, hi - lo, c);
        }

        switch (m)
        {
            case BuildMaterial.Wood:
            {
                int rows = Math.Max(1, Mathf.RoundToInt(height / 0.3f));
                for (int r = 0; r < rows; r++)
                {
                    float v0 = min[up] + r * height / rows, v1 = v0 + height / rows;
                    var c = r % 2 == 0 ? new Color(0.58f, 0.40f, 0.23f) : new Color(0.51f, 0.35f, 0.20f);
                    Block(min.X, max.X, v0, v1, 0.008f, 0, c);
                }
                break;
            }
            case BuildMaterial.Stone:
            {
                Block(min.X, max.X, min[up], max[up], 0, -0.02f, new Color(0.40f, 0.40f, 0.38f));   // mortar
                int rows = Math.Max(1, Mathf.RoundToInt(height / 0.4f));
                for (int r = 0; r < rows; r++)
                {
                    float v0 = min[up] + r * height / rows, v1 = v0 + height / rows;
                    float offset = r % 2 == 0 ? 0 : 0.4f;
                    for (float x = min.X - offset; x < max.X; x += 0.8f)
                    {
                        float x0 = Mathf.Max(x, min.X), x1 = Mathf.Min(x + 0.8f, max.X);
                        var c = ((int)((x + 10) / 0.8f) + r) % 2 == 0 ? new Color(0.61f, 0.60f, 0.56f) : new Color(0.54f, 0.54f, 0.50f);
                        Block(x0, x1, v0, v1, 0.025f, 0, c);
                    }
                }
                break;
            }
            case BuildMaterial.Metal:
            {
                Block(min.X, max.X, min[up], max[up], 0, 0, new Color(0.40f, 0.44f, 0.48f));
                for (float x = min.X + 0.09f; x + 0.12f < max.X; x += 0.3f)
                    Block(x, x + 0.12f, min[up], max[up], 0, 0.025f, new Color(0.50f, 0.54f, 0.58f));
                break;
            }
            case BuildMaterial.Sandbag:
            {
                int rows = Math.Max(1, Mathf.RoundToInt(height / 0.3f));
                for (int r = 0; r < rows; r++)
                {
                    float v0 = min[up] + r * height / rows, v1 = v0 + height / rows;
                    float offset = r % 2 == 0 ? 0 : 0.3f;
                    for (float x = min.X - offset; x < max.X; x += 0.6f)
                    {
                        float x0 = Mathf.Max(x, min.X), x1 = Mathf.Min(x + 0.6f, max.X);
                        var c = ((int)((x + 10) / 0.6f) + r) % 2 == 0 ? new Color(0.74f, 0.66f, 0.46f) : new Color(0.68f, 0.60f, 0.41f);
                        Block(x0, x1, v0, v1, 0.03f, 0, c);
                    }
                }
                break;
            }
        }
    }

    /// <summary>A post under a grounded piece's foot, down to the terrain: a unit-tall column scaled to the gap.</summary>
    public static ArrayMesh Stilt(BuildMaterial m)
    {
        if (Stilts.TryGetValue(m, out var cached)) return cached;
        var s = new MeshScratch();
        var c = m switch
        {
            BuildMaterial.Stone => new Color(0.52f, 0.52f, 0.49f),
            BuildMaterial.Metal => new Color(0.36f, 0.39f, 0.43f),
            _ => new Color(0.42f, 0.29f, 0.17f),
        };
        s.Box(new Vector3(0, -0.5f, 0), new Vector3(0.2f, 1f, 0.2f), c);
        return Stilts[m] = s.Build();
    }

    /// <summary>The piece's colliders, in its own frame (not turned by Build).</summary>
    public static IEnumerable<(Shape3D Shape, Transform3D At)> Colliders(PieceKind kind, BuildMaterial m)
    {
        switch (kind)
        {
            case PieceKind.Floor:
                yield return (new BoxShape3D { Size = new Vector3(S, FloorT, S) }, new Transform3D(Basis.Identity, new Vector3(0, -FloorT / 2, 0)));
                break;
            case PieceKind.Pillar:
                yield return (new BoxShape3D { Size = new Vector3(0.36f, H, 0.36f) }, new Transform3D(Basis.Identity, new Vector3(0, H / 2, 0)));
                break;
            case PieceKind.Stairs:
            {
                // a ramp under the steps' nosings, climbing toward +Z: walkable at 45° (the foot limit is 52°)
                float len = Mathf.Sqrt(S * S + H * H);
                var basis = new Basis(Vector3.Right, -Mathf.Atan2(H, S));
                yield return (new BoxShape3D { Size = new Vector3(S - 0.1f, 0.2f, len) }, new Transform3D(basis, new Vector3(0, H / 2 - 0.1f, 0)));
                break;
            }
            case PieceKind.Roof:
            {
                float len = Mathf.Sqrt(S * S / 4 + H * H / 4) + 0.3f;
                foreach (int side in new[] { -1, 1 })
                {
                    var basis = new Basis(Vector3.Right, side * RoofAngle);
                    yield return (new BoxShape3D { Size = new Vector3(S + 0.3f, 0.12f, len) },
                        new Transform3D(basis, new Vector3(0, H / 4 + 0.06f, side * S / 4)));
                }
                break;
            }
            default:
            {
                float t = Thickness(m);
                foreach (var (x0, x1, y0, y1) in Rects(kind))
                    yield return (new BoxShape3D { Size = new Vector3(x1 - x0, y1 - y0, t) },
                        new Transform3D(Basis.Identity, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, 0)));
                break;
            }
        }
    }

    /// <summary>Where a grounded piece's legs go, in its own frame: its foot's corners (an edge piece: its two ends).</summary>
    public static Vector3[] Feet(PieceKind kind) => kind switch
    {
        PieceKind.Floor => new[] { new Vector3(-S / 2 + 0.15f, -FloorT, -S / 2 + 0.15f), new Vector3(S / 2 - 0.15f, -FloorT, -S / 2 + 0.15f),
                                   new Vector3(-S / 2 + 0.15f, -FloorT, S / 2 - 0.15f), new Vector3(S / 2 - 0.15f, -FloorT, S / 2 - 0.15f) },
        PieceKind.Stairs => new[] { new Vector3(-S / 2 + 0.15f, 0, -S / 2 + 0.15f), new Vector3(S / 2 - 0.15f, 0, -S / 2 + 0.15f),
                                    new Vector3(-S / 2 + 0.15f, 0, S / 2 - 0.15f), new Vector3(S / 2 - 0.15f, 0, S / 2 - 0.15f) },
        PieceKind.Pillar => new[] { Vector3.Zero },
        PieceKind.Roof => Array.Empty<Vector3>(),
        _ => new[] { new Vector3(-S / 2 + 0.15f, 0, 0), new Vector3(S / 2 - 0.15f, 0, 0) },
    };
}
