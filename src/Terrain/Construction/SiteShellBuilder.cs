using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// The half-built shells of a tile's building sites (#608), as one mesh and its collision faces:
/// each site's <see cref="ShellPlan"/> turned into vertex-coloured boxes in tile-local coordinates
/// (x = E − MinE, y = height, z = MaxN − N), drawn with the prop material like the piers
/// (<see cref="PierMeshBuilder"/>). The collision joins the tile's road cells, which collide from
/// both sides, because a slab and a ramp are walked on from above. The site's own solid is not
/// drawn (<see cref="BuildingMeshBuilder.Drawn"/>). Any thread.
/// </summary>
public static class SiteShellBuilder
{
    // vertex colours, raw linear (the prop shader takes them as they are); alpha 0 = no glow
    private static Color C(float r, float g, float b) => new Color(r, g, b).SrgbToLinear() with { A = 0f };

    private static readonly Color[] Colors =
    {
        // concrete darker than it looks on a site: the realistic style's sun took 0.66 to white
        C(0.53f, 0.52f, 0.49f),   // Concrete, cured
        C(0.61f, 0.60f, 0.57f),   // FreshConcrete
        C(0.93f, 0.72f, 0.10f),   // Formwork: the yellow panel system
        C(0.62f, 0.40f, 0.24f),   // Plywood
        C(0.45f, 0.24f, 0.13f),   // Rebar, rusty
        C(0.72f, 0.74f, 0.76f),   // Tube, galvanised
        C(0.58f, 0.46f, 0.31f),   // Deck boards
        C(0.20f, 0.42f, 0.24f),   // Net, debris netting
        C(0.94f, 0.94f, 0.90f),   // Insulation, EPS
        C(0.24f, 0.25f, 0.27f),   // Frame
        C(0.86f, 0.30f, 0.16f),   // Board: the toe boards
        default,                  // Invisible: never drawn
    };

    /// <summary>Faces lit by the sun in a flat-shaded world: the top full, the sides darker, the bottom darkest.</summary>
    private const float Top = 1f, SideX = 0.86f, SideZ = 0.76f, Bottom = 0.6f;

    /// <summary>The ground floor's slab stands this far over the highest ground under it, so no terrain shows through.</summary>
    private const float SlabClearance = 0.12f;
    /// <summary>The ground under a building is read this often, m: five points let the ground show through the slab.</summary>
    private const float GroundStep = 2f;

    /// <summary>
    /// The ground each site stands on: the highest point of the drawn terrain under its plan box
    /// (the mesh height, the surface on screen), plus <see cref="SlabClearance"/>, and the lowest.
    /// </summary>
    private static (float High, float Low) Ground(ConstructionSite site, TileId id, ChunkGrid? grid, Building b)
    {
        if (grid == null) return (b.MinY + SlabClearance, b.MinY);
        float high = float.MinValue, low = float.MaxValue;
        var box = site.Box;
        int nu = Math.Max(1, (int)MathF.Ceiling(box.Width / GroundStep)), nv = Math.Max(1, (int)MathF.Ceiling(box.Depth / GroundStep));
        for (int i = 0; i <= nu; i++)
            for (int j = 0; j <= nv; j++)
            {
                var p = box.Center + box.AxisU * (box.Width * ((float)i / nu - 0.5f)) + box.AxisV * (box.Depth * ((float)j / nv - 0.5f));
                float h = (float)grid.SampleMeshHeight(id.MinE + Mathf.Clamp(p.X, 0, ChunkFormat.TileSizeM),
                    id.MaxN - Mathf.Clamp(p.Y, 0, ChunkFormat.TileSizeM));
                high = Math.Max(high, h);
                low = Math.Min(low, h);
            }
        return (high + SlabClearance, low);
    }

    /// <summary>A point of a site's frame (x along its plan box's AxisU, z along AxisV, from its centre) in tile-local metres.</summary>
    public static Vector3 ToTile(ConstructionSite site, Vector3 p) =>
        new(site.Box.Center.X + site.Box.AxisU.X * p.X + site.Box.AxisV.X * p.Z, p.Y,
            site.Box.Center.Y + site.Box.AxisU.Y * p.X + site.Box.AxisV.Y * p.Z);

