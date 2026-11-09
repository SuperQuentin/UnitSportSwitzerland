using System.Diagnostics;
using System.Globalization;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// The floating origin (#185): keeps world space near the camera, so float32 stays precise however
/// far from the starting origin the world reaches.
///
/// <para>
/// When the camera gets more than <see cref="ThresholdM"/> from the origin, the origin moves to
/// the camera's position snapped to <see cref="StepM"/> (whole tiles by default, so a tile's
/// corner stays an exact integer and nothing drifts over many shifts), and everything follows
/// in one synchronous call, at the start of a frame, before any other node processes:
/// </para>
/// <list type="number">
/// <item><see cref="WorldOrigin.MoveTo"/>: the new frame.</item>
/// <item>Every node placed in world space moves by the shift: the children of
/// <see cref="IOriginContainer"/> nodes and of plain <c>Node</c>s, and every <c>TopLevel</c>
/// node anywhere. Nested nodes follow their parents for free.</item>
/// <item>Kinematic bodies are teleported in the physics server (<see cref="Teleport"/>), and
/// doppler trackers reset (<see cref="ResetDoppler"/>): neither may read the shift as motion.</item>
/// <item><see cref="IOriginShiftAware.OnOriginShifted"/> on every node that has one, then
/// <see cref="WorldOrigin.Shifted"/>: caches, rings, uniforms.</item>
/// <item>The <c>world_origin_offset</c> shader global, so surface patterns stay put.</item>
/// </list>
///
/// <para>
/// Altitude never shifts: it stays absolute, and under 10 km float32 is finer than 1 mm.
/// Online too: every peer has its own origin, and positions on the wire are LV95. The dedicated
/// server has no shifter: its players can be anywhere, and it measures in LV95.
/// </para>
/// </summary>
public partial class OriginShifter : Node
{
    /// <summary>A plain Node3D whose children are in world space joins this group (see <see cref="IOriginContainer"/>).</summary>
    public const string ContainerGroup = "origin_container";

    /// <summary>
    /// Period of <c>world_origin_offset</c>: a multiple of every surface pattern's period
    /// (parking bays 2.5 x 5 m, vine rows 2 m, mowing stripes 6 m, the building hash's 24 m cells),
    /// so those never jump; small enough that world XZ plus the offset stays precise on the GPU.
    /// Only patterns with no period (water waves, sparkle) jump, when the origin crosses one.
    /// </summary>
    public const double PatternPeriodM = 9600;

    public static OriginShifter? Instance { get; private set; }

    /// <summary>
    /// How the origin follows the camera (#1430 H3): past <see cref="ThresholdM"/> from it (horizontally,
    /// or in 3D when <see cref="Vertical"/>) it moves to the camera. <see cref="Rotates"/>: to an anchor
    /// frame turned to the ellipsoid there; off while tiles are flat LV95, switched on with round tiles
    /// (#1443 G2), for the ground and Space alike.
    /// </summary>
    public readonly record struct ReanchorMode(double ThresholdM, bool Vertical, bool Rotates);

    /// <summary>
    /// The Space regime's mode: every 20 km, turned to the ellipsoid under the camera above 250 km (#1480:
    /// an orbit goes round the whole Earth, where the flat LV95 plane stretches; below, the flat tiles are
    /// still drawn as one plane, so the frame stays flat there; the ground keeps its flat frame until round
    /// tiles, #1443 G2). No rise yet: at 400 km a float still holds 3 cm at the camera, and world Y below
    /// about -2 km is the interiors' band (<c>InteriorManager.InteriorBaseY</c>), which a risen origin puts
    /// the camera in on the way down. The vertical origin (<see cref="OriginFrame.Alt0"/>) waits until the
    /// interiors' band is told apart by altitude rather than by raw Y.
    /// </summary>
    public static ReanchorMode SpaceMode { get; set; } = new(20_000, Vertical: false, Rotates: true);

    /// <summary>
    /// The earth world's ground re-anchor (#1512, G2 C3): past <see cref="ReanchorMode.ThresholdM"/> from the anchor
    /// (horizontally) the world moves to <see cref="FrameAxes.Tangent"/> under the camera at altitude 0, its axes the
    /// old ones turned by the least rotation (0.009° a km), so up and gravity stay the ellipsoid's under the camera.
    /// With <see cref="ReanchorMode.Rotates"/> off (C1's frame), the anchor moves along its own tangent plane, its
    /// axes kept. The earth world has one frame at every altitude: above <see cref="Curvature.SpaceEnterM"/> it
    /// re-anchors past <see cref="SpaceMode"/>'s threshold instead, with no handover (the flat world's Space band
    /// and its two rigid moves exist because its tiles are flat).
    /// </summary>
    public static ReanchorMode GroundMode { get; set; } = new(3000, Vertical: false, Rotates: true);

