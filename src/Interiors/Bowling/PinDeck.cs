using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// One lane's pin deck simulated for one ball (#1323), on the server (or offline, on the one peer that
/// is the server): ten pins and the ball as Jolt rigid bodies in a physics space of their own, built
/// from the lane's numbers (<see cref="BowlingSizes"/>), never from the building, which the dedicated
/// server does not build. It steps with the engine's physics ticks; <see cref="Record"/> keeps each
/// tick's poses, which go to the peers inside to be replayed. Freed when the roll has settled.
///
/// <para>
/// Frame: the lane's, as <see cref="BowlingBall"/>'s: x across from the lane's centre (+x the bowler's
/// left), z from the foul line, y up from the deck (a pin's base at 0).
/// </para>
/// </summary>
public sealed class PinDeck : IDisposable
{
    /// <summary>A pin's mass [S: 1.5-1.6 kg] and a house ball's [E: 6.4 kg, a 14 lb ball].</summary>
    public const float PinMass = 1.55f, BallMass = 6.35f;
    /// <summary>A roll is over once nothing moves, or after this long whatever moves.</summary>
    public const float MaxTime = 6f;
    /// <summary>Pins and ball standing still for this long end the roll early.</summary>
    private const float StillFor = 0.6f;

    /// <summary>The poses of one physics tick: the ball first, then the ten pins.</summary>
    public readonly record struct Frame(float Time, Transform3D[] Bodies);

    private readonly BowlingSizes.Run _lane;
    private readonly Rid _space;
    private readonly List<Rid> _rids = new();
    private readonly List<Rid> _shapes = new();
    private readonly Rid _ball;
    private readonly Rid[] _pins = new Rid[10];
    private readonly bool[] _present = new bool[10];
    public readonly List<Frame> Record = new();
    private float _time, _still;

    public bool Done { get; private set; }

    /// <summary>
    /// A deck with the pins of <paramref name="standing"/> (bit i = pin i+1) on their spots, as the
    /// deadwood was swept and the standing ones re-spotted, and the ball arriving as its run left it.
    /// </summary>
    public PinDeck(BowlingSizes.Run lane, ushort standing, BowlingBall.State ball)
    {
        _lane = lane;
        var ps = PhysicsServer3D.Singleton;
        _space = ps.SpaceCreate();
        ps.AreaSetParam(_space, PhysicsServer3D.AreaParameter.Gravity, 9.81f);
        ps.AreaSetParam(_space, PhysicsServer3D.AreaParameter.GravityVector, Vector3.Down);
        ps.SpaceSetActive(_space, true);

        // the lane's end and the deck, the gutters a little lower, the kickbacks, the pit and its cushion
        float half = BowlingSizes.HalfLane, gutter = BowlingSizes.Gutter, wall = half + gutter;
        float z0 = lane.HeadPin - BowlingBall.HandOver - 1f, end = lane.LaneEnd, pit = end + 0.6f * lane.Scale;
        var floor = Static();
        AddBox(floor, new Vector3(-half, -0.1f, z0), new Vector3(half, 0, end));
        AddBox(floor, new Vector3(-wall, -0.15f, z0), new Vector3(-half, -0.048f, end));
        AddBox(floor, new Vector3(half, -0.15f, z0), new Vector3(wall, -0.048f, end));
        AddBox(floor, new Vector3(-wall - 0.1f, -0.5f, z0), new Vector3(-wall, 0.9f, pit));
        AddBox(floor, new Vector3(wall, -0.5f, z0), new Vector3(wall + 0.1f, 0.9f, pit));
        // the pit's floor reaches under the deck too: nothing that drops in can fall out of the world
        AddBox(floor, new Vector3(-wall, -0.6f, z0), new Vector3(wall, -0.5f, pit));
        AddBox(floor, new Vector3(-wall, -0.6f, pit), new Vector3(wall, 0.9f, pit + 0.1f));
        ps.BodySetParam(floor, PhysicsServer3D.BodyParameter.Friction, 0.2f);
        ps.BodySetParam(floor, PhysicsServer3D.BodyParameter.Bounce, 0.25f);

        var pinShape = ps.ConvexPolygonShapeCreate();
        ps.ShapeSetData(pinShape, PinHull());
        _shapes.Add(pinShape);
        for (int i = 0; i < 10; i++)
        {
            _present[i] = (standing & (1 << i)) != 0;
            if (!_present[i]) continue;
            var body = Body(pinShape, PinMass, new Transform3D(Basis.Identity, Spot(lane, i)));
            ps.BodySetParam(body, PhysicsServer3D.BodyParameter.Friction, 0.3f);
            ps.BodySetParam(body, PhysicsServer3D.BodyParameter.Bounce, 0.55f);
            _pins[i] = body;
        }

        var ballShape = ps.SphereShapeCreate();
        ps.ShapeSetData(ballShape, BowlingSizes.BallRadius);
        _shapes.Add(ballShape);
        // the deck is the lane's height here: the ball's centre a radius above it
        _ball = Body(ballShape, BallMass, new Transform3D(Basis.Identity, ball.Pos with { Y = BowlingSizes.BallRadius }));
        ps.BodySetParam(_ball, PhysicsServer3D.BodyParameter.Friction, 0.2f);
        ps.BodySetParam(_ball, PhysicsServer3D.BodyParameter.Bounce, 0.1f);
        ps.BodySetState(_ball, PhysicsServer3D.BodyState.LinearVelocity, ball.Vel);
        ps.BodySetState(_ball, PhysicsServer3D.BodyState.AngularVelocity, ball.Spin);
        Snapshot();
    }

