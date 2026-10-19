using UnitSport.Core;
using Godot;

namespace UnitSport.Player;

/// <summary>
/// The phone in the hands (#889): drawn on every peer from the replicated <see cref="PhoneState"/>,
/// and for the owner a camera laid over the normal one, out over the shoulder onto the phone and,
/// opened, onto its inner screen until that covers the view. The local controller is
/// <see cref="Phone.PhoneController"/>. Docs: <c>docs/notes/ui/phone.md</c>.
/// </summary>
public partial class FootPlayer
{
    public const int PhonePocketState = 0, PhoneFoldedState = 1, PhoneOpenState = 2;

    /// <summary>0 in the pocket, 1 out folded, 2 open: replicated on change, so everyone sees it in the hands.</summary>
    [Export] public int PhoneState { get; set; }

    /// <summary>0..1: the owner's camera over the shoulder onto the phone (set every frame by the controller, linear).</summary>
    public float PhoneLook { get; set; }

    /// <summary>0..1: the owner's camera onto the open inner screen, at 1 covering the view (linear; eased here).</summary>
    public float PhoneCover { get; set; }

    /// <summary>The hinge, 0 folded to 1 open: the owner's controller sets it, a remote copy eases it from <see cref="PhoneState"/>.</summary>
    public float PhoneFold { get; set; }

    /// <summary>The phone as drawn in the hands, null until first taken out.</summary>
    public Phone.PhoneModel? PhoneModel => _phoneModel;

    private Phone.PhoneModel? _phoneModel;
    /// <summary>The lens's near plane before the phone came out (NaN when the phone camera is off).</summary>
    private float _phoneNear = float.NaN, _phoneFov;
    /// <summary>The lens's field of view on the phone: narrower than the game's, so it stands back over the shoulder and still fills a third of the view.</summary>
    private const float PhoneFov = 32f;

    /// <summary>Puts the phone in the hands where the arm pose has them, or hides it.</summary>
    private void StepPhone(float dt)
    {
        if (!IsMultiplayerAuthority())
            PhoneFold = Mathf.MoveToward(PhoneFold, PhoneState == PhoneOpenState ? 1f : 0f, dt / Phone.PhoneModel.UnfoldSeconds);
        bool held = _walker != null && HandLocal is not null && Ride == RideKind.OnFoot && !RidingAlong && !IsSwimming && !Ragdolled
                    && _itemArmCur is Avatar.ItemArmPose.Phone or Avatar.ItemArmPose.PhoneOpen && _itemArmBlend > 0.3f;
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
        _phoneModel.Transform = PhoneInHands();
        _phoneModel.Visible = true;
    }

    /// <summary>
    /// The phone in this body's frame: gripped by its right edge in the item hand, its screens turned
    /// to the eyes, its long side up.
    /// </summary>
    private Transform3D PhoneInHands()
    {
        var hand = HandLocal!.Value.Origin;
        var eye = _walker!.Transform * new Vector3(0f, EyeHeight - 0.08f, -0.09f);
        var z = (eye - hand).Normalized();
        var y = (Vector3.Up - z * z.Dot(Vector3.Up)).Normalized();
        var x = y.Cross(z);
        // the hand at the right edge, a little below the middle: the phone's centre is up and across from it
        // in front of the fingers, which hold it from behind and at the edges
        return new Transform3D(new Basis(x, y, z), hand - x * 0.028f + y * 0.03f + z * 0.075f);
    }

    /// <summary>
    /// The owner's phone camera, after the normal one has placed itself: eased from it to over the
    /// shoulder onto the phone (<see cref="PhoneLook"/>), then onto the open screen (<see cref="PhoneCover"/>).
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
            return;
        }
        if (float.IsNaN(_phoneNear))
        {
            _phoneNear = _camera.Near;
            _phoneFov = _camera.Fov;
        }
        var phone = _phoneModel.GlobalTransform.Orthonormalized();
        var lens = _camera.GlobalTransform.InterpolateWith(PhoneShoulderView(phone), look);
        if (cover > 0f) lens = lens.InterpolateWith(PhoneScreenView(phone), cover);
        _camera.GlobalTransform = lens;
        // on its way to the inner screen the lens passes the face: the body is seen from the eye
        // then (only its shadow drawn), the phone stays
        if (cover > 0.12f) SeeBodyFromEye(true);
        // the screen ends up a few centimetres from the lens
        _camera.Near = Mathf.Lerp(_phoneNear, 0.01f, look);
        // set from the saved FOV each frame, not eased from the last: the game's own easing starts again from it
        _camera.Fov = Mathf.Lerp(_phoneFov, PhoneFov, look);
    }

    /// <summary>
    /// High over the right shoulder (the left, over that one), looking down the folded phone's screen
    /// almost square on: above and outside the head, so neither the head nor the shoulder is in the way.
    /// </summary>
    private Transform3D PhoneShoulderView(Transform3D phone)
    {
        // the body's facing (the figure is built facing -Z), not the phone's, which leans toward the eyes
        var ahead = MathX.Flat(-(_walker?.GlobalBasis.Z ?? GlobalBasis.Z));
        ahead = ahead.LengthSquared() > 1e-4f ? ahead.Normalized() : Vector3.Forward;
        var right = ahead.Cross(Vector3.Up);
        var at = phone * Phone.PhoneModel.CoverScreenCentre;
        var lens = at + Vector3.Up * 0.6f + right * (0.42f * _shoulderSide) - ahead * 0.2f;
        // the phone's long side up the picture: it reads upright, as a phone held up to read does
        return new Transform3D(Basis.LookingAt(at - lens, phone.Basis.Y), lens);
    }

    /// <summary>
    /// Square onto the open inner screen, as close as makes it cover the whole view: its width
    /// across a wide window, its height up a tall one. That band of the screen is what the
    /// full-screen picture shows, so the hand-over to it is seamless.
    /// </summary>
    private Transform3D PhoneScreenView(Transform3D phone)
    {
        var size = GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0f ? size.X / size.Y : 16f / 9f;
        float half = Phone.PhoneModel.InnerSize * 0.5f / Mathf.Max(aspect, 1f);
        float distance = half / Mathf.Tan(Mathf.DegToRad(PhoneFov) * 0.5f);
        var centre = phone * Phone.PhoneModel.InnerScreenCentre;
        return new Transform3D(phone.Basis, centre + phone.Basis.Z * distance);
    }
}
