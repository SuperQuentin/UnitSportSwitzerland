using Godot;
using UnitSport.Phone;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// The phone in VR (#920, <c>docs/plans/phone-vr.md</c>): a real thing in the real hand.
/// <list type="bullet">
/// <item>Phase 1: a grip closing at the front pocket of the holding side takes it out into that hand
/// (the left; the right with left-handed play); the other hand's index fingertip is the pointer:
/// within <see cref="HoverReach"/> of the glass a ring shows where it would press, crossing the glass
/// (<see cref="TouchReach"/>) presses there, and lifting off (<see cref="Rearm"/>) releases, so a tap
/// is a click and a slide is a drag, through <see cref="PhoneController.VrPointer"/>. A light tick on
/// both hands at the touch and at the release stands in for the feel of glass. X (on the holding
/// hand) opens and folds it.</item>
/// <item>Phase 2: the free hand grips the front half's loose edge and swings it open or shut about the
/// hinge (a tick at flat and at shut; let go between, it settles to the nearer); in the camera app the
/// arm aims (the lens is where the hand holds it, no levelling), the holding hand's trigger is the
/// shutter, and turning the phone's back to the face is a selfie.</item>
/// <item>Phase 3: a teleport waits for the phone raised to the eyes (a blink after
/// <see cref="PhoneController"/>'s wait otherwise); a call buzzes the holding hand twice and rings
/// from the pocket, and the phone raised to an ear answers it; let go at the pocket it goes in, let
/// go anywhere else it hangs a moment and flies home.</item>
/// </list>
/// The game's camera never moves (#186). The aim nodes (<c>_left</c>, <c>_right</c>) are already swapped
/// for left-handed play, so the holding hand's controller is always <c>_left</c>; the grip poses and
/// the drawn hands follow the real hands.
/// </summary>
internal sealed class XrPhone
{
    /// <summary>The grip closes above this and opens below <see cref="GripOpen"/> (as <see cref="XrHands"/>).</summary>
    private const float GripClose = 0.7f, GripOpen = 0.35f;
    /// <summary>The front pocket, in the head's yaw frame: below the eyes, to the holding side, a little ahead, m.</summary>
    private const float PocketBelowMin = 0.55f, PocketBelowMax = 1.0f, PocketSideMin = 0.06f, PocketSideMax = 0.36f, PocketAhead = 0.04f;
    /// <summary>The fingertip this near the glass hovers (the ring shows), m.</summary>
    private const float HoverReach = 0.03f;
    /// <summary>The fingertip this near the glass touches it; it lifts off past <see cref="Rearm"/>, m.</summary>
    private const float TouchReach = 0.006f, Rearm = 0.012f;
    /// <summary>A fingertip this far behind the glass is not touching it from the front (it went round the phone), m.</summary>
    private const float Behind = 0.03f;
    /// <summary>The free hand's grip centre within this of the front half's loose edge takes the hinge, m.</summary>
    private const float EdgeReach = 0.06f;
    /// <summary>The screen this near the eyes covers them (a travel runs behind it); the cover this near an ear answers, m.</summary>
    private const float EyesReach = 0.14f, EarReach = 0.10f;
    /// <summary>The phone's back this square to the face (cosine), this long, is a selfie; back past <see cref="SelfieOff"/>, it ends.</summary>
    private const float SelfieOn = 0.5f, SelfieOff = 0.2f, SelfieHold = 0.3f;
    /// <summary>The aim ray reaches the screen this far, m.</summary>
    private const float RayReach = 1.5f;
    /// <summary>Let go away from the pocket: it hangs this long, then flies home in <see cref="HomeFlight"/>, s.</summary>
    private const float HomeWait = 0.5f, HomeFlight = 0.3f;

