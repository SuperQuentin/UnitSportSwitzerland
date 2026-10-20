using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Styles;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// Building sites for the model viewer (#605: <c>--models</c>, <c>docs/notes/avatar/model-viewer.md</c>):
/// one hand-made <c>Im Bau</c> building per phase, planned (<see cref="ConstructionSites.Plan"/>),
/// shelled (#608), dressed (#609) and craned (#610) by the game's own builders, on flat ground, the
/// cranes slewing by the clocks as they do in the world. What a tile's site looks like without
/// finding one.
/// </summary>
public static class SiteShowcase
{
    /// <summary>The hand-made buildings: a footprint, the height measured so far, the floors GWR says it will have.</summary>
    private static readonly (string Name, float W, float D, float H, byte Floors)[] Buildings =
    {
        ("foundations, a block of four floors", 24f, 15f, 1.2f, 4),
        ("foundations, a house", 14f, 11f, 1.0f, 2),
        ("shell, a block of four floors", 24f, 15f, 6.3f, 4),
        ("topped out, a house", 14f, 11f, 6.3f, 2),
        ("topped out, a six-floor block", 40f, 18f, 18.4f, 6),
    };

    [Showcase("Terrain", "Building site")]
    private static IEnumerable<(string, Func<Node3D>)> Sites() =>
        Buildings.Select(b => (b.Name, (Func<Node3D>)(() => Site(b.W, b.D, b.H, b.Floors))))
            .Append(("a pallet on a crane's hook (#618)", HookedPallet))
            .Append(("a site office with its gallery, one stack (#827)", () => OfficeGallery(14f, 11f, 6.3f, 2)))
            .Append(("a site office with its gallery, two stacks (#827)", () => OfficeGallery(52f, 30f, 18.4f, 6)));

    /// <summary>
    /// A site office close up (#827): its containers, its toilet cabins and the gallery up to the
    /// upper row, the outside stair and the walkway, from a site the planner fits one on (the first
    /// street direction round the plot that leaves room for it). The yard's own boxes, as the world draws them.
    /// </summary>
    private static Node3D OfficeGallery(float w, float d, float h, byte floors)
    {
        var root = new Node3D { Name = "OfficeGallery" };
        var centre = new Vector2(80f, 80f);
        var b = Prism(centre, w, d, h, floors);
        var box = PlanBox.Of(b)!.Value;
        ConstructionSite? site = null;
        for (int k = 0; k < 16 * 32 && site?.GallerySide is null or 0; k++)
            site = ConstructionSites.Plan($"2600_1200_{k / 16}", b, box, centre + Vector2.FromAngle(k % 16 * Mathf.Tau / 16) * (d + 14f), _ => false);
        if (site == null || site.GallerySide == 0) return root;
        var yard = SiteDressings.Plan(site, (_, _) => 0f);
        var boxes = new List<ShellBox>();
        boxes.AddRange(yard.Boxes.Skip(yard.OfficeBoxes.Start).Take(yard.OfficeBoxes.Count));
        boxes.AddRange(yard.Boxes.Skip(yard.ToiletBoxes.Start).Take(yard.ToiletBoxes.Count));
        boxes.AddRange(yard.Boxes.Skip(yard.GalleryBoxes.Start).Take(yard.GalleryBoxes.Count));
        var middle = yard.Office!.Center;
        var mesh = SiteShellBuilder.MeshOf(boxes.Select(x => x with
        {
            Min = x.Min - new Vector3(middle.X, 0, middle.Y), Max = x.Max - new Vector3(middle.X, 0, middle.Y),
        }));
        root.AddChild(new MeshInstance3D { Name = "Office", Mesh = ChunkNode.ToPropMesh(mesh, StyleKit.Material(MaterialRole.Prop)) });
        return root;
    }

    /// <summary>A crane's hook block with a pallet of bricks in its four slings, as a worked crane carries one (#618).</summary>
    private static Node3D HookedPallet()
    {
        var material = StyleKit.Material(MaterialRole.Prop);
        var root = new Node3D { Name = "HookedPallet" };
        float hookY = CraneControl.SlingHang;
        root.AddChild(new MeshInstance3D { Name = "Hook", Mesh = ChunkNode.ToPropMesh(SiteShellBuilder.MeshOf(CranePlans.Hook()), material), Position = new Vector3(0, hookY, 0) });
        root.AddChild(new MeshInstance3D
        {
            Name = "Slings", Mesh = ChunkNode.ToPropMesh(SiteCranes.SlingMesh(), material),
            Position = new Vector3(0, hookY - CraneControl.HookBottom, 0), Scale = new Vector3(1, CraneControl.Slings, 1),
        });
        var pallet = Items.PalletNode.Carried(Items.Pallets.LoadOf(0.95f, Items.Pallets.NarrowDepth));
        pallet.Position = new Vector3(0, Items.Pallets.Seat, 0);
        root.AddChild(pallet);
        return root;
    }

    /// <summary>A flat-topped prism, as swissBUILDINGS3D draws an <c>Im Bau</c> volume, turned a little.</summary>
    private static Building Prism(Vector2 c, float w, float d, float h, byte floors)
    {
        var u = Vector2.FromAngle(0.3f);
        var v = new Vector2(-u.Y, u.X);
        var corners = new[] { c - u * w / 2 - v * d / 2, c + u * w / 2 - v * d / 2, c + u * w / 2 + v * d / 2, c - u * w / 2 + v * d / 2 };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y + Lift, p.Y);
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            Tri(P(a, 0), P(b, 0), P(b, h));
            Tri(P(a, 0), P(b, h), P(a, h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = BuildingKind.UnderConstruction, Floors = floors, MinY = Lift, MaxY = Lift + h, Triangles = tris.ToArray() };
    }