    /// <summary>The world is the earth world's anchor frame (<c>--frame earth</c>): re-anchored by <see cref="GroundMode"/>.</summary>
    public bool Earth { get; init; }

    /// <summary>Whether the origin follows the camera in Space (above 60 km climbing, until below 50 km).</summary>
    public bool InSpace { get; private set; }

    /// <summary>Whether the Space frame turns now (#1480: above 250 km, until below 240, where no flat tile is drawn).</summary>
    public bool Turned { get; private set; }


    /// <summary>The world's origin, for whatever reads an altitude through it (the spectator's ceiling).</summary>
    public WorldOrigin Origin => _origin;

    /// <summary>Horizontal metres from the origin past which the camera triggers a shift.</summary>
    public double ThresholdM { get; set; } = 3000;

    /// <summary>The new origin is the camera's position snapped to this.</summary>
    public double StepM { get; set; } = 1000;

    /// <summary>Shifts so far, and what the last one cost (the performance overlay shows both).</summary>
    public int ShiftCount { get; private set; }
    public double LastShiftMs { get; private set; }
    public int LastShiftNodes { get; private set; }

    private readonly WorldOrigin _origin;
    private readonly Func<Vector3?> _focus;
    private readonly Func<bool> _allowed;
    private readonly bool _stress;

    /// <summary>
    /// <c>--originyaw &lt;deg&gt;</c> with <c>--originstress</c> (#1430 H3): every shift goes to an anchor frame
    /// turned by this much about the up, one way then back, so every handler meets a rotation.
    /// </summary>
    private readonly float _stressYaw;
    private bool _stressTurned;
    /// <summary><c>--originstress</c> or <c>--originshift</c> set <see cref="ThresholdM"/>: the earth world takes it too.</summary>
    private readonly bool _thresholdGiven;
    private readonly HashSet<string> _warned = new();

    /// <param name="focus">Where precision matters most: the active camera.</param>
    /// <param name="allowed">False while shifting would break something.</param>
    public OriginShifter(WorldOrigin origin, Func<Vector3?> focus, Func<bool> allowed)
    {
        _origin = origin;
        _focus = focus;
        _allowed = allowed;
        Name = "OriginShifter";
        // before every other node's _Process: nothing sees half the world moved
        ProcessPriority = int.MinValue;

        if (ParseStress() is { } stress)
        {
            _stress = true;
            ThresholdM = stress;
            StepM = 1;
            _stressYaw = CmdArgs.Double("--originyaw") is double yaw ? Mathf.DegToRad((float)yaw) : 0f;
            GD.Print($"[origin] stress: shift past {stress:F0} m, to the metre"
                + (_stressYaw != 0 ? $", each shift turning the world {Mathf.RadToDeg(_stressYaw):F1}° (anchor frames, #1430 H3)" : ""));
        }
        else if (ParseThreshold() is { } threshold)
            ThresholdM = threshold;
        _thresholdGiven = _stress || ParseThreshold() != null;
    }

    /// <summary>"--originstress &lt;m&gt;": shift every few metres, so a missed cached position shows at once.</summary>
    public static double? ParseStress() => ParseMetres("--originstress");

    /// <summary>"--originshift &lt;m&gt;": a different threshold, still snapped to whole tiles.</summary>
    public static double? ParseThreshold() => ParseMetres("--originshift");

    private static double? ParseMetres(string flag) => CmdArgs.Double(flag) is double m && m > 0 ? m : null;

