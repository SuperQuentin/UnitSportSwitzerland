using UnitSport.Core;
using Godot;

namespace UnitSport.Player;

/// <summary>
/// The phone in the hands (#889): drawn on every peer from the replicated <see cref="PhoneState"/>,
/// and for the owner a camera laid over the normal one, out over the shoulder onto the phone and,
/// opened, square onto it with its rim still in view and the world dimmed behind (a backdrop on the
/// lens). The lens follows a smoothed copy of the phone, so the hands' sway, a step or a turn shows
/// as the phone moving in the picture. The local controller is <see cref="Phone.PhoneController"/>.
/// Docs: <c>docs/notes/ui/phone.md</c>.
/// </summary>
public partial class FootPlayer
{
    public const int PhonePocketState = 0, PhoneFoldedState = 1, PhoneOpenState = 2, PhoneCameraState = 3, PhoneSelfieState = 4;

    /// <summary>
    /// 0 in the pocket, 1 out folded, 2 open, 3 open and raised as a camera, 4 the same turned round for a
    /// selfie (the main camera and the cover screen toward the face): replicated on change.
    /// </summary>
    [Export] public int PhoneState { get; set; }

    /// <summary>0..1: the owner's camera over the shoulder onto the phone (set every frame by the controller, linear).</summary>
    public float PhoneLook { get; set; }

    /// <summary>0..1: the owner's camera square onto the open phone (linear; eased here).</summary>
    public float PhoneCover { get; set; }

    /// <summary>The hinge, 0 folded to 1 open: the owner's controller sets it, a remote copy eases it from <see cref="PhoneState"/>.</summary>
    public float PhoneFold { get; set; }

    /// <summary>0..1: how dark the world behind the open phone is wanted (the owner's controller sets it).</summary>
    public float PhoneBackdrop { get; set; }

    /// <summary>How dark the world behind the phone is now, 0..1: a hidden teleport waits for 1.</summary>
    public float PhoneBackdropShown => _backdropShown;

    /// <summary>−1..1 each way: the open phone leans a little toward the cursor (the owner's controller sets it).</summary>
    public Vector2 PhoneTilt { get; set; }

    /// <summary>0..1: the owner's camera behind the raised phone, level, the camera app's view (linear; eased here).</summary>
    public float PhoneCameraApp { get; set; }

    /// <summary>
    /// Where the free hand's index fingertip goes, in world space, while it works the phone's screen
    /// (the controller sets it from the cursor and WASD); null: both hands on the phone. The owner's
    /// alone: drawn on this peer, never sent.
    /// </summary>
    public Vector3? PhonePointTip { get; set; }

    /// <summary>The way the finger goes to <see cref="PhonePointTip"/> (world space): into the screen, from below and to the left.</summary>
    public Vector3? PhonePointDir { get; set; }

    /// <summary>The way a finger comes onto the phone's screen in view: into the glass, up it and to the right.</summary>
    public Vector3? PhoneFingerWay()
    {
        if (_phoneModel is not { Visible: true }) return null;
        var b = _phoneModel.GlobalBasis.Orthonormalized();
        return (-b.Z * 0.3f + b.Y * 0.8f + b.X * 0.5f).Normalized();
    }

    /// <summary>The fingertip the figure was last built pointing at (its mesh frame), for the checks.</summary>
    internal Vector3? DrawnPoint => _poseKey.Point?.Tip;

    /// <summary>
    /// In VR, where the phone is in the owner's real hand (world), set every frame by <c>XR.XrPhone</c>;
    /// null: in the figure's hands as every other peer draws it.
    /// </summary>
    public Transform3D? PhoneVrPose { get; set; }

    /// <summary>The phone as drawn in the hands, null until first taken out.</summary>
    public Phone.PhoneModel? PhoneModel => _phoneModel;

    private Phone.PhoneModel? _phoneModel;
    /// <summary>The lens's near plane and FOV before the phone came out (NaN when the phone camera is off).</summary>
    private float _phoneNear = float.NaN, _phoneFov;
    /// <summary>The lens's field of view on the phone: narrower than the game's, so it stands back and still fills the view.</summary>
    private const float PhoneFov = 32f;
    /// <summary>The smoothed phone the lens follows (global); NaN origin = take the next as it is.</summary>
    private Transform3D _phoneSeen = new(Basis.Identity, new Vector3(float.NaN, 0, 0));
    private Vector2 _phoneTiltShown;
    /// <summary>0..1: the open phone turned round for a selfie, eased on every peer.</summary>
    private float _phoneTurn;
    private float _backdropShown;
    private MeshInstance3D? _backdrop;
    private StandardMaterial3D? _backdropMat;

