using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// Floating aboard the space shuttle (#1493, <c>docs/notes/player/shuttle-aboard.md</c>): walking about its decks, a
/// player feels what the craft feels (<see cref="Airliner.Felt"/>). In free fall (a coast in orbit, the cruise drive) that
/// is nothing, and they float: no floor, pushing off the walls, the seats and the ladder with the stick (toward where they
/// look) and Jump / Crouch (toward the cabin's roof / floor), holding on (stopping) by anything in reach when they let go
/// of it, drifting at the speed they had away from everything. Under the rockets in orbit they fall aft, onto the floor
/// the thrust makes; when what they feel is toward the cabin's floor again (re-entry's lift, the air, the runway, the
/// Moon's ground) they stand and walk as on any deck.
///
/// <para>
/// The motion is the walk's own: the body's velocity relative to the deck, collided against the deck's boxes (all of
/// them walls, <see cref="CharacterBody3D.MotionModeEnum.Floating"/>), carried with it each frame. Others see it from
/// <see cref="DeckPos"/> as any walker aboard, and the figure swims (the swim's pose: treading when held, the stroke
/// when pushing off), so nothing new goes on the wire.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>Floating aboard: free fall, or pressed against what is not the floor (#1493).</summary>
    public bool Floating { get; private set; }

    /// <summary>The speed a push off something gives (m/s), and how fast it is reached and a hold stops one (m/s²).</summary>
    public const float PushSpeed = 2.5f, PushAccel = 6f, HoldAccel = 4f;

    /// <summary>The fastest one floats about the cabin, m/s (a cabin's length in a second).</summary>
    private const float FloatCap = 8f;

    /// <summary>Anything solid within this of the body's middle is in reach of a hand or a foot, m.</summary>
    private const float FloatReach = 1.15f;

    /// <summary>For checks: the felt acceleration aboard last step (world axes), NaN off a spacecraft's deck.</summary>
    public Vector3 FeltAboard { get; private set; } = new(float.NaN, 0, 0);

    /// <summary>For checks: something was in reach to push off or hold on to last step.</summary>
    public bool FloatInReach { get; private set; }

    /// <summary>For checks: a scripted wish while floating (world axes, m/s); null: the player's input or the bot's walk.</summary>
    public Vector3? FloatWish { get; set; }

    private SphereShape3D? _reachShape;
    private PhysicsShapeQueryParameters3D? _reachQuery;

    /// <summary>
    /// What the deck under this player makes them feel (world axes, m/s²), or null when it is not a spacecraft's (the
    /// walk's own gravity then). The craft's own <see cref="Airliner.Felt"/> where it is stepped here (its pilot's peer,
    /// a parked one's authority); elsewhere from what the pose says: nothing in the drive, the rockets' push above the
    /// Space handover, else the body's gravity (walking).
    /// </summary>
    private Vector3? FeltOnDeck()
    {
        if (!Aboard || HostNamed(DeckOn) is not { } host || RideOfHost(host) is not Airliner { Spec.Spacecraft: true } jet) return null;
        switch (host)
        {
            case FootPlayer p when p.Vehicle is Airliner own:
                return own.Felt;
            case VehicleBody v when v.IsMultiplayerAuthority():
                return jet.Felt;
        }
        // a copy: the pose's drive bit and the spool, and the height over the body the frame is on
        var look = host is FootPlayer pilot ? Airliner.ShuttleLook(Airliner.LookOf(pilot.Anim)) : default;
        if (look.Drive) return Vector3.Zero;
        if (Origin is not { } origin) return null;
        var body = CelestialBody.Of(origin.Frame.Axes?.Body ?? BodyId.Earth);
        if (origin.AltitudeOf(host.GlobalPosition) < body.SpaceEnterM) return null;
        // the orbiter's three rockets: 2.1 g at full spool (shuttle.md)
        var nose = SectionFrame(host, 0) is { } frame ? -frame.GlobalBasis.Z.Normalized() : Vector3.Forward;
        return -nose * look.Spool * 2.1f * 9.81f;
    }

    /// <summary>
    /// Each physics step on the owner's peer, aboard, before the walk: floating or not by what is felt (with a little
    /// hysteresis), and the float's motion when floating. True when it moved the body (the walk is skipped).
    /// </summary>
    private bool ZeroGPhysics(float dt)
    {
        var felt = FeltOnDeck();
        FeltAboard = felt ?? new Vector3(float.NaN, 0, 0);
        if (felt is not { } f || !_deckCarried)
        {
            if (Floating) StopFloating();
            return false;
        }
        var up = _carriedFrom.Basis.Y.Normalized();
        if (!ShuttleFlight.Floats(f, up, Floating))
        {
            if (Floating) StopFloating();
            return false;
        }
        if (!Floating) StartFloating();

        // what this body wants: the stick toward where it looks, Jump / Crouch toward the cabin's roof / floor
        var wish = FloatWish ?? WishWhileFloating(up);
        FloatInReach = SomethingInReach();
        var v = Velocity;
        if (FloatInReach)
        {
            // a hand or a foot on something: pushing off toward the wish, or holding on (stopping) without one
            v = wish.LengthSquared() > 0.01f
                ? v.MoveToward(wish.LimitLength(1f) * PushSpeed, PushAccel * dt)
                : v.MoveToward(Vector3.Zero, HoldAccel * dt);
        }
        // and whatever the craft makes one feel: nothing coasting, aft under the rockets
        v += f * dt;
        Velocity = v.LimitLength(FloatCap);
        MoveAndSlide();
        _fallSpeed = 0f;
        _airTime = 0f;
        // third person the body turns to where it pushes, first person it is where one looks
        if (_thirdPerson) FaceTravel(dt, (wish - up * wish.Dot(up)) with { Y = 0 });
        UpdateCameraFeel(dt, false, false);
        return true;
    }

    /// <summary>The player's (or a bot's) wish while floating, world axes, at most 1: the stick in the view's full basis.</summary>
    private Vector3 WishWhileFloating(Vector3 up)
    {
        if (WalkControls?.Invoke() is { } walk) return walk.Wish.LimitLength(1f);
        if (Npc) return Vector3.Zero;
        var input = PlayerInput.Move;
        var look = new Basis(Vector3.Up, _viewYaw) * new Basis(Vector3.Right, _pitch);
        var wish = look * new Vector3(input.X, 0, input.Y);
        float climb = (PlayerInput.Held(PlayerInput.Jump) ? 1f : 0f) - (PlayerInput.Held(PlayerInput.CrouchSlide) ? 1f : 0f);
        return (wish + up * climb).LimitLength(1f);
    }

    /// <summary>A deck's box within <see cref="FloatReach"/> of the body's middle: a wall, a seat, the ladder, a locker.</summary>
    private bool SomethingInReach()
    {
        _reachShape ??= new SphereShape3D { Radius = FloatReach };
        _reachQuery ??= new PhysicsShapeQueryParameters3D { Shape = _reachShape, CollisionMask = DeckLayer, CollideWithAreas = false };
        _reachQuery.Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * 0.9f);
        return GetWorld3D().DirectSpaceState.IntersectShape(_reachQuery, 1).Count > 0;
    }

    private void StartFloating()
    {
        Floating = true;
        MotionMode = MotionModeEnum.Floating;
        _stumble = Vector3.Zero;
        GD.Print($"[zerog] {Name} floats aboard");
    }

    private void StopFloating()
    {
        Floating = false;
        FloatInReach = false;
        MotionMode = MotionModeEnum.Grounded;
        // the fall back onto the floor is a few centimetres, not the float's speed
        _fallSpeed = 0f;
        GD.Print($"[zerog] {Name} stands again");
    }

    /// <summary>
    /// The floater's figure (#1493): the swim's, so every peer draws it as it is (<see cref="ApplySwimFigure"/>): treading,
    /// sculling, while held or drifting; the stroke along the push while pushing off; laid along the cabin's up.
    /// </summary>
    private void PublishFloatPose(float dt)
    {
        DanceId = 0;
        _airTime = 0f;
        var rel = Velocity;
        bool pushing = FloatInReach && (FloatWish ?? Vector3.Zero).LengthSquared() + PlayerInput.Move.LengthSquared() > 0.01f;
        var style = pushing ? Avatar.SwimStyle.Under : Avatar.SwimStyle.Tread;
        _swimMove = rel.Length();
        _swimPhase = Avatar.HumanMeshBuilder.AdvanceSwim(_swimPhase, style, Mathf.Max(_swimMove, 0.4f), dt);
        // a slow drift of the whole figure, as a floating body turns: about its middle, easing to the push's way
        float lay = pushing && rel.LengthSquared() > 0.05f
            ? Mathf.Atan2(rel.Y, new Vector2(rel.X, rel.Z).Length()) - Mathf.Pi * 0.5f
            : -0.35f + 0.25f * Mathf.Sin((float)GameClock.Now * 0.4f);
        _swimLay = Mathf.Lerp(_swimLay, lay, 1f - Mathf.Exp(-3f * dt));
        PoseKind = PoseSwim;
        Anim = new Vector4(_swimMove, _swimPhase, (float)style, 0f);
        var pivot = new Vector3(0, SwimPivot, 0);
        BodyPose = new Transform3D(Basis.Identity, pivot) * new Transform3D(new Basis(Vector3.Right, _swimLay), Vector3.Zero)
                   * new Transform3D(Basis.Identity, -pivot);
    }

    /// <summary>The prompts while floating (#1493): what the stick, Jump and Crouch do.</summary>
    public IEnumerable<(string Action, string Text)> FloatHints()
    {
        if (!Floating) yield break;
        yield return (PlayerInput.Jump, "Push up");
        yield return (PlayerInput.CrouchSlide, "Push down");
    }

    // ---- the side hatch: E inside by it gets out, outside it is the way in ------------------------------------------

    /// <summary>How near the hatch's inside one must stand to climb out by it, m.</summary>
    private const float HatchReach = 1.1f;

    /// <summary>
    /// The shuttle this player walks about in when they stand by its hatch inside and may climb out (#1493): not in the
    /// orbit seam or the drive (the cabin is pressurised: nobody floats out), nor while floating. Null otherwise.
    /// </summary>
    private (Node3D Host, Rideable Ride, Transform3D Frame)? HatchHere()
    {
        if (!Aboard || Floating || HostNamed(DeckOn) is not { } host || RideOfHost(host) is not Airliner { Kind: RideKind.Shuttle } jet
            || SectionFrame(host, 0) is not { } node) return null;
        var frame = node.GlobalTransform.Orthonormalized();
        if (GlobalPosition.DistanceTo(frame * Avatar.AircraftMeshBuilder.Flip(Avatar.ShuttleDeck.HatchInside)) > HatchReach) return null;
        if (FeltOnDeck() is { } f && f.LengthSquared() < ShuttleFlight.StandAbove * ShuttleFlight.StandAbove) return null;
        return (host, jet, frame);
    }

    /// <summary>
    /// E by the shuttle's hatch inside (#1493): out through it, onto the ground beside the hull (or into the air beside it,
    /// a bail-out, at the craft's speed). True when there was a hatch to use.
    /// </summary>
    private bool TryShuttleHatch()
    {
        if (HatchHere() is not { } at) return false;
        var velocity = VelocityOfHost(at.Host);
        bool grounded = IsOnFloor() && velocity.LengthSquared() < 4f;
        // out to the left, the hatch's side: the walker's own right is no guide inside
        var left = at.Frame.Basis * Vector3.Left;
        left = (left with { Y = 0 }).Normalized();
        // measured from the middle, as getting out of a seat is: the hull's half width and a step clear of it
        var door = at.Frame * Avatar.AircraftMeshBuilder.Flip(new Vector3(0f, 0f, Avatar.ShuttleLayout.HatchZ));
        float side = Mathf.Max(at.Ride.BodyRadius, at.Ride.ParkedBox.Size.X * 0.5f) + BodyRadius + 0.5f;
        LeaveDeck(keepVelocity: true);
        _handoverSpot = null;
        GlobalPosition = FindExit(door, left, side, at.Frame, at.Ride, grounded);
        GD.Print($"[zerog] {Name} climbs out by the shuttle's hatch");
        return true;
    }

    /// <summary>
    /// A bot's (and a check's) way out of the shuttle it stands in (#1493): put by the hatch inside and out through it, as
    /// E there does. A bot does not find its way down the ladder; a player walks it. False when not aboard a shuttle or
    /// the hatch cannot be used now (floating, in Space).
    /// </summary>
    public bool OutByShuttleHatch()
    {
        if (!Aboard || HostNamed(DeckOn) is not { } host || RideOfHost(host) is not Airliner { Kind: RideKind.Shuttle }
            || SectionFrame(host, 0) is not { } node) return false;
        GlobalPosition = node.GlobalTransform.Orthonormalized() * Avatar.AircraftMeshBuilder.Flip(Avatar.ShuttleDeck.HatchInside);
        return TryShuttleHatch();
    }

    /// <summary>The prompt by the hatch inside: "Out by the hatch"; null when it cannot be used.</summary>
    public string? HatchHint => HatchHere() != null ? "Out by the hatch" : null;
}
