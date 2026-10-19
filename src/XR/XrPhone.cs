using Godot;
using UnitSport.Phone;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// The phone in VR (#920, <c>docs/plans/phone-vr.md</c>), phase 1: a real thing in the real hand.
/// A grip closing at the front pocket of the holding side takes it out into that hand (the left; the
/// right with left-handed play); the other hand's index fingertip is the pointer: within
/// <see cref="HoverReach"/> of the glass a ring shows where it would press, crossing the glass
/// (<see cref="TouchReach"/>) presses there, and lifting off (<see cref="Rearm"/>) releases, so a tap
/// is a click and a slide is a drag, through <see cref="PhoneController.VrPointer"/>. A light tick on
/// both hands at the touch and at the release stands in for the feel of glass. Opening the grip puts
/// the phone back in the pocket; X (on the holding hand) opens and folds it; the game's camera never
/// moves (#186).
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

    private readonly XRController3D _left, _right, _leftGrip, _rightGrip;
    private readonly XrHand _leftHand, _rightHand;
    private bool _gripClosed, _heldByGrip, _touching;

    /// <summary>The grip that holds the phone is spent: <see cref="XrHands"/> takes nothing with it.</summary>
    public bool HoldingGripClaimed { get; private set; }

    public XrPhone(XRController3D left, XRController3D right, XRController3D leftGrip, XRController3D rightGrip, XrHand leftHand, XrHand rightHand)
    {
        _left = left;
        _right = right;
        _leftGrip = leftGrip;
        _rightGrip = rightGrip;
        _leftHand = leftHand;
        _rightHand = rightHand;
    }

    // ---- a scripted hand for --xrsim checks (no tracked controllers there) ----------------------

    /// <summary>The holding hand's grip pose (world), set by a check under --xrsim; null: the controller's.</summary>
    public static Transform3D? SimHold { get; set; }
    /// <summary>The holding hand's grip squeezed, for a check.</summary>
    public static bool SimGrip { get; set; }
    /// <summary>The pointing fingertip (world), for a check; null: the real hand's.</summary>
    public static Vector3? SimTip { get; set; }

    /// <param name="leftHanded">Left-handed play (#439): the phone in the right hand, the left finger points.</param>
    public void Update(FootPlayer? player, Transform3D head, bool leftHanded, float dt)
    {
        var phone = PhoneController.Instance;
        var holdingCtl = leftHanded ? _right : _left;
        var holdingGrip = leftHanded ? _rightGrip : _leftGrip;
        var pointing = leftHanded ? _leftHand : _rightHand;
        bool sim = XrSession.Simulated && SimHold != null;
        var hold = sim ? SimHold!.Value : holdingGrip.GlobalTransform;
        bool tracked = sim || holdingGrip.GetHasTrackingData();
        float g = sim ? (SimGrip ? 1f : 0f) : tracked ? holdingCtl.GetFloat("grip") : 0f;
        bool closing = !_gripClosed && g > GripClose, opening = _gripClosed && g < GripOpen;
        if (closing) _gripClosed = true;
        if (opening) _gripClosed = false;

        if (phone == null || player == null)
        {
            HoldingGripClaimed = false;
            return;
        }

        // out of the pocket: a grip closing at the front pocket of the holding side
        if (!phone.IsOut && closing && tracked && AtPocket(hold.Origin, head, leftHanded) && phone.TakeOut())
        {
            _heldByGrip = true;
            Tick(both: false, 0.4f);
        }
        if (!phone.IsOut)
        {
            _heldByGrip = false;
            _touching = false;
            HoldingGripClaimed = false;
            player.PhoneVrPose = null;
            phone.VrPointer(null, false, false);
            return;
        }
        // taken out from the wrist menu: held in the hand all the same; a grip closed and opened puts it away
        if (closing) _heldByGrip = true;
        if (opening && _heldByGrip)
        {
            _heldByGrip = false;
            phone.PutAway();
        }
        HoldingGripClaimed = _gripClosed;

        // in the palm: the long side along the fingers, the screens facing away from the palm
        player.PhoneVrPose = tracked ? hold * InPalm(leftHanded) : null;

        // the pointing fingertip on the glass
        Vector3? tip = sim ? SimTip : pointing.Visible ? pointing.IndexTip : null;
        if (tip is not { } t || player.PhoneModel is not { Visible: true } model)
        {
            Release(phone);
            return;
        }
        bool cover = model.Fold < 0.01f;
        if (!cover && model.Fold < 0.99f) { Release(phone); return; }
        var (centre, size) = cover
            ? (PhoneModel.CoverScreenCentre, PhoneModel.CoverSize)
            : (PhoneModel.InnerScreenCentre, new Vector2(PhoneModel.InnerW, PhoneModel.InnerH));
        var local = model.GlobalTransform.Orthonormalized().AffineInverse() * t - centre;
        var uv = new Vector2(local.X / size.X + 0.5f, 0.5f - local.Y / size.Y);
        bool over = uv.X is > -0.08f and < 1.08f && uv.Y is > -0.08f and < 1.08f;
        float depth = local.Z;   // + in front of the glass, toward the eyes
        if (!over || depth > HoverReach || depth < -Behind)
        {
            Release(phone);
            phone.VrPointer(null, cover, false);
            return;
        }
        bool was = _touching;
        _touching = _touching ? depth < Rearm : depth < TouchReach;
        if (_touching != was) Tick(both: true, _touching ? 0.25f : 0.12f);
        phone.VrPointer(uv, cover, _touching);
    }

    private void Release(PhoneController phone)
    {
        if (!_touching) return;
        _touching = false;
        Tick(both: true, 0.12f);
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

    /// <summary>Whether a hand at <paramref name="at"/> is at the front pocket of the holding side, in the head's yaw frame.</summary>
    private static bool AtPocket(Vector3 at, Transform3D head, bool leftHanded)
    {
        var local = XrRig.YawOnly(head.Basis).Inverse() * (at - head.Origin);
        float side = leftHanded ? local.X : -local.X;   // toward the holding side
        float below = -local.Y, ahead = -local.Z;
        return below is > PocketBelowMin and < PocketBelowMax && side is > PocketSideMin and < PocketSideMax && ahead > -PocketAhead;
    }

    private void Tick(bool both, float strength) =>
        XrSession.Rig?.Rumble(strength, 0.012f, both);
}
