using Godot;
using UnitSport.Core;

namespace UnitSport.Occasions;

/// <summary>
/// Falling snow around the camera while the top occasion's <see cref="OccasionAtmosphere.Snowfall"/>
/// is above zero (Christmas). One static mesh animated entirely by <c>snowfall.gdshader</c>;
/// this node only builds it once and sets its density: thinner in the low valleys and none above the
/// cloud layer (<see cref="SnowCeiling"/>, #1637), by the camera's altitude through the world's frame
/// (in the earth frame and in space world Y is not the altitude). The shader draws nothing indoors,
/// per camera, so a door portal looking out shows the street's snow. While it snows a cloud deck
/// hangs over it (#1643, <c>cloud_deck.gdshader</c>): a slab of fog over the same ground, one
/// full-screen pass. Client only.
/// </summary>
public partial class OccasionPrecip : Node3D, Core.IOriginContainer
{
    private const int Flakes = 4000;
    private const float Box = 60f;

    private MeshInstance3D _snow = null!;
    private ShaderMaterial _material = null!;
    private MeshInstance3D _deck = null!;
    private ShaderMaterial _deckMaterial = null!;

    private readonly WorldOrigin? _origin;
    private readonly Func<Vector3, float?>? _groundHeight;

    /// <param name="origin">The world's frame: the camera's altitude is read through it.</param>
    /// <param name="groundHeight">The ground's (or still water's) world Y under a point, where a tile is loaded.</param>
    public OccasionPrecip(WorldOrigin? origin = null, Func<Vector3, float?>? groundHeight = null)
    {
        Name = "OccasionPrecip";
        _origin = origin;
        _groundHeight = groundHeight;
    }

    public override void _Ready()
    {
        _material = Styles.StyleKit.Material(Styles.MaterialRole.Precip);
        _material.SetShaderParameter("box", Box);
        FogUniforms.Apply(_material);
        _snow = new MeshInstance3D
        {
            Name = "Snowfall",
            Mesh = BuildMesh(),
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_snow);

        _deckMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cloud_deck.gdshader") };
        // after the other transparent passes (the water's surface is not in the depth buffer)
        _deckMaterial.RenderPriority = (int)Material.RenderPriorityMax - 1;
        _deck = new MeshInstance3D
        {
            Name = "CloudDeck",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _deckMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the shader puts it over the whole screen: never cull it
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_deck);
    }

    private static readonly StringName Density = "density", DeckBase = "deck_base", DeckTop = "deck_top", Strength = "strength";

    private static OccasionPrecip? _instance;
    private static float? _snowfallOverride;

    /// <summary>The snowfall now: the top occasion's, or what the movie studio replays.</summary>
    public static float Snowfall => _snowfallOverride ?? OccasionManager.Instance?.Atmosphere?.Atmosphere.Snowfall ?? 0f;

    /// <summary>
    /// The movie studio replaying a recording (#775): the snow held at this density, shown at once
    /// (the game may be paused). Null gives the occasion its snow back.
    /// </summary>
    public static float? SnowfallOverride
    {
        get => _snowfallOverride;
        set
        {
            _snowfallOverride = value;
            _instance?.Refresh();
        }
    }

    public override void _EnterTree() => _instance = this;

    public override void _ExitTree()
    {
        if (_instance == this) _instance = null;
    }

    public override void _Process(double delta)
    {
        if (Snowfall > 0f) TrackGround((float)delta);
        Refresh();
    }

    // the ground's altitude the cloud layer sits on: NaN until one is known
    private double _groundAlt = double.NaN;
    private float _groundSampleIn;
    private float _pushed = -1f, _pushedBase = float.NaN, _pushedStrength = -1f;

