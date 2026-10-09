using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The lit lenses of a tile's traffic lights (#350): one <see cref="MultiMeshInstance3D"/> per
/// lens shape (round, left and right arrows, pedestrian squares), every lens an instance whose
/// colour is its lamp, on or off. A junction's lenses are recoloured only when one of its groups
/// changes aspect (<see cref="SignalPlan.UntilChange"/>), and on each half second while one of its
/// flashers blinks; between changes a frame costs one comparison per junction. The aspect is a
/// function of the server clock (<c>ClockSync.ServerNow</c>), so every peer shows the same lights
/// and nothing is replicated. A child of the tile's <see cref="ChunkNode"/>, in its tile-local frame.
/// </summary>
public partial class SignalLamps : Node3D
{
    private static readonly Color RedOn = new(1.0f, 0.10f, 0.05f), RedOff = new(0.16f, 0.03f, 0.02f);
    private static readonly Color AmberOn = new(1.0f, 0.60f, 0.04f), AmberOff = new(0.16f, 0.10f, 0.02f);
    private static readonly Color GreenOn = new(0.15f, 1.0f, 0.55f), GreenOff = new(0.02f, 0.13f, 0.08f);

    /// <summary>Flashing yellow: on for half a second, off for half a second, on the server clock.</summary>
    private const double BlinkHalf = 0.5;

    /// <summary>
    /// What a lens shows (#759): a car lens its shape, a round one the arrow of its moves when it
    /// does not give them all (<see cref="SignalBuilder.ArrowMoves"/>); a pedestrian lens a standing
    /// figure in the red and a walking one in the green and yellow; a bike lens a bicycle. One
    /// mesh, material and MultiMesh each.
    /// </summary>
    private enum Icon : byte { Round, LeftArrow, RightArrow, Standing, Walking, Bike, Straight, StraightLeft, StraightRight }
    private const int IconCount = 9;

    private static Icon IconOf(SignalBuilder.Lens lens, SignalPlan plan) => lens.Shape switch
    {
        SignalBuilder.Shape.LeftArrow => Icon.LeftArrow,
        SignalBuilder.Shape.RightArrow => Icon.RightArrow,
        SignalBuilder.Shape.Square => lens.Role == SignalBuilder.Role.Red ? Icon.Standing : Icon.Walking,
        SignalBuilder.Shape.Bike => Icon.Bike,
        _ when lens.Role == SignalBuilder.Role.Flash => Icon.Round,
        _ => SignalBuilder.ArrowMoves(plan, lens.Group) switch
        {
            SignalMoves.Through => Icon.Straight,
            SignalMoves.Through | SignalMoves.Left => Icon.StraightLeft,
            SignalMoves.Through | SignalMoves.Right => Icon.StraightRight,
            SignalMoves.Left => Icon.LeftArrow,
            SignalMoves.Right => Icon.RightArrow,
            _ => Icon.Round,
        },
    };

    private static Shader? _shader;
    private static readonly Material?[] Materials = new Material?[IconCount];
    private static readonly Mesh?[] Meshes = new Mesh?[IconCount];

