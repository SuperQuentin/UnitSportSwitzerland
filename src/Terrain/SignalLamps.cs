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

    private static Material? _material;
    private static readonly Mesh?[] Meshes = new Mesh?[4];

    /// <summary>Lenses must stay lit (unshaded) and show their colour whatever the light.</summary>
    private static Material Material() => _material ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private SignalPlan[] _plans = [];
    private SignalBuilder.Lens[] _lenses = [];
    /// <summary>Per lens, its index in its shape's MultiMesh.</summary>
    private int[] _slot = [];
    /// <summary>Per junction, its lenses (start, count) in <see cref="_lenses"/>, sorted by junction.</summary>
    private (int Start, int Count)[] _byJunction = [];
    private double[] _next = [];
    private readonly MultiMesh?[] _multi = new MultiMesh?[4];

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
        var counts = new int[4];
        for (int i = 0; i < _lenses.Length; i++) _slot[i] = counts[(int)_lenses[i].Shape]++;
        for (int s = 0; s < 4; s++)
        {
            if (counts[s] == 0) continue;
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = LensMesh((SignalBuilder.Shape)s),
                InstanceCount = counts[s],
            };
            _multi[s] = multi;
            AddChild(new MultiMeshInstance3D
            {
                Multimesh = multi, MaterialOverride = Material(),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        for (int i = 0; i < _lenses.Length; i++)
            _multi[(int)_lenses[i].Shape]!.SetInstanceTransform(_slot[i], _lenses[i].Transform);

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
            _multi[(int)lens.Shape]!.SetInstanceColor(_slot[i], Colour(lens.Role, aspect, blinkOn));
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

    /// <summary>A flat lens facing +Z, built once per shape (main thread).</summary>
    private static Mesh LensMesh(SignalBuilder.Shape shape)
    {
        if (Meshes[(int)shape] is { } done) return done;
        float r = SignalBuilder.LensRadius;
        Vector2[] outline = shape switch
        {
            SignalBuilder.Shape.Square => [new(-r, -r), new(r, -r), new(r, r), new(-r, r)],
            SignalBuilder.Shape.LeftArrow => Arrow(r, -1),
            SignalBuilder.Shape.RightArrow => Arrow(r, 1),
            _ => Circle(r, 10),
        };
        var vertices = new Vector3[outline.Length];
        var normals = new Vector3[outline.Length];
        for (int i = 0; i < outline.Length; i++) { vertices[i] = new Vector3(outline[i].X, outline[i].Y, 0); normals[i] = Vector3.Back; }
        var indices = new List<int>();
        if (shape is SignalBuilder.Shape.LeftArrow or SignalBuilder.Shape.RightArrow)
        {
            // the head (0, 1, 2) and the shaft (3, 4, 5, 6)
            indices.AddRange([0, 1, 2, 3, 4, 5, 3, 5, 6]);
        }
        else
            for (int i = 1; i + 1 < outline.Length; i++) indices.AddRange([0, i, i + 1]);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return Meshes[(int)shape] = mesh;
    }

    private static Vector2[] Circle(float r, int n)
    {
        var p = new Vector2[n];
        for (int i = 0; i < n; i++) p[i] = new Vector2(r * Mathf.Cos(Mathf.Tau * i / n), r * Mathf.Sin(Mathf.Tau * i / n));
        return p;
    }

    /// <summary>An arrow pointing along <paramref name="dir"/> (-1 left, +1 right as the viewer sees it): a head, then a shaft.</summary>
    private static Vector2[] Arrow(float r, float dir) =>
    [
        new(dir * r, 0), new(0, r * 0.75f), new(0, -r * 0.75f),
        new(0, -r * 0.28f), new(0, r * 0.28f), new(-dir * r, r * 0.28f), new(-dir * r, -r * 0.28f),
    ];
}
