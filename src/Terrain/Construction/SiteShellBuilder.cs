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
/// drawn (<see cref="BuildingMeshBuilder.Drawn"/>). The yard (#609) and the cranes' masts (#610)
/// go in the same mesh; what a crane slews is built here too, as meshes of its own. Any thread.
/// </summary>
public static class SiteShellBuilder
{
    // vertex colours, raw linear (the prop shader takes them as they are); alpha 0 = no glow
    private static Color C(float r, float g, float b) => new Color(r, g, b).SrgbToLinear() with { A = 0f };

    internal static readonly Color[] Colors =
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
        C(0.88f, 0.89f, 0.87f),   // Container, a site office's white
        C(0.16f, 0.30f, 0.52f),   // ContainerTrim, its blue frame and door
        C(0.15f, 0.42f, 0.75f),   // Toilet, the blue cabin
        C(0.22f, 0.58f, 0.30f),   // ToiletAlt, the green one
        C(0.86f, 0.50f, 0.10f),   // Skip
        C(0.36f, 0.33f, 0.30f),   // Debris
        C(0.45f, 0.34f, 0.23f),   // Soil
        C(0.66f, 0.30f, 0.20f),   // Brick
        C(0.84f, 0.83f, 0.78f),   // Cement, the bags
        C(0.70f, 0.72f, 0.73f),   // Fence, the mesh panels
        C(0.12f, 0.45f, 0.32f),   // Banner, a builder's green
        C(0.98f, 0.78f, 0.10f),   // Lamp, the warning lamps
        C(0.18f, 0.22f, 0.27f),   // Window
        C(0.95f, 0.95f, 0.93f),   // SignBoard, the builder's board
        C(0.95f, 0.76f, 0.08f),   // CraneYellow
        C(0.92f, 0.92f, 0.90f),   // Cab
        C(0.95f, 0.70f, 0.10f),   // HookBlock
        C(0.12f, 0.12f, 0.13f),   // Rope
        // a light source (alpha 1): darkened by day, shining at its own colour at night
        new Color(0.95f, 0.08f, 0.05f).SrgbToLinear() with { A = 1f },   // LampRed
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

    /// <summary>The drawn ground under a tile-local plan point, or the building's base with no grid.</summary>
    private static float GroundAt(BuildingTile tile, ConstructionSite site, ChunkGrid? grid, Vector2 p)
    {
        if (grid == null) return tile.Buildings[int.Parse(site.Key.Split('_')[2])].MinY;
        var id = tile.Id;
        return (float)grid.SampleMeshHeight(id.MinE + Mathf.Clamp(p.X, 0, ChunkFormat.TileSizeM),
            id.MaxN - Mathf.Clamp(p.Y, 0, ChunkFormat.TileSizeM));
    }

    /// <summary>
    /// The yard of one site (#609): the ground under any point of its frame read from the drawn
    /// terrain, or the building's base with no grid.
    /// </summary>
    public static SiteDressingPlan Dressing(BuildingTile tile, ConstructionSite site, ChunkGrid? grid) =>
        SiteDressings.Plan(site, (x, z) =>
        {
            var p = ToTile(site, new Vector3(x, 0, z));
            return GroundAt(tile, site, grid, new Vector2(p.X, p.Z));
        });

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
    /// One crane's moving parts (#610), built on the worker: the pivot at its mast's top
    /// (tile-local), its job, and the meshes of what slews (<see cref="CranePlans"/>) in the
    /// pivot's frame. <see cref="SiteCranes"/> stands them up and moves them.
    /// </summary>
    public sealed record CraneRigData(CraneSpot Spot, CraneJob Job, Vector3 Pivot,
        PierMeshBuilder.MeshData Jib, PierMeshBuilder.MeshData Trolley, PierMeshBuilder.MeshData Hook,
        PierMeshBuilder.MeshData Rope, PierMeshBuilder.MeshData Load, Vector3[] Lamps);

    /// <summary>A tile's sites: the mesh of everything that stands still, its collision faces, and the cranes' moving parts.</summary>
    public sealed record SiteBuild(PierMeshBuilder.MeshData? Mesh, Vector3[] Faces, List<CraneRigData> Cranes);

    /// <summary>The mesh lists and collision triangles being built.</summary>
    private sealed class Geo(bool mesh, bool collision)
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Color> Colors = new();
        public readonly List<int> Indices = new();
        public readonly List<Vector3> Faces = new();
        private readonly Vector3[] _p = new Vector3[8];

        public bool Mesh => mesh;

