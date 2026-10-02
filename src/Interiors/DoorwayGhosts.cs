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
public partial class DoorwayGhosts : Node3D, Core.IOriginContainer
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

    // The group is scanned at 10 Hz for things roughly near an open doorway (#221); only those
    // get the exact per-frame test. The scan's extra reach covers 40 m/s between two scans.
    private const float ScanPeriod = 0.1f, Near8 = 8f, ScanReach = Near8 + 4f;
    private float _scanIn;
    private int _openCount;
    private readonly List<DoorLink> _open = new();
    private readonly List<Node3D> _candidates = new();
    private readonly HashSet<Node3D> _seen = new();
    private readonly List<Node3D> _gone = new();

    public override void _Process(double delta)
    {
        _seen.Clear();
        _open.Clear();
        foreach (var l in _links()) if (l.Swing > 0f) _open.Add(l);
        _scanIn -= (float)delta;
        if (_open.Count != _openCount || _scanIn <= 0f) Scan();
        if (_open.Count > 0)
            foreach (var thing in _candidates)
            {
                if (!IsInstanceValid(thing) || !thing.IsVisibleInTree()) continue;
                var at = thing.GlobalPosition;
                bool inside = at.Y < InteriorManager.InteriorBaseY + 1000f;
                string? plan = inside ? _planAt(at) : null;
                foreach (var link in _open)
                {
                    if (inside && link.Plan != plan) continue;
                    var frame = inside ? link.Inside : link.Outside;
                    var local = frame.AffineInverse() * at;
                    // a cheap test first; then the thing's own size decides
                    if (local.Length() > Near8) continue;
                    _meshes.Clear();
                    Collect(thing, _meshes);
                    if (_meshes.Count == 0 || !Near(frame, link, _meshes)) continue;
                    Draw(thing, inside ? link.ToOutside : link.ToInside);
                    _seen.Add(thing);
                    break;
                }
            }
        _gone.Clear();
        foreach (var thing in _ghosts.Keys)
            if (!_seen.Contains(thing)) _gone.Add(thing);
        foreach (var thing in _gone)
        {
            foreach (var g in _ghosts[thing]) g.QueueFree();
            _ghosts.Remove(thing);
        }
    }

    private void Scan()
    {
        _scanIn = ScanPeriod;
        _openCount = _open.Count;
        _candidates.Clear();
        if (_open.Count == 0) return;
        foreach (var node in GetTree().GetNodesInGroup(Group))
        {
            if (node is not Node3D thing) continue;
            var at = thing.GlobalPosition;
            bool inside = at.Y < InteriorManager.InteriorBaseY + 1000f;
            foreach (var link in _open)
                if ((inside ? link.Inside : link.Outside).Origin.DistanceTo(at) < ScanReach)
                {
                    _candidates.Add(thing);
                    break;
                }
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
            // the first-person viewmodel (and its print, screen) belongs to the screen's camera only
            if (child is VisualInstance3D v && (v.Layers & Items.HeldItemVisual.ViewmodelLayer) != 0) continue;
            if (child is MeshInstance3D m && m.Visible && m.Mesh != null && (m.Layers & DoorPortals.AllQuadLayers) == 0)
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
