using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// A figure drawn from its look's skinned mesh (#1202, docs/notes/avatar/skinned-figures.md): the
/// mesh is built once per look (<see cref="HumanMeshBuilder.Skin"/>) and a pose only sets its bones
/// (<see cref="HumanMeshBuilder.PoseStride"/> and the other <c>Pose…</c>), which the GPU deforms it
/// by. The bones are a rendering-server skeleton of its own, attached to this instance and to any
/// copy that should move with it (<see cref="Share"/>: the own body's shadow twin, a doorway's ghost).
/// </summary>
public partial class SkinnedFigure : MeshInstance3D
{
    private static readonly StringName FlutterParam = "skirt_flutter";

    private Rid _skeleton;
    private readonly Transform3D[] _bones = new Transform3D[HumanMeshBuilder.MaxBones];
    private int _boneCount;
    private bool _posed;
    private Vector2 _flutter;

    /// <summary>The look it draws; null until <see cref="Show"/>.</summary>
    public FigureSkin? Skin { get; private set; }

    /// <summary>How many times its bones were set (a check counts them; nothing rebuilds a mesh for it).</summary>
    public int Poses { get; private set; }

    /// <summary>Draws <paramref name="skin"/>: its mesh, posed by this figure's bones (held as they were).</summary>
    public void Show(FigureSkin skin)
    {
        if (Skin == skin) return;
        Skin = skin;
        Mesh = skin.Mesh;
        var leader = Leader;
        leader.EnsureSkeleton(skin.BoneCount);
        leader.Attach(this);
        if (!skin.Flutters && _flutter != Vector2.Zero) SetFlutter(Vector2.Zero);
    }

    /// <summary>The figure whose bones this one moves by: itself, or the one it was <see cref="Share"/>d by.</summary>
    private SkinnedFigure Leader => _follow is { } f && IsInstanceValid(f) ? f : this;

    /// <summary>Shows the look of <paramref name="look"/> (<see cref="HumanMeshBuilder.Skin"/>).</summary>
    public void Show(in FigureLook look) => Show(HumanMeshBuilder.Skin(look));

    /// <summary>
    /// Sets the bones from frames (author space, <see cref="HumanMeshBuilder"/>'s): each bone moves
    /// its vertices from the rest frame to this one. Only bones that moved go to the server.
    /// </summary>
    internal void Pose(ReadOnlySpan<Transform3D> frames)
    {
        if (Leader is var leader && leader != this) { leader.Pose(frames); return; }
        if (Skin is not { } skin) return;
        EnsureSkeleton(skin.BoneCount);
        for (int i = 0; i < skin.BoneCount; i++)
        {
            // author space (+Z forward) to the mesh's (−Z): a half turn either side
            var m = Flip * (frames[i] * skin.BindInverse[i]) * Flip;
            if (_posed && m.IsEqualApprox(_bones[i])) continue;
            _bones[i] = m;
            RenderingServer.SkeletonBoneSetTransform(_skeleton, i, m);
        }
        _posed = true;
        Poses++;
    }

    /// <summary>The lowest a skirt hangs, model space (the shader's <c>skirt_floor</c>), set only when it changes.</summary>
    public void SetFloor(float floor)
    {
        if (floor == _floor) return;
        _floor = floor;
        SetInstanceShaderParameter(FloorParam, floor);
    }

    private float _floor = float.NaN;
    private static readonly StringName FloorParam = "skirt_floor";

    /// <summary>The hem's ripple and phase for the figure's shader (<see cref="HumanMeshBuilder.SkirtFlutter"/>), set only when it changes.</summary>
    public void SetFlutter(Vector2 flutter)
    {
        if (flutter == _flutter) return;
        _flutter = flutter;
        SetInstanceShaderParameter(FlutterParam, flutter);
    }

    /// <summary>
    /// Moves <paramref name="copy"/> (drawing this figure's mesh) by this figure's bones: the own
    /// body's shadow twin, a ghost through a doorway. A copy that is itself a <see cref="SkinnedFigure"/>
    /// keeps following across being taken out of the tree and put back.
    /// </summary>
    public void Share(MeshInstance3D copy)
    {
        var leader = Leader;
        if (copy == leader) return;
        leader.EnsureSkeleton(leader.Skin?.BoneCount ?? HumanMeshBuilder.BodyBones);
        if (copy is SkinnedFigure f) f._follow = leader;
        leader.Attach(copy);
        if (Skin is { Flutters: true }) copy.SetInstanceShaderParameter(FlutterParam, _flutter);
    }

    /// <summary>Stops <paramref name="copy"/> following any figure's bones (it draws another mesh now).</summary>
    public static void Unshare(MeshInstance3D copy)
    {
        if (copy is SkinnedFigure f) f._follow = null;
        if (copy.IsInsideTree()) RenderingServer.InstanceAttachSkeleton(copy.GetInstance(), default);
    }

    private SkinnedFigure? _follow;

    /// <summary>The figure whose bones this copy moves by (<see cref="Share"/>), or null.</summary>
    public SkinnedFigure? Following => _follow;

    private static readonly Transform3D Flip = new(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero);

    /// <summary>A skeleton of at least <paramref name="bones"/> (a look with a skirt has more), its bones at rest.</summary>
    private void EnsureSkeleton(int bones)
    {
        if (_skeleton.IsValid && _boneCount >= bones) return;
        if (!_skeleton.IsValid) _skeleton = RenderingServer.SkeletonCreate();
        _boneCount = bones;
        RenderingServer.SkeletonAllocateData(_skeleton, bones);
        for (int i = 0; i < bones; i++)
        {
            _bones[i] = Transform3D.Identity;
            RenderingServer.SkeletonBoneSetTransform(_skeleton, i, Transform3D.Identity);
        }
        _posed = false;
    }

    private void Attach(MeshInstance3D to)
    {
        if (to.IsInsideTree()) RenderingServer.InstanceAttachSkeleton(to.GetInstance(), _skeleton);
    }

    public override void _Notification(int what)
    {
        // a MeshInstance3D attaches its skeleton path's (none) when it enters the tree; a script's
        // notification comes after the engine's, so the bones are put back here
        if (what == NotificationEnterTree)
        {
            if (_follow is { } leader && IsInstanceValid(leader)) leader.Attach(this);
            else if (Skin != null && _skeleton.IsValid) Attach(this);
        }
        else if (what == NotificationPredelete && _skeleton.IsValid)
        {
            RenderingServer.FreeRid(_skeleton);
            _skeleton = default;
        }
    }
}
