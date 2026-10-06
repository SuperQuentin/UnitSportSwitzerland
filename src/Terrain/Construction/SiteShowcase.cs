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
        ("shell, a block of four floors", 24f, 15f, 6.3f, 4),
        ("topped out, a house", 14f, 11f, 6.3f, 2),
        ("topped out, a six-floor block", 40f, 18f, 18.4f, 6),
    };

    [Showcase("Terrain", "Building site")]
    private static IEnumerable<(string, Func<Node3D>)> Sites() =>
        Buildings.Select(b => (b.Name, (Func<Node3D>)(() => Site(b.W, b.D, b.H, b.Floors))));

    /// <summary>A flat-topped prism, as swissBUILDINGS3D draws an <c>Im Bau</c> volume, turned a little.</summary>
    private static Building Prism(Vector2 c, float w, float d, float h, byte floors)
    {
        var u = Vector2.FromAngle(0.3f);
        var v = new Vector2(-u.Y, u.X);
        var corners = new[] { c - u * w / 2 - v * d / 2, c + u * w / 2 - v * d / 2, c + u * w / 2 + v * d / 2, c - u * w / 2 + v * d / 2 };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            Tri(P(a, 0), P(b, 0), P(b, h));
            Tri(P(a, 0), P(b, h), P(a, h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = BuildingKind.UnderConstruction, Floors = floors, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
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
        if (build.Cranes.Count > 0)
            plot.AddChild(new SiteCranes(build.Cranes.Select(c => CraneRig.Make(c, material)).ToArray(), material));
        root.AddChild(plot);
        return root;
    }
}
