using Godot;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// Draws what a player holds, on every copy of that player.
///
/// <para>
/// Two placements. In the local player's first person there is no figure to hold anything, so
/// the item is a <b>viewmodel</b> parented to the camera, low and to the right, swaying back into
/// place when the view turns. Everywhere else — third person, and every remote player — it rides
/// on <see cref="FootPlayer.HandLocal"/>, the figure's right wrist taken from the same rig the
/// body mesh is posed from, so it swings with the arm.
/// </para>
///
/// <para>
/// Reads <see cref="FootPlayer.HeldItemId"/> rather than the inventory: that property is the one
/// that is replicated, so the local and the remote copies go through exactly the same path.
/// </para>
/// </summary>
public partial class HeldItemVisual : Node3D
{
    /// <summary>Where a viewmodel rests in camera space: low, right, and far enough out not to clip the near plane.</summary>
    private static readonly Vector3 ViewmodelRest = new(0.27f, -0.25f, -0.66f);

    private readonly FootPlayer _player;
    private MeshInstance3D _inHand = null!;
    private MeshInstance3D? _viewmodel;
    private ItemId _shown = ItemId.None;

    private Basis _lastCamera = Basis.Identity;
    private Vector3 _sway;
    private float _raise;   // 0 lowered .. 1 at rest; eased in when the item changes

    /// <summary>Set while the item is at the eye (binoculars) or a photo is being taken: nothing to draw.</summary>
    public bool Suppressed { get; set; }

    /// <summary>A short push toward the camera, 0..1 — the recoil of a shutter or a bite.</summary>
    public float Kick { get; set; }

    public HeldItemVisual(FootPlayer player) => _player = player;

    public HeldItemVisual() : this(null!) { }

    public override void _Ready()
    {
        _inHand = new MeshInstance3D
        {
            Name = "InHand",
            MaterialOverride = ItemDefs.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_inHand);
    }

    public override void _Process(double delta)
    {
        if (_player == null) return;
        float dt = (float)delta;

        var id = (ItemId)_player.HeldItemId;
        bool onFoot = _player.Ride == RideKind.OnFoot;
        if (id != _shown)
        {
            _shown = id;
            var mesh = ItemDefs.HandMesh(id);
            _inHand.Mesh = mesh;
            if (_viewmodel != null) _viewmodel.Mesh = mesh;
            _raise = 0f;
        }
        bool any = id != ItemId.None && onFoot && !Suppressed;

        // --- on the figure ---
        if (_player.HandLocal is { } hand && any)
        {
            _inHand.Visible = true;
            // the wrist is the end of the arm, so the grip sits a hand's length past it
            _inHand.Transform = new Transform3D(hand.Basis, hand.Origin + hand.Basis * new Vector3(0, -0.05f, -0.03f));
        }
        else _inHand.Visible = false;

        // --- in front of the local camera ---
        if (!_player.IsMultiplayerAuthority()) return;
        bool firstPerson = _player.IsFirstPerson || _player.ScopeView;
        EnsureViewmodel();
        if (_viewmodel == null) return;

        _viewmodel.Visible = any && firstPerson;
        if (!_viewmodel.Visible)
        {
            _raise = 0f;
            return;
        }

        // Sway: the item trails a turn by the angle the camera just swept, then springs back.
        // It is the whole difference between something held and something painted on the lens.
        var cam = _player.Camera.GlobalTransform.Basis;
        var turn = _lastCamera.Inverse() * cam;
        _lastCamera = cam;
        var euler = turn.GetEuler();
        var target = new Vector3(-euler.Y, euler.X, 0) * 0.35f;
        target = target.LimitLength(0.06f);
        _sway = _sway.Lerp(target, 1f - Mathf.Exp(-10f * dt));

        _raise = Mathf.MoveToward(_raise, 1f, dt * 4f);
        Kick = Mathf.MoveToward(Kick, 0f, dt * 5f);
        float lowered = (1f - _raise * _raise) * 0.25f;

        _viewmodel.Position = ViewmodelRest + _sway
            + new Vector3(0, -lowered, Kick * 0.06f);
        // tipped a little toward the centre of the screen, the way a hand actually holds it
        _viewmodel.Rotation = new Vector3(Kick * 0.3f, 0.12f, 0);
    }

    private void EnsureViewmodel()
    {
        if (_viewmodel != null && IsInstanceValid(_viewmodel)) return;
        var camera = _player.Camera;
        if (camera == null) return;

        _viewmodel = new MeshInstance3D
        {
            Name = "Viewmodel",
            Mesh = ItemDefs.HandMesh(_shown),
            MaterialOverride = ItemDefs.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        camera.AddChild(_viewmodel);
        _lastCamera = camera.GlobalTransform.Basis;
    }

    public override void _ExitTree()
    {
        if (_viewmodel != null && IsInstanceValid(_viewmodel)) _viewmodel.QueueFree();
        _viewmodel = null;
    }
}
