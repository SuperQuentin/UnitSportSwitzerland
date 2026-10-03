using Godot;

namespace UnitSport.Items;

/// <summary>
/// Star glints rising and twinkling round a radio while it plays (#387), kicking out on every beat.
///
/// <para>
/// Built for cost, not for knobs: no <c>GPUParticles3D</c> (a compute pass and a buffer per
/// emitter) and no <c>CPUParticles3D</c> (a simulation per frame). Every radio shares one static
/// mesh of <see cref="Count"/> quads and one material; <c>shaders/radio_sparkles.gdshader</c> works
/// out each glint from <c>TIME</c> alone. Per radio that is one draw call, culled past
/// <see cref="Range"/> and not drawn at all while silent; per frame, one or two instance uniforms
/// through cached <see cref="StringName"/>s, nothing allocated.
/// </para>
///
/// <para>
/// The beat comes from the caller (<see cref="RadioBody.BeatOf"/>, the shared clock), so the glints
/// kick in time with the bounce and the dancers on every screen, with nothing on the wire.
/// </para>
/// </summary>
public partial class RadioSparkles : MeshInstance3D
{
    public const int Count = 40;
    public const float Range = 40f;

    private const float FadeIn = 2.5f, FadeOut = 1.5f;   // per second

    private static readonly StringName UOn = "on", UPulse = "pulse", UBeat = "beat";

    private float _on, _shownOn = -1f, _shownPulse = -1f;

    /// <summary>How much shows, 0 (hidden) to 1. For the probes.</summary>
    public float Shown => _on;
    private int _shownBeat = -1;

    public RadioSparkles()
    {
        Name = "Sparkles";
        Mesh = SharedMesh;
        MaterialOverride = SharedMaterial;
        CastShadow = ShadowCastingSetting.Off;
        GIMode = GIModeEnum.Disabled;
        // the shader moves every vertex: the box it can reach, so culling stays right
        CustomAabb = new Aabb(new Vector3(-0.45f, -0.2f, -0.35f), new Vector3(0.9f, 0.85f, 0.7f));
        VisibilityRangeEnd = Range;
        Visible = false;
    }

    /// <summary>
    /// Once a frame from the radio's owner node: <paramref name="playing"/> fades the glints in or
    /// out; with a <paramref name="beat"/> (false while the CD is unknown here) they kick on it.
    /// </summary>
    public void Step(bool playing, bool hasBeat, float phase, int beat, float dt)
    {
        _on = Mathf.MoveToward(_on, playing ? 1f : 0f, dt * (playing ? FadeIn : FadeOut));
        bool show = _on > 0f;
        if (Visible != show) Visible = show;
        if (!show) return;
        Set(UOn, _on, ref _shownOn);
        Set(UPulse, hasBeat ? Mathf.Exp(-phase * 6f) : 0f, ref _shownPulse);
        int b = hasBeat ? beat & 3 : 0;
        if (b != _shownBeat)
        {
            _shownBeat = b;
            SetInstanceShaderParameter(UBeat, (float)b);
        }
    }

    /// <summary>Gone at once (the hand now holds something else).</summary>
    public void Off()
    {
        _on = 0f;
        if (Visible) Visible = false;
    }

    private void Set(StringName name, float value, ref float shown)
    {
        if (Mathf.Abs(value - shown) < 0.004f) return;
        shown = value;
        SetInstanceShaderParameter(name, value);
    }

    // ---- shared by every radio --------------------------------------------------------------------

    private static ArrayMesh? _mesh;
    private static ShaderMaterial? _material;

    private static ShaderMaterial SharedMaterial => _material ??= new ShaderMaterial
    {
        Shader = GD.Load<Shader>("res://shaders/radio_sparkles.gdshader"),
    };

    /// <summary>
    /// <see cref="Count"/> quads, each glint's four corners on its spawn point round the boombox
    /// (0.46 x 0.22 x 0.16 m), with its corner in UV and two hashes in UV2. Seeded: the same everywhere.
    /// </summary>
    private static ArrayMesh SharedMesh
    {
        get
        {
            if (_mesh != null) return _mesh;
            var rng = new Random(387);
            var verts = new Vector3[Count * 4];
            var uv = new Vector2[Count * 4];
            var uv2 = new Vector2[Count * 4];
            var idx = new int[Count * 6];
            Span<Vector2> corners = stackalloc Vector2[] { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) };
            for (int i = 0; i < Count; i++)
            {
                // round the box, a little outside it, mostly on the upper half
                float a = rng.NextSingle() * Mathf.Tau;
                var home = new Vector3(
                    Mathf.Cos(a) * (0.26f + 0.06f * rng.NextSingle()),
                    -0.06f + 0.22f * rng.NextSingle(),
                    Mathf.Sin(a) * (0.11f + 0.05f * rng.NextSingle()));
                var hash = new Vector2(rng.NextSingle(), rng.NextSingle());
                for (int c = 0; c < 4; c++)
                {
                    verts[i * 4 + c] = home;
                    uv[i * 4 + c] = corners[c];
                    uv2[i * 4 + c] = hash;
                }
                int v = i * 4, k = i * 6;
                idx[k] = v; idx[k + 1] = v + 1; idx[k + 2] = v + 2;
                idx[k + 3] = v; idx[k + 4] = v + 2; idx[k + 5] = v + 3;
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.TexUV] = uv;
            arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
            arrays[(int)Mesh.ArrayType.Index] = idx;
            _mesh = new ArrayMesh();
            _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return _mesh;
        }
    }
}
