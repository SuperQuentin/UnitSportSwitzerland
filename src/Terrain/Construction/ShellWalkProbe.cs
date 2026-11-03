using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// <c>--shellwalkcheck --world flat</c> (#608): a building site's shell is walked, not looked at.
/// Hand-made sites (a block three storeys into four, a house topped out and turned 31°, a six-storey
/// block topped out) are planned by <see cref="SitePlans"/>, their collision built by
/// <see cref="SiteShellBuilder"/> exactly as a tile builds it, into one body that collides from
/// both sides like the tile's road cells. A capsule body the player's size then walks each one's
/// <see cref="ShellPlan.Route"/> under real physics: across the landing, up both flights of every
/// storey, to the top slab. It fails if a body is stuck on a waypoint, arrives at the wrong
/// height (fell through a slab, or stands on something it should not), or ends anywhere but on
/// the top slab. The unit tests check the boxes; this checks the body.
/// <para>
/// And the site office's gallery (#827), on a one-stack and a two-stack office the planner fits
/// one on: from the stair's foot up onto the landing, along the walkway to every upper door's
/// doorstep, and back down to the foot, the containers' walls colliding too. This is the climb a
/// server-run bot makes on the same collision the server builds round it.
/// </para>
/// </summary>
public partial class ShellWalkProbe : Node3D
{
    public static bool Requested() => CmdArgs.Has("--shellwalkcheck");

    private readonly record struct Spec(float Width, float Depth, float Height, byte Floors, float Turn);

    private static readonly Spec[] Specs =
    {
        new(24f, 15f, 9.3f, 4, 0f),
        new(14f, 11f, 6.3f, 2, 31f),
        new(40f, 18f, 18.4f, 6, -17f),
    };

    /// <summary>A walker's pace, m/s: brisk, so the six storeys fit in the check's time.</summary>
    private const float Pace = 3f;
    /// <summary>A waypoint is reached within this, m, in plan.</summary>
    private const float Reach = 0.3f;
    /// <summary>Longest a walker may take to reach one waypoint, s, before it is stuck.</summary>
    private const double StuckAfter = 8;
    /// <summary>The player's capsule (<c>FootPlayer</c>): radius and height, m.</summary>
    private const float Radius = 0.3f, Tall = 1.8f;

    private sealed class Walker
    {
        public required string Name;
        public required CharacterBody3D Body;
        public required List<Vector3> Route;
        public int Next = 1;
        public double OnLeg;
        public bool Done;
    }

    private readonly List<Walker> _walkers = new();
    private int _failures, _expected;