    public override void _EnterTree()
    {
        Instance = this;
        PushPatternOffset();
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Process(double delta)
    {
        if (!_allowed() || _focus() is not { } at) return;
        // a camera gone to NaN would shift the whole world to NaN with it (#1006)
        if (!at.IsFinite()) return;

        // the Space regime (#1430 H3): entered climbing past 60 km, left below 50 km. Both handovers are
        // one rigid move of everything, the camera with it: the picture cannot change.
        double altitude = _origin.AltitudeOf(at);
        if (Earth)
        {
            EarthReanchor(at, altitude);
            return;
        }
        bool space = Curvature.SpaceRegime(InSpace, altitude);
        if (space != InSpace)
        {
            InSpace = space;
            var (se, sn) = _origin.ToLv95(at);
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[origin] {(space ? "into" : "out of")} the Space regime at {altitude / 1000:F1} km"));
            double enterAlt0 = SpaceMode.Vertical ? altitude : 0;
            // in at 60 km the frame is flat (it turns from 250 km); out at 50 km it is the ground frame again, flat
            // and on a whole-km corner, whatever it was
            Turned = false;
            if (space) ShiftTo(se, sn, exact: true, alt0: enterAlt0);
            else ShiftTo(se, sn);
            return;
        }
        if (InSpace)
        {
            double alt0 = SpaceMode.Vertical ? altitude : _origin.Alt0;
            // the frame turns only where no flat tile is drawn any more (#1480, above 250 km, back below 240):
            // a change is one rigid move, as the handovers are, to a turned anchor or to the flat frame there
            bool turn = SpaceMode.Rotates && Curvature.TurnRegime(Turned, altitude);
            if (turn != Turned)
            {
                Turned = turn;
                var (te, tn) = _origin.ToLv95(at);
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"[origin] the Space frame {(turn ? "turns" : "stops turning")} at {altitude / 1000:F1} km"));
                ShiftTo(te, tn, exact: true, alt0: alt0, axes: turn ? TurnedAnchor(at, alt0) : null);
                return;
            }
            if (SpaceMode.Vertical ? at.Length() <= SpaceMode.ThresholdM : new Vector2(at.X, at.Z).Length() <= SpaceMode.ThresholdM) return;
            var (pe, pn) = _origin.ToLv95(at);
            ShiftTo(pe, pn, exact: true, alt0: alt0, axes: Turned ? TurnedAnchor(at, alt0) : null);
            return;
        }
        if (new Vector2(at.X, at.Z).Length() <= ThresholdM) return;

