using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;

namespace UnitSport.Styles;

/// <summary>
/// How much the active camera zooms over the walking view, for every level of detail picked by
/// distance (#1433, C0 of #1430): what decides detail is how big a thing is on screen, and a narrow
/// field of view shows a thing at d as big as the walking view shows it at d / <see cref="Value"/>.
/// So each distance LOD compares d / scale instead of d: the tree shaders through the
/// <c>world_lod_scale</c> global, <see cref="Terrain.NearTrees"/> and the terrain rings inside
/// the view cone (<see cref="InCone"/>), and Godot's visibility ranges through
/// <see cref="ZoomRanges"/>.
///
/// <para>
/// tan(<see cref="ZoomRule.ReferenceFov"/> / 2) / tan(fov / 2), never below 1: a wider view than walking
/// (a fast car, a plane) keeps the detail it has. The camera item's 35 mm is x2, its 200 mm x11,
/// the binoculars x8.6. Set by <see cref="CameraGlobal"/> before each frame is drawn.
/// </para>
/// </summary>
public static class LodScale
{
    /// <summary>The step between <see cref="Step"/>s: managers rebuild on a step, never per frame of a zoom ease.</summary>
    public const float StepRatio = ZoomRule.StepRatio;

    /// <summary>
    /// The view cone's margin past the horizontal half field of view, degrees: half the half
    /// angle, 2° to 15°, so a turn is not a hole at the edge, and a 200 mm view (±6°) refines
    /// ±9°, not ±21°.
    /// </summary>
    public static float ConeMarginDeg(float halfDeg) => Mathf.Clamp(halfDeg * 0.5f, 2f, 15f);

    /// <summary><c>--zoom-lod off</c>: every LOD by distance only, as before (the before/after check).</summary>
    public static bool Enabled { get; } = CmdArgs.Value("--zoom-lod") is not ("off" or "0" or "false");

    /// <summary>The zoom this frame, 1 at the walking view.</summary>
    public static float Value { get; private set; } = 1f;

    /// <summary>
    /// <see cref="Value"/> rounded down to a power of <see cref="StepRatio"/>, with a little
    /// hysteresis: what everything uses, the <c>world_lod_scale</c> shader global included, so
    /// they agree on which tree is 3D, and nothing rebuilds per frame of a zoom ease.
    /// </summary>
    public static float Step { get; private set; } = 1f;

    /// <summary>The power of <see cref="StepRatio"/> that <see cref="Step"/> is; 0 when not zoomed.</summary>
    public static int StepIndex { get; private set; }

    /// <summary>Bumped on every change of <see cref="Step"/>.</summary>
    public static int Version { get; private set; }

    /// <summary>The camera's position and forward, for the view cone.</summary>
    public static Vector3 ViewPosition { get; private set; }
    public static Vector3 ViewDirection { get; private set; } = Vector3.Forward;

    /// <summary>The horizontal half angle of the view cone, margin included, degrees.</summary>
    public static float ConeHalfDeg { get; private set; } = 180f;

    /// <summary>From the camera that draws the frame. Main thread, before drawing.</summary>
    internal static void Update(Camera3D cam)
    {
        float value = 1f;
        if (Enabled && cam.Projection != Camera3D.ProjectionType.Orthogonal && !XR.XrSession.Active)
        {
            // below 1° (the telescope, #1440) the camera is a frustum of the same angle
            float fov = Core.CameraLens.FovOf(cam);
            // Fov is the horizontal angle when the camera keeps its width: the vertical one is what is compared
            var size = cam.GetViewport().GetVisibleRect().Size;
            float aspect = size.Y > 0 ? size.X / size.Y : 16f / 9f;
            if (cam.KeepAspect == Camera3D.KeepAspectEnum.Width)
                fov = Mathf.RadToDeg(2f * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(fov) * 0.5f) / aspect));
            value = ZoomRule.Of(fov);
            float halfH = Mathf.RadToDeg(Mathf.Atan(Mathf.Tan(Mathf.DegToRad(fov) * 0.5f) * aspect));
            ConeHalfDeg = Mathf.Min(180f, halfH + ConeMarginDeg(halfH));
        }
        else ConeHalfDeg = 180f;
        Value = value;
        ViewPosition = cam.GlobalPosition;
        ViewDirection = -cam.GlobalTransform.Basis.Z;
        int index = ZoomRule.StepFor(value, StepIndex);
        if (index != StepIndex)
        {
            StepIndex = index;
            Step = ZoomRule.Step(index);
            Version++;
        }
    }

    /// <summary>
    /// The zoom at <paramref name="at"/>: <see cref="Step"/> inside the view cone (a point within
    /// <paramref name="radius"/> of the cone counts), 1 outside it.
    /// </summary>
    public static float At(Vector3 at, float radius = 0f) => StepIndex > 0 && InCone(at, radius) ? Step : 1f;

    /// <summary>Whether a point (or a ball of <paramref name="radius"/> round it) is inside the horizontal view cone.</summary>
    public static bool InCone(Vector3 at, float radius = 0f)
    {
        if (ConeHalfDeg >= 180f) return true;
        float dx = at.X - ViewPosition.X, dz = at.Z - ViewPosition.Z;
        float fx = ViewDirection.X, fz = ViewDirection.Z;
        float fl = Mathf.Sqrt(fx * fx + fz * fz);
        // nearly straight down or up: every heading is in view
        if (fl < 0.2f) return true;
        float r = Mathf.Sqrt(dx * dx + dz * dz);
        if (r <= radius) return true;
        float cos = (dx * fx + dz * fz) / (r * fl);
        float off = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(cos, -1f, 1f))) - Mathf.RadToDeg(Mathf.Asin(Mathf.Min(1f, radius / r)));
        return off <= ConeHalfDeg;
    }
}

