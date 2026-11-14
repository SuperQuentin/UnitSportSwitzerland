using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using UnitSport.World.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The Moon's sites near the camera (#1939, docs/plans/moon-surface.md section 3): every half second, while the anchor is
/// on the Moon and the camera low over it, the sites within <see cref="ReachM"/> of the camera's ground point are built
/// (their parts from shared meshes, a few solid boxes, the traces on a worker) and the far ones freed. Each stands in
/// its own ENU frame on <see cref="BodyHeights"/> (the stamps calm the ground under it), placed from doubles at every
/// re-anchor, as <see cref="BodyGround"/>'s squares are. Nothing is rebuilt per frame; nothing is replicated: the table
/// and the seeds make the same sites on every peer.
/// </summary>
public partial class MoonSurface : Node
{
    /// <summary>A site is built while the camera's ground point is this close to it (plus its extent), metres.</summary>
    public const double ReachM = 3000;

    /// <summary>And freed past this (a margin, so walking along the edge does not rebuild it).</summary>
    public const double DropM = 3600;

    /// <summary>Only while the camera is under this over the reference surface, metres (the ground squares' own limit).</summary>
    public const double NearM = BodyGround.NearM;

    private static readonly StringName Tint = "tint";

    private readonly BodyGround _ground;
    private readonly WorldOrigin _origin;
    private readonly Dictionary<string, Placed> _placed = new();
    private readonly HashSet<string> _parked = new();
    private double _scan;
    private static ShaderMaterial? _darken, _brighten;

    /// <summary>The sites in place (the check reads them).</summary>
    public static MoonSurface? Active { get; private set; }

    /// <summary>A site in the world: its node, its frame, the epoch it was placed at.</summary>
    public sealed class Placed(MoonSite? site, Node3D node, EnuFrame frame)
    {
        /// <summary>The site, or null for a colony.</summary>
        public readonly MoonSite? Site = site;
        public readonly Node3D Node = node;
        public readonly EnuFrame Frame = frame;
        public int Epoch = -1;
        public Task<Godot.Collections.Array?>? Traces;
        public bool TracesIn;
    }

    public IReadOnlyDictionary<string, Placed> Sites => _placed;

    /// <summary>Whether the anchor is on the Moon (the suit's question).</summary>
    public bool OnMoon => _origin.Body == BodyId.Moon;

    public MoonSurface(BodyGround ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
        Name = "MoonSurface";
    }

    public override void _EnterTree() => Active = this;

    public override void _ExitTree()
    {
        if (Active == this) Active = null;
    }

    public override void _Process(double delta)
    {
        if (_origin.Body != BodyId.Moon || _origin.Frame.Axes is not { } axes)
        {
            if (_placed.Count > 0) DropAll();
            return;
        }
        foreach (var p in _placed.Values)
        {
            if (p.Epoch != _origin.Epoch) Place(p, axes);
            if (!p.TracesIn && p.Traces is { IsCompleted: true } t)
            {
                p.TracesIn = true;
                if (t.IsCompletedSuccessfully && t.Result is { } arrays) AddTraces(p, arrays);
            }
        }
        _scan -= delta;
        if (_scan > 0) return;
        _scan = 0.5;
        var cam = GetViewport()?.GetCamera3D();
        if (cam == null) return;
        var at = cam.GlobalPosition;
        if (_origin.AltitudeOf(at) > NearM) return;
        var n = _ground.Heights.NormalUnder(axes.ToEcef(at.X, at.Y, at.Z));
        Scan(n, axes);
    }

    /// <summary>Builds the sites within reach of a unit normal and frees the far ones.</summary>
    public void Scan(EcefPos n, FrameAxes axes)
    {
        double r = Ellipsoid.Moon.A;
        foreach (var s in MoonSites.All)
        {
            double d = Math.Acos(Math.Clamp(Dot(n, s.Normal), -1, 1)) * r;
            bool have = _placed.ContainsKey(s.Id);
            if (!have && d < ReachM + s.Extent) Build(s, axes);
            else if (have && d > DropM + s.Extent) Drop(s.Id);
        }
        foreach (var c in MoonColonies.All)
        {
            double d = Math.Acos(Math.Clamp(Dot(n, c.Normal), -1, 1)) * r;
            bool have = _placed.ContainsKey(c.Id);
            if (!have && d < ColonyReachM) BuildColony(c, axes);
            else if (have && d > ColonyReachM + 600)
            {
                Drop(c.Id);
                _parked.Remove(c.Id);
            }
            // its rovers once the camera is near enough for the ground's collision to be under their slots (#1943)
            if (_placed.TryGetValue(c.Id, out var colony) && d < MoonRoverDepot.NearM && _parked.Add(c.Id))
            {
                int n2 = MoonRoverDepot.Park(c, ColonyPlan.For(c), colony.Node.GlobalTransform, _origin);
                if (n2 > 0) GD.Print($"[moon] {c.Name}: {n2} rover(s) parked");
            }
        }
        foreach (var o in MoonOutposts.All)
        {
            double d = Math.Acos(Math.Clamp(Dot(n, o.Normal), -1, 1)) * r;
            bool have = _placed.ContainsKey(o.Id);
            if (!have && d < OutpostReachM + o.Extent) BuildOutpost(o, axes);
            else if (have && d > OutpostReachM + o.Extent + 600) Drop(o.Id);
        }
    }