    private readonly XRController3D _left, _right, _leftGrip, _rightGrip;
    private readonly XrHand _leftHand, _rightHand;
    private readonly AudioStreamPlayer3D _sound;
    private readonly MeshInstance3D _beam;
    private bool _rayDown, _rayTrigger;
    private bool _gripClosed, _freeClosed, _heldByGrip, _touching, _hinge, _triggerDown;
    private float _turned;
    /// <summary>Flying home: seconds since let go (−1: not), and the pose it was let go at.</summary>
    private float _homing = -1f;
    private Transform3D _homeFrom;
    private int _rungFor = -1, _answered = -1;
    private double _secondPulseAt = -1;

    /// <summary>The grip that holds the phone is spent: <see cref="XrHands"/> takes nothing with it.</summary>
    public bool HoldingGripClaimed { get; private set; }
    /// <summary>The free hand's grip is on the hinge.</summary>
    public bool FreeGripClaimed => _hinge;

    public XrPhone(Node parent, XRController3D left, XRController3D right, XRController3D leftGrip, XRController3D rightGrip, XrHand leftHand, XrHand rightHand)
    {
        _left = left;
        _right = right;
        _leftGrip = leftGrip;
        _rightGrip = rightGrip;
        _leftHand = leftHand;
        _rightHand = rightHand;
        _sound = new AudioStreamPlayer3D { Name = "PhoneVrSound", Bus = Audio.SfxBus.Name, UnitSize = 2f, VolumeDb = -4f };
        parent.AddChild(_sound);
        // the menus' pointer look (XrUi): a faint white beam; the screen's ring is the reticle
        _beam = new MeshInstance3D
        {
            Name = "PhoneRay",
            Mesh = new CylinderMesh { TopRadius = 0.0015f, BottomRadius = 0.0025f, Height = 1f, RadialSegments = 6, Rings = 1 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(Ui.UiTheme.Text, 0.35f),
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            Layers = XrSession.HeadsetOnlyLayer,
        };
        parent.AddChild(_beam);
    }

    // ---- a scripted hand for --xrsim checks (no tracked controllers there) ----------------------

    /// <summary>The holding hand's grip pose (world), set by a check under --xrsim; null: the controller's.</summary>
    public static Transform3D? SimHold { get; set; }
    /// <summary>The holding hand's grip squeezed, for a check.</summary>
    public static bool SimGrip { get; set; }
    /// <summary>The holding hand's trigger pulled, for a check.</summary>
    public static bool SimTrigger { get; set; }
    /// <summary>The pointing fingertip (world), for a check; null: the real hand's.</summary>
    public static Vector3? SimTip { get; set; }
    /// <summary>The free hand's grip centre (world), for a check; null: the controller's.</summary>
    public static Vector3? SimFree { get; set; }
    /// <summary>The free hand's grip squeezed, for a check.</summary>
    public static bool SimFreeGrip { get; set; }
    /// <summary>The pointing hand's aim pose (world, the ray along -Z), for a check; null: the controller's.</summary>
    public static Transform3D? SimRay { get; set; }
    /// <summary>The pointing hand's trigger pulled, for a check.</summary>
    public static bool SimRayTrigger { get; set; }

    /// <summary>Where a check found it last frame: raised to the eyes, at an ear.</summary>
    public static bool AtEyesNow { get; private set; }

    /// <param name="leftHanded">Left-handed play (#439): the phone in the right hand, the left finger points.</param>
    public void Update(FootPlayer? player, Transform3D head, bool leftHanded, float dt)
    {
        var phone = PhoneController.Instance;
        var holdingGrip = leftHanded ? _rightGrip : _leftGrip;
        var freeGrip = leftHanded ? _leftGrip : _rightGrip;
        var pointing = leftHanded ? _leftHand : _rightHand;
        bool simulated = XrSession.Simulated && SimHold != null;
        var hold = simulated ? SimHold!.Value : holdingGrip.GlobalTransform;
        bool tracked = simulated || holdingGrip.GetHasTrackingData();
        float g = simulated ? (SimGrip ? 1f : 0f) : tracked ? _left.GetFloat("grip") : 0f;
        bool closing = !_gripClosed && g > GripClose, opening = _gripClosed && g < GripOpen;
        if (closing) _gripClosed = true;
        if (opening) _gripClosed = false;
        bool simFree = XrSession.Simulated && SimFree != null;
        var freeAt = simFree ? SimFree!.Value : freeGrip.GlobalPosition;
        float fg = simFree ? (SimFreeGrip ? 1f : 0f) : freeGrip.GetHasTrackingData() ? _right.GetFloat("grip") : 0f;
        bool freeClosing = !_freeClosed && fg > GripClose, freeOpening = _freeClosed && fg < GripOpen;
        if (freeClosing) _freeClosed = true;
        if (freeOpening) _freeClosed = false;
        AtEyesNow = false;
        _beam.Visible = false;

        if (phone == null || player == null)
        {
            HoldingGripClaimed = _hinge = false;
            return;
        }
        Ring(phone, head, leftHanded);

        // out of the pocket: a grip closing at the front pocket of the holding side
        if (!phone.IsOut && closing && tracked && AtPocket(hold.Origin, head, leftHanded) && phone.TakeOut())
        {
            _heldByGrip = true;
            Tick(holding: true, 0.4f);
        }
        if (!phone.IsOut)
        {
            _heldByGrip = _touching = _hinge = false;
            _homing = -1f;
            HoldingGripClaimed = false;
            player.PhoneVrPose = null;
            phone.VrPointer(null, false, false);
            phone.VrHinge(null);
            return;
        }

        var pose = tracked ? hold * InPalm(leftHanded) : (Transform3D?)null;
        // let go: at the pocket it goes in; anywhere else it hangs a moment, then flies home (#920 phase 3)
        if (_homing >= 0f)
        {
            // gripped again where it hangs: back in the hand
            if (closing && pose is { } p && p.Origin.DistanceTo(_homeFrom.Origin) < 0.15f)
            {
                _homing = -1f;
                _heldByGrip = true;
                Tick(holding: true, 0.3f);
            }
            else
            {
                Home(player, phone, head, leftHanded, dt);
                return;
            }
        }
        // taken out from the wrist menu: held in the hand all the same; a grip closed and opened puts it away
        if (closing) _heldByGrip = true;
        if (opening && _heldByGrip)
        {
            _heldByGrip = false;
            if (AtPocket(hold.Origin, head, leftHanded))
            {
                Release(phone);
                phone.PutAway();
                Tick(holding: true, 0.3f);
            }
            else if (player.PhoneModel is { Visible: true } m)
            {
                _homing = 0f;
                _homeFrom = m.GlobalTransform;
                _hinge = false;
                phone.VrHinge(null);
                Off(phone);
                return;
            }
            else phone.PutAway();
        }
        HoldingGripClaimed = _gripClosed;

        // in the palm: the long side along the fingers, the screens facing away from the palm
        player.PhoneVrPose = pose;
        if (player.PhoneModel is not { Visible: true } model)
        {
            Release(phone);
            return;
        }
        var frame = model.GlobalTransform.Orthonormalized();

        Hinge(phone, frame, model.Fold, freeAt, freeClosing, freeOpening);
        Camera(phone, frame, head, tracked && !simulated ? _left.GetFloat("trigger") : SimTrigger ? 1f : 0f, dt);
        Near(phone, frame, model.Fold, head);

        // the pointing fingertip on the glass (not while that hand holds the hinge); off it, the ray
        if (_hinge)
        {
            Off(phone);
            return;
        }
        bool cover = model.Fold < 0.01f;
        if (!cover && model.Fold < 0.99f) { Off(phone); return; }
        var (centre, size) = cover
            ? (PhoneModel.CoverScreenCentre, PhoneModel.CoverSize)
            : (PhoneModel.InnerScreenCentre, new Vector2(PhoneModel.InnerW, PhoneModel.InnerH));
        Vector3? tip = XrSession.Simulated && SimTip != null ? SimTip : pointing.Visible ? pointing.IndexTip : null;
        if (tip is { } t)
        {
            var local = frame.AffineInverse() * t - centre;
            var uv = new Vector2(local.X / size.X + 0.5f, 0.5f - local.Y / size.Y);
            bool over = uv.X is > -0.08f and < 1.08f && uv.Y is > -0.08f and < 1.08f;
            float depth = local.Z;   // + in front of the glass, toward the eyes
            if (over && depth <= HoverReach && depth >= -Behind)
            {
                _rayDown = _rayTrigger = false;
                bool was = _touching;
                _touching = _touching ? depth < Rearm : depth < TouchReach;
                if (_touching != was) Tick(holding: false, _touching ? 0.25f : 0.12f, both: true);
                phone.VrPointer(uv, cover, _touching);
                return;
            }
        }
        Release(phone);
        Ray(phone, frame, centre, size, cover);
    }

    /// <summary>
    /// The ray (#920): when the fingertip is not at the glass, the pointing hand's aim ray onto the
    /// screen is the pointer (the screen's ring is its reticle) and its trigger the press, as
    /// <see cref="XrUi"/>'s ray on the panel. Off the phone, in the camera app, a pull of that trigger
    /// steps the zoom (1x, 2x, 5x, round again).
    /// </summary>
    private void Ray(PhoneController phone, Transform3D frame, Vector3 centre, Vector2 size, bool cover)
    {
        bool simulated = XrSession.Simulated && SimRay != null;
        bool tracked = simulated || _right.GetHasTrackingData();
        float trigger = simulated ? (SimRayTrigger ? 1f : 0f) : tracked ? _right.GetFloat("trigger") : 0f;
        bool pulled = _rayTrigger ? trigger > 0.3f : trigger > 0.7f;
        bool pull = pulled && !_rayTrigger;
        _rayTrigger = pulled;
        Vector2? uv = null;
        Vector3 from = default, hit = default;
        if (tracked)
        {
            var aim = simulated ? SimRay!.Value : _right.GlobalTransform;
            from = aim.Origin;
            var dir = -aim.Basis.Z;
            var normal = frame.Basis.Z;
            var plane = new Godot.Plane(normal, frame * centre);
            if (dir.Dot(normal) < -0.05f && plane.IntersectsRay(from, dir) is { } at && at.DistanceTo(from) < RayReach)
            {
                var local = frame.AffineInverse() * at - centre;
                var u = new Vector2(local.X / size.X + 0.5f, 0.5f - local.Y / size.Y);
                if (u.X is >= 0f and <= 1f && u.Y is >= 0f and <= 1f)
                {
                    uv = u;
                    hit = at;
                }
            }
        }
        if (uv is not { } on)
        {
            // a press that slides off the screen lets go
            _rayDown = false;
            phone.VrPointer(null, cover, false);
            if (pull && phone.CameraApp) phone.VrZoom();
            return;
        }
        bool down = _rayDown ? pulled : pull;
        if (down != _rayDown) Tick(holding: false, down ? 0.2f : 0.1f);
        _rayDown = down;
        phone.VrPointer(on, cover, down);
        float length = from.DistanceTo(hit);
        var way = (hit - from) / Mathf.Max(length, 0.001f);
        _beam.Visible = true;
        _beam.GlobalTransform = new Transform3D(
            Basis.LookingAt(way, Mathf.Abs(way.Y) > 0.99f ? Vector3.Forward : Vector3.Up) * new Basis(Vector3.Right, -Mathf.Pi / 2f) * Basis.FromScale(new Vector3(1f, length, 1f)),
            from + way * length * 0.5f);
    }

    /// <summary>
    /// Phase 2: the free hand on the front half's loose edge swings it about the hinge. The hinge
    /// line is the node's local Y at x = −HalfWidth/2; the loose edge runs a half's width from it,
    /// at the fold's angle (<see cref="PhoneModel.Fold"/> is eased by a smoothstep, so the hand's
    /// angle is turned back through its inverse).
    /// </summary>
    private void Hinge(PhoneController phone, Transform3D frame, float fold, Vector3 freeAt, bool closing, bool opening)
    {
        var hingeAt = new Vector3(-PhoneModel.HalfWidth * 0.5f, 0f, 0f);
        var d = frame.AffineInverse() * freeAt - hingeAt;
        if (!_hinge && closing && phone.CanSwing)
        {
            float a = Mathf.Pi * Mathf.SmoothStep(0f, 1f, fold);
            var edge = new Vector3(PhoneModel.HalfWidth * Mathf.Cos(a), 0f, PhoneModel.HalfWidth * Mathf.Sin(a));
            if (new Vector2(d.X - edge.X, d.Z - edge.Z).Length() < EdgeReach && Mathf.Abs(d.Y) < PhoneModel.Height * 0.5f + 0.03f)
            {
                _hinge = true;
                Release(phone);
                Tick(holding: false, 0.3f, both: true);
            }
        }
        if (!_hinge) return;
        if (opening)
        {
            _hinge = false;
            phone.VrHinge(null);
            return;
        }
        float angle = Mathf.Atan2(d.Z, d.X);
        if (angle < 0f) angle = d.X > 0f ? 0f : Mathf.Pi;
        float to = InverseSmoothStep(angle / Mathf.Pi);
        // a detent at shut and at flat: a firmer tick as it lands there
        if (to >= 0.99f && fold < 0.99f || to <= 0.01f && fold > 0.01f) Tick(holding: true, 0.5f, both: true);
        phone.VrHinge(to);
    }

    /// <summary>The x whose smoothstep is <paramref name="y"/> (0..1).</summary>
    internal static float InverseSmoothStep(float y) =>
        0.5f - Mathf.Sin(Mathf.Asin(1f - 2f * Mathf.Clamp(y, 0f, 1f)) / 3f);

    /// <summary>Phase 2: in the camera app the holding hand's trigger shoots, and the phone's back turned to the face is a selfie.</summary>
    private void Camera(PhoneController phone, Transform3D frame, Transform3D head, float trigger, float dt)
    {
        bool pulled = trigger > 0.7f;
        if (pulled && !_triggerDown && phone.CameraApp) phone.VrShutter();
        _triggerDown = pulled ? true : trigger > 0.3f && _triggerDown;
        if (!phone.CameraApp)
        {
            _turned = 0f;
            return;
        }
        float facing = (-frame.Basis.Z).Dot((head.Origin - frame.Origin).Normalized());
        bool want = phone.Selfie ? facing > SelfieOff : facing > SelfieOn;
        _turned = want != phone.Selfie ? _turned + dt : 0f;
        if (_turned < SelfieHold) return;
        _turned = 0f;
        phone.VrSelfie(want);
        Tick(holding: true, 0.2f);
    }

    /// <summary>Phase 3: the screen raised to the eyes (a travel's curtain), the cover at an ear (a call answered).</summary>
    private void Near(PhoneController phone, Transform3D frame, float fold, Transform3D head)
    {
        var screen = frame * (fold > 0.5f ? PhoneModel.InnerScreenCentre : PhoneModel.CoverScreenCentre);
        AtEyesNow = screen.DistanceTo(head.Origin) < EyesReach;
        phone.VrAtEyes = AtEyesNow;
        int call = phone.RingingId;
        if (call < 0 || call == _answered) return;
        var cover = frame * PhoneModel.CoverScreenCentre;
        foreach (float side in new[] { -1f, 1f })
            if (cover.DistanceTo(head * new Vector3(side * 0.075f, -0.02f, 0.01f)) < EarReach)
            {
                _answered = call;
                phone.VrAnswer();
                Tick(holding: true, 0.3f);
                return;
            }
    }

    /// <summary>Phase 3: a new call buzzes the holding hand twice and rings from the pocket.</summary>
    private void Ring(PhoneController phone, Transform3D head, bool leftHanded)
    {
        int call = phone.RingingId;
        if (call != _rungFor)
        {
            _rungFor = call;
            if (call >= 0)
            {
                Tick(holding: true, 0.6f, seconds: 0.15f);
                _secondPulseAt = Core.RealClock.Now + 0.3;
                _sound.GlobalPosition = Pocket(head, leftHanded);
                _sound.Stream = PhoneSounds.Buzz;
                _sound.Play();
            }
        }
        if (_secondPulseAt >= 0 && Core.RealClock.Now >= _secondPulseAt)
        {
            _secondPulseAt = -1;
            Tick(holding: true, 0.6f, seconds: 0.15f);
        }
    }

    /// <summary>Let go away from the pocket: it hangs where it was let go, then flies to the pocket and goes in.</summary>
    private void Home(FootPlayer player, PhoneController phone, Transform3D head, bool leftHanded, float dt)
    {
        HoldingGripClaimed = false;
        float was = _homing;
        _homing += dt;
        var pocket = new Transform3D(_homeFrom.Basis, Pocket(head, leftHanded));
        if (was < HomeWait && _homing >= HomeWait)
        {
            _sound.GlobalPosition = _homeFrom.Origin;
            _sound.Stream = PhoneSounds.Draw;
            _sound.Play();
        }
        float k = Mathf.Clamp((_homing - HomeWait) / HomeFlight, 0f, 1f);
        player.PhoneVrPose = _homeFrom.InterpolateWith(pocket, Mathf.SmoothStep(0f, 1f, k));
        if (k >= 1f && phone.IsOut)
        {
            phone.PutAway();
            Tick(holding: true, 0.2f);
        }
    }

    /// <summary>The fingertip off the glass, its ring hidden.</summary>
    private void Off(PhoneController phone)
    {
        Release(phone);
        phone.VrPointer(null, false, false);
    }

    private void Release(PhoneController phone)
    {
        if (!_touching) return;
        _touching = false;
        Tick(holding: false, 0.12f, both: true);
        phone.VrPointer(null, false, false);
    }

    /// <summary>
    /// The phone in the holding hand's grip frame (open fingers along −Y, the left palm facing +X): its
    /// back in the palm, its top toward the fingertips, its screens away from the palm.
    /// </summary>
    internal static Transform3D InPalm(bool leftHanded)
    {
        float m = leftHanded ? -1f : 1f;   // the left palm faces +X, the right −X
        var z = new Vector3(m, 0f, 0f);
        var y = Vector3.Down;
        var x = y.Cross(z);
        return new Transform3D(new Basis(x, y, z), new Vector3(m * 0.012f, -0.035f, 0f));
    }

    /// <summary>The front pocket of the holding side, in the world.</summary>
    internal static Vector3 Pocket(Transform3D head, bool leftHanded) =>
        head.Origin + XrRig.YawOnly(head.Basis) * new Vector3(leftHanded ? 0.2f : -0.2f, -0.78f, -0.06f);

    /// <summary>Whether a hand at <paramref name="at"/> is at the front pocket of the holding side, in the head's yaw frame.</summary>
    private static bool AtPocket(Vector3 at, Transform3D head, bool leftHanded)
    {
        var local = XrRig.YawOnly(head.Basis).Inverse() * (at - head.Origin);
        float side = leftHanded ? local.X : -local.X;   // toward the holding side
        float below = -local.Y, ahead = -local.Z;
        return below is > PocketBelowMin and < PocketBelowMax && side is > PocketSideMin and < PocketSideMax && ahead > -PocketAhead;
    }

    private void Tick(bool holding, float strength, bool both = false, float seconds = 0.012f) =>
        XrSession.Rig?.RumbleHand(holding, strength, seconds, both);
}
