using Godot;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// A Swiss watch on the left wrist (#439): the world's time, the altitude and the speed, on the back
/// of the wrist, where looking at it for a moment opens the wrist menu (<see cref="XrWristMenu"/>).
/// Drawn in the headset only; its text is set only when it changes, twice a second at most.
/// </summary>
internal sealed partial class XrWatch : Node3D
{
    private const float Every = 0.5f;
    private Label3D _text = null!;
    private float _wait;

    public override void _Ready()
    {
        Name = "Watch";
        // on the back of the wrist (the controller's +X, the left hand's outside), face out
        Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(0.035f, 0.0f, 0.06f));
        var steel = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.74f, 0.76f), Metallic = 0.6f, Roughness = 0.35f };
        var strap = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.11f, 0.1f) };
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.021f, BottomRadius = 0.021f, Height = 0.008f, RadialSegments = 16 },
            Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, 0, -0.004f)),
            MaterialOverride = steel,
            Layers = XrSession.HeadsetOnlyLayer,
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.024f, 0.09f, 0.004f) },
            Transform = new Transform3D(Basis.Identity, new Vector3(0, 0, -0.008f)),
            MaterialOverride = strap,
            Layers = XrSession.HeadsetOnlyLayer,
        });
        _text = new Label3D
        {
            PixelSize = 0.00045f,
            FontSize = 24,
            OutlineSize = 0,
            Modulate = new Color(0.1f, 0.1f, 0.12f),
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector3(0, 0, 0.0005f),
            Layers = XrSession.HeadsetOnlyLayer,
            Shaded = false,
            DoubleSided = false,
        };
        AddChild(_text);
        // the dial: white under the text, a red Swiss cross at 12
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.018f, Height = 0.001f, RadialSegments = 16 },
            Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), Vector3.Zero),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.96f, 0.96f, 0.94f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Layers = XrSession.HeadsetOnlyLayer,
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.005f, 0.005f, 0.0012f) },
            Position = new Vector3(0, 0.013f, 0.0004f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.1f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Layers = XrSession.HeadsetOnlyLayer,
        });
    }

    /// <summary>Twice a second: the time, the altitude above the sea and the speed, set only when they change.</summary>
    public void Tick(FootPlayer? player, bool tracked, float dt)
    {
        // an untracked controller sits inside the head: not drawn then, like the hand markers
        Visible = tracked;
        _wait -= dt;
        if (_wait > 0f) return;
        _wait = Every;
        double hour = World.DayNight.Instance?.Hour ?? 12.0;
        int h = (int)hour % 24, m = (int)((hour - Math.Floor(hour)) * 60.0);
        string text = $"{h:00}:{m:00}";
        if (player != null)
        {
            double alt = player.Global.Alt;
            float kmh = player.Velocity.Length() * 3.6f;
            text += $"\n{alt:0} m\n{kmh:0} km/h";
        }
        if (text != _text.Text) _text.Text = text;
    }
}
