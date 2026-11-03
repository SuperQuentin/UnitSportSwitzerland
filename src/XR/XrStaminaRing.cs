using Godot;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.XR;

/// <summary>
/// The stamina ring on the left wrist (#1225, <c>docs/notes/ui/stamina-ring.md</c>): the same
/// <see cref="StaminaRing"/> and shader as the flat HUD, drawn into a small transparent
/// <see cref="SubViewport"/> and shown on a billboard quad beside the watch, so it faces the eye.
/// A child of <see cref="XrWatch"/> (it follows the wrist). Drawn in the headset only, and only while the
/// ring is shown (the meter not full), the viewport updating only then.
/// </summary>
internal sealed partial class XrStaminaRing : Node3D
{
    private const int Pixels = 64;
    private const float Metres = 0.045f;

    private readonly SubViewport _view;
    private readonly StaminaRing _ring;
    private readonly MeshInstance3D _quad;

    public XrStaminaRing()
    {
        Name = "StaminaRing";
        _view = new SubViewport
        {
            Size = new Vector2I(Pixels, Pixels),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Disable3D = true,
        };
        AddChild(_view);
        _ring = new StaminaRing(null, inWorld: true) { Size = new Vector2(Pixels, Pixels) };
        _view.AddChild(_ring);
        _quad = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(Metres, Metres) },
            // along the arm from the dial, a little proud of it
            Position = new Vector3(0f, 0.05f, 0.012f),
            Layers = XrSession.HeadsetOnlyLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = _view.GetTexture(),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = false,
            },
        };
        AddChild(_quad);
    }

    /// <summary>Once a frame after the watch: the meter of the body in the rig.</summary>
    public void Tick(FootPlayer? player, float dt)
    {
        _ring.Player = player;
        _ring.Tick(dt);
        bool show = _ring.Shown;
        if (_quad.Visible != show)
        {
            _quad.Visible = show;
            _view.RenderTargetUpdateMode = show ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }
    }
}
