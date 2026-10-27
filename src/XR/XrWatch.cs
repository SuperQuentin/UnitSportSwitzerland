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
    /// <summary>The compass bezel (#1226): its N turned to the north, the amber notch toward the tracked target.</summary>
    private Node3D _bezel = null!;
    private Node3D _notch = null!;
    private static ArrayMesh? _ringMesh;

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
        // the compass bezel (#1226): a ring of ticks round the dial, N in red, turned so N points north; an amber notch
        // on it toward the tracked target. One mesh built once, turned per frame (a transform, no rebuild)
        _bezel = new Node3D { Name = "Bezel" };
        AddChild(_bezel);
        _bezel.AddChild(new MeshInstance3D
        {
            Mesh = _ringMesh ??= Ring(),
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Layers = XrSession.HeadsetOnlyLayer,
        });
        _notch = new Node3D { Name = "Target", Visible = false };
        _bezel.AddChild(_notch);
        _notch.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.004f, 0.005f, 0.002f) },
            Position = new Vector3(0, 0.0215f, 0.0012f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.98f, 0.72f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
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
        TurnBezel(player);
        _wait -= dt;
        if (_wait > 0f) return;
        _wait = Every;
        double hour = World.DayNight.Instance?.Hour ?? 12.0;
        int h = (int)hour % 24, m = (int)((hour - Math.Floor(hour)) * 60.0);
        Set(_time, $"{h:00}:{m:00}");
        Set(_alt, player != null ? $"{player.Global.Alt:0} m" : "");
        // tracking something (#1226): its distance where the speed was
        if (player != null && Adventure.Navigation.NavTarget.Current is { } t)
        {
            var g = player.Global;
            Set(_speed, "◆ " + Adventure.Navigation.NavFormat.Distance(Math.Sqrt((t.E - g.E) * (t.E - g.E) + (t.N - g.N) * (t.N - g.N))));
        }
        else Set(_speed, player != null ? $"{player.Velocity.Length() * 3.6f:0} km/h" : "");
    }

    /// <summary>
    /// Turns the bezel so its N points to the world's north (-Z) as seen on the dial, and its notch toward the tracked
    /// target. Per frame, a rotation only.
    /// </summary>
    private void TurnBezel(FootPlayer? player)
    {
        if (!IsInsideTree()) return;
        var b = GlobalBasis.Orthonormalized();
        // the world's north in the dial's plane (the dial faces local +Z; the ring's N tick is its local +Y)
        var north = b.Inverse() * new Vector3(0, 0, -1);
        float angle = Mathf.Atan2(-north.X, north.Y);
        _bezel.Rotation = new Vector3(0, 0, angle);
        if (player != null && Adventure.Navigation.NavTarget.Current is { } t)
        {
            var g = player.Global;
            float bearing = Ui.CompassStrip.Bearing(new Vector2((float)(t.E - g.E), (float)(t.N - g.N)));
            _notch.Rotation = new Vector3(0, 0, -Mathf.DegToRad(bearing));
            _notch.Visible = true;
        }
        else _notch.Visible = false;
    }

    /// <summary>The bezel ring: 24 ticks round the dial, every third longer, N red.</summary>
    private static ArrayMesh Ring()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < 24; i++)
        {
            float a = i * Mathf.Tau / 24;
            float len = i % 6 == 0 ? 0.004f : i % 3 == 0 ? 0.003f : 0.0018f, w = i == 0 ? 0.0022f : 0.0011f;
            var c = i == 0 ? new Color(0.85f, 0.1f, 0.1f) : new Color(0.12f, 0.12f, 0.14f);
            var dir = new Vector3(-Mathf.Sin(a), Mathf.Cos(a), 0);
            var side = new Vector3(dir.Y, -dir.X, 0) * w;
            var p0 = dir * 0.0195f + new Vector3(0, 0, 0.0011f);
            var p1 = dir * (0.0195f + len) + new Vector3(0, 0, 0.0011f);
            foreach (var v in new[] { p0 - side, p1 - side, p1 + side, p0 - side, p1 + side, p0 + side })
            {
                st.SetColor(c);
                st.SetNormal(Vector3.Back);
                st.AddVertex(v);
            }
        }
        return st.Commit();
    }

    private static void Set(Label3D label, string text)
    {
        if (text != label.Text) label.Text = text;
    }
}
