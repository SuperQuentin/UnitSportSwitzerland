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
    public const int PhonePocketState = 0, PhoneFoldedState = 1, PhoneOpenState = 2, PhoneCameraState = 3;

    /// <summary>0 in the pocket, 1 out folded, 2 open, 3 open and raised as a camera: replicated on change.</summary>
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
        return (-b.Z * 0.75f + b.Y * 0.5f + b.X * 0.35f).Normalized();
    }

    /// <summary>The fingertip the figure was last built pointing at (its mesh frame), for the checks.</summary>
    internal Vector3? DrawnPoint => _poseKey.Point?.Tip;

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
    private float _backdropShown;
    private MeshInstance3D? _backdrop;
    private StandardMaterial3D? _backdropMat;

    /// <summary>Puts the phone in the hands where the arm pose has them, or hides it.</summary>
    private void StepPhone(float dt)
    {
        if (!IsMultiplayerAuthority())
            PhoneFold = Mathf.MoveToward(PhoneFold, PhoneState >= PhoneOpenState ? 1f : 0f, dt / Phone.PhoneModel.UnfoldSeconds);
        bool held = _walker != null && HandLocal is not null && Ride == RideKind.OnFoot && !RidingAlong && !IsSwimming && !Ragdolled
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
        _phoneTiltShown = _phoneTiltShown.Lerp(PhoneTilt, MathX.Damp(6f, dt));
        _phoneModel.Transform = PhoneInHands();
        _phoneModel.Visible = true;
    }

    private static bool IsPhonePose(Avatar.ItemArmPose pose) =>
        pose is Avatar.ItemArmPose.Phone or Avatar.ItemArmPose.PhoneOpen or Avatar.ItemArmPose.PhoneRaised;

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
        if (PhoneState == PhoneCameraState && IsMultiplayerAuthority() && !Npc && MathX.Flat(z).LengthSquared() > 1e-4f) z = MathX.Flat(z).Normalized();
        var y = (Vector3.Up - z * z.Dot(Vector3.Up)).Normalized();
        var x = y.Cross(z);
        var basis = new Basis(x, y, z);
        if (IsMultiplayerAuthority() && !Npc)
        {
            float t = (float)GameClock.Now;
            // a held thing is never still: a slow breath, a slower drift
            var breathe = new Vector3(0.010f * Mathf.Sin(t * 1.3f), 0.012f * Mathf.Sin(t * 0.8f + 1f), 0.006f * Mathf.Sin(t * 0.6f + 2f));
            basis = basis * Basis.FromEuler(breathe + new Vector3(-_phoneTiltShown.Y * 0.05f, _phoneTiltShown.X * 0.05f, 0f));
            if (PhoneState == PhoneCameraState) basis = basis * new Basis(Vector3.Right, Mathf.Clamp(_pitch + _punch, -1.2f, 1.2f));
        }
        // the hand at the right edge, a little below the middle: the phone's centre is up and across from it,
        // in front of the fingers, which hold it from behind and at the edges
        return new Transform3D(basis, hand - x * 0.034f + y * 0.02f + z * 0.075f);
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
        float look = Mathf.SmoothStep(0f, 1f, PhoneLook), cover = Mathf.SmoothStep(0f, 1f, PhoneCover);
        if (look <= 0f || _phoneModel is not { Visible: true } || XR.XrSession.Active)
        {
            if (!float.IsNaN(_phoneNear))
            {
                _camera.Near = _phoneNear;
                _camera.Fov = _phoneFov;
                _phoneNear = float.NaN;
            }
            _phoneSeen.Origin = new Vector3(float.NaN, 0, 0);
            ShowBackdrop(0f, dt);
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

        // over the shoulder onto the phone, folded or open; behind it, level, for the camera app; square
        // onto it, close, for the map
        float camApp = Mathf.SmoothStep(0f, 1f, PhoneCameraApp);
        var target = PhoneShoulderView(_phoneSeen);
        if (camApp > 0f) target = target.InterpolateWith(PhoneCameraAppView(_phoneSeen), camApp);
        if (cover > 0f) target = target.InterpolateWith(PhoneScreenView(_phoneSeen), cover);
        _camera.GlobalTransform = _camera.GlobalTransform.InterpolateWith(target, look);
        // the screen ends up a few centimetres from the lens
        _camera.Near = Mathf.Lerp(_phoneNear, 0.01f, look);
        // set from the saved FOV each frame, not eased from the last: the game's own easing starts again from it
        _camera.Fov = Mathf.Lerp(Mathf.Lerp(_phoneFov, PhoneFov, look), CameraAppFov, camApp * (1f - cover));
        // on its way to the map or behind the raised phone the lens passes the face: the body is seen
        // from the eye then (only its shadow drawn), the phone stays
        if (cover > 0.12f || camApp > 0.4f) SeeBodyFromEye(true);
        ShowBackdrop(PhoneBackdrop, dt);
    }

    /// <summary>The camera app's lens: wide enough that the world shows round the raised phone.</summary>
    private const float CameraAppFov = 52f;

    /// <summary>
    /// Behind and above the right shoulder (the left, over that one), looking down at the phone: the
    /// folded phone's cover, or closer in, the open phone with the hand working it.
    /// </summary>
    private Transform3D PhoneShoulderView(Transform3D phone)
    {
        // the body's facing (the figure is built facing -Z), not the phone's, which leans toward the eyes
        var ahead = MathX.Flat(-(_walker?.GlobalBasis.Z ?? GlobalBasis.Z));
        ahead = ahead.LengthSquared() > 1e-4f ? ahead.Normalized() : Vector3.Forward;
        var right = ahead.Cross(Vector3.Up);
        float open = _phoneModel != null ? Mathf.SmoothStep(0f, 1f, _phoneModel.Fold) : 0f;
        var at = phone * Phone.PhoneModel.CoverScreenCentre.Lerp(Phone.PhoneModel.InnerScreenCentre, open);
        // open, a third closer: the phone and the finger on it fill the middle of the picture
        float k = Mathf.Lerp(1f, 0.68f, open);
        var lens = at + (Vector3.Up * 0.6f + right * (0.42f * _shoulderSide) - ahead * 0.2f) * k;
        // the phone's long side up the picture: it reads upright, as a phone held up to read does
        return new Transform3D(Basis.LookingAt(at - lens, phone.Basis.Y), lens);
    }

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

    /// <summary>A point of a screen (0..1 across and down) in world space, <paramref name="lift"/> metres off the glass.</summary>
    public Vector3? PhoneScreenToWorld(Vector2 uv, bool cover, float lift)
    {
        if (_phoneModel is not { Visible: true }) return null;
        var phone = _phoneModel.GlobalTransform.Orthonormalized();
        var (centre, size) = cover
            ? (Phone.PhoneModel.CoverScreenCentre, Phone.PhoneModel.CoverSize)
            : (Phone.PhoneModel.InnerScreenCentre, new Vector2(Phone.PhoneModel.InnerW, Phone.PhoneModel.InnerH));
        return phone * (centre + new Vector3((uv.X - 0.5f) * size.X, (0.5f - uv.Y) * size.Y, lift));
    }

    /// <summary>A dark sheet on the lens just beyond the phone: the world goes, the phone and its rim stay.</summary>
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
        if (_camera == null || _phoneModel is not { Visible: true } model) return null;
        if (model.Fold < 0.01f) cover = true;
        else if (model.Fold <= 0.99f) return null;
        var phone = model.GlobalTransform.Orthonormalized();
        var (centre, size) = cover
            ? (Phone.PhoneModel.CoverScreenCentre, Phone.PhoneModel.CoverSize)
            : (Phone.PhoneModel.InnerScreenCentre, new Vector2(Phone.PhoneModel.InnerW, Phone.PhoneModel.InnerH));
        var plane = new Godot.Plane(phone.Basis.Z, phone * centre);
        if (plane.IntersectsRay(_camera.ProjectRayOrigin(window), _camera.ProjectRayNormal(window)) is not { } hit) return null;
        var local = phone.AffineInverse() * hit - centre;
        return new Vector2(local.X / size.X + 0.5f, 0.5f - local.Y / size.Y);
    }
}
