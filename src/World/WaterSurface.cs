using Godot;

namespace UnitSport.World;

/// <summary>
/// The client side of the water (#299), one per client world: pushes the wave constants at boot
/// and on every sea-state change (<see cref="WaterField.PushGlobals"/>), the wave clock every frame,
/// and, while the camera is under the surface, the underwater look: a full-screen fog and tint
/// (<c>shaders/underwater.gdshader</c>) and a low-pass on the master bus.
/// </summary>
public partial class WaterSurface : Node
{
    /// <summary>Under the surface by this much before the underwater look comes on (no flicker on the waterline).</summary>
    private const float Hysteresis = 0.05f;

    /// <summary>The muffled master bus's cut-off, Hz.</summary>
    private const float MuffledHz = 650f;

    private static readonly StringName SurfaceY = "surface_y", Retro = "retro";

    private MeshInstance3D? _fog;
    private ShaderMaterial? _fogMaterial;
    private AudioEffectLowPassFilter? _lowPass;
    private int _lowPassIndex = -1;
    private bool _under;

    /// <summary>Whether the camera is under the water now (for probes and the HUD).</summary>
    public bool CameraUnderwater => _under;

    public static WaterSurface? Instance { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        WaterField.PushGlobals();
        WaterField.SeaStateChanged += OnSeaState;

        _fogMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/underwater.gdshader") };
        _fogMaterial.RenderPriority = (int)Material.RenderPriorityMax;
        _fog = new MeshInstance3D
        {
            Name = "UnderwaterFog",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _fogMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the shader puts it over the whole screen: never cull it
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_fog);

        _lowPass = new AudioEffectLowPassFilter { CutoffHz = MuffledHz, Resonance = 0.5f };
        AudioServer.AddBusEffect(0, _lowPass);
        _lowPassIndex = AudioServer.GetBusEffectCount(0) - 1;
        AudioServer.SetBusEffectEnabled(0, _lowPassIndex, false);
    }

    public override void _ExitTree()
    {
        WaterField.SeaStateChanged -= OnSeaState;
        if (Instance == this) Instance = null;
        // remove our effect only, wherever it ended up
        for (int i = AudioServer.GetBusEffectCount(0) - 1; i >= 0; i--)
            if (AudioServer.GetBusEffect(0, i) == _lowPass) AudioServer.RemoveBusEffect(0, i);
    }

    private static void OnSeaState(float _) => WaterField.PushGlobals();

    public override void _Process(double delta)
    {
        WaterField.PushTime(WaterField.Now);

        var camera = GetViewport().GetCamera3D();
        bool under = false;
        float level = 0f;
        if (camera != null && WaterField.TryLevelAt(camera.GlobalPosition, out level))
            under = camera.GlobalPosition.Y < level - (_under ? -Hysteresis : Hysteresis);

        if (under)
        {
            _fogMaterial!.SetShaderParameter(SurfaceY, level);
        }
        if (under == _under) return;
        _under = under;
        _fog!.Visible = under;
        if (under) _fogMaterial!.SetShaderParameter(Retro, Styles.StyleKit.Applied == Styles.VisualStyle.Ps1);
        if (_lowPassIndex >= 0 && _lowPassIndex < AudioServer.GetBusEffectCount(0)
            && AudioServer.GetBusEffect(0, _lowPassIndex) == _lowPass)
            AudioServer.SetBusEffectEnabled(0, _lowPassIndex, under);
    }
}