    /// <summary>An outpost (#1942) is built while the camera's ground point is this close to it (plus its extent), metres.</summary>
    public const double OutpostReachM = 5000;

    /// <summary>
    /// An industrial outpost (the ice mine, the radio telescope, the mass driver): its pieces in its own ENU frame on its
    /// stamp's ground (each piece at the ground under it: the mass driver's strip slopes between its ends), in the colours
    /// of the colony that runs it.
    /// </summary>
    private void BuildOutpost(MoonOutpost o, FrameAxes axes)
    {
        var heights = _ground.Heights;
        var g0 = Ellipsoid.Moon.ToGeodetic(heights.Ground(o.Normal));
        var frame = new EnuFrame(g0, Ellipsoid.Moon);
        var probe = o.Probe;
        Vector3 Ground(double e, double n)
        {
            var (x, y, z) = frame.ToLocal(heights.Ground(MoonSites.Offset(probe, e, n)));
            return new Vector3((float)x, (float)y, (float)z);
        }
        var root = new Node3D { Name = o.Id };
        var body = new StaticBody3D { Name = "Solid" };
        root.AddChild(body);
        var theme = MoonColonies.Find(o.Colony)?.Theme ?? ColonyTheme.Artemis;
        root.AddChild(MoonIndustry.Build(MoonOutposts.Plan(o), theme, o.Lat, o.Lon, Ground, body));
        AddChild(root);
        var placed = new Placed(null, root, frame) { TracesIn = true };
        _placed[o.Id] = placed;
        Place(placed, axes);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[moon] {o.Name}: built ({root.GetChildCount()} nodes, ground {g0.H:F1} m)"));
    }

    /// <summary>A colony is built while the camera's ground point is this close to its centre, metres (its lights take over past it, phase 8).</summary>
    public const double ColonyReachM = 8000;

    private void BuildColony(MoonColony c, FrameAxes axes)
    {
        var heights = _ground.Heights;
        var g0 = Ellipsoid.Moon.ToGeodetic(heights.Ground(c.Normal));
        var frame = new EnuFrame(g0, Ellipsoid.Moon);
        // the ground's height in the colony's frame (a pit's rim, #1944)
        float GroundY(double e, double n)
        {
            var (_, y, _) = frame.ToLocal(heights.Ground(MoonSites.Offset(new MoonSite(c.Id, c.Name, MoonSiteKind.Colony, c.Lat, c.Lon, []), e, n)));
            return (float)y;
        }
        var root = ColonyBuilder.Build(c, ColonyPlan.For(c), GroundY);
        AddChild(root);
        var placed = new Placed(null, root, frame) { TracesIn = true };
        _placed[c.Id] = placed;
        Place(placed, axes);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[moon] {c.Name}: built ({root.GetChildCount()} nodes, plateau {g0.H:F1} m)"));
    }