        public PierMeshBuilder.MeshData? Data() =>
            mesh && Vertices.Count > 0 ? new PierMeshBuilder.MeshData(Vertices.ToArray(), Colors.ToArray(), Indices.ToArray()) : null;

        /// <summary>A box, its corners carried through <paramref name="t"/>: drawn unless invisible, solid when it is.</summary>
        public void Box(ShellBox box, Func<float, float, float, Vector3> t)
        {
            var (n, m) = (box.Min, box.Max);
            // the eight corners: bottom ring, then top ring
            var p = _p;
            p[0] = t(n.X, n.Y, n.Z); p[1] = t(m.X, n.Y, n.Z); p[2] = t(m.X, n.Y, m.Z); p[3] = t(n.X, n.Y, m.Z);
            p[4] = t(n.X, m.Y, n.Z); p[5] = t(m.X, m.Y, n.Z); p[6] = t(m.X, m.Y, m.Z); p[7] = t(n.X, m.Y, m.Z);
            var col = SiteShellBuilder.Colors[(int)box.Part];
            bool draw = mesh && box.Part != ShellPart.Invisible, solid = collision && box.Solid;
            void Quad(int a, int b, int c, int d, float shade)
            {
                if (draw)
                {
                    int i = Vertices.Count;
                    Vertices.Add(p[a]); Vertices.Add(p[b]); Vertices.Add(p[c]); Vertices.Add(p[d]);
                    var shaded = new Color(col.R * shade, col.G * shade, col.B * shade, col.A);
                    for (int k = 0; k < 4; k++) Colors.Add(shaded);
                    // one winding: the prop shaders draw both sides (cull_disabled)
                    Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
                    Indices.Add(i); Indices.Add(i + 2); Indices.Add(i + 3);
                }
                if (solid)
                {
                    Faces.Add(p[a]); Faces.Add(p[b]); Faces.Add(p[c]);
                    Faces.Add(p[a]); Faces.Add(p[c]); Faces.Add(p[d]);
                }
            }
            Quad(4, 5, 6, 7, Top);
            Quad(0, 3, 2, 1, Bottom);
            Quad(0, 1, 5, 4, SideZ);
            Quad(2, 3, 7, 6, SideZ);
            Quad(1, 2, 6, 5, SideX);
            Quad(3, 0, 4, 7, SideX);
        }