/// <summary>
/// Godot's visibility ranges have no field-of-view term, so while the camera zooms
/// (<see cref="LodScale.Step"/> above 1) every tracked range is multiplied by the step, straight
/// on the rendering server: the node keeps its own range, so whatever reads it (the movie
/// recorder) still sees the base one. Only what is on screen pays for it: frustum culling drops
/// the rest before its range is ever looked at.
///
/// <para>
/// A node with a range is tracked with <see cref="Track{T}"/> (or every one under a root with
/// <see cref="TrackTree{T}"/>) once its range is set, and again whenever code sets it anew (a
/// node's own setter pushes its base range back). Freed nodes are dropped on the next pass.
/// A step change is applied over a few frames, <see cref="PerFrame"/> at a time.
/// </para>
///
/// <para>
/// Not tracked on purpose: <see cref="Vehicles.VehicleBody"/>'s fade at the edge of the server's
/// interest range (#689), which must end before the vehicle leaves this peer, zoomed or not.
/// </para>
/// </summary>
public static class ZoomRanges
{
    private struct Entry
    {
        public GeometryInstance3D Node;
        public ulong Id;
        public Rid Instance;
        public float Begin, End, BeginMargin, EndMargin;
        public RenderingServer.VisibilityRangeFadeMode Fade;
        public float Applied;
    }

    /// <summary>Ranges rescaled per frame after a step change.</summary>
    public const int PerFrame = 4000;

    private static readonly List<Entry> Entries = new();
    private static readonly Dictionary<ulong, int> Index = new();
    private static int _cursor, _appliedVersion, _prunedAt = 1024;

    /// <summary>How many nodes are tracked (freed ones until the next pass).</summary>
    public static int Count => Entries.Count;

    /// <summary>How long the last full pass took, ms, and how many ranges it set: the cost of a zoom step.</summary>
    public static double LastPassMs { get; private set; }
    public static int LastPassSet { get; private set; }
    private static double _passMs;
    private static int _passSet;

    /// <summary>Tracks <paramref name="node"/>'s current visibility range. Main thread.</summary>
    public static T Track<T>(T node) where T : GeometryInstance3D
    {
        if (!LodScale.Enabled) return node;
        var e = new Entry
        {
            Node = node,
            Id = node.GetInstanceId(),
            Instance = node.GetInstance(),
            Begin = node.VisibilityRangeBegin,
            End = node.VisibilityRangeEnd,
            BeginMargin = node.VisibilityRangeBeginMargin,
            EndMargin = node.VisibilityRangeEndMargin,
            Fade = (RenderingServer.VisibilityRangeFadeMode)(int)node.VisibilityRangeFadeMode,
            Applied = 1f,
        };
        ulong id = e.Id;
        bool ranged = e.Begin > 0f || e.End > 0f;
        if (Index.TryGetValue(id, out int at))
        {
            if (ranged) { Entries[at] = e; Apply(at); }
            else Remove(at);
            return node;
        }
        if (!ranged) return node;
        if (Entries.Count >= _prunedAt) Prune();
        Index[id] = Entries.Count;
        Entries.Add(e);
        Apply(Entries.Count - 1);
        return node;
    }

    /// <summary>Tracks every node with a range under (and including) <paramref name="root"/>.</summary>
    public static T TrackTree<T>(T root) where T : Node
    {
        if (!LodScale.Enabled) return root;
        if (root is GeometryInstance3D g) Track(g);
        foreach (var child in root.GetChildren()) TrackTree(child);
        return root;
    }

    /// <summary>Once a frame from <see cref="CameraGlobal"/>: carries a step change on.</summary>
    internal static void Tick()
    {
        if (_appliedVersion != LodScale.Version)
        {
            _appliedVersion = LodScale.Version;
            _cursor = 0;
            _passMs = 0;
            _passSet = 0;
        }
        if (_cursor >= Entries.Count) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int end = Math.Min(Entries.Count, _cursor + PerFrame);
        int set = 0;
        while (_cursor < end && _cursor < Entries.Count)
        {
            if (!GodotObject.IsInstanceValid(Entries[_cursor].Node)) { Remove(_cursor); end--; continue; }
            if (Apply(_cursor)) set++;
            _cursor++;
        }
        _passMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _passSet += set;
        if (_cursor >= Entries.Count)
        {
            LastPassMs = _passMs;
            LastPassSet = _passSet;
        }
    }

    /// <summary>Sets one entry's range at the current step. Whether it changed.</summary>
    private static bool Apply(int at)
    {
        var e = Entries[at];
        float s = LodScale.Step;
        if (e.Applied == s) return false;
        RenderingServer.InstanceGeometrySetVisibilityRange(e.Instance, e.Begin * s, e.End * s, e.BeginMargin * s, e.EndMargin * s, e.Fade);
        e.Applied = s;
        Entries[at] = e;
        return true;
    }

    /// <summary>Swap-removes an entry.</summary>
    private static void Remove(int at)
    {
        Index.Remove(Entries[at].Id);
        int last = Entries.Count - 1;
        if (at != last)
        {
            Entries[at] = Entries[last];
            Index[Entries[at].Id] = at;
        }
        Entries.RemoveAt(last);
    }

    /// <summary>Drops freed nodes, so the list does not grow with every tile the player passed.</summary>
    private static void Prune()
    {
        var keep = new List<Entry>(Entries.Count);
        Index.Clear();
        foreach (var e in Entries)
            if (GodotObject.IsInstanceValid(e.Node))
            {
                Index[e.Id] = keep.Count;
                keep.Add(e);
            }
        Entries.Clear();
        Entries.AddRange(keep);
        _cursor = Math.Min(_cursor, Entries.Count);
        _prunedAt = Math.Max(1024, Entries.Count * 2);
    }
}