    /// <summary>A pin's spot on the deck, pin <paramref name="i"/> (0 the head pin), as <see cref="LaneGeometry.PinSpot"/> lays them.</summary>
    public static Vector3 Spot(BowlingSizes.Run lane, int i)
    {
        int row = i < 1 ? 0 : i < 3 ? 1 : i < 6 ? 2 : 3;
        int first = row * (row + 1) / 2;
        return new Vector3((row / 2f - (i - first)) * BowlingSizes.PinSpacing * lane.Scale, 0, lane.HeadPin + row * BowlingSizes.PinRow * lane.Scale);
    }

    /// <summary>
    /// Records the poses the physics step just left and judges whether the roll is over. Called once
    /// per physics tick by the deck's owner, after the engine stepped the space.
    /// </summary>
    public void Tick(float delta)
    {
        if (Done) return;
        _time += delta;
        var ps = PhysicsServer3D.Singleton;
        bool moving = ps.BodyGetState(_ball, PhysicsServer3D.BodyState.LinearVelocity).AsVector3().LengthSquared() > 0.0025f
            && ps.BodyGetState(_ball, PhysicsServer3D.BodyState.Transform).AsTransform3D().Origin.Z < _lane.LaneEnd;
        for (int i = 0; i < 10 && !moving; i++)
            moving = _present[i] && (ps.BodyGetState(_pins[i], PhysicsServer3D.BodyState.LinearVelocity).AsVector3().LengthSquared() > 0.0025f
                || ps.BodyGetState(_pins[i], PhysicsServer3D.BodyState.AngularVelocity).AsVector3().LengthSquared() > 0.01f);
        _still = moving ? 0 : _still + delta;
        Cushion(_ball);
        for (int i = 0; i < 10; i++)
            if (_present[i]) Cushion(_pins[i]);
        Snapshot();
        if (_time >= MaxTime || _time > 1f && _still >= StillFor) Done = true;
    }

    /// <summary>
    /// The pit's padded curtain: what flies past the deck is caught and dropped, never sent back onto
    /// it, as a real cushion does. A body over the pit loses most of its speed, and none of it back.
    /// </summary>
    private void Cushion(Rid body)
    {
        var ps = PhysicsServer3D.Singleton;
        if (ps.BodyGetState(body, PhysicsServer3D.BodyState.Transform).AsTransform3D().Origin.Z < _lane.LaneEnd + 0.1f) return;
        var v = ps.BodyGetState(body, PhysicsServer3D.BodyState.LinearVelocity).AsVector3();
        ps.BodySetState(body, PhysicsServer3D.BodyState.LinearVelocity, new Vector3(v.X * 0.6f, Mathf.Min(v.Y, 0), Mathf.Max(v.Z, 0) * 0.6f));
    }

