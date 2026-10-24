using Godot;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// A Swiss watch on the left wrist (#439): the world's time, the altitude and the speed. It sits on
/// the drawn left hand's wrist (<see cref="XrHand.WatchSpot"/>, #1097), and reading it opens the
/// radial menu over it (<see cref="XrWristRing"/>). Drawn in the headset only; its text is set only
/// when it changes, twice a second at most.
/// </summary>
internal sealed partial class XrWatch : Node3D
{
    private const float Every = 0.5f;
    private Label3D _time = null!, _alt = null!, _speed = null!;
    private float _wait;

    public override void _Ready()
    {
        Name = "Watch";
        var steel = Styles.StyleKit.Figure(new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.74f, 0.76f), Metallic = 0.6f, Roughness = 0.35f });
        var strap = Styles.StyleKit.Figure(new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.11f, 0.1f), CullMode = BaseMaterial3D.CullModeEnum.Disabled });
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.021f, BottomRadius = 0.021f, Height = 0.008f, RadialSegments = 16 },
            Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, 0, -0.004f)),
            MaterialOverride = steel,
            Layers = XrSession.HeadsetOnlyLayer,
        });
        // the strap: a band round the wrist (its axis the arm's, local Y; the dial sits 3 cm off the arm's line,
        // XrHand.WatchSpot), its top under the case. A flat bar along the arm pointed at the fingers (#1112)
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.029f, BottomRadius = 0.029f, Height = 0.02f, RadialSegments = 20, Rings = 1, CapTop = false, CapBottom = false },
            Transform = new Transform3D(Basis.Identity, new Vector3(0, 0, -0.03f)),
            MaterialOverride = strap,
            Layers = XrSession.HeadsetOnlyLayer,
        });
        // the face: upright to the eye that reads it, a quarter turn left of the strap's line (#1112: the text read
        // sideways), the time in the middle and the altitude and the speed small above and under it, inside the dial
        var face = new Node3D { Name = "Face", Basis = new Basis(Vector3.Back, Mathf.Pi / 2) };
        AddChild(face);
        _time = Line(32, 0.00034f, 0f);
        _alt = Line(24, 0.00022f, 0.0085f);
        _speed = Line(24, 0.00022f, -0.0085f);
        face.AddChild(_time);
        face.AddChild(_alt);
        face.AddChild(_speed);
        // the dial: white under the text, a red Swiss cross at 12
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.018f, Height = 0.001f, RadialSegments = 16 },
            Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), Vector3.Zero),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.96f, 0.96f, 0.94f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Layers = XrSession.HeadsetOnlyLayer,
        });
        face.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.004f, 0.004f, 0.0012f) },
            Position = new Vector3(0, 0.0148f, 0.0004f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.1f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Layers = XrSession.HeadsetOnlyLayer,
        });
    }

    private static Label3D Line(int fontSize, float pixel, float y) => new()
    {
        PixelSize = pixel,
        FontSize = fontSize,
        OutlineSize = 0,
        Modulate = new Color(0.1f, 0.1f, 0.12f),
        HorizontalAlignment = HorizontalAlignment.Center,
        Position = new Vector3(0, y, 0.0009f),   // clear of the dial's face (0.0005)
        Layers = XrSession.HeadsetOnlyLayer,
        Shaded = false,
        DoubleSided = false,
    };

    /// <summary>The watch in the model viewer, moved off the headset-only layer so the viewer's camera sees it.</summary>
    [Core.Showcase("Figures", "VR watch")]
    private static Node3D ShowcaseWatch()
    {
        // stood on the strap's end, the dial clear of the viewer's ground
        var root = new Node3D();
        var watch = Viewable();
        watch.Position = new Vector3(0, 0.045f, 0);
        root.AddChild(watch);
        return root;
    }

    /// <summary>A watch the model viewer's camera sees (not on the headset-only layer), with sample readings.</summary>
    internal static XrWatch Viewable()
    {
        var watch = new XrWatch();
        watch.Ready += () =>
        {
            watch._time.Text = "10:32";
            watch._alt.Text = "1048 m";
            watch._speed.Text = "112 km/h";
            foreach (var v in watch.FindChildren("*", "VisualInstance3D", true, false).OfType<VisualInstance3D>()) v.Layers = 1;
        };
        return watch;
    }

    /// <summary>Twice a second: the time, the altitude above the sea and the speed, set only when they change.</summary>
    /// <param name="spot">Where it sits on the hand it is a child of (<see cref="XrHand.WatchSpot"/>).</param>
    public void Tick(FootPlayer? player, bool tracked, Transform3D spot, float dt)
    {
        // an untracked controller sits inside the head: not drawn then, like the hands
        Visible = tracked;
        if (Transform != spot) Transform = spot;
        _wait -= dt;
        if (_wait > 0f) return;
        _wait = Every;
        double hour = World.DayNight.Instance?.Hour ?? 12.0;
        int h = (int)hour % 24, m = (int)((hour - Math.Floor(hour)) * 60.0);
        Set(_time, $"{h:00}:{m:00}");
        Set(_alt, player != null ? $"{player.Global.Alt:0} m" : "");
        Set(_speed, player != null ? $"{player.Velocity.Length() * 3.6f:0} km/h" : "");
    }

    private static void Set(Label3D label, string text)
    {
        if (text != label.Text) label.Text = text;
    }
}