        var (e, n) = _origin.ToLv95(at);
        ShiftTo(e, n);
    }

    /// <summary>
    /// The earth world's one frame (#1512, G2 C3): <see cref="GroundMode"/> near the ground, <see cref="SpaceMode"/>'s
    /// threshold in Space, the same turning anchor in both. Crossing into Space or out of it moves nothing: the tiles
    /// are round, so there is no flat band to hand over from.
    /// </summary>
    private void EarthReanchor(Vector3 at, double altitude)
    {
        bool space = Curvature.SpaceRegime(InSpace, altitude);
        if (space != InSpace)
        {
            InSpace = space;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[origin] the earth frame re-anchors every {(space ? SpaceMode : GroundMode).ThresholdM / 1000:F0} km from {altitude / 1000:F1} km"));
        }
        var mode = InSpace ? SpaceMode : GroundMode;
        Turned = mode.Rotates;
        double threshold = _thresholdGiven ? ThresholdM : mode.ThresholdM;
        if (new Vector2(at.X, at.Z).Length() <= threshold) return;
        if (_origin.Frame.Axes is not { } axes) return;

        var (e, n) = _origin.ToLv95(at);
        if (mode.Rotates)
        {
            ShiftTo(e, n, exact: true, axes: TurnedAnchor(at, 0));
            return;
        }
        // C1's frame: the anchor moves to the camera's plane point, to the step, its axes kept
        double step = Math.Max(1, StepM);
        double x = Math.Round(at.X / step) * step, z = Math.Round(at.Z / step) * step;
        ShiftTo(e, n, exact: true, axes: axes with { Origin = axes.ToEcef(x, 0, z) });
    }

    /// <summary>
    /// Moves the origin to (near) an LV95 point now, wherever the camera is: a teleport calls this
    /// first, so the destination is placed close to the new origin rather than far from the old one.
    /// Returns false when shifting is not allowed or would change nothing. <paramref name="exact"/>
    /// puts the origin on the point itself rather than snapping it to <see cref="StepM"/>.
    /// </summary>
    /// <param name="axes">The anchor to move to (a turning shift, <see cref="TurnedAnchor"/>); null for one built from <paramref name="e"/> / <paramref name="n"/>.</param>
    public bool ShiftTo(double e, double n, bool exact = false, double alt0 = 0, FrameAxes? axes = null)
    {
        if (!_allowed()) return false;
        // the earth world stays an anchor frame (#1512): a teleport, a shot or a tool lands on the tangent anchor
        // at altitude 0 under the point, turned from the current axes by the least rotation
        if (Earth && axes == null && _origin.Frame.Axes is { } current)
        {
            axes = FrameAxes.Tangent(SwissGeodesy.ToEcef(e, n, 0), 0, current);
            exact = true;
        }
        double step = Math.Max(1, StepM);
        double newE = exact ? e : Math.Round(e / step) * step, newN = exact ? n : Math.Round(n / step) * step;
        if (!_origin.Frame.IsAnchor && newE == _origin.E && newN == _origin.N && alt0 == _origin.Alt0) return false;

        var clock = Stopwatch.StartNew();
        var shift = _stressYaw != 0 ? _origin.MoveTo(StressAxes(newE, newN, Earth ? axes : null))
            : axes != null ? _origin.MoveTo(axes)
            // a turning re-anchor (round tiles, #1443 G2): the ellipsoid's own axes at the camera
            : InSpace && Turned ? _origin.MoveTo(FrameAxes.GroundAt(newE, newN, alt0))
            : _origin.MoveTo(newE, newN, alt0);
        var pass = new Pass(shift) { Owner = this };
        Visit(GetTree().Root, pass, childrenAreGlobal: true);
        foreach (var body in pass.Kinematic) Teleport(body);
        foreach (var node in pass.Doppler) ResetDoppler(node);
        double nodesMs = clock.Elapsed.TotalMilliseconds;
        foreach (var node in pass.Aware) node.OnOriginShifted(shift);
        Player.PlayerSnapshot.Forget();   // this tick's positions were taken in the old world space
        _origin.RaiseShifted(shift);
        PushPatternOffset();

        ShiftCount++;
        LastShiftNodes = pass.Moved;
        LastShiftMs = clock.Elapsed.TotalMilliseconds;
        if (!_stress || ShiftCount <= 50 || ShiftCount % 50 == 0)
            GD.Print($"[origin] shift {ShiftCount} by {shift}{(shift.Turns ? Turn(shift) : "")} to LV95 {newE:F0}/{newN:F0}: "
                + $"{pass.Moved} nodes ({pass.Kinematic.Count} kinematic) in {nodesMs:F1} ms, "
                + $"{pass.Aware.Count} handlers, {LastShiftMs:F1} ms in all");
        return true;
    }

    /// <summary>How a turning shift turned: the tilt of up and the yaw about it, degrees (the log line).</summary>
    private static string Turn(OriginShift shift)
    {
        var up = shift.Direction(Vector3.Up);
        double tilt = Math.Asin(Math.Min(1.0, Vector3.Up.Cross(up).Length()));
        return string.Create(CultureInfo.InvariantCulture, $", turned {tilt * 180 / Math.PI:F4}° tilt {shift.Yaw * 180 / Math.PI:F4}° yaw");
    }

    /// <summary>
    /// The turning anchor under the camera (#1430 H5 in Space, #1512 on the earth world's ground): built in ECEF
    /// (<see cref="FrameAxes.Tangent"/>), its axes the current ones turned by the least rotation, its origin
    /// <paramref name="alt0"/> high (LV95 heights, as the ground frame's) under the camera. No LV95 in it: an orbit
    /// goes everywhere, and so do the earth world's leaves.
    /// </summary>
    private FrameAxes TurnedAnchor(Vector3 camera, double alt0)
    {
        var frame = _origin.Frame;
        var at = frame.ToEcef(camera);
        double drop = frame.AltitudeOf(camera) - alt0;
        // LV95's heights fail only at its two singular points: the ellipsoid's there, near enough
        if (!double.IsFinite(drop)) drop = Ellipsoid.Wgs84.ToGeodetic(at).H - alt0;
        // from the flat ground frame (entering Space, near Switzerland): its grid axes under the camera
        var previous = frame.Axes;
        if (previous == null)
        {
            var g = frame.ToGlobal(camera);
            previous = FrameAxes.GroundAt(g.Lv95.E, g.Lv95.N, alt0);
        }
        return FrameAxes.Tangent(at, drop, previous);
    }

    /// <summary>
    /// Moves one node placed in world space, and what it holds, into another frame: what a whole-world
    /// shift does to every node, done to this one only. A server region hands a body to another region
    /// this way (#1456): its node keeps its path, its frame changes. Kinematic bodies are teleported, the
    /// handlers told, as in <see cref="ShiftTo"/>.
    /// </summary>
    public static void Move(Node3D node, OriginShift shift)
    {
        var pass = new Pass(shift);
        if (node is IOriginShiftAware handler) pass.Aware.Add(handler);
        node.GlobalTransform = shift.Apply(node.GlobalTransform);
        pass.Moved++;
        // a turning shift turns what moves, as Visit does for what the node holds (a region's frame turns with G2)
        if (shift.Turns)
        {
            if (node is RigidBody3D rigid)
            {
                rigid.LinearVelocity = shift.Direction(rigid.LinearVelocity);
                rigid.AngularVelocity = shift.Direction(rigid.AngularVelocity);
            }
            else if (node is CharacterBody3D character and not IOriginShiftAware)
                character.Velocity = shift.Direction(character.Velocity);
        }
        if (node is PhysicsBody3D body && PhysicsServer3D.BodyGetMode(body.GetRid()) == PhysicsServer3D.BodyMode.Kinematic)
            pass.Kinematic.Add(body);
        Visit(node, pass, childrenAreGlobal: false);
        foreach (var kinematic in pass.Kinematic) Teleport(kinematic);
        foreach (var aware in pass.Aware) aware.OnOriginShifted(shift);
    }

    /// <summary>
    /// The anchor frame a yawed stress shift goes to: the ground's own axes at the new place, at
    /// altitude 0 (so world Y stays near the altitude), turned about the up by the stress yaw every
    /// other shift. In the earth world (<paramref name="anchor"/>, the turning anchor the shift goes to, which
    /// carries the last turn with it) it turns by the yaw one way, then back.
    /// </summary>
    private FrameAxes StressAxes(double e, double n, FrameAxes? anchor = null)
    {
        var g = anchor ?? FrameAxes.GroundAt(e, n, 0);
        _stressTurned = !_stressTurned;
        double yaw = anchor != null ? (_stressTurned ? _stressYaw : -_stressYaw) : _stressYaw;
        if (anchor == null && !_stressTurned) return g;
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        // turned about up (Y): X' = X cos - Z sin... as Basis(Up, yaw) turns the world's X and Z
        var x = new EcefPos(g.X.X * c - g.Z.X * s, g.X.Y * c - g.Z.Y * s, g.X.Z * c - g.Z.Z * s);
        var z = new EcefPos(g.X.X * s + g.Z.X * c, g.X.Y * s + g.Z.Y * c, g.X.Z * s + g.Z.Z * c);
        return g with { X = x, Z = z };
    }

    /// <summary>What one shift has moved and found so far.</summary>
    private sealed class Pass(OriginShift shift)
    {
        public readonly OriginShift Shift = shift;
        /// <summary>The shifter doing a whole-world shift (it warns about containers under <c>--originstress</c>); null for <see cref="Move"/>.</summary>
        public OriginShifter? Owner;
        public int Moved;
        public readonly List<IOriginShiftAware> Aware = new();
        public readonly List<PhysicsBody3D> Kinematic = new();
        public readonly List<Node3D> Doppler = new();
    }

    /// <summary>
    /// A doppler-tracked sound or camera measures its velocity from where it was: the shift would
    /// read as kilometres in one step, a pitch spike. Setting the tracking again resets the tracker
    /// to where the node is now.
    /// </summary>
    private static void ResetDoppler(Node3D node)
    {
        switch (node)
        {
            case AudioStreamPlayer3D player:
                var mode = player.DopplerTracking;
                player.DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled;
                player.DopplerTracking = mode;
                break;
            case AudioListener3D listener:
                var listenerMode = listener.DopplerTracking;
                listener.DopplerTracking = AudioListener3D.DopplerTrackingEnum.Disabled;
                listener.DopplerTracking = listenerMode;
                break;
            case Camera3D camera:
                var cameraMode = camera.DopplerTracking;
                camera.DopplerTracking = Camera3D.DopplerTrackingEnum.Disabled;
                camera.DopplerTracking = cameraMode;
                break;
        }
    }

    /// <summary>
    /// Puts a kinematic body where its node now is, at once and with no velocity.
    ///
    /// <para>
    /// Jolt does not teleport a kinematic body whose transform is set: it sweeps it there with
    /// MoveKinematic during the next step, so for that step its collision is still where it was and
    /// it moves at the whole shift per step. A parked vehicle (a CharacterBody3D) 31 m from the
    /// player then stood, in the physics world, right under the shifted player, and swept off at
    /// 1,860 m/s carrying them (<c>--origincheck</c> has the case). A static body is teleported, so
    /// the body is made static for the move and kinematic again after.
    /// </para>
    /// </summary>
    public static void Teleport(PhysicsBody3D body)
    {
        var rid = body.GetRid();
        PhysicsServer3D.BodySetMode(rid, PhysicsServer3D.BodyMode.Static);
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Transform, body.GlobalTransform);
        PhysicsServer3D.BodySetMode(rid, PhysicsServer3D.BodyMode.Kinematic);
    }

    /// <summary>
    /// Moves every node placed in world space. <paramref name="childrenAreGlobal"/>: the children
    /// of <paramref name="node"/> have no Node3D parent transform to follow (or follow a container
    /// that stays at the identity), so their own transform is their world position.
    /// </summary>
    private static void Visit(Node node, Pass pass, bool childrenAreGlobal)
    {
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            var child = node.GetChild(i);
            if (child is IOriginShiftAware handler) pass.Aware.Add(handler);

            // the UI, and worlds of their own (previews, icons), are not in world space; a handler
            // in the UI may still keep world positions (an inset camera), so it is still told
            if (child is CanvasItem or CanvasLayer)
            {
                CollectAware(child, pass.Aware);
                continue;
            }
            if (child is Viewport { OwnWorld3D: true }) continue;

            if (child is not Node3D spatial)
            {
                Visit(child, pass, childrenAreGlobal: true);
                continue;
            }

            bool global = childrenAreGlobal || spatial.TopLevel;
            if (global && (spatial is IOriginContainer || spatial.IsInGroup(ContainerGroup)))
            {
                Visit(spatial, pass, childrenAreGlobal: true);
                continue;
            }
            if (global)
            {
                // a node that handles the shift itself (ThrowAim) knows what it holds
                if (pass.Owner is { _stress: true } owner && spatial is not IOriginShiftAware) owner.WarnIfContainer(spatial);
                spatial.Transform = pass.Shift.Apply(spatial.Transform);
                pass.Moved++;
            }
            // a turning shift turns what moves (#1430 H3); a body that handles the shift turns its own
            if (pass.Shift.Turns)
            {
                if (spatial is RigidBody3D rigid)
                {
                    rigid.LinearVelocity = pass.Shift.Direction(rigid.LinearVelocity);
                    rigid.AngularVelocity = pass.Shift.Direction(rigid.AngularVelocity);
                }
                else if (spatial is CharacterBody3D character and not IOriginShiftAware)
                    character.Velocity = pass.Shift.Direction(character.Velocity);
            }
            // whether it moved itself or with its parent, it is somewhere else now
            if (spatial is PhysicsBody3D body
                && PhysicsServer3D.BodyGetMode(body.GetRid()) == PhysicsServer3D.BodyMode.Kinematic)
                pass.Kinematic.Add(body);
            if (spatial is AudioStreamPlayer3D { DopplerTracking: not AudioStreamPlayer3D.DopplerTrackingEnum.Disabled }
                or Camera3D { DopplerTracking: not Camera3D.DopplerTrackingEnum.Disabled }
                or AudioListener3D { DopplerTracking: not AudioListener3D.DopplerTrackingEnum.Disabled })
                pass.Doppler.Add(spatial);
            // what it holds follows it, except TopLevel nodes and nodes under a plain Node
            Visit(spatial, pass, childrenAreGlobal: false);
        }
    }

    /// <summary>The handlers under a node whose subtree is not moved.</summary>
    private static void CollectAware(Node node, List<IOriginShiftAware> aware)
    {
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            var child = node.GetChild(i);
            if (child is IOriginShiftAware handler) aware.Add(handler);
            CollectAware(child, aware);
        }
    }

    /// <summary>
    /// A node at exactly the identity with spatial children is almost certainly a manager that
    /// should be an <see cref="IOriginContainer"/>: moving it leaves its children's coordinates
    /// large, and the next one it places lands a shift away.
    /// </summary>
    private void WarnIfContainer(Node3D node)
    {
        // a body standing at the origin (a player spawned before its first state) is an object, not a manager
        if (node.Transform != Transform3D.Identity || node is PhysicsBody3D) return;
        bool hasSpatialChild = false;
        for (int i = 0; i < node.GetChildCount() && !hasSpatialChild; i++) hasSpatialChild = node.GetChild(i) is Node3D;
        if (!hasSpatialChild) return;
        string path = node.GetPath();
        if (_warned.Add(path))
            GD.PushWarning($"[origin] moved {path} ({node.GetType().Name}) from the identity: should it be an IOriginContainer?");
    }

    private void PushPatternOffset()
    {
        double e = _origin.E % PatternPeriodM, n = _origin.N % PatternPeriodM;
        RenderingServer.GlobalShaderParameterSet("world_origin_offset", new Vector2((float)e, (float)-n));
    }
}