    /// <summary>Puts the phone in the hands where the arm pose has them, or hides it.</summary>
    private void StepPhone(float dt)
    {
        if (!IsMultiplayerAuthority())
            PhoneFold = Mathf.MoveToward(PhoneFold, PhoneState >= PhoneOpenState ? 1f : 0f, dt / Phone.PhoneModel.UnfoldSeconds);
        bool held = _walker != null && HandLocal is not null && Ride == RideKind.OnFoot && !RidingAlong && !IsSwimming && !IsClimbing && !Ragdolled
                    && IsPhonePose(_itemArmCur) && _itemArmBlend > 0.3f;
        if (!held)
        {
            if (_phoneModel != null) _phoneModel.Visible = false;
            return;
        }
        if (_phoneModel == null)
        {
            _phoneModel = new Phone.PhoneModel();
            AddChild(_phoneModel);
        }
        _phoneModel.Fold = PhoneFold;
        _phoneModel.CoverWhenOpen = PhoneState == PhoneSelfieState;
        _phoneTiltShown = _phoneTiltShown.Lerp(PhoneTilt, MathX.Damp(6f, dt));
        _phoneTurn = Mathf.MoveToward(_phoneTurn, PhoneState == PhoneSelfieState ? 1f : 0f, dt / 0.45f);
        // in VR the owner holds it in the real hand (#920); everyone else sees it in the figure's
        if (IsMultiplayerAuthority() && XR.XrSession.Active && PhoneVrPose is { } real) _phoneModel.GlobalTransform = real;
        else _phoneModel.Transform = PhoneInHands();
        _phoneModel.Visible = true;
    }

    private static bool IsPhonePose(Avatar.ItemArmPose pose) =>
        pose is Avatar.ItemArmPose.Phone or Avatar.ItemArmPose.PhoneOpen or Avatar.ItemArmPose.PhoneRaised or Avatar.ItemArmPose.PhoneSelfie;

    /// <summary>
    /// The phone in this body's frame: gripped by its right edge in the item hand, its screens turned
    /// to the eyes, its long side up; for its owner it breathes a little, leans toward the cursor, and
    /// raised as a camera it tips with the view's pitch.
    /// </summary>
    private Transform3D PhoneInHands()
    {
        var hand = HandLocal!.Value.Origin;
        var eye = _walker!.Transform * new Vector3(0f, EyeHeight - 0.08f, -0.09f);
        var z = (eye - hand).Normalized();
        // raised as a camera, the owner holds it upright, level with the horizon; the view's pitch tips it below
        bool raised = PhoneState is PhoneCameraState or PhoneSelfieState;
        if (raised && IsMultiplayerAuthority() && !Npc && MathX.Flat(z).LengthSquared() > 1e-4f) z = MathX.Flat(z).Normalized();
        var y = (Vector3.Up - z * z.Dot(Vector3.Up)).Normalized();
        var x = y.Cross(z);
        var basis = new Basis(x, y, z);
        if (IsMultiplayerAuthority() && !Npc)
        {
            float t = (float)GameClock.Now;
            // a held thing is never still: a slow breath, a slower drift
            var breathe = new Vector3(0.010f * Mathf.Sin(t * 1.3f), 0.012f * Mathf.Sin(t * 0.8f + 1f), 0.006f * Mathf.Sin(t * 0.6f + 2f));
            basis = basis * Basis.FromEuler(breathe + new Vector3(-_phoneTiltShown.Y * 0.05f, _phoneTiltShown.X * 0.05f, 0f));
            if (raised) basis = basis * new Basis(Vector3.Right, Mathf.Clamp(_pitch + _punch, -1.2f, 1.2f));
        }
        // the hand at the right edge, a little below the middle: the phone's centre is up and across from it,
        // in front of the fingers, which hold it from behind and at the edges
        var held = new Transform3D(basis, hand - x * 0.034f + y * 0.02f + z * 0.075f);
        if (_phoneTurn <= 0f) return held;
        // a selfie (#889): turned half round about the open phone's middle, the camera island and the cover
        // screen to the face (the arm already holds it up and out)
        float turn = Mathf.SmoothStep(0f, 1f, _phoneTurn);
        var centre = Phone.PhoneModel.InnerScreenCentre;
        var spin = new Transform3D(Basis.Identity, centre) * new Transform3D(new Basis(Vector3.Up, Mathf.Pi * turn), Vector3.Zero)
                   * new Transform3D(Basis.Identity, -centre);
        return held * spin;
    }

