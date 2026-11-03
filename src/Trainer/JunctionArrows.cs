using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Gpx;
using UnitSport.Player;

namespace UnitSport.Trainer;

/// <summary>
/// The next junction's roads as arrows (#901), the way a cycling game signs them: a row standing over the
/// junction, facing the rider coming in, one per road, leftmost on the left, each turned to its road's bearing
/// (straight on points up, a left turn left). The road the way takes is lit orange, the others are pale.
/// Set only when the junction or the road taken changes (<see cref="Show"/>), never per frame.
/// </summary>
public sealed partial class JunctionArrows : Node3D
{
    /// <summary>Height of the row over the junction's road, metres: over the rider's head, under any bridge.</summary>
    private const float Height = 3.6f;
    /// <summary>Between two arrows of the row, metres.</summary>
    private const float Spacing = 3.6f;
    /// <summary>The mesh at this scale: a 3 m arrow, read 100 m out.</summary>
    private const float Scale = 1f;

    private static readonly ArrayMesh Arrow = Mesh();
    private static readonly StandardMaterial3D Taken = Lit(new Color(1f, 0.55f, 0.1f));
    private static readonly StandardMaterial3D Other = Lit(new Color(1f, 1f, 1f, 0.6f));

    private readonly List<MeshInstance3D> _arrows = new();
    private (int Node, int Taken) _shown = (-1, -1);

    public JunctionArrows() => Name = "JunctionArrows";

    /// <summary>The arrows showing now and the way each points, in the world (a check reads them).</summary>
    public IEnumerable<(Vector3 At, Vector3 Points, bool Taken)> Showing => _arrows.Where(a => a.Visible)
        .Select(a => (a.GlobalPosition, (a.GlobalBasis * Vector3.Forward).Normalized(), a.MaterialOverride == Taken));

    /// <summary>The arrows of <paramref name="j"/> on graph <paramref name="g"/>, placed with <paramref name="origin"/>; null hides them.</summary>
    public void Show(Junction? j, IWalkGraph? g, WorldOrigin origin)
    {
        var key = j == null || g == null ? (-1, -1) : (j.Node, j.Taken);
        if (key == _shown) return;
        _shown = key;
        foreach (var a in _arrows) a.Visible = false;
        if (j == null || g == null) return;
        var (je, jn, jh) = g.Pos(j.Node);
        var centre = origin.ToWorld(je, jn, jh) + Vector3.Up * Height;
        // facing the rider: local -Z along the way in, so local -X is the rider's left
        var travel = RaceRoute.Flat(origin.ToWorld(je + j.InE, jn + j.InN, jh) - origin.ToWorld(je, jn, jh));
        var face = new Basis(Vector3.Up, Mathf.Atan2(-travel.X, -travel.Z));
        for (int i = 0; i < j.Branches.Count; i++)
        {
            var b = j.Branches[i];
            if (i >= _arrows.Count)
            {
                var m = new MeshInstance3D { Mesh = Arrow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(m);
                _arrows.Add(m);
            }
            var arrow = _arrows[i];
            arrow.MaterialOverride = b.Taken ? Taken : Other;
            // stood up (the mesh's -Z to +Y), then rolled to the bearing in the sign's plane (negative: left)
            var stand = new Basis(Vector3.Right, Mathf.Pi / 2);
            var roll = new Basis(Vector3.Back, Mathf.DegToRad(-(float)b.Bearing));
            float across = (i - (j.Branches.Count - 1) / 2f) * Spacing;
            arrow.GlobalTransform = new Transform3D(face * roll * stand * Basis.FromScale(Vector3.One * Scale),
                centre + face * new Vector3(across, 0, 0));
            arrow.Visible = true;
        }
    }

    /// <summary>The arrows follow the world when its origin moves: placed again on the next <see cref="Show"/>.</summary>
    public void Forget() => _shown = (-1, -1);

    private static StandardMaterial3D Lit(Color c) => new()
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = c.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
    };

    /// <summary>
    /// A flat chevron arrow, 3 m long, pointing along -Z: a shaft and a two-bar head, low-poly boxes. Authored
    /// pointing +Z, as <see cref="MeshScratch"/> authors (its build turns a half turn about Y to Godot's -Z).
    /// </summary>
    [Showcase("Training", "Junction arrow")]
    private static ArrayMesh Mesh()
    {
        var s = new MeshScratch();
        var white = new Color(1, 1, 1);
        s.Box(new Vector3(0, 0, -0.45f), new Vector3(0.5f, 0.1f, 2.0f), white);
        foreach (int side in new[] { -1, 1 })
            s.Box(new Vector3(side * 0.42f, 0, 0.75f), new Vector3(0.45f, 0.1f, 1.5f), white,
                new Basis(Vector3.Up, -side * Mathf.Pi / 4));   // each bar's back end out to its side
        return s.Build();
    }
}
