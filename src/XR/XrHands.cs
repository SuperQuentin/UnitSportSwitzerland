using Godot;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// What the real hands take hold of (#243): the steering wheel from the driver's seat, and doors
/// on foot. Squeezing a grip is the hand closing.
///
/// <para>
/// <b>The wheel.</b> A grip closed with the hand on the rim catches it there: the hand's marker
/// snaps onto the rim and rides round with it. Turning the hands about the column turns the wheel,
/// several turns up to the vehicle's lock, sent as an absolute angle (<see cref="XrSession.WheelAngle"/>,
/// the same channel a real wheel uses, #68). With both hands on, the wheel turns by the mean of
/// their turns. A hand lets go when its grip opens, or when it is pulled further than
/// <see cref="SlipOff"/> off the point it holds; with no hand on, the sticks steer again and the
/// wheel self-centres.
/// </para>
///
/// <para>
/// <b>Doors.</b> On foot, a grip closed with the hand at a car door or a building's door works it,
/// through the same server-checked paths as G and E. A grip that holds the wheel or worked a door
/// is not also a shoulder press (<see cref="XrPad.LeftGripBusy"/>) until it opens.
/// </para>
///
/// <para>
/// <b>Things on the ground (#437).</b> On foot, a grip closed with the hand at a dropped item takes
/// it, at a radio opens its panel: the same paths as E on the thing pointed at. A grip squeezed on
/// nothing and let go with a fling drops the item in hand (Q), so a hand that throws throws.
/// </para>
/// </summary>
internal sealed class XrHands
{
    /// <summary>The grip closes above this and opens below <see cref="GripOpen"/>.</summary>
    private const float GripClose = 0.7f, GripOpen = 0.35f;
    /// <summary>The hand must be this near the rim's centre line to catch it, m.</summary>
    private const float RimCatch = 0.14f;
    /// <summary>Pulled this far off the point it holds, a hand lets go, m.</summary>
    private const float SlipOff = 0.22f;
    /// <summary>A light tick in the hands every this much the wheel turns, radians.</summary>
    private const float TickEvery = 0.4f;
    /// <summary>A hand this near a dropped item or a radio takes it, m.</summary>
    private const float TakeReach = 0.3f;
    /// <summary>A hand this far from the eyes, in front, is reaching out: its grip does what E does there (#437), m.</summary>
    private const float ReachOut = 0.45f;
    /// <summary>A hand this far below the eyes, and to a side, is at the hip: its grip steps the hotbar, m.</summary>
    private const float HipDrop = 0.75f, HipSide = 0.12f;
    /// <summary>An empty squeeze let go this fast (in the play space, so walking does not count) drops the held item, m/s.</summary>
    private const float FlingSpeed = 2.5f;

    private sealed class Hand
    {
        public required XRController3D Ctl;
        public required Node3D Marker;
        public bool Closed;
        /// <summary>The grip is spent (holding, or it worked a door) until it opens.</summary>
        public bool Busy;
        public bool OnWheel;
        /// <summary>Closed on nothing: let go with a fling, it drops the held item.</summary>
        public bool EmptySqueeze;
        /// <summary>Where the hand was last frame in the play space, for its speed.</summary>
        public Vector3 PrevLocal;
        public float Speed;
        /// <summary>The point held, in the wheel node's own frame: it turns with the wheel.</summary>
        public Vector3 HeldLocal;
        /// <summary>The hand's direction from the hub, in the column's (the wheel's parent's) frame.</summary>
        public Vector3 PrevRadial;
    }

    private readonly Hand _left, _right;
    private Transform3D _head;
    /// <summary>The wheel's turn while held, radians, + anticlockwise from the seat (<see cref="Avatar.CarRig.WheelTurn"/>).</summary>
    private float _turn, _lastTick;

    public XrHands(XRController3D left, Node3D leftMarker, XRController3D right, Node3D rightMarker)
    {
        _left = new Hand { Ctl = left, Marker = leftMarker };
        _right = new Hand { Ctl = right, Marker = rightMarker };
    }

    public bool LeftBusy => _left.Busy;
    public bool RightBusy => _right.Busy;