    /// <summary>
    /// The lowest ground round the camera (under it and 1.5 km out on eight sides), as an altitude,
    /// sampled twice a second and eased, like the occasion mist's top: the cloud layer pools over the
    /// valley rather than jumping over every ridge. Far up, where no tile under the camera is loaded,
    /// the last one is kept (0 m before any, which only matters in space, far above every cloud).
    /// </summary>
    private void TrackGround(float delta)
    {
        _groundSampleIn -= delta;
        if (_groundSampleIn > 0f || _groundHeight == null || GetViewport()?.GetCamera3D() is not { } cam) return;
        _groundSampleIn = 0.5f;
        var c = cam.GlobalPosition;
        if (c.Y < Interiors.InteriorManager.InteriorBaseY + 1000f) return;
        double? lowest = null;
        for (int i = -1; i < 8; i++)
        {
            var at = i < 0 ? c : c + new Vector3(Mathf.Cos(i * Mathf.Pi / 4f), 0, Mathf.Sin(i * Mathf.Pi / 4f)) * 1500f;
            if (_groundHeight(at) is not { } y) continue;
            double alt = _origin?.AltitudeOf(new Vector3(at.X, y, at.Z)) ?? y;
            if (lowest == null || alt < lowest) lowest = alt;
        }
        if (lowest is { } floor) _groundAlt = double.IsNaN(_groundAlt) ? floor : _groundAlt + (floor - _groundAlt) * 0.25;
    }

    private void Refresh()
    {
        if (_snow == null) return;
        float snowfall = Snowfall;
        var cam = GetViewport()?.GetCamera3D();
        // indoors (3 km down) the snow shader hides the flakes itself, and a door portal looks out at street level
        bool indoors = cam == null || cam.GlobalPosition.Y < Interiors.InteriorManager.InteriorBaseY + 1000f;
        double altitude = cam == null ? 0 : _origin?.AltitudeOf(cam.GlobalPosition) ?? cam.GlobalPosition.Y;
        double ground = double.IsNaN(_groundAlt) ? 0 : _groundAlt;

        float density = snowfall * (indoors ? 1f : SnowCeiling.Share(altitude, ground));
        bool shown = density > 0.001f;
        // the cloud layer crossed (not the occasion starting or ending): logged, it is rare
        if (shown != _snow.Visible && snowfall > 0f && cam != null)
            GD.Print(FormattableString.Invariant($"[snow] {(shown ? "under" : "above")} the clouds at {altitude:F0} m, ground {_groundAlt:F0} m"));
        _snow.Visible = shown;
        if (_snow.Visible && Mathf.Abs(density - _pushed) > 0.002f)
        {
            _pushed = density;
            _material.SetShaderParameter(Density, density);
        }

        // the deck, its heights over the camera: none indoors
        float strength = indoors ? 0f : SnowCeiling.DeckStrength(snowfall, altitude);
        _deck.Visible = strength > 0.001f;
        if (!_deck.Visible) return;
        float under = (float)(ground - altitude);
        float deckBase = under + (float)SnowCeiling.DeckBaseAboveGroundM, deckTop = under + (float)SnowCeiling.DeckTopAboveGroundM;
        if (!(Mathf.Abs(deckBase - _pushedBase) < 0.05f))
        {
            _pushedBase = deckBase;
            _deckMaterial.SetShaderParameter(DeckBase, deckBase);
            _deckMaterial.SetShaderParameter(DeckTop, deckTop);
        }
        if (Mathf.Abs(strength - _pushedStrength) > 0.002f)
        {
            _pushedStrength = strength;
            _deckMaterial.SetShaderParameter(Strength, strength);
        }
    }

    /// <summary>Every flake a quad whose corners carry its home, its corner and its hash; placed by the shader.</summary>
    private static ArrayMesh BuildMesh()
    {
        var rng = new Random(1225);
        var verts = new Vector3[Flakes * 4];
        var uv = new Vector2[Flakes * 4];
        var uv2 = new Vector2[Flakes * 4];
        var idx = new int[Flakes * 6];
        Vector2[] corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
        for (int f = 0; f < Flakes; f++)
        {
            var home = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * Box;
            float hash = (float)rng.NextDouble();
            for (int c = 0; c < 4; c++)
            {
                verts[f * 4 + c] = home;
                uv[f * 4 + c] = corners[c];
                uv2[f * 4 + c] = new Vector2(hash, 0);
            }
            int[] quad = [0, 1, 2, 0, 2, 3];
            for (int k = 0; k < 6; k++) idx[f * 6 + k] = f * 4 + quad[k];
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        // the shader moves every flake to wherever the camera is: never cull it
        mesh.CustomAabb = new Aabb(new Vector3(-1e5f, -1e4f, -1e5f), new Vector3(2e5f, 2e4f, 2e5f));
        return mesh;
    }
}