    /// <summary>
    /// The sites stand this far up, on a block of ground of their own: the viewer's floor is a plane
    /// at 0, which would close any pit (#619) dug into it.
    /// </summary>
    private const float Lift = 8f;

    /// <summary>
    /// The block's top this far under the plot, m: the yard's boxes stand on <see cref="Lift"/>, and the
    /// prop material draws both sides, so a ground flush with their feet z-fought every one
    /// (`--zfightcheck`, docs/notes/general/zfight.md).
    /// </summary>
    private const float GroundDrop = 0.02f;

    /// <summary>
    /// The plot's ground: a 1 m lattice <see cref="Lift"/> up, the site's square and 3 m round it, with the
    /// pits cut in it and lined by the ground's own code (<see cref="PitTerrain.Cut"/>), and its sides
    /// down to the viewer's floor.
    /// </summary>
    private static PierMeshBuilder.MeshData Ground(SiteRect area, IReadOnlyList<PitCut> pits)
    {
        var (x0, z0, x1, z1) = PitTerrain.Bounds(area);
        const float quad = 1f;
        float half = MathF.Ceiling(Math.Max(x1 - x0, z1 - z0) / 2 + 3f);
        int m = (int)(2 * half / quad) + 1;
        var origin = new Vector2((x0 + x1) / 2 - half, (z0 + z1) / 2 - half);
        var grass = new Color(0.36f, 0.52f, 0.25f).SrgbToLinear() with { A = 0f };
        var earth = new Color(0.42f, 0.33f, 0.24f).SrgbToLinear() with { A = 0f };
        var v = new Vector3[m * m];
        var c = new Color[m * m];
        for (int r = 0; r < m; r++)
            for (int k = 0; k < m; k++)
            {
                v[r * m + k] = new Vector3(k * quad, Lift - GroundDrop, r * quad);
                c[r * m + k] = grass;
            }
        var idx = new List<int>();
        for (int r = 0; r < m - 1; r++)
            for (int k = 0; k < m - 1; k++)
            {
                int v00 = r * m + k;
                idx.AddRange(new[] { v00, v00 + 1, v00 + m, v00 + 1, v00 + m + 1, v00 + m });
            }
        // the cut works in the lattice's own frame: the pits moved into it and the result moved back
        var local = pits.Select(p => p with
        {
            Rect = p.Rect with { Center = p.Rect.Center - origin }, TopOuter = p.TopOuter - origin, TopInner = p.TopInner - origin,
        }).ToList();
        var up = Enumerable.Repeat(Vector3.Up, v.Length).ToArray();
        var (cv, cc, _, ci) = PitTerrain.Cut(v, c, up, idx.ToArray(), m, quad, local);
        var verts = cv.Select(p => p + new Vector3(origin.X, 0, origin.Y)).ToList();
        var cols = cc.ToList();
        var tris = ci.ToList();
        // the block's four sides, down to the floor
        var corners = new[] { new Vector3(0, 0, 0), new Vector3(2 * half, 0, 0), new Vector3(2 * half, 0, 2 * half), new Vector3(0, 0, 2 * half) };
        for (int s = 0; s < 4; s++)
        {
            var a = corners[s] + new Vector3(origin.X, 0, origin.Y);
            var b = corners[(s + 1) % 4] + new Vector3(origin.X, 0, origin.Y);
            int i = verts.Count;
            verts.AddRange(new[] { a, b, b + Vector3.Up * (Lift - GroundDrop), a + Vector3.Up * (Lift - GroundDrop) });
            cols.AddRange(new[] { earth, earth, earth, earth });
            tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }
        return new PierMeshBuilder.MeshData(verts.ToArray(), cols.ToArray(), tris.ToArray());
    }

    private static Node3D Site(float w, float d, float h, byte floors)
    {
        // a tile of its own, the building in the middle of a clear plot, the street to its south
        var centre = new Vector2(80f, 80f);
        var tile = new BuildingTile { Id = new TileId(2600, 1200), Buildings = new List<Building> { Prism(centre, w, d, h, floors) } };
        var key = new BuildingKey(tile.Id.E, tile.Id.N, 0).ToString();
        var site = ConstructionSites.Plan(key, tile.Buildings[0], PlanBox.Of(tile.Buildings[0])!.Value, centre + new Vector2(0, d + 14f), _ => false);
        var root = new Node3D { Name = "BuildingSite" };
        if (site == null) return root;
        var build = SiteShellBuilder.Build(tile, new[] { site }, null, mesh: true, collision: false);
        if (build?.Mesh is not { } mesh) return root;
        var material = StyleKit.Material(MaterialRole.Prop);
        // the plot round the origin, so the viewer frames the site rather than the tile's corner
        var plot = new Node3D { Name = "Plot", Position = new Vector3(-centre.X, 0, -centre.Y) };
        plot.AddChild(new MeshInstance3D { Name = "Site", Mesh = ChunkNode.ToPropMesh(mesh, material) });
        plot.AddChild(new MeshInstance3D { Name = "Ground", Mesh = ChunkNode.ToPropMesh(Ground(site.Area, build.Pits), material) });
        if (build.Cranes.Count > 0)
            plot.AddChild(new SiteCranes(build.Cranes.Select(c => CraneRig.Make(c, material)).ToArray(), material));
        root.AddChild(plot);
        return root;
    }
}
