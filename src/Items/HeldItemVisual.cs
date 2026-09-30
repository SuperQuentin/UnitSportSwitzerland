using Godot;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>Named places the viewmodel can be held; blended between smoothly, see <see cref="HeldItemVisual.SetPose"/>.</summary>
public enum ViewPose { Rest, Aim, Eye, Mouth, Plant, Inspect }

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

    /// <summary>
    /// The viewmodel is drawn at this fraction of its real size, pulled in by the same fraction
    /// (same look on screen). Halving it halves how far a shotgun pokes out in front of the
    /// camera, so it goes through walls far less — no shader or extra layer needed.
    /// </summary>
    public const float ViewScale = 0.5f;

    private readonly FootPlayer _player;
    private MeshInstance3D _inHand = null!;
    private MeshInstance3D? _viewmodel;
    private ItemId _shown = ItemId.None;
    private string? _shownData;

    // a Polaroid print sliding out of the bottom of the camera viewmodel (ShowPrint)
    private MeshInstance3D? _print;
    private float _printT;
    private static ArrayMesh? _printMesh;

    private Basis _lastCamera = Basis.Identity;
    private Vector3 _sway;
    private float _raise;   // 0 lowered .. 1 at rest; eased in when the item changes

    private ViewPose _pose = ViewPose.Rest;
    private Vector3 _curPos = ViewmodelRest, _curRot = new(0, 0.12f, 0);
    private float _blendStart = 1f;

    // one-shot animation (eat, plant...): pose weight 0..1 over in / hold / out
    private bool _shotActive;
    private ViewPose _shotPose;
    private float _shotIn, _shotHold, _shotOut, _shotT;
    private bool _shotPeaked;
    private System.Action? _shotPeak;

    /// <summary>The pose being blended toward.</summary>
    public ViewPose Pose => _pose;

    /// <summary>0 just after the pose changed .. 1 arrived.</summary>
    public float PoseBlend01 { get; private set; } = 1f;

    /// <summary>True once the current pose is reached and no one-shot is playing.</summary>
    public bool PoseSettled => PoseBlend01 >= 0.98f && !_shotActive;

    /// <summary>Blends the viewmodel toward <paramref name="pose"/> (eased, exponential damping).</summary>
    public void SetPose(ViewPose pose)
    {
        if (pose == _pose) return;
        _pose = pose;
        var (p, r) = PoseTransform(pose);
        _blendStart = Mathf.Max(0.01f, (p - _curPos).Length() + (r - _curRot).Length() * 0.2f);
        PoseBlend01 = 0f;
    }

    /// <summary>
    /// Plays a there-and-back animation to <paramref name="target"/> over the current pose:
    /// eases in for <paramref name="inTime"/>, calls <paramref name="onPeak"/> once on arrival,
    /// holds, eases out for <paramref name="outTime"/>. A new call replaces one in progress.
    /// </summary>
    public void PlayOneShot(ViewPose target, float inTime, float hold, float outTime, System.Action? onPeak = null)
    {
        _shotActive = true;
        _shotPose = target;
        _shotIn = Mathf.Max(0.01f, inTime);
        _shotHold = Mathf.Max(0f, hold);
        _shotOut = Mathf.Max(0.01f, outTime);
        _shotT = 0f;
        _shotPeaked = false;
        _shotPeak = onPeak;
    }

    /// <summary>Position and euler rotation (camera space, full scale) of a pose for the held item.</summary>
    private (Vector3 pos, Vector3 rot) PoseTransform(ViewPose pose)
    {
        var use = ItemDefs.Get(_shown)?.Use;
        return pose switch
        {
            // shotgun shouldered: the barrel line (y +0.025 above the grip) sits on the screen centre line
            ViewPose.Aim when use == ItemUse.Shoot => (new Vector3(0f, -0.045f, -0.46f), Vector3.Zero),
            ViewPose.Aim => (new Vector3(0.05f, -0.16f, -0.50f), new Vector3(0, 0.05f, 0)),
            // camera raised in front of the eye, slightly below centre; binoculars right at the eyes
            ViewPose.Eye when use == ItemUse.Optic => (new Vector3(0f, -0.03f, -0.20f), Vector3.Zero),
            ViewPose.Eye => (new Vector3(0f, -0.12f, -0.38f), Vector3.Zero),
            ViewPose.Mouth => (new Vector3(0.02f, -0.15f, -0.28f), new Vector3(0.55f, 0, 0)),
            ViewPose.Plant => (new Vector3(0.10f, -0.42f, -0.50f), new Vector3(-0.9f, 0.1f, 0)),
            ViewPose.Inspect => (new Vector3(0.02f, -0.06f, -0.36f), new Vector3(0.3f, 0.6f, 0.1f)),
            _ => (ViewmodelRest, new Vector3(0, 0.12f, 0)),
        };
    }

    /// <summary>Set while the item is at the eye (binoculars) or a photo is being taken: nothing to draw.</summary>
    public bool Suppressed { get; set; }

    /// <summary>
    /// Per-instance data of the held stack (a photo's id), for items drawn from it. Set by the
    /// local <see cref="ItemController"/> only: remote copies see the item, not which one.
    /// </summary>
    public string? HeldData { get; set; }

    /// <summary>The first-person viewmodel is on screen right now.</summary>
    public bool ViewmodelShown => _viewmodel != null && IsInstanceValid(_viewmodel) && _viewmodel.Visible;

    /// <summary>
    /// A print slides out of the bottom of the viewmodel (the Polaroid camera) and hangs there,
    /// drawn with <paramref name="material"/> (the caller animates its developing), until
    /// <see cref="HidePrint"/> or the item changes. False if there is no viewmodel on screen.
    /// </summary>
    public bool ShowPrint(Material material)
    {
        if (!ViewmodelShown) return false;
        HidePrint();
        _printMesh ??= PhotoVisuals.BuildCard(Vector3.Zero);
        _print = new MeshInstance3D
        {
            Name = "Print", Mesh = _printMesh, MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0, 0.03f, 0),
        };
        _viewmodel!.AddChild(_print);
        _printT = 0f;
        return true;
    }

    public void HidePrint()
    {
        if (_print != null && IsInstanceValid(_print)) _print.QueueFree();
        _print = null;
    }

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
        if (id != _shown || HeldData != _shownData)
        {
            if (id != _shown)
            {
                _raise = 0f;
                HidePrint();
            }
            _shown = id;
            _shownData = HeldData;
            var mesh = ItemDefs.HandMesh(id);
            var material = ItemDefs.HandMaterial(id, _shownData) ?? ItemDefs.Material;
            _inHand.Mesh = mesh;
            _inHand.MaterialOverride = material;
            if (_viewmodel != null)
            {
                _viewmodel.Mesh = mesh;
                _viewmodel.MaterialOverride = material;
            }
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

        // ease toward the pose; Rest keeps the old hand-held tilt
        var (tp, tr) = PoseTransform(_pose);
        float k = 1f - Mathf.Exp(-12f * dt);
        _curPos = _curPos.Lerp(tp, k);
        _curRot = _curRot.Lerp(tr, k);
        float remaining = (tp - _curPos).Length() + (tr - _curRot).Length() * 0.2f;
        PoseBlend01 = Mathf.Clamp(1f - remaining / _blendStart, 0f, 1f);
        if (remaining < 0.004f) PoseBlend01 = 1f;

        var pos = _curPos;
        var rot = _curRot;
        if (_shotActive)
        {
            _shotT += dt;
            float w;
            if (_shotT < _shotIn) w = _shotT / _shotIn;
            else if (_shotT < _shotIn + _shotHold) w = 1f;
            else w = 1f - (_shotT - _shotIn - _shotHold) / _shotOut;
            if (!_shotPeaked && _shotT >= _shotIn) { _shotPeaked = true; _shotPeak?.Invoke(); }
            if (_shotT >= _shotIn + _shotHold + _shotOut) { _shotActive = false; w = 0f; }
            w = Mathf.Clamp(w, 0f, 1f);
            w = w * w * (3f - 2f * w);
            var (sp, sr) = PoseTransform(_shotPose);
            pos = pos.Lerp(sp, w);
            rot = rot.Lerp(sr, w);
        }

        // less sway once the item is raised to a pose
        float swayScale = _pose == ViewPose.Rest ? 1f : 0.25f;
        _viewmodel.Scale = Vector3.One * ViewScale;
        _viewmodel.Position = (pos + _sway * swayScale + new Vector3(0, -lowered, Kick * 0.06f)) * ViewScale;
        _viewmodel.Rotation = rot + new Vector3(Kick * 0.3f, 0, 0);

        if (_print != null && IsInstanceValid(_print))
        {
            // out of the slot in 0.8 s, easing to a stop, with a slight droop as it comes free
            _printT += dt;
            float u = Mathf.Clamp(_printT / 0.8f, 0f, 1f);
            u = 1f - (1f - u) * (1f - u);
            _print.Position = new Vector3(0, Mathf.Lerp(0.03f, -0.045f, u), 0.001f);
            _print.Rotation = new Vector3(-0.12f * u, 0, 0);
        }
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
            MaterialOverride = ItemDefs.HandMaterial(_shown, _shownData) ?? ItemDefs.Material,
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