    private void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        GD.Print($"[shellwalk] {(ok ? "ok  " : "FAIL")} {what}");
    }

    public override void _Ready()
    {
        var id = new TileId(2600, 1200);
        for (int k = 0; k < Specs.Length; k++)
        {
            var spec = Specs[k];
            var b = Prism(new Vector2(150 + 200 * k, 150), spec.Width, spec.Depth, spec.Height, spec.Floors, Mathf.DegToRad(spec.Turn));
            var tile = new BuildingTile { Id = id, Buildings = new List<Building> { b } };
            var sites = SitePlans.For(tile, null);
            if (sites.Count != 1) { Check(false, $"site {k}: planned ({sites.Count})"); continue; }
            var site = sites[0];
            string name = $"{spec.Width:F0}x{spec.Depth:F0} m {site.Phase} {site.BuiltStoreys}/{site.TargetStoreys}";
            var shell = SiteShellBuilder.Shell(tile, site, null);
            if (SiteShellBuilder.Build(tile, sites, null, mesh: true, collision: true) is not { } built)
            {
                Check(false, $"{name}: built");
                continue;
            }
            // the tile's frame is this world's: the flat plane is at 0, and so is the building's base
            var body = new StaticBody3D { Name = $"Shell{k}" };
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = built.Faces, BackfaceCollision = true } });
            AddChild(body);
            if (built.Mesh is { } data)
                AddChild(new MeshInstance3D { Mesh = ChunkNode.ToPropMesh(data, Styles.StyleKit.Material(Styles.MaterialRole.Prop)) });
            Check(shell.Route.Count >= 2, $"{name}: a route of {shell.Route.Count} points, {shell.Levels.Count - 1} storeys; "
                + $"{shell.Boxes.Count} boxes ({shell.Boxes.Count(x => x.Solid)} solid), {(built.Mesh?.Indices.Length ?? 0) / 3} triangles drawn, "
                + $"{built.Faces.Length / 3} collision triangles");

            var route = shell.Route.Select(p => SiteShellBuilder.ToTile(site, p)).ToList();
            var walker = new CharacterBody3D
            {
                Name = $"Walker{k}",
                FloorMaxAngle = Mathf.DegToRad(52f),
                FloorSnapLength = 0.4f,
                Position = route[0] + Vector3.Up * (Tall / 2 + 0.05f),
            };
            walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = Radius, Height = Tall } });
            AddChild(walker);
            _walkers.Add(new Walker { Name = name, Body = walker, Route = route });
        }
        _expected = Specs.Length;
        // the office galleries (#827): a small plot (one stack) and a big block (two)
        foreach (var (w, d, h, floors, at) in new[] { (14f, 11f, 6.3f, (byte)2, new Vector2(150, 450)), (52f, 30f, 18.4f, (byte)6, new Vector2(450, 450)) })
        {
            _expected++;
            var b = Prism(at, w, d, h, floors, 0.3f);
            var box = Interiors.PlanBox.Of(b)!.Value;
            ConstructionSite? site = null;
            // the first street direction round the plot that leaves the planner room for the gallery
            for (int k = 0; k < 16 * 32 && site?.GallerySide is null or 0; k++)
                site = ConstructionSites.Plan($"{2600 + k / 16}_1200_0", b, box, at + Vector2.FromAngle(k % 16 * Mathf.Tau / 16) * (d + 14f), _ => false);
            string name = $"the gallery of a {w:F0}x{d:F0} m site's office";
            if (site == null || site.GallerySide == 0) { Check(false, $"{name}: no street direction gives it room"); continue; }
            var tile = new BuildingTile { Id = id, Buildings = new List<Building> { b } };
            if (SiteShellBuilder.Build(tile, new[] { site }, null, mesh: true, collision: true) is not { } built
                || SiteShellBuilder.Office(tile, site, null)?.Gallery is not { } gallery)
            {
                Check(false, $"{name}: built");
                continue;
            }
            var body = new StaticBody3D { Name = $"Gallery{_expected}" };
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = [.. built.Faces, .. built.OfficeFaces], BackfaceCollision = true } });
            AddChild(body);
            if (built.Mesh is { } data)
                AddChild(new MeshInstance3D { Mesh = ChunkNode.ToPropMesh(data, Styles.StyleKit.Material(Styles.MaterialRole.Prop)) });
            var route = new List<Vector3> { gallery.Foot, gallery.Top };
            route.AddRange(gallery.Doorsteps);
            route.Add(gallery.Top);
            route.Add(gallery.Foot);
            var tilePoints = route.Select(p => SiteShellBuilder.ToTile(site, p)).ToList();
            Check(true, $"{name}: side {site.GallerySide}, {gallery.Steps} steps, {gallery.Doorsteps.Count} upper door(s), a route of {tilePoints.Count} points");
            var walker = new CharacterBody3D
            {
                Name = $"GalleryWalker{_expected}",
                FloorMaxAngle = Mathf.DegToRad(52f),
                FloorSnapLength = 0.4f,
                Position = tilePoints[0] + Vector3.Up * (Tall / 2 + 0.05f),
            };
            walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = Radius, Height = Tall } });
            AddChild(walker);
            _walkers.Add(new Walker { Name = name, Body = walker, Route = tilePoints });
        }
        if (_walkers.Count == 0) Finish();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_walkers.Count == 0) return;
        foreach (var w in _walkers)
        {
            if (w.Done) continue;
            var target = w.Route[w.Next];
            var at = w.Body.GlobalPosition;
            var flat = new Vector2(target.X - at.X, target.Z - at.Z);
            float feet = at.Y - Tall / 2;
            if (flat.Length() < Reach)
            {
                // arrived: at the height the route says, so no slab was fallen through or climbed onto
                if (Math.Abs(feet - target.Y) > 0.45f)
                {
                    Check(false, $"{w.Name}: waypoint {w.Next} reached with feet at {feet:F2} m, expected {target.Y:F2} m");
                    w.Done = true;
                    continue;
                }
                w.Next++;
                w.OnLeg = 0;
                if (w.Next == w.Route.Count)
                {
                    w.Done = true;
                    Check(true, $"{w.Name}: walked {(w.Name.StartsWith("the gallery") ? "up, to every upper door and back down" : "to the top slab")}, feet at {feet:F2} m (last waypoint {target.Y:F2} m)");
                }
                continue;
            }
            w.OnLeg += delta;
            if (w.OnLeg > StuckAfter)
            {
                Check(false, $"{w.Name}: stuck before waypoint {w.Next} of {w.Route.Count} at {at} (feet {feet:F2} m, aiming at {target})");
                w.Done = true;
                continue;
            }
            var dir = flat.Normalized() * Math.Min(Pace, flat.Length() / (float)delta);
            var v = w.Body.Velocity;
            v.X = dir.X;
            v.Z = dir.Y;
            v.Y = w.Body.IsOnFloor() ? 0f : v.Y - 9.81f * (float)delta;
            w.Body.Velocity = v;
            w.Body.MoveAndSlide();
        }
        if (_walkers.All(w => w.Done)) Finish();
    }

    private void Finish()
    {
        GD.Print($"[shellwalk] RESULT: {(_failures == 0 && _walkers.Count == _expected ? "ok" : $"FAILED ({_failures})")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
        _walkers.Clear();
    }

    /// <summary>A flat-topped prism with its base at 0, as swissBUILDINGS3D draws an <c>Im Bau</c> volume.</summary>
    private static Building Prism(Vector2 c, float w, float d, float h, byte floors, float turn)
    {
        var u = Vector2.FromAngle(turn);
        var v = new Vector2(-u.Y, u.X);
        var corners = new[] { c - u * w / 2 - v * d / 2, c + u * w / 2 - v * d / 2, c + u * w / 2 + v * d / 2, c - u * w / 2 + v * d / 2 };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
        for (int i = 0; i < 4; i++)
        {
            Tri(P(corners[i], 0), P(corners[(i + 1) % 4], 0), P(corners[(i + 1) % 4], h));
            Tri(P(corners[i], 0), P(corners[(i + 1) % 4], h), P(corners[i], h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = BuildingKind.UnderConstruction, Floors = floors, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }
}
