using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// Anything near an open doorway is drawn on both sides of it, the way Portal did it: the real
/// thing where it is, and a copy carried across by the door's map. Something straddling the sill
/// is then whole from either side: the part past the doorway's plane is hidden behind the doorway
/// quad, and the portal picture shows the copy's same part instead. Client only.
///
/// <para>
/// Generic: any <see cref="Node3D"/> in the <see cref="Group"/> group takes part (players,
/// vehicles, and whatever joins later), and every visible <see cref="MeshInstance3D"/> under it is
/// copied, mesh and materials and all, every frame it is near a doorway. Its side is read from
/// where it is (interiors are 3 km down), so remote copies need nothing replicated for it.
/// </para>
/// </summary>
public partial class DoorwayGhosts : Node3D
{
    /// <summary>The group a node joins to be drawn on both sides of a doorway it is near.</summary>
    public const string Group = "doorway_travellers";

    /// <summary>How far from the doorway's plane a thing's own size is counted from.</summary>
    private const float Reach = 0.8f;

    private readonly Func<IEnumerable<DoorLink>> _links;
    private readonly Func<Vector3, string?> _planAt;
    private readonly Dictionary<Node3D, List<MeshInstance3D>> _ghosts = new();
    private readonly List<MeshInstance3D> _meshes = new();

    public DoorwayGhosts(Func<IEnumerable<DoorLink>> links, Func<Vector3, string?> planAt)
    {
        _links = links;
        _planAt = planAt;
    }

    public override void _Process(double delta)
    {
        var seen = new HashSet<Node3D>();
        var links = _links().Where(l => l.Swing > 0f).ToList();
        if (links.Count > 0)
            foreach (var node in GetTree().GetNodesInGroup(Group))
            {
                if (node is not Node3D thing || !thing.IsVisibleInTree()) continue;
                var at = thing.GlobalPosition;
                bool inside = at.Y < InteriorManager.InteriorBaseY + 1000f;
                string? plan = inside ? _planAt(at) : null;
                foreach (var link in links)
                {
                    if (inside && link.Plan != plan) continue;
                    var frame = inside ? link.Inside : link.Outside;
                    var local = frame.AffineInverse() * at;
                    // a cheap test first; then the thing's own size decides
                    if (local.Length() > 8f) continue;
                    _meshes.Clear();
                    Collect(thing, _meshes);
                    if (_meshes.Count == 0 || !Near(frame, link, _meshes)) continue;
                    Draw(thing, inside ? link.ToOutside : link.ToInside);
                    seen.Add(thing);
                    break;
                }
            }
        foreach (var (thing, ghosts) in _ghosts.ToList())
            if (!seen.Contains(thing))
            {
                foreach (var g in ghosts) g.QueueFree();
                _ghosts.Remove(thing);
            }
    }

    /// <summary>Whether any of the thing's meshes comes within <see cref="Reach"/> of the doorway.</summary>
    private static bool Near(Transform3D frame, DoorLink link, List<MeshInstance3D> meshes)
    {
        var toFrame = frame.AffineInverse();
        foreach (var m in meshes)
        {
            var box = m.GetAabb();
            var centre = toFrame * (m.GlobalTransform * box.GetCenter());
            float radius = box.Size.Length() * 0.5f * m.GlobalBasis.Scale.X;
            if (Mathf.Abs(centre.Z) > Reach + radius) continue;
            if (Mathf.Abs(centre.X) > Math.Max(link.OutsideWidth, link.InsideWidth) / 2 + radius) continue;
            if (centre.Y < -1.5f - radius || centre.Y > link.PassHeight + radius) continue;
            return true;
        }
        return false;
    }

    private static void Collect(Node node, List<MeshInstance3D> into)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is MeshInstance3D m && m.Visible && m.Mesh != null && (m.Layers & DoorPortals.QuadLayer) == 0)
                into.Add(m);
            if (child is Node3D { Visible: false }) continue;
            Collect(child, into);
        }
    }

    /// <summary>The copies of this frame's meshes, carried across by <paramref name="map"/>.</summary>
    private void Draw(Node3D thing, Transform3D map)
    {
        if (!_ghosts.TryGetValue(thing, out var ghosts)) _ghosts[thing] = ghosts = new List<MeshInstance3D>();
        while (ghosts.Count < _meshes.Count)
        {
            var g = new MeshInstance3D { Name = "Ghost", TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(g);
            ghosts.Add(g);
        }
        for (int i = 0; i < ghosts.Count; i++)
        {
            var g = ghosts[i];
            if (i >= _meshes.Count) { g.Visible = false; continue; }
            var m = _meshes[i];
            g.Visible = true;
            if (g.Mesh != m.Mesh)
            {
                g.Mesh = m.Mesh;
                for (int s = 0; s < m.GetSurfaceOverrideMaterialCount(); s++)
                    g.SetSurfaceOverrideMaterial(s, m.GetSurfaceOverrideMaterial(s));
            }
            g.MaterialOverride = m.MaterialOverride;
            g.Layers = m.Layers;
            g.GlobalTransform = map * m.GlobalTransform;
        }
    }
}