    private void Build(MoonSite s, FrameAxes axes)
    {
        var heights = _ground.Heights;
        var n0 = s.Normal;
        var g0 = Ellipsoid.Moon.ToGeodetic(heights.Ground(n0));
        var frame = new EnuFrame(g0, Ellipsoid.Moon);
        var root = new Node3D { Name = s.Id };
        foreach (var part in s.Parts)
        {
            var (x, y, z) = PartAt(heights, s, frame, part);
            // the model's front along the heading (degrees from north; the frame's z is south): authored facing +Z,
            // MeshScratch.Build turns it to face -Z, so -Z goes along the heading
            float yaw = -Mathf.DegToRad((float)part.Heading);
            var t = new Transform3D(new Basis(Vector3.Up, yaw), new Vector3((float)x, (float)y, (float)z));
            var mesh = MoonSiteMeshes.Instance(part.Part, part.Flag);
            mesh.Transform = t;
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
            root.AddChild(mesh);
            var solids = MoonSiteMeshes.Solids(part.Part);
            if (solids.Length == 0) continue;
            var body = new StaticBody3D { Name = part.Part + "Body", Transform = t };
            foreach (var (c, size) in solids)
                body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = c });
            root.AddChild(body);
        }
        AddChild(root);
        var placed = new Placed(s, root, frame);
        _placed[s.Id] = placed;
        Place(placed, axes);
        placed.Traces = Task.Run(() => TraceArrays(heights, s, frame));
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[moon] {s.Name}: built ({s.Parts.Length} parts, ground {g0.H:F1} m)"));
    }

    /// <summary>A part's place in the site's frame: on the ground under it, its lowest foot touching (the ground under its box's corners).</summary>
    public static (double X, double Y, double Z) PartAt(BodyHeights heights, MoonSite s, EnuFrame frame, SitePart part)
    {
        double y = double.MaxValue;
        double reach = part.Part switch
        {
            MoonPart.DescentStage => 4.4, MoonPart.LunaStage or MoonPart.LunaRampStage or MoonPart.ChangeLander => 2.2,
            MoonPart.Lrv or MoonPart.Surveyor or MoonPart.Lunokhod => 1.2, _ => 0.4,
        };
        for (int k = 0; k < 5; k++)
        {
            double e = part.East, nn = part.North;
            if (k > 0)
            {
                double a = (k - 1) * Math.PI / 2 + Math.PI / 4 + part.Heading * Math.PI / 180;
                e += Math.Sin(a) * reach;
                nn += Math.Cos(a) * reach;
            }
            var n = MoonSites.Offset(s, e, nn);
            var (_, ly, _) = frame.ToLocal(heights.Ground(n));
            y = Math.Min(y, ly);
        }
        var (x, _, z) = frame.ToLocal(heights.Ground(MoonSites.Offset(s, part.East, part.North)));
        // wrecks and debris sit in the ground (a crash dug them in)
        double sink = part.Part switch { MoonPart.RocketWreck => 0.9, MoonPart.SivbDebris or MoonPart.WreckDebris => 0.25, _ => 0.03 };
        return (x, y - sink, z);
    }

    /// <summary>The site from its frame into world space, in doubles from the anchor (once a re-anchor).</summary>
    private void Place(Placed p, FrameAxes axes)
    {
        p.Epoch = _origin.Epoch;
        var f = p.Frame;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(f.Origin);
        var south = new EcefPos(-f.North.X, -f.North.Y, -f.North.Z);
        p.Node.GlobalTransform = new Transform3D(new Basis(Turn(f.East), Turn(f.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
    }

    private void Drop(string id)
    {
        if (!_placed.Remove(id, out var p)) return;
        p.Node.QueueFree();
    }

    private void DropAll()
    {
        foreach (var p in _placed.Values) p.Node.QueueFree();
        _placed.Clear();
    }

    // ---- traces: footpaths, rover tracks, an impact's rays ---------------------------------------------------------

    /// <summary>
    /// The traces' mesh on a worker: footpaths 0.5 m wide, a rover's two tracks 0.23 m wide 1.8 m apart, laid on the
    /// ground every 1.5 m (surface 0, darkened); an impact's bright rays (surface 1). Null when there is none.
    /// </summary>
    private static Godot.Collections.Array? TraceArrays(BodyHeights heights, MoonSite s, EnuFrame frame)
    {
        var dark = new List<Vector3>();
        var bright = new List<Vector3>();
        // UV: x across the ribbon (0..1), y along it (0..1): the shader softens the edges and fades a ray out
        var darkUv = new List<Vector2>();
        var brightUv = new List<Vector2>();
        Vector3 At(double e, double n)
        {
            var (x, y, z) = frame.ToLocal(heights.Ground(MoonSites.Offset(s, e, n)));
            return new Vector3((float)x, (float)y + 0.03f, (float)z);
        }
        void Ribbon(List<Vector3> into, (double E, double N)[] pts, double offset, double half, bool taper = false)
        {
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                var (e0, n0) = pts[i];
                var (e1, n1) = pts[i + 1];
                double dx = e1 - e0, dy = n1 - n0, l = Math.Sqrt(dx * dx + dy * dy);
                if (l < 1e-3) continue;
                double sx = -dy / l, sy = dx / l;
                // a ray narrows to nothing at its end
                double h0 = taper ? half * (1 - i / (double)(pts.Length - 1)) : half;
                double h1 = taper ? half * (1 - (i + 1) / (double)(pts.Length - 1)) : half;
                var a0 = At(e0 + sx * (offset - h0), n0 + sy * (offset - h0));
                var b0 = At(e0 + sx * (offset + h0), n0 + sy * (offset + h0));
                var a1 = At(e1 + sx * (offset - h1), n1 + sy * (offset - h1));
                var b1 = At(e1 + sx * (offset + h1), n1 + sy * (offset + h1));
                into.Add(a0); into.Add(a1); into.Add(b0);
                into.Add(b0); into.Add(a1); into.Add(b1);
                var uv = into == dark ? darkUv : brightUv;
                float v0 = i / (float)(pts.Length - 1), v1 = (i + 1) / (float)(pts.Length - 1);
                uv.Add(new Vector2(0, v0)); uv.Add(new Vector2(0, v1)); uv.Add(new Vector2(1, v0));
                uv.Add(new Vector2(1, v0)); uv.Add(new Vector2(0, v1)); uv.Add(new Vector2(1, v1));
            }
        }
        foreach (var t in MoonSites.Paths(s))
        {
            if (t.Rover)
            {
                Ribbon(dark, t.Points, -0.9, 0.115);
                Ribbon(dark, t.Points, 0.9, 0.115);
            }
            else Ribbon(dark, t.Points, 0, 0.25);
        }
        if (s.CraterM > 0)
        {
            // bright rays from the fresh crater, out to ten radii, thinning
            var rng = new Random(s.Seed);
            int rays = 16 + rng.Next(8);
            for (int i = 0; i < rays; i++)
            {
                double a = rng.NextDouble() * Math.Tau, len = s.CraterM * (5 + rng.NextDouble() * 6);
                var pts = new (double, double)[12];
                for (int k = 0; k < pts.Length; k++)
                {
                    double d = s.CraterM * 1.1 + (len - s.CraterM * 1.1) * k / (pts.Length - 1);
                    pts[k] = (Math.Sin(a) * d, Math.Cos(a) * d);
                }
                Ribbon(bright, pts, 0, s.CraterM * (0.08 + 0.1 * rng.NextDouble()), taper: true);
            }
        }
        if (dark.Count == 0 && bright.Count == 0) return null;
        var result = new Godot.Collections.Array();
        result.Add(dark.ToArray());
        result.Add(bright.ToArray());
        result.Add(darkUv.ToArray());
        result.Add(brightUv.ToArray());
        return result;
    }

    private void AddTraces(Placed p, Godot.Collections.Array arrays)
    {
        var mesh = new ArrayMesh();
        for (int i = 0; i < 2; i++)
        {
            var v = arrays[i].AsVector3Array();
            if (v.Length == 0) continue;
            var a = new Godot.Collections.Array();
            a.Resize((int)Mesh.ArrayType.Max);
            a[(int)Mesh.ArrayType.Vertex] = v;
            a[(int)Mesh.ArrayType.TexUV] = arrays[i + 2].AsVector2Array();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, a);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, i == 0 ? Darken() : Brighten());
        }
        if (mesh.GetSurfaceCount() == 0) return;
        p.Node.AddChild(new MeshInstance3D
        {
            Name = "Traces", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 500,
        });
    }

    /// <summary>
    /// The traces' look: unshaded, a dark (footpaths, tracks) or a light (rays) film over the ground behind, pulled a
    /// little toward the camera along the view ray so they never sink into the ground's 2 m triangles.
    /// </summary>
    private const string TraceShader = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, skip_vertex_transform, blend_mix;
        uniform vec4 tint = vec4(0.85, 0.85, 0.85, 1.0);
        uniform float fade_along = 0.0;
        void vertex() {
            vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
            VERTEX = v.xyz * 0.994;
        }
        void fragment() {
            ALBEDO = tint.rgb;
            float across = 1.0 - pow(abs(UV.x * 2.0 - 1.0), 2.0);
            ALPHA = tint.a * across * (1.0 - fade_along * UV.y);
        }
        """;

    private static ShaderMaterial Darken() => _darken ??= Trace(new Color(0.05f, 0.05f, 0.06f, 0.28f));

    private static ShaderMaterial Brighten()
    {
        if (_brighten != null) return _brighten;
        _brighten = Trace(new Color(1f, 1f, 0.98f, 0.09f));
        _brighten.SetShaderParameter("fade_along", 1.0f);
        return _brighten;
    }

    private static ShaderMaterial Trace(Color tint)
    {
        var m = new ShaderMaterial { Shader = new Shader { Code = TraceShader } };
        m.SetShaderParameter(Tint, tint);
        return m;
    }

    private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