        /// <summary>A triangle shaded by the way it faces: a heap's side.</summary>
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            if (mesh)
            {
                var n = (b - a).Cross(c - a).Normalized();
                float shade = 0.7f + 0.3f * Math.Abs(n.Y) + 0.08f * n.X;
                int i = Vertices.Count;
                Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
                var shaded = new Color(col.R * shade, col.G * shade, col.B * shade, col.A);
                Colors.Add(shaded); Colors.Add(shaded); Colors.Add(shaded);
                Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
            }
            if (collision) { Faces.Add(a); Faces.Add(b); Faces.Add(c); }
        }

        /// <summary>A walkable quad that is collision only: a flight's ramp.</summary>
        public void Solid(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            if (!collision) return;
            Faces.Add(a); Faces.Add(b); Faces.Add(c);
            Faces.Add(a); Faces.Add(c); Faces.Add(d);
        }
    }

    /// <summary>Boxes in their own frame as one mesh: a crane part.</summary>
    public static PierMeshBuilder.MeshData MeshOf(IEnumerable<ShellBox> boxes)
    {
        var g = new Geo(mesh: true, collision: false);
        foreach (var b in boxes) g.Box(b, (x, y, z) => new Vector3(x, y, z));
        return g.Data()!;
    }

    /// <summary>
    /// The tile's sites: the mesh of their shells, yards and crane masts (null when not asked for),
    /// the collision faces (empty when not asked for), and each crane's moving parts (when the mesh
    /// is asked for). Null when the tile has no site.
    /// </summary>
    public static SiteBuild? Build(BuildingTile tile, IReadOnlyList<ConstructionSite> sites,
        ChunkGrid? grid, bool mesh, bool collision)
    {
        if (sites.Count == 0 || !mesh && !collision) return null;
        var g = new Geo(mesh, collision);
        var cranes = new List<CraneRigData>();
        foreach (var site in sites)
        {
            var shell = Shell(tile, site, grid);
            var yard = Dressing(tile, site, grid);
            Vector3 T(float x, float y, float z) => ToTile(site, new Vector3(x, y, z));
            foreach (var box in shell.Boxes) g.Box(box, T);
            foreach (var box in yard.Boxes) g.Box(box, T);
            foreach (var m in yard.Mounds) Mound(g, m, site);
            foreach (var r in shell.Ramps)
                g.Solid(T(r.A.X, r.A.Y, r.A.Z), T(r.B.X, r.B.Y, r.B.Z), T(r.C.X, r.C.Y, r.C.Z), T(r.D.X, r.D.Y, r.D.Z));

            // the cranes: the mast stands with the rest, what slews is its own (#610)
            for (int k = 0; k < site.Cranes.Count; k++)
            {
                var crane = site.Cranes[k];
                var d0 = crane.Base - site.Box.Center;
                var at = new Vector2(d0.Dot(site.Box.AxisU), d0.Dot(site.Box.AxisV));
                float ground = GroundAt(tile, site, grid, crane.Base);
                foreach (var box in CranePlans.Mast(crane, at, ground)) g.Box(box, T);
                if (!mesh) continue;
                var pivot = new Vector3(crane.Base.X, ground + CranePlans.MastHeight(crane), crane.Base.Y);
                float jibY = pivot.Y + CranePlans.JibBottom * (crane.Kind == CraneKind.Tower ? 1f : 0.62f);
                var job = new CraneJob($"{site.Key}|{k}", crane.Base, jibY, crane.JibLength, crane.RestYaw,
                    PickOf(tile, site, grid, crane), DropsOf(site, shell));
                int load = Core.Fnv.Unit($"{site.Key}|crane|load|{k}") < 0.6 ? 0 : 1;
                cranes.Add(new CraneRigData(crane, job, pivot, MeshOf(CranePlans.Jib(crane)), MeshOf(CranePlans.Trolley(crane)),
                    MeshOf(CranePlans.Hook()), MeshOf(CranePlans.Rope()), MeshOf(CranePlans.Load(load)), CranePlans.Lamps(crane)));
            }
        }
        return new SiteBuild(g.Data(), g.Faces.ToArray(), cranes);
    }

    /// <summary>A heap: an eight-sided foot, a shoulder, a crown and a point.</summary>
    private static void Mound(Geo g, ShellMound m, ConstructionSite site)
    {
        Vector3 T(float x, float y, float z) => ToTile(site, new Vector3(x, y, z));
        const int sides = 8;
        float[] ring = { 1f, 0.62f, 0.25f };
        float[] lift = { -0.15f, 0.58f, 0.92f };
        var rings = new Vector3[ring.Length][];
        for (int r = 0; r < ring.Length; r++)
        {
            rings[r] = new Vector3[sides];
            for (int s = 0; s < sides; s++)
            {
                float a = s * Mathf.Tau / sides + 0.3f;
                // a little lumpy, the same on every peer: by side and ring, not by chance
                float lump = 1f + 0.08f * MathF.Sin(s * 2.3f + r * 1.7f);
                rings[r][s] = T(m.Center.X + MathF.Cos(a) * m.RadiusX * ring[r] * lump, m.Ground + m.Height * lift[r],
                    m.Center.Y + MathF.Sin(a) * m.RadiusZ * ring[r] * lump);
            }
        }
        var top = T(m.Center.X, m.Ground + m.Height, m.Center.Y);
        var col = Colors[(int)m.Part];
        for (int r = 0; r + 1 < ring.Length; r++)
            for (int s = 0; s < sides; s++)
            {
                int t = (s + 1) % sides;
                g.Tri(rings[r][s], rings[r][t], rings[r + 1][t], col);
                g.Tri(rings[r][s], rings[r + 1][t], rings[r + 1][s], col);
            }
        for (int s = 0; s < sides; s++)
            g.Tri(rings[^1][s], rings[^1][(s + 1) % sides], top, col);
    }

    /// <summary>Where a crane picks its loads up: the materials, or the yard in front of its mast.</summary>
    private static Vector3 PickOf(BuildingTile tile, ConstructionSite site, ChunkGrid? grid, CraneSpot crane)
    {
        var at = site.Zones.FirstOrDefault(z => z.Kind == SiteZoneKind.Materials)?.Rect.Center ?? crane.Base + site.Front * 6f;
        return new Vector3(at.X, GroundAt(tile, site, grid, at), at.Y);
    }

    /// <summary>Where it sets them down: on the top slab, at its corners and its middle, tile-local.</summary>
    private static List<Vector3> DropsOf(ConstructionSite site, ShellPlan shell)
    {
        float y = shell.Levels[^1];
        var drops = new List<Vector3>();
        float hw = Math.Max(0, site.Box.Width / 2 - 3f), hd = Math.Max(0, site.Box.Depth / 2 - 3f);
        foreach (var (u, v) in new[] { (0f, 0f), (-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd) })
            drops.Add(ToTile(site, new Vector3(u, y, v)));
        return drops;
    }
}