    /// <summary>
    /// The owner's phone camera, after the normal one has placed itself: eased from it to over the
    /// shoulder onto the phone (<see cref="PhoneLook"/>), then square onto the open phone (<see cref="PhoneCover"/>).
    /// Not in VR: there the eye never moves but with the head (#186).
    /// </summary>
    private void PhoneCamera(float dt)
    {
        StepPhone(dt);
        if (_camera == null) return;
        StepPhoneViewmodel(dt);
        float look = Mathf.SmoothStep(0f, 1f, PhoneLook), cover = Mathf.SmoothStep(0f, 1f, PhoneCover);
        float camApp = Mathf.SmoothStep(0f, 1f, PhoneCameraApp);
        // folded or open on desktop and pad the gameplay camera is not touched (#1825): only the camera app, the selfie and
        // the travel curtain (and the touch screen) move it
        if (look <= 0f || _phoneModel is not { Visible: true } || XR.XrSession.Active || cover <= 0f && camApp <= 0f)
        {
            if (!float.IsNaN(_phoneNear))
            {
                _camera.Near = _phoneNear;
                _camera.Fov = _phoneFov;
                _phoneNear = float.NaN;
            }
            _phoneSeen.Origin = new Vector3(float.NaN, 0, 0);
            ShowBackdrop(look > 0f && !XR.XrSession.Active ? PhoneBackdrop : 0f, dt);
            return;
        }
        if (float.IsNaN(_phoneNear))
        {
            _phoneNear = _camera.Near;
            _phoneFov = _camera.Fov;
        }
        // the lens follows the phone a beat late: the hands' sway, a step, a turn show as the phone moving
        var phone = _phoneModel.GlobalTransform.Orthonormalized();
        if (float.IsNaN(_phoneSeen.Origin.X) || _phoneSeen.Origin.DistanceTo(phone.Origin) > 1f) _phoneSeen = phone;
        else _phoneSeen = _phoneSeen.InterpolateWith(phone, MathX.Damp(PhoneCover > 0.5f ? 9f : 6f, dt)).Orthonormalized();

        // from the game's own lens: behind the raised phone, level, for the camera app; square onto the screen, close,
        // for the travel curtain (and the touch screen)
        var target = _camera.GlobalTransform;
        if (camApp > 0f)
        {
            float selfie = Mathf.SmoothStep(0f, 1f, _phoneTurn);
            var app = PhoneCameraAppView(_phoneSeen);
            if (selfie > 0f) app = app.InterpolateWith(PhoneSelfieView(_phoneSeen), selfie);
            target = target.InterpolateWith(app, camApp);
        }
        if (cover > 0f) target = target.InterpolateWith(PhoneScreenView(_phoneSeen), cover);
        _camera.GlobalTransform = _camera.GlobalTransform.InterpolateWith(target, look);
        _camera.Near = Mathf.Lerp(_phoneNear, 0.01f, Mathf.Max(cover, camApp));
        // set from the saved FOV each frame, not eased from the last: the game's own easing starts again from it
        _camera.Fov = Mathf.Lerp(Mathf.Lerp(_phoneFov, PhoneFov, cover), CameraAppFov, camApp * (1f - cover));
        // on its way to the screen or behind the raised phone the lens passes the face: the body is seen
        // from the eye then (only its shadow drawn), the phone stays
        if (cover > 0.12f || camApp > 0.4f) SeeBodyFromEye(true);
        ShowBackdrop(PhoneBackdrop, dt);
    }

    /// <summary>The phone moves the lens: the camera app and selfie, the travel curtain, the touch screen (not folded or open on desktop and pad, #1825).</summary>
    private bool PhoneMovesLens => PhoneLook > 0f && !XR.XrSession.Active
        && (PhoneState >= PhoneCameraState || PhoneCover > 0f || PhoneBackdrop > 0f || Core.Platform.IsTouch);