    public void Update(FootPlayer? player, Transform3D head, float dt)
    {
        _head = head;
        var grip = WheelOf(player);
        foreach (var hand in new[] { _left, _right })
        {
            var local = hand.Ctl.Position;
            hand.Speed = (local - hand.PrevLocal).Length() / Mathf.Max(dt, 1e-3f);
            hand.PrevLocal = local;

            float g = hand.Ctl.GetHasTrackingData() ? hand.Ctl.GetFloat("grip") : 0f;
            bool closing = !hand.Closed && g > GripClose;
            if (hand.Closed && g < GripOpen)
            {
                hand.Closed = false;
                hand.Busy = false;
                if (hand.OnWheel) LetGo(hand, buzz: false);
                if (hand.EmptySqueeze && hand.Speed > FlingSpeed && player is { Ride: RideKind.OnFoot, RidingWith: 0 }
                    && Input.MouseMode == Input.MouseModeEnum.Captured)
                    XrPad.Tap(Core.PlayerInput.DropItem);
                hand.EmptySqueeze = false;
            }
            else if (closing) hand.Closed = true;

            if (hand.OnWheel && grip == null) LetGo(hand, buzz: false);
            if (closing && !hand.Busy) TryTake(hand, player, grip);
        }
        UpdateWheel(player, grip);
    }

    /// <summary>The driver's wheel, when this player sits at it in first person.</summary>
    private static (Node3D Wheel, Vector3 Axis, float Radius, float Lock, float Turn)? WheelOf(FootPlayer? player)
    {
        if (player is not { InCockpit: true, Vehicle: { WheelLock: > 0f } ride } || player.Ride == RideKind.OnFoot)
            return null;
        return player.Visual switch
        {
            Avatar.CarRig { SteeringGrip: { } g } car => (g.Wheel, g.Axis, g.Radius, ride.WheelLock, car.WheelTurn),
            Avatar.HeavyRig { SteeringGrip: { } g } heavy => (g.Wheel, g.Axis, g.Radius, ride.WheelLock, heavy.WheelTurn),
            _ => null,
        };
    }

    private void TryTake(Hand hand, FootPlayer? player, (Node3D Wheel, Vector3 Axis, float Radius, float Lock, float Turn)? grip)
    {
        var at = hand.Ctl.GlobalPosition;
        if (grip is { } w)
        {
            var (axis, radial) = Split(w.Wheel, w.Axis, at);
            float along = (at - w.Wheel.GlobalPosition).Dot(axis);
            if (radial.LengthSquared() < 1e-4f
                || new Vector2(along, radial.Length() - w.Radius).Length() > RimCatch) return;
            // the first hand on takes the wheel where it stands
            if (!_left.OnWheel && !_right.OnWheel) _lastTick = _turn = w.Turn;
            var held = w.Wheel.GlobalPosition + radial.Normalized() * w.Radius;
            hand.HeldLocal = w.Wheel.GlobalTransform.AffineInverse() * held;
            hand.PrevRadial = ToColumn(w.Wheel, radial);
            hand.OnWheel = hand.Busy = true;
            Buzz(hand, 0.45f, 0.05f);
            return;
        }
        if (player is not { Ride: RideKind.OnFoot, RidingWith: 0 }) return;
        if (player.TryToggleCarDoor(at) || Interiors.InteriorManager.Instance?.TryDoorByHand(player, at) == true
            || TakeAt(at) || AtHip(at) || ReachingOut(player, at))
        {
            hand.Busy = true;
            Buzz(hand, 0.5f, 0.06f);
            return;
        }
        hand.EmptySqueeze = true;
    }

    /// <summary>
    /// A hand at the hip steps the hotbar (#437): the right hip to the next item, the left to the
    /// previous, as a holster would hand them over.
    /// </summary>
    private bool AtHip(Vector3 at)
    {
        if (Input.MouseMode != Input.MouseModeEnum.Captured) return false;
        var local = _head.AffineInverse() * at;
        if (local.Y > -HipDrop || Mathf.Abs(local.X) < HipSide) return false;
        XrPad.Tap(local.X > 0f ? Core.PlayerInput.NextItem : Core.PlayerInput.PrevItem);
        return true;
    }

    /// <summary>
    /// A hand reaching out in front closes on what E would act on there (#437): a seat, a ladder, a
    /// crate, a cupboard, a car's door, a building's. Never the dance, which is not a thing.
    /// </summary>
    private bool ReachingOut(FootPlayer player, Vector3 at)
    {
        if (Input.MouseMode != Input.MouseModeEnum.Captured) return false;
        var to = at - _head.Origin;
        if (to.Length() < ReachOut || (-_head.Basis.Z).Dot(to.Normalized()) < 0.5f) return false;
        return player.TryInteract(byHand: true);
    }