    /// <summary>The shell of one site, its wings read off its own roof (<see cref="PlanOutline.Wings"/>, #577).</summary>
    public static ShellPlan Shell(BuildingTile tile, ConstructionSite site, ChunkGrid? grid)
    {
        int index = int.Parse(site.Key.Split('_')[2]);
        var b = tile.Buildings[index];
        var wings = PlanOutline.Wings(b, site.Box.Center, site.Box.AxisU, site.Box.Width, site.Box.Depth)?
            .Select(r => new Rect2D(r.X0, r.Z0, r.X1, r.Z1)).ToList();
        var (high, low) = Ground(site, tile.Id, grid, b);
        return ShellPlans.Plan(site, wings, high, low);
    }

    /// <summary>
    /// The tile's shells: the mesh (null when not asked for, or no site) and the collision faces
    /// (empty when not asked for). Null when the tile has no site.
    /// </summary>
    public static (PierMeshBuilder.MeshData? Mesh, Vector3[] Faces)? Build(BuildingTile tile, IReadOnlyList<ConstructionSite> sites,
        ChunkGrid? grid, bool mesh, bool collision)
    {
        if (sites.Count == 0 || !mesh && !collision) return null;
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var indices = new List<int>();
        var faces = new List<Vector3>();
        foreach (var site in sites)
        {
            var shell = Shell(tile, site, grid);
            Vector3 T(float x, float y, float z) => ToTile(site, new Vector3(x, y, z));

            foreach (var box in shell.Boxes)
            {
                var (n, m) = (box.Min, box.Max);
                // the eight corners: bottom ring, then top ring
                var p = new[]
                {
                    T(n.X, n.Y, n.Z), T(m.X, n.Y, n.Z), T(m.X, n.Y, m.Z), T(n.X, n.Y, m.Z),
                    T(n.X, m.Y, n.Z), T(m.X, m.Y, n.Z), T(m.X, m.Y, m.Z), T(n.X, m.Y, m.Z),
                };
                var col = Colors[(int)box.Part];
                void Quad(int a, int bb, int c, int d, float shade)
                {
                    if (mesh && box.Part != ShellPart.Invisible)
                    {
                        int i = vertices.Count;
                        vertices.Add(p[a]); vertices.Add(p[bb]); vertices.Add(p[c]); vertices.Add(p[d]);
                        var shaded = new Color(col.R * shade, col.G * shade, col.B * shade, col.A);
                        for (int k = 0; k < 4; k++) colors.Add(shaded);
                        // one winding: the prop shaders draw both sides (cull_disabled)
                        indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
                        indices.Add(i); indices.Add(i + 2); indices.Add(i + 3);
                    }
                    if (collision && box.Solid)
                    {
                        faces.Add(p[a]); faces.Add(p[bb]); faces.Add(p[c]);
                        faces.Add(p[a]); faces.Add(p[c]); faces.Add(p[d]);
                    }
                }
                Quad(4, 5, 6, 7, Top);
                Quad(0, 3, 2, 1, Bottom);
                Quad(0, 1, 5, 4, SideZ);
                Quad(2, 3, 7, 6, SideZ);
                Quad(1, 2, 6, 5, SideX);
                Quad(3, 0, 4, 7, SideX);
            }
            if (collision)
                foreach (var r in shell.Ramps)
                {
                    Vector3 a = T(r.A.X, r.A.Y, r.A.Z), b = T(r.B.X, r.B.Y, r.B.Z), c = T(r.C.X, r.C.Y, r.C.Z), d = T(r.D.X, r.D.Y, r.D.Z);
                    faces.Add(a); faces.Add(b); faces.Add(c);
                    faces.Add(a); faces.Add(c); faces.Add(d);
                }
        }
        return (mesh && vertices.Count > 0 ? new PierMeshBuilder.MeshData(vertices.ToArray(), colors.ToArray(), indices.ToArray()) : null,
            faces.ToArray());
    }
}