    // ------------------------------------------------------------------ the owner's viewmodel (#1825)

    private Phone.PhoneModel? _phoneView;
    private bool _worldPhoneHidden;

    /// <summary>The phone's viewmodel on the owner's camera, when it is what is drawn (desktop and pad, folded or open).</summary>
    public Phone.PhoneModel? PhoneViewmodel => _phoneView;

    /// <summary>
    /// The phone's Map app has the big map up (#1826): the viewmodel eases to a small pose in the lower right, wholly outside the map's
    /// rectangle (the right fifth of the view), and back when it closes. Set by <c>PhoneMap</c>; posed on the same node, nothing rebuilt.
    /// </summary>
    public bool PhoneMapAway { get; set; }
    private float _mapAway;

    /// <summary>The share of the view's width the phone has while the map is up.</summary>
    internal const float ViewMapShare = 0.16f;

    /// <summary>The viewmodel is on screen now (the pointing finger and the in-world phone's screen yield to it).</summary>
    public bool PhoneViewShown { get; private set; }

    /// <summary>The share of the view's width the open inner screen and the folded cover fill (16:9), and where they sit.</summary>
    internal const float ViewOpenShare = 0.40f, ViewCoverShare = 0.15f, ViewDepth = 0.45f;

    /// <summary>
    /// The phone drawn in camera space, lower right: open, the inner screen 43 % of the view's width, its right edge 2 % from the
    /// view's edge and its bottom 3 % up; folded, the cover 15 % wide, above the hint line and left of the key list. Square to the
    /// picture with a few degrees of tilt and a light walk sway; the hinge unfolds as a node. The share is by width, clamped by
    /// height so it never leaves the view (4:3, 21:9). The lens is not touched.
    /// </summary>
    private void StepPhoneViewmodel(float dt)
    {
        bool want = IsMultiplayerAuthority() && !Npc && !XR.XrSession.Active && !Core.Platform.IsTouch
            && PhoneState is PhoneFoldedState or PhoneOpenState && PhoneCover <= 0.001f && PhoneCameraApp <= 0.001f && PhoneBackdrop <= 0.001f;
        // in first person the in-world copy would show twice (and float where the hidden body's hand is)
        HideWorldPhone(want && !_thirdPerson);
        if (!want)
        {
            PhoneViewShown = false;
            if (_phoneView != null) _phoneView.Visible = false;
            return;
        }
        if (_phoneView == null)
        {
            _phoneView = new Phone.PhoneModel { Viewmodel = true };
            _camera!.AddChild(_phoneView);
        }
        var size = GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0f ? size.X / size.Y : 16f / 9f;
        float tanV = Mathf.Tan(Mathf.DegToRad(_camera!.Fov) * 0.5f), tanH = tanV * aspect;
        float open = Mathf.SmoothStep(0f, 1f, _phoneView.Fold);
        // by width, but never taller than the view allows
        float openW = Mathf.Min(ViewOpenShare, 0.58f * (Phone.PhoneModel.InnerW / Phone.PhoneModel.InnerH) / aspect);
        float coverW = Mathf.Min(ViewCoverShare, 0.46f * (Phone.PhoneModel.CoverSize.X / Phone.PhoneModel.CoverSize.Y) / aspect);
        _mapAway = Mathf.MoveToward(_mapAway, PhoneMapAway ? 1f : 0f, dt / (Ui.UiMotion.Reduced ? 0.08f : 0.28f));
        openW = Mathf.Lerp(openW, Mathf.Min(openW, ViewMapShare), Mathf.SmoothStep(0f, 1f, _mapAway));
        float w = Mathf.Lerp(coverW, openW, open);
        var refSize = new Vector2(Mathf.Lerp(Phone.PhoneModel.CoverSize.X, Phone.PhoneModel.InnerW, open), Mathf.Lerp(Phone.PhoneModel.CoverSize.Y, Phone.PhoneModel.InnerH, open));
        float scale = w * 2f * ViewDepth * tanH / refSize.X;
        float hFrac = refSize.Y * scale / (2f * ViewDepth * tanV);   // the screen's height as a share of the view's
        // the screen's middle in the picture (ndc): open at the corner, 2 % in and 3 % up; folded 15 % in from the right edge
        // (clear of the key list) and above the hint line
        var openAt = new Vector2(1f - 0.07f - w, -1f + 0.07f + hFrac);
        var coverAt = new Vector2(1f - 0.30f - w, -1f + 0.24f + hFrac);
        var at = coverAt.Lerp(openAt, open);
        // comes up from below as it comes out
        at.Y -= (1f - Mathf.SmoothStep(0f, 1f, PhoneLook)) * 1.3f;
        // a few degrees of tilt and a light sway with the walk
        float t = (float)GameClock.Now, speed = MathX.FlatLength(Velocity);
        var tilt = Basis.FromEuler(new Vector3(0.02f * Mathf.Sin(t * 0.9f), -0.07f + 0.012f * Mathf.Sin(t * 1.3f), 0.04f + 0.01f * Mathf.Sin(t * 0.7f)));
        at.Y += Mathf.Min(speed, 6f) * 0.004f * Mathf.Sin(t * 8f);
        var centre = Phone.PhoneModel.CoverScreenCentre.Lerp(Phone.PhoneModel.InnerScreenCentre, open);
        var targetPos = new Vector3(at.X * tanH * ViewDepth, at.Y * tanV * ViewDepth, -ViewDepth);
        _phoneView.Basis = tilt.Scaled(Vector3.One * scale);
        _phoneView.Position = targetPos - tilt * (centre * scale);
        _phoneView.Fold = PhoneFold;
        _phoneView.Visible = true;
        PhoneViewShown = true;
    }

