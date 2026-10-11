using Godot;
using UnitSport.Core;

namespace UnitSport.Movie;

/// <summary>
/// The recorded world on stage (#775): one visual-only node per recorded object of a
/// <see cref="WorldTrack"/>, posed every frame from the track at the movie's time. No physics, no
/// network, no game: just the meshes as they were drawn. Group nodes are top level and placed from
/// LV95 every frame, so an origin shift needs nothing.
/// </summary>
public partial class WorldGhosts : Node3D
{
    private readonly WorldOrigin _origin;
    private WorldTrack? _track;
    private Node3D?[] _nodes = Array.Empty<Node3D?>();
    private int[] _looks = Array.Empty<int>();

    public WorldGhosts(WorldOrigin origin)
    {
        Name = "WorldGhosts";
        _origin = origin;
    }

    /// <summary>Objects drawn at the last <see cref="Show"/> (for the checks).</summary>
    public int Drawn { get; private set; }

    /// <summary>The ghost of the track's entity <paramref name="i"/>, if it was ever drawn (for the checks).</summary>
    public Node3D? NodeOf(int i) => i >= 0 && i < _nodes.Length ? _nodes[i] : null;

    /// <summary>Nothing of the recorded world on screen.</summary>
    public void Clear() => Use(null);

    /// <summary>
    /// <paramref name="track"/> as it was at its local time <paramref name="t"/>, or nothing when
    /// <paramref name="track"/> is null.
    /// </summary>
    public void Show(WorldTrack? track, double t)
    {
        if (track != _track) Use(track);
        Drawn = 0;
        if (track == null) return;
        var ents = track.Entities;
        // groups first: a part needs its group's node to hang from
        for (int i = 0; i < ents.Length; i++)
        {
            var e = ents[i];
            if (e.IsEnvironment)
            {
                if (e.AliveAt(t)) { var (hour, sea, snow) = e.EnvironmentAt(t); MovieEnvironment.Show(hour, sea, snow); }
                continue;
            }
            if (e.Parent >= 0) continue;
            bool alive = e.AliveAt(t);
            if (!alive) { Hide(i); continue; }
            var node = _nodes[i] ??= MakeGroup();
            node.Visible = true;
            var p = e.PoseAt(t);
            node.GlobalTransform = new Transform3D(new Basis(p.Rot).Scaled(p.Scale), _origin.ToWorld(p.X, p.Y, p.Z));
        }
        for (int i = 0; i < ents.Length; i++)
        {
            var e = ents[i];
            if (e.Parent < 0) continue;
            var group = _nodes[e.Parent];
            if (!e.AliveAt(t) || group == null || !group.Visible || !e.ShownAt(t)) { Hide(i); continue; }
            int look = e.LookAt(t);
            var wears = look >= 0 ? track.Looks[look] : null;
            if (look != _looks[i] || _nodes[i] == null)
            {
                // a light or emitter is its own copy, made again when its look changes; a mesh is re-dressed
                if (_nodes[i] != null && (wears?.Template != null || _nodes[i] is not MeshInstance3D))
                {
                    // out of the scene now: freed at the frame's end, it would still draw this frame
                    _nodes[i]!.GetParent()?.RemoveChild(_nodes[i]);
                    _nodes[i]!.QueueFree();
                    _nodes[i] = null;
                }
                _nodes[i] ??= MakePart(group, wears);
                if (_nodes[i] is MeshInstance3D m) Dress(m, wears);
                _looks[i] = look;
            }
            var part = _nodes[i]!;
            var p = e.PoseAt(t);
            part.Transform = new Transform3D(new Basis(p.Rot).Scaled(p.Scale), new Vector3((float)p.X, (float)p.Y, (float)p.Z));
            part.Visible = true;
            Drawn++;
        }
    }

    private void Hide(int i)
    {
        if (_nodes[i] is { Visible: true } n) n.Visible = false;
    }

    public override void _ExitTree() => MovieEnvironment.Restore();

    private void Use(WorldTrack? track)
    {
        MovieEnvironment.Restore();
        foreach (var n in _nodes)
            if (n != null && n.GetParent() == this) n.QueueFree();
        _track = track;
        int count = track?.Entities.Length ?? 0;
        _nodes = new Node3D?[count];
        _looks = new int[count];
        Array.Fill(_looks, int.MinValue);
    }

    private Node3D MakeGroup()
    {
        var n = new Node3D { TopLevel = true };
        AddChild(n);
        return n;
    }

    private static Node3D MakePart(Node3D group, WorldLook? look)
    {
        var n = look?.Template != null ? (Node3D)look.Template.Duplicate(0) : new MeshInstance3D();
        group.AddChild(n);
        return n;
    }

    private static void Dress(MeshInstance3D m, WorldLook? look)
    {
        if (look != null) WorldLookStore.Dress(m, look);
        else { m.Mesh = null; m.MaterialOverride = null; }
    }
}