    private void Snapshot()
    {
        var ps = PhysicsServer3D.Singleton;
        var bodies = new Transform3D[11];
        bodies[0] = ps.BodyGetState(_ball, PhysicsServer3D.BodyState.Transform).AsTransform3D();
        for (int i = 0; i < 10; i++)
            bodies[i + 1] = _present[i] ? ps.BodyGetState(_pins[i], PhysicsServer3D.BodyState.Transform).AsTransform3D() : Gone;
        Record.Add(new Frame(_time, bodies));
    }

    /// <summary>Where a pin is that was not on the deck for this ball: below the pit, out of sight.</summary>
    public static readonly Transform3D Gone = new(Basis.Identity, new Vector3(0, -5, 0));

    /// <summary>
    /// The pins still standing as the roll left them (bit i = pin i+1): upright, on the deck, in its
    /// width. One knocked off its spot but still upright stands; the machine re-spots it.
    /// </summary>
    public ushort Standing()
    {
        var last = Record[^1].Bodies;
        ushort mask = 0;
        for (int i = 0; i < 10; i++)
        {
            if (!_present[i]) continue;
            var t = last[i + 1];
            bool upright = t.Basis.Y.Normalized().Y > 0.94f;
            bool onDeck = t.Origin.Y > -0.02f && Mathf.Abs(t.Origin.X) < BowlingSizes.HalfLane && t.Origin.Z < _lane.LaneEnd;
            if (upright && onDeck) mask |= (ushort)(1 << i);
        }
        return mask;
    }

    /// <summary>How many pins this ball knocked down: those there before that do not stand now.</summary>
    public int Knocked()
    {
        int n = 0;
        ushort left = Standing();
        for (int i = 0; i < 10; i++)
            if (_present[i] && (left & (1 << i)) == 0) n++;
        return n;
    }

    private Rid Static()
    {
        var ps = PhysicsServer3D.Singleton;
        var body = ps.BodyCreate();
        ps.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
        ps.BodySetSpace(body, _space);
        _rids.Add(body);
        return body;
    }

    private void AddBox(Rid body, Vector3 min, Vector3 max)
    {
        var ps = PhysicsServer3D.Singleton;
        var shape = ps.BoxShapeCreate();
        ps.ShapeSetData(shape, (max - min) / 2);
        _shapes.Add(shape);
        ps.BodyAddShape(body, shape, new Transform3D(Basis.Identity, (min + max) / 2));
    }

    private Rid Body(Rid shape, float mass, Transform3D at)
    {
        var ps = PhysicsServer3D.Singleton;
        var body = ps.BodyCreate();
        ps.BodySetMode(body, PhysicsServer3D.BodyMode.Rigid);
        ps.BodyAddShape(body, shape);
        ps.BodySetParam(body, PhysicsServer3D.BodyParameter.Mass, mass);
        ps.BodySetEnableContinuousCollisionDetection(body, true);
        ps.BodySetSpace(body, _space);
        ps.BodySetState(body, PhysicsServer3D.BodyState.Transform, at);
        _rids.Add(body);
        return body;
    }

    /// <summary>A pin's hull: the turned outline at eight points round each of its rings.</summary>
    private static Vector3[] PinHull()
    {
        var pts = new List<Vector3>();
        foreach (var (y, r) in PinOutline)
        {
            if (r <= 0) { pts.Add(new Vector3(0, y, 0)); continue; }
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.Tau / 8;
                pts.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
            }
        }
        return pts.ToArray();
    }

    /// <summary>
    /// The pin's outline for its hull: the body only, the neck filled out (a hull is convex anyway),
    /// the same base, belly and height as the drawn pin [S].
    /// </summary>
    private static readonly (float Y, float R)[] PinOutline =
    {
        (0f, 0.0258f), (0.019f, 0.036f), (0.057f, 0.0478f), (0.102f, 0.0567f), (0.143f, 0.0605f), (0.19f, 0.0548f),
        (0.2286f, 0.0416f), (0.27f, 0.026f), (0.3302f, 0.0315f), (0.3683f, 0.022f), (0.381f, 0f),
    };

    private bool _disposed;

    /// <summary>Frees the space and its bodies; its <see cref="Record"/> stays readable. Safe to call again.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ps = PhysicsServer3D.Singleton;
        foreach (var rid in _rids) ps.FreeRid(rid);
        foreach (var rid in _shapes) ps.FreeRid(rid);
        ps.SpaceSetActive(_space, false);
        ps.FreeRid(_space);
        _rids.Clear();
        _shapes.Clear();
    }
}