    /// <summary>First person: the in-world phone is put on the layer the eye never draws, back on the default one otherwise.</summary>
    private void HideWorldPhone(bool hide)
    {
        if (hide == _worldPhoneHidden || _phoneModel == null) return;
        _worldPhoneHidden = hide;
        uint layers = hide ? Avatar.OwnBody.Layer : 1u;
        foreach (var node in _phoneModel.FindChildren("*", "MeshInstance3D", true, false))
            if (node is MeshInstance3D mi) mi.Layers = layers;
    }

    /// <summary>The camera app's lens: wide enough that the world shows round the raised phone.</summary>
    private const float CameraAppFov = 52f;

    /// <summary>
    /// Square onto the open phone, as close as keeps all of it in view with a little room round its
    /// rim: the phone stays a thing in the hands, never the whole screen.
    /// </summary>
    private Transform3D PhoneScreenView(Transform3D phone)
    {
        var size = GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0f ? size.X / size.Y : 16f / 9f;
        float tanV = Mathf.Tan(Mathf.DegToRad(PhoneFov) * 0.5f);
        const float Room = 1.1f;
        var open = Phone.PhoneModel.OpenSize * Room;
        float distance = Mathf.Max(open.Y * 0.5f / tanV, open.X * 0.5f / (tanV * aspect));
        var centre = phone * Phone.PhoneModel.InnerScreenCentre;
        return new Transform3D(phone.Basis, centre + phone.Basis.Z * distance);
    }

    /// <summary>
    /// Behind the raised phone at the eyes, level with it: the phone's screen in the lower middle of the
    /// picture, the world it films all round it, the horizon level unless the player tips the view.
    /// </summary>
    private Transform3D PhoneCameraAppView(Transform3D phone)
    {
        var centre = phone * Phone.PhoneModel.InnerScreenCentre;
        return new Transform3D(phone.Basis, centre + phone.Basis.Z * 0.3f + phone.Basis.Y * 0.045f);
    }

    /// <summary>
    /// A selfie: from the face, looking at the turned phone's cover screen, which shows the main camera's
    /// view of that face; the phone in the middle of the picture, the world behind it.
    /// </summary>
    private Transform3D PhoneSelfieView(Transform3D phone)
    {
        // turned, the phone's -Z (the cover's and the camera island's way) points at the player
        var cover = phone * Phone.PhoneModel.CoverOpenCentre;
        var toPlayer = -phone.Basis.Z;
        var basis = new Basis(-phone.Basis.X, phone.Basis.Y, -phone.Basis.Z);
        return new Transform3D(basis, cover + toPlayer * 0.3f + phone.Basis.Y * 0.03f);
    }

