using System.Diagnostics;
using System.Globalization;
using Godot;

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
/// Online it is off until positions on the wire are origin-independent (#185, phase 2).
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
    private readonly HashSet<string> _warned = new();

    /// <param name="focus">Where precision matters most: the active camera.</param>
    /// <param name="allowed">False while shifting would break something (online, until phase 2).</param>
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
            GD.Print($"[origin] stress: shift past {stress:F0} m, to the metre");
        }
        else if (ParseThreshold() is { } threshold)
            ThresholdM = threshold;
    }

    /// <summary>"--originstress &lt;m&gt;": shift every few metres, so a missed cached position shows at once.</summary>
    public static double? ParseStress() => ParseMetres("--originstress");

    /// <summary>"--originshift &lt;m&gt;": a different threshold, still snapped to whole tiles.</summary>
    public static double? ParseThreshold() => ParseMetres("--originshift");

    private static double? ParseMetres(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length) return null;
        return double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0
            ? m : null;
    }

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
        if (new Vector2(at.X, at.Z).Length() <= ThresholdM) return;

        var (e, n) = _origin.ToLv95(at);
        ShiftTo(e, n);
    }

    /// <summary>
    /// Moves the origin to (near) an LV95 point now, wherever the camera is: a teleport calls this
    /// first, so the destination is placed close to the new origin rather than far from the old one.
    /// Returns false when shifting is not allowed or would change nothing. <paramref name="exact"/>
    /// puts the origin on the point itself rather than snapping it to <see cref="StepM"/>.
    /// </summary>
    public bool ShiftTo(double e, double n, bool exact = false)
    {
        if (!_allowed()) return false;
        double step = Math.Max(1, StepM);
        double newE = exact ? e : Math.Round(e / step) * step, newN = exact ? n : Math.Round(n / step) * step;
        if (newE == _origin.E && newN == _origin.N) return false;

        var clock = Stopwatch.StartNew();
        var shift = _origin.MoveTo(newE, newN);
        var pass = new Pass(shift);
        Visit(GetTree().Root, pass, childrenAreGlobal: true);
        foreach (var body in pass.Kinematic) Teleport(body);
        foreach (var node in pass.Doppler) ResetDoppler(node);
        double nodesMs = clock.Elapsed.TotalMilliseconds;
        foreach (var node in pass.Aware) node.OnOriginShifted(shift);
        _origin.RaiseShifted(shift);
        PushPatternOffset();

        ShiftCount++;
        LastShiftNodes = pass.Moved;
        LastShiftMs = clock.Elapsed.TotalMilliseconds;
        if (!_stress || ShiftCount <= 50 || ShiftCount % 50 == 0)
            GD.Print($"[origin] shift {ShiftCount} by {shift} to LV95 {newE:F0}/{newN:F0}: "
                + $"{pass.Moved} nodes ({pass.Kinematic.Count} kinematic) in {nodesMs:F1} ms, "
                + $"{pass.Aware.Count} handlers, {LastShiftMs:F1} ms in all");
        return true;
    }

    /// <summary>What one shift has moved and found so far.</summary>
    private sealed class Pass(OriginShift shift)
    {
        public readonly OriginShift Shift = shift;
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
    private static void Teleport(PhysicsBody3D body)
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
    private void Visit(Node node, Pass pass, bool childrenAreGlobal)
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
                if (_stress) WarnIfContainer(spatial);
                spatial.Transform = pass.Shift.Apply(spatial.Transform);
                pass.Moved++;
            }
            // whether it moved itself or with its parent, it is somewhere else now
            if (spatial is PhysicsBody3D body
                && PhysicsServer3D.BodyGetMode(body.GetRid()) == PhysicsServer3D.BodyMode.Kinematic)
                pass.Kinematic.Add(body);
            if (spatial is AudioStreamPlayer3D { DopplerTracking: not AudioStreamPlayer3D.DopplerTrackingEnum.Disabled }
                or Camera3D { DopplerTracking: not Camera3D.DopplerTrackingEnum.Disabled })
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
        if (node.Transform != Transform3D.Identity) return;
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
