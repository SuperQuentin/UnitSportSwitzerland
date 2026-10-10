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
            var mesh = (MeshInstance3D)(_nodes[i] ??= MakePart(group));
            int look = e.LookAt(t);
            if (look != _looks[i]) { Dress(mesh, look >= 0 ? track.Looks[look] : null); _looks[i] = look; }
            var p = e.PoseAt(t);
            mesh.Transform = new Transform3D(new Basis(p.Rot).Scaled(p.Scale), new Vector3((float)p.X, (float)p.Y, (float)p.Z));
            mesh.Visible = true;
            Drawn++;
        }
    }

    private void Hide(int i)
    {
        if (_nodes[i] is { Visible: true } n) n.Visible = false;
    }

    private void Use(WorldTrack? track)
    {
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

    private static MeshInstance3D MakePart(Node3D group)
    {
        var m = new MeshInstance3D();
        group.AddChild(m);
        return m;
    }

    private static void Dress(MeshInstance3D m, WorldLook? look)
    {
        if (look != null) WorldLookStore.Dress(m, look);
        else { m.Mesh = null; m.MaterialOverride = null; }
    }
}