    /// <summary>The phone whose screen the owner sees and points at: the viewmodel when it is drawn, else the one in the hands.</summary>
    private Phone.PhoneModel? ScreenModel => _phoneView is { Visible: true } ? _phoneView : _phoneModel is { Visible: true } ? _phoneModel : null;

    /// <summary>A point of a screen (0..1 across and down) in world space, <paramref name="lift"/> metres off the glass.</summary>
    public Vector3? PhoneScreenToWorld(Vector2 uv, bool cover, float lift)
    {
        if (ScreenModel is not { } model) return null;
        var phone = model.GlobalTransform;   // scaled for the viewmodel: the model's own sizes below stay true
        var (centre, size) = cover
            ? (Phone.PhoneModel.CoverScreenCentre, Phone.PhoneModel.CoverSize)
            : (Phone.PhoneModel.InnerScreenCentre, new Vector2(Phone.PhoneModel.InnerW, Phone.PhoneModel.InnerH));
        return phone * (centre + new Vector3((uv.X - 0.5f) * size.X, (0.5f - uv.Y) * size.Y, lift));
    }

    /// <summary>A dark sheet on the lens just beyond the phone: the world goes, the phone and its rim stay.</summary>
    /// <summary>
    /// The backdrop off at once. It eases out only in the phone's own camera step, which runs on
    /// foot: a mount taken within its 0.4 s fade (a training mounts the bike as soon as the trip to
    /// its start lands, #901) left the dark sheet on the riding camera for good.
    /// </summary>
    private void DropPhoneBackdrop()
    {
        _backdropShown = 0f;
        if (_backdrop != null) _backdrop.Visible = false;
    }

    private void ShowBackdrop(float want, float dt)
    {
        _backdropShown = Mathf.MoveToward(_backdropShown, want, dt / 0.4f);
        if (_backdropShown <= 0f)
        {
            if (_backdrop != null) _backdrop.Visible = false;
            return;
        }
        if (_backdrop == null)
        {
            _backdropMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                DisableFog = true,
                AlbedoColor = new Color(0.035f, 0.04f, 0.06f, 0f),
            };
            _backdrop = new MeshInstance3D
            {
                Name = "PhoneBackdrop",
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = _backdropMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _camera!.AddChild(_backdrop);
        }
        // just beyond the phone, wide enough to fill the view
        float far = (_phoneModel != null ? _camera!.GlobalPosition.DistanceTo(_phoneModel.GlobalPosition) : 0.3f) + 0.06f;
        float h = 2.6f * far * Mathf.Tan(Mathf.DegToRad(_camera!.Fov) * 0.5f);
        var size = GetViewport().GetVisibleRect().Size;
        _backdrop.Transform = new Transform3D(Basis.Identity.Scaled(new Vector3(h * size.X / Mathf.Max(1f, size.Y), h, 1f)), new Vector3(0f, 0f, -far));
        _backdrop.Visible = true;
        var c = _backdropMat!.AlbedoColor;
        if (!Mathf.IsEqualApprox(c.A, _backdropShown)) _backdropMat.AlbedoColor = c with { A = _backdropShown };
    }

    /// <summary>
    /// Where a point of the window lands on the phone's screen in view (the cover, folded; the inner
    /// screen, open), as 0..1 across and down, unclamped: a little outside it is "a bit around" the
    /// screen. Null when neither screen faces the lens (the hinge moving, no phone).
    /// </summary>
    public Vector2? PhoneScreenPoint(Vector2 window, out bool cover)
    {
        cover = false;
        if (_camera == null || ScreenModel is not { } model) return null;
        if (model.Fold < 0.01f) cover = true;
        else if (model.Fold <= 0.99f) return null;
        var phone = model.GlobalTransform;
        var (centre, size) = cover
            ? (Phone.PhoneModel.CoverScreenCentre, Phone.PhoneModel.CoverSize)
            : (Phone.PhoneModel.InnerScreenCentre, new Vector2(Phone.PhoneModel.InnerW, Phone.PhoneModel.InnerH));
        var plane = new Godot.Plane(phone.Basis.Z.Normalized(), phone * centre);
        if (plane.IntersectsRay(_camera.ProjectRayOrigin(window), _camera.ProjectRayNormal(window)) is not { } hit) return null;
        var local = phone.AffineInverse() * hit - centre;
        return new Vector2(local.X / size.X + 0.5f, 0.5f - local.Y / size.Y);
    }
}