    /// <summary>
    /// Lenses stay lit (unshaded) and show their colour whatever the light (#759):
    /// <list type="bullet">
    /// <item>The road mesh the heads are part of is pulled toward the eye by a fraction of its
    /// distance (<c>shaders/body/road.gdshaderinc</c>: <c>road_depth_bias</c>, <c>far_lift_*</c>),
    /// which put a head's housing in front of its lenses, 6 mm ahead of it, past about 15 m. A
    /// lens is pulled the same way and a hair more.</item>
    /// <item>A 90 mm lens covers less than a pixel past about 150 m, so a lit lens of a car head
    /// never covers less than <c>min_pixels</c> of radius: it grows with distance, the glare of a
    /// lamp seen from afar, and is pulled a hair further to draw over the dark lenses beside it.
    /// The glare fades between 500 m and 1 km, or a straight road with lights every kilometre
    /// stacks them into one row of dots on the horizon. Pedestrian and bike lenses keep their
    /// size: grown, a junction's crossings smeared into a band of colour.</item>
    /// <item>A lens seen from behind is not drawn: the pull would show it through its head.</item>
    /// </list>
    /// </summary>
    private static Material Material(Icon icon)
    {
        if (Materials[(int)icon] is { } done) return done;
        _shader ??= new Shader { Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform float lens_radius;
uniform float min_pixels;   // the smallest radius a lit lens is drawn at; 0 keeps it at its size
// shaders/body/road.gdshaderinc's defaults: the pull of the road mesh the heads belong to
const float ROAD_DEPTH_BIAS = 0.0004, FAR_LIFT_START = 800.0, FAR_LIFT_RATE = 0.001, FAR_LIFT_MAX = 2.0;
const float LENS_BIAS = 0.0002;   // a lens's own pull past its head's, twice that for a lit lens
const float GLARE_FADE_START = 500.0, GLARE_FADE_END = 1000.0;
void vertex() {
    vec3 centre = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 front = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 1.0, 0.0)).xyz;
    float facing = step(0.0, dot(front, -centre));
    float lit = step(0.5, max(COLOR.r, max(COLOR.g, COLOR.b)));
    // the world size of a pixel at the lens's depth (the projection flips y: its sign is not ours)
    float depth = max(-centre.z, 0.0);
    float pixel = 2.0 * depth / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y);
    float glare = min_pixels * (1.0 - smoothstep(GLARE_FADE_START, GLARE_FADE_END, depth));
    float grow = facing * mix(1.0, max(1.0, glare * pixel / lens_radius), lit);
    vec4 view = MODELVIEW_MATRIX * vec4(VERTEX.xy * grow, VERTEX.z, 1.0);
    float dist = length(view.xyz);
    float lift = clamp((dist - FAR_LIFT_START) * FAR_LIFT_RATE, 0.0, FAR_LIFT_MAX);
    view.xyz *= 1.0 - lift / max(dist, 1.0) - ROAD_DEPTH_BIAS - LENS_BIAS * (1.0 + lit);
    POSITION = PROJECTION_MATRIX * view;
}
void fragment() {
    ALBEDO = COLOR.rgb;
}" };
        var m = new ShaderMaterial { Shader = _shader };
        float r = SignalBuilder.LensRadius;
        m.SetShaderParameter("lens_radius", icon == Icon.Bike ? r * 0.5f : r);
        m.SetShaderParameter("min_pixels", Blooms(icon) ? 1.5f : 0f);
        return Materials[(int)icon] = m;
    }

    private static Material? _haloMaterial;
    private static Mesh? _haloMesh;

    /// <summary>
    /// The bloom round a lit car lens (#759): a quad facing the camera at the lens, added onto
    /// what is behind it, bright in the middle and fading to nothing at its edge. It is never
    /// smaller than a few pixels and larger at night (<c>world_night</c>, set by World/DayNight),
    /// when it also reaches further. A head shines along its axis: seen from the side its bloom
    /// fades out, so the heads of the cross street do not glow down the road you are on. The pull
    /// toward the eye is the lit lens's, so the bloom hides behind what hides the lens.
    /// </summary>
    private static Material HaloMaterial() => _haloMaterial ??= new ShaderMaterial { Shader = new Shader { Code = @"
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled;
global uniform float world_night;
// shaders/body/road.gdshaderinc's defaults, and the lens shader's pull of a lit lens
const float ROAD_DEPTH_BIAS = 0.0004, FAR_LIFT_START = 800.0, FAR_LIFT_RATE = 0.001, FAR_LIFT_MAX = 2.0;
const float LIT_BIAS = 0.0004;
const vec2 RADIUS_M = vec2(0.2, 0.32);        // day, night: the bloom's radius up close
const vec2 RADIUS_PX = vec2(2.5, 4.0);        // and the smallest it is drawn, in pixels
const vec2 STRENGTH = vec2(0.18, 0.45);
const vec2 FADE_START = vec2(500.0, 800.0), FADE_END = vec2(1000.0, 1500.0);
varying vec2 corner;
varying float strength;
void vertex() {
    vec3 centre = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 front = normalize((MODELVIEW_MATRIX * vec4(0.0, 0.0, 1.0, 0.0)).xyz);
    float night = clamp(world_night, 0.0, 1.0);
    float lit = step(0.5, max(COLOR.r, max(COLOR.g, COLOR.b)));
    float beam = smoothstep(0.3, 0.85, dot(front, normalize(-centre)));
    float depth = max(-centre.z, 0.0);
    float fade = 1.0 - smoothstep(mix(FADE_START.x, FADE_START.y, night), mix(FADE_END.x, FADE_END.y, night), depth);
    float pixel = 2.0 * depth / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y);
    float radius = max(mix(RADIUS_M.x, RADIUS_M.y, night), mix(RADIUS_PX.x, RADIUS_PX.y, night) * pixel);
    strength = lit * beam * fade * mix(STRENGTH.x, STRENGTH.y, night);
    corner = VERTEX.xy;
    vec4 view = vec4(centre + vec3(VERTEX.xy * radius * step(0.001, strength), 0.0), 1.0);
    float dist = length(view.xyz);
    float lift = clamp((dist - FAR_LIFT_START) * FAR_LIFT_RATE, 0.0, FAR_LIFT_MAX);
    view.xyz *= 1.0 - lift / max(dist, 1.0) - ROAD_DEPTH_BIAS - LIT_BIAS;
    POSITION = PROJECTION_MATRIX * view;
}
void fragment() {
    float d = clamp(1.0 - length(corner), 0.0, 1.0);
    ALBEDO = COLOR.rgb * strength * d * d;
}" } };

    /// <summary>The bloom's quad, corners at ±1 (main thread).</summary>
    private static Mesh HaloMesh()
    {
        if (_haloMesh != null) return _haloMesh;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0) };
        arrays[(int)Mesh.ArrayType.Normal] = new Vector3[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return _haloMesh = mesh;
    }

    /// <summary>A car head's lens blooms and grows with distance; a pedestrian or bike lens does not.</summary>
    private static bool Blooms(Icon icon) => icon is not (Icon.Standing or Icon.Walking or Icon.Bike);

    private SignalPlan[] _plans = [];
    private SignalBuilder.Lens[] _lenses = [];
    /// <summary>Per lens, its index in its shape's MultiMesh.</summary>
    private int[] _slot = [];
    /// <summary>Per junction, its lenses (start, count) in <see cref="_lenses"/>, sorted by junction.</summary>
    private (int Start, int Count)[] _byJunction = [];
    private double[] _next = [];
    private readonly MultiMesh?[] _multi = new MultiMesh?[IconCount];
    /// <summary>Per lens, its <see cref="Icon"/>.</summary>
    private Icon[] _icon = [];
    /// <summary>Per lens, its index in <see cref="_halos"/>, or -1 for a lens that does not bloom.</summary>
    private int[] _haloSlot = [];
    private MultiMesh? _halos;

    /// <summary>The time source: the server's clock; a probe may pin it.</summary>
    public static Func<double> Clock { get; set; } = () => Net.ClockSync.ServerNow;

    public static SignalLamps Create(SignalBuilder.Lamps lamps)
    {
        var node = new SignalLamps { Name = "SignalLamps" };
        node.Fill(lamps);
        return node;
    }

    private void Fill(SignalBuilder.Lamps lamps)
    {
        _plans = lamps.Plans.ToArray();
        _lenses = lamps.Lenses.OrderBy(l => l.Junction).ToArray();
        _slot = new int[_lenses.Length];
        _icon = new Icon[_lenses.Length];
        var counts = new int[IconCount];
        for (int i = 0; i < _lenses.Length; i++) _slot[i] = counts[(int)(_icon[i] = IconOf(_lenses[i], _plans[_lenses[i].Junction]))]++;
        for (int s = 0; s < IconCount; s++)
        {
            if (counts[s] == 0) continue;
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = LensMesh((Icon)s),
                InstanceCount = counts[s],
            };
            _multi[s] = multi;
            AddChild(new MultiMeshInstance3D
            {
                Multimesh = multi, MaterialOverride = Material((Icon)s),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        for (int i = 0; i < _lenses.Length; i++)
            _multi[(int)_icon[i]]!.SetInstanceTransform(_slot[i], _lenses[i].Transform);

        _haloSlot = new int[_lenses.Length];
        int halos = 0;
        for (int i = 0; i < _lenses.Length; i++) _haloSlot[i] = Blooms(_icon[i]) ? halos++ : -1;
        if (halos > 0)
        {
            _halos = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = HaloMesh(),
                InstanceCount = halos,
            };
            AddChild(new MultiMeshInstance3D
            {
                Name = "Halos", Multimesh = _halos, MaterialOverride = HaloMaterial(),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            for (int i = 0; i < _lenses.Length; i++)
                if (_haloSlot[i] >= 0) _halos.SetInstanceTransform(_haloSlot[i], _lenses[i].Transform);
        }

        _byJunction = new (int, int)[_plans.Length];
        for (int i = 0; i < _lenses.Length; i++)
        {
            int j = _lenses[i].Junction;
            if (_byJunction[j].Count == 0) _byJunction[j].Start = i;
            _byJunction[j].Count++;
        }
        _next = new double[_plans.Length];
        Array.Fill(_next, double.NegativeInfinity);
    }

    public override void _Process(double delta)
    {
        double now = Clock();
        for (int j = 0; j < _plans.Length; j++)
            if (now >= _next[j]) _next[j] = Recolour(j, now);
    }

    /// <summary>Sets a junction's lenses for <paramref name="now"/>; returns when they next change.</summary>
    private double Recolour(int j, double now)
    {
        var plan = _plans[j];
        var (start, count) = _byJunction[j];
        bool blinkOn = (long)Math.Floor(now / BlinkHalf) % 2 == 0, blinking = false;
        double until = double.PositiveInfinity;
        for (int i = start; i < start + count; i++)
        {
            var lens = _lenses[i];
            var aspect = plan.State(lens.Group, now);
            if (aspect == SignalAspect.FlashingAmber) blinking = true;
            var colour = Colour(lens.Role, aspect, blinkOn);
            _multi[(int)_icon[i]]!.SetInstanceColor(_slot[i], colour);
            if (_haloSlot[i] >= 0) _halos!.SetInstanceColor(_haloSlot[i], colour);   // dark lenses do not bloom
            until = Math.Min(until, plan.UntilChange(lens.Group, now));
        }
        if (blinking) until = Math.Min(until, BlinkHalf - (now % BlinkHalf));
        // a hair past the change, never a busy loop on a rounding edge
        return now + Math.Max(0.02, until + 0.005);
    }

    private static Color Colour(SignalBuilder.Role role, SignalAspect aspect, bool blinkOn) => role switch
    {
        // a two-lens pedestrian head has no yellow lens: its plan has no yellow either (#349)
        SignalBuilder.Role.Red => aspect is SignalAspect.Red or SignalAspect.RedAmber ? RedOn : RedOff,
        SignalBuilder.Role.Amber => aspect is SignalAspect.Amber or SignalAspect.RedAmber ? AmberOn : AmberOff,
        SignalBuilder.Role.Green => aspect == SignalAspect.Green ? GreenOn : GreenOff,
        _ => aspect == SignalAspect.FlashingAmber && blinkOn ? AmberOn : AmberOff,
    };

    /// <summary>A flat lens facing +Z, built once per icon (main thread). Figures in lens radii.</summary>
    private static Mesh LensMesh(Icon icon)
    {
        if (Meshes[(int)icon] is { } done) return done;
        float r = SignalBuilder.LensRadius;
        var flat = new Flat(icon == Icon.Bike ? r * 0.5f : r);   // a 100 mm bike lens (#351)
        switch (icon)
        {
            case Icon.LeftArrow: flat.Arrow(-1); break;
            case Icon.RightArrow: flat.Arrow(1); break;
            case Icon.Straight:
                flat.Fan([new(0f, 0.95f), new(-0.62f, 0.22f), new(0.62f, 0.22f)]);
                flat.Line(new(0f, 0.3f), new(0f, -0.85f), 0.4f);
                break;
            case Icon.StraightLeft: flat.StraightAndTurn(-1); break;
            case Icon.StraightRight: flat.StraightAndTurn(1); break;
            case Icon.Standing:
                // the red figure: upright, arms at its sides, feet together
                flat.Disc(new(0f, 0.64f), 0.17f, 10);
                flat.Fan([new(-0.21f, 0.43f), new(0.21f, 0.43f), new(0.17f, -0.14f), new(-0.17f, -0.14f)]);
                flat.Line(new(-0.27f, 0.38f), new(-0.3f, -0.1f), 0.11f);
                flat.Line(new(0.27f, 0.38f), new(0.3f, -0.1f), 0.11f);
                flat.Line(new(-0.09f, -0.12f), new(-0.1f, -0.84f), 0.14f);
                flat.Line(new(0.09f, -0.12f), new(0.1f, -0.84f), 0.14f);
                break;
            case Icon.Walking:
                // the green figure, striding to the right
                flat.Disc(new(0.08f, 0.66f), 0.17f, 10);
                flat.Line(new(0.04f, 0.44f), new(-0.04f, -0.08f), 0.27f);
                flat.Line(new(0.02f, 0.36f), new(0.32f, 0.04f), 0.1f);
                flat.Line(new(0.02f, 0.36f), new(-0.3f, 0.1f), 0.1f);
                flat.Line(new(-0.04f, -0.1f), new(0.36f, -0.84f), 0.14f);
                flat.Line(new(-0.04f, -0.1f), new(-0.12f, -0.44f), 0.14f);
                flat.Line(new(-0.12f, -0.44f), new(-0.4f, -0.82f), 0.14f);
                break;
            case Icon.Bike:
            {
                // two wheels, the diamond frame, saddle and bars
                Vector2 rear = new(-0.5f, -0.28f), crank = new(-0.02f, -0.28f), seat = new(-0.18f, 0.2f),
                    head = new(0.36f, 0.2f), front = new(0.5f, -0.28f);
                flat.Ring(rear, 0.34f, 0.1f, 12);
                flat.Ring(front, 0.34f, 0.1f, 12);
                const float W = 0.09f;
                flat.Line(rear, crank, W); flat.Line(rear, seat, W); flat.Line(seat, crank, W);
                flat.Line(seat, head, W); flat.Line(head, crank, W); flat.Line(head, front, W);
                flat.Line(new(-0.32f, 0.3f), new(-0.06f, 0.3f), 0.08f);
                flat.Line(head, new(0.3f, 0.38f), W);
                flat.Line(new(0.24f, 0.4f), new(0.44f, 0.36f), 0.08f);
                break;
            }
            default: flat.Disc(Vector2.Zero, 1f, 10); break;
        }
        return Meshes[(int)icon] = flat.Mesh();
    }

    /// <summary>Flat triangles in the lens plane, in units of the lens radius.</summary>
    private sealed class Flat(float radius)
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<int> _indices = new();

        /// <summary>A convex polygon.</summary>
        public void Fan(Vector2[] ring)
        {
            int start = _vertices.Count;
            foreach (var p in ring) _vertices.Add(new Vector3(p.X * radius, p.Y * radius, 0f));
            for (int i = 1; i + 1 < ring.Length; i++) _indices.AddRange([start, start + i, start + i + 1]);
        }

        public void Disc(Vector2 centre, float r, int n)
        {
            var ring = new Vector2[n];
            for (int i = 0; i < n; i++) ring[i] = centre + r * new Vector2(Mathf.Cos(Mathf.Tau * i / n), Mathf.Sin(Mathf.Tau * i / n));
            Fan(ring);
        }

        /// <summary>A stroke with round ends, so strokes that meet join cleanly.</summary>
        public void Line(Vector2 a, Vector2 b, float width)
        {
            var n = (b - a).Normalized().Orthogonal() * (width * 0.5f);
            Fan([a - n, b - n, b + n, a + n]);
            Disc(a, width * 0.5f, 6);
            Disc(b, width * 0.5f, 6);
        }

        public void Ring(Vector2 centre, float r, float width, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var d0 = new Vector2(Mathf.Cos(Mathf.Tau * i / n), Mathf.Sin(Mathf.Tau * i / n));
                var d1 = new Vector2(Mathf.Cos(Mathf.Tau * (i + 1) / n), Mathf.Sin(Mathf.Tau * (i + 1) / n));
                Fan([centre + d0 * (r - width * 0.5f), centre + d0 * (r + width * 0.5f), centre + d1 * (r + width * 0.5f), centre + d1 * (r - width * 0.5f)]);
            }
        }

        /// <summary>
        /// Straight on and a turn to <paramref name="dir"/> (-1 left, +1 right as the viewer sees
        /// it): a stem up with its head, and a branch off its middle with its own.
        /// </summary>
        public void StraightAndTurn(float dir)
        {
            float x = -0.3f * dir;
            Fan([new(x, 0.95f), new(x - 0.5f, 0.4f), new(x + 0.5f, 0.4f)]);
            Line(new(x, 0.45f), new(x, -0.85f), 0.32f);
            Line(new(x, -0.25f), new(x + 0.7f * dir, -0.25f), 0.32f);
            Fan([new(x + 1.22f * dir, -0.25f), new(x + 0.7f * dir, 0.25f), new(x + 0.7f * dir, -0.75f)]);
        }

        /// <summary>An arrow pointing along <paramref name="dir"/> (-1 left, +1 right as the viewer sees it): a head, then a shaft.</summary>
        public void Arrow(float dir)
        {
            Fan([new(dir, 0f), new(0f, 0.75f), new(0f, -0.75f)]);
            Fan([new(0f, -0.28f), new(0f, 0.28f), new(-dir, 0.28f), new(-dir, -0.28f)]);
        }

        public Mesh Mesh()
        {
            var normals = new Vector3[_vertices.Count];
            Array.Fill(normals, Vector3.Back);
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Godot.Mesh.ArrayType.Max);
            arrays[(int)Godot.Mesh.ArrayType.Vertex] = _vertices.ToArray();
            arrays[(int)Godot.Mesh.ArrayType.Normal] = normals;
            arrays[(int)Godot.Mesh.ArrayType.Index] = _indices.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }
}
