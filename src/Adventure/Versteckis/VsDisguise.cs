using Godot;
using UnitSport.Player;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// A body's disguise (#1268, plan section 3), on every peer that draws the body: a <see cref="Node3D"/> child of the
/// <see cref="FootPlayer"/>, made on its first disguise, holding one <see cref="MeshInstance3D"/> for the prop (and the
/// cow's head on its neck joint). Disguising swaps the shared mesh in (<see cref="VsPropMeshes"/>: a reference, never a
/// build) and hides the figure, the held item and the name tag; undisguising shows them again. Walking tilts the prop
/// up to 6° with the speed (a transform, set only when it changes); standing still settles it. Docs:
/// <c>docs/notes/adventure/versteckis.md</c>.
/// </summary>
public partial class VsDisguise : Node3D
{
    public const string NodeName = "VsDisguise";
    private const float MaxTilt = 0.105f;   // 6°

    private FootPlayer _body = null!;
    private MeshInstance3D _prop = null!;
    private MeshInstance3D? _head;
    private double _check;
    private Node3D? _held;
    private Node3D? _tag;

    /// <summary>The prop drawn now, <see cref="VsProp.None"/> when the body is itself.</summary>
    public VsProp Kind { get; private set; }

    /// <summary>The mesh the prop shows (checks: the shared mesh, never a new one).</summary>
    public Mesh? PropMesh => _prop.Mesh;

    /// <summary>Meshes this node has had to build (always 0: every prop is a shared mesh; <c>--versteckischeck</c>).</summary>
    public static int Built { get; private set; }

    /// <summary>The body's disguise node, made on first use.</summary>
    public static VsDisguise For(FootPlayer body)
    {
        if (body.GetNodeOrNull<VsDisguise>(NodeName) is { } d) return d;
        d = new VsDisguise { Name = NodeName, _body = body };
        d._prop = new MeshInstance3D { Name = "Prop", MaterialOverride = Rewards.RewardMeshes.Material(), Visible = false };
        d.AddChild(d._prop);
        body.AddChild(d);
        return d;
    }

    /// <summary>The body's disguise node if it has one.</summary>
    public static VsDisguise? Of(FootPlayer body) => body.GetNodeOrNull<VsDisguise>(NodeName);

    /// <summary>Puts on <paramref name="kind"/> (a mesh reference swap), or takes it off with <see cref="VsProp.None"/>.</summary>
    public void Set(VsProp kind)
    {
        if (kind == Kind) return;
        Kind = kind;
        _prop.Mesh = VsPropMeshes.Of(kind);
        _prop.Visible = kind != VsProp.None;
        if (kind == VsProp.Cow)
        {
            if (_head == null)
            {
                _head = new MeshInstance3D { Name = "Head", Mesh = VsPropMeshes.CowHead(), Position = Farming.CowMeshBuilder.Neck,
                    MaterialOverride = Rewards.RewardMeshes.Material() };
                _prop.AddChild(_head);
            }
            _head.Visible = true;
        }
        else if (_head != null) _head.Visible = false;
        ShowBody(kind == VsProp.None);
        _check = 0;
    }

    /// <summary>Hides (or shows) everything that gives the body away: the figure, what it holds, its name over its head.</summary>
    private void ShowBody(bool show)
    {
        if (_body.Visual is { } v && v.Visible != show) v.Visible = show;
        _held ??= _body.GetNodeOrNull<Node3D>("HeldItem");
        if (_held != null && _held.Visible != show) _held.Visible = show;
        if (_tag == null)
            foreach (var child in _body.GetChildren())
                if (child is Label3D or Bots.BotTag) { _tag = child as Node3D; break; }
        if (_tag != null && _tag.Visible != show) _tag.Visible = show;
    }

    public override void _Process(double delta)
    {
        if (Kind == VsProp.None) return;
        // the figure is rebuilt on a new look or ride: hidden again within a quarter second
        if ((_check -= delta) <= 0)
        {
            _check = 0.25;
            ShowBody(false);
        }
        // a lean with the speed, about the axis across the motion, in the body's frame
        var v = _body.IsMultiplayerAuthority() ? _body.Velocity : _body.NetVel;
        var flat = new Vector3(v.X, 0, v.Z);
        float speed = flat.Length();
        Basis want = Basis.Identity;
        if (speed > 0.3f)
        {
            var local = _body.GlobalTransform.Basis.Inverse() * flat;
            var axis = new Vector3(local.Z, 0, -local.X).Normalized();
            if (axis.LengthSquared() > 0.5f) want = new Basis(axis, Mathf.Min(MaxTilt, speed * 0.03f));
        }
        if (!_prop.Basis.IsEqualApprox(want)) _prop.Basis = want;
    }
}