    /// <summary>A dropped item at the hand goes into the inventory; a radio there opens its panel.</summary>
    private static bool TakeAt(Vector3 at)
    {
        if (Input.MouseMode != Input.MouseModeEnum.Captured) return false;   // a menu is open
        Node3D? best = null;
        float bestD = TakeReach;
        if (Items.DroppedItems.Instance is { } dropped)
            foreach (var item in dropped.Items)
                if (!dropped.IsClaimed(item) && item.GlobalPosition.DistanceTo(at) is var d && d < bestD) { bestD = d; best = item; }
        if (Items.RadioManager.Instance is { } radios)
            foreach (var node in radios.GetChildren())
                if (node is Items.RadioBody r && r.GlobalPosition.DistanceTo(at) is var d && d < bestD) { bestD = d; best = r; }
        switch (best)
        {
            case Items.DroppedItem item when Items.ItemController.Instance is { } items:
                items.PickUp(item);
                return true;
            case Items.RadioBody radio when Items.RadioUi.Instance is { } ui:
                ui.Open(radio);
                return true;
            default:
                return false;
        }
    }

    private void UpdateWheel(FootPlayer? player, (Node3D Wheel, Vector3 Axis, float Radius, float Lock, float Turn)? grip)
    {
        if (grip is not { } w || !_left.OnWheel && !_right.OnWheel)
        {
            XrSession.WheelAngle = float.NaN;
            return;
        }
        float sum = 0f;
        int held = 0;
        foreach (var hand in new[] { _left, _right })
        {
            if (!hand.OnWheel) continue;
            var (_, radial) = Split(w.Wheel, w.Axis, hand.Ctl.GlobalPosition);
            if (radial.LengthSquared() > 1e-4f)
            {
                var now = ToColumn(w.Wheel, radial);
                sum += hand.PrevRadial.SignedAngleTo(now, w.Axis);
                hand.PrevRadial = now;
            }
            held++;
        }
        float half = w.Lock * 0.5f;
        _turn = Mathf.Clamp(_turn + sum / held, -half, half);
        XrSession.WheelAngle = -_turn;

        if (Mathf.Abs(_turn - _lastTick) > TickEvery)
        {
            _lastTick = _turn;
            foreach (var hand in new[] { _left, _right })
                if (hand.OnWheel) Buzz(hand, 0.12f, 0.02f);
        }

        // each held hand sits on its point of the rim, as the wheel is drawn; pulled well off, it lets go
        foreach (var hand in new[] { _left, _right })
        {
            if (!hand.OnWheel) continue;
            var point = w.Wheel.GlobalTransform * hand.HeldLocal;
            if (point.DistanceTo(hand.Ctl.GlobalPosition) > SlipOff)
            {
                LetGo(hand, buzz: true);
                continue;
            }
            hand.Marker.GlobalTransform = new Transform3D(hand.Ctl.GlobalBasis, point);
        }
        if (!_left.OnWheel && !_right.OnWheel) XrSession.WheelAngle = float.NaN;
    }

    /// <summary>The column in world space, and a point's offset from the hub across it.</summary>
    private static (Vector3 Axis, Vector3 Radial) Split(Node3D wheel, Vector3 columnAxis, Vector3 point)
    {
        var axis = (wheel.GetParentNode3D().GlobalBasis * columnAxis).Normalized();
        var v = point - wheel.GlobalPosition;
        return (axis, v - axis * v.Dot(axis));
    }

    /// <summary>A world direction in the column's frame, which the vehicle carries round with it.</summary>
    private static Vector3 ToColumn(Node3D wheel, Vector3 world) =>
        wheel.GetParentNode3D().GlobalBasis.Orthonormalized().Inverse() * world;

    private static void LetGo(Hand hand, bool buzz)
    {
        hand.OnWheel = false;
        hand.Marker.Transform = Transform3D.Identity;
        if (buzz) Buzz(hand, 0.3f, 0.04f);
    }

    private static void Buzz(Hand hand, float strength, float seconds)
    {
        if (Core.GameSettings.Current.Vibration)
            hand.Ctl.TriggerHapticPulse("haptic", 0.0, strength, seconds, 0.0);
    }
}
