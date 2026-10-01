using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A little photo studio for one vehicle: its own <see cref="World3D"/> (nothing of the game world
/// in it), a key light, a camera that frames whatever it is given from three-quarters front, and two
/// spotlights at the nose for headlight pools on the floor. <see cref="RideUi"/> uses one to
/// pre-render the card thumbnails (<see cref="RideThumbs"/>, transparent background) and one live,
/// for the vehicle under the pointer, whose doors and lamps it works (<see cref="Juice"/>).
/// </summary>
public partial class RideStage : SubViewport
{
    private Node3D _turntable = null!;
    private Node3D? _subject;
    private Camera3D _camera = null!;
    private SpotLight3D _lampL = null!, _lampR = null!;
    private MeshInstance3D? _floor;
    private bool _live;
    private float _lights;

    /// <summary>Doors open and lamps on, 0..1: what the hover asks for (the rig eases its own doors).</summary>
    public float Juice { get; set; }

    /// <summary>Turntable yaw, radians, about the subject's centre.</summary>
    public float Yaw { get => _turntable.Rotation.Y; set => _turntable.Rotation = new Vector3(0, value, 0); }

    /// <summary>The thumbnails' angle: front-left three-quarters.</summary>
    public const float ThumbYaw = 0f;

    public Node3D? Subject => _subject;

    /// <param name="live">With a floor and an opaque backdrop (the preview pane); without, transparent (thumbnails).</param>
    public static RideStage Create(Vector2I size, bool live) => new()
    {
        Name = live ? "RideStage" : "ThumbStage",
        Size = size,
        OwnWorld3D = true,
        TransparentBg = !live,
        Msaa3D = Viewport.Msaa.Msaa4X,
        RenderTargetUpdateMode = UpdateMode.Disabled,
        _live = live,
    };

    public override void _Ready()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = _live ? Godot.Environment.BGMode.Color : Godot.Environment.BGMode.ClearColor,
            BackgroundColor = new Color(0.07f, 0.08f, 0.10f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.66f, 0.74f),
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            GlowEnabled = _live,
            GlowIntensity = 0.9f,
            GlowBloom = 0.08f,
            GlowHdrThreshold = 0.9f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D { LightEnergy = 1.25f, ShadowEnabled = _live };
        sun.RotationDegrees = new Vector3(-48, -32, 0);
        AddChild(sun);
        // a cool rim from behind, so a dark car's roofline still reads against the backdrop
        var rim = new DirectionalLight3D { LightEnergy = 0.55f, LightColor = new Color(0.7f, 0.8f, 1f) };
        rim.RotationDegrees = new Vector3(-20, 150, 0);
        AddChild(rim);

        _camera = new Camera3D { Fov = 28f, Current = true, Near = 0.05f, Far = 200f };
        AddChild(_camera);

        _turntable = new Node3D { Name = "Turntable" };
        AddChild(_turntable);

        if (_live)
        {
            // a dark disc to stand on: catches the headlight pools and the sun's shadow
            var disc = new CylinderMesh { TopRadius = 1, BottomRadius = 1, Height = 0.02f, RadialSegments = 48 };
            _floor = new MeshInstance3D
            {
                Mesh = disc,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.17f, 0.2f), Roughness = 0.85f },
            };
            AddChild(_floor);
        }

        _lampL = Lamp();
        _lampR = Lamp();
    }

    private SpotLight3D Lamp()
    {
        var lamp = new SpotLight3D
        {
            LightColor = new Color(1f, 0.95f, 0.82f),
            LightEnergy = 0,
            SpotRange = 9f,
            SpotAngle = 28f,
            SpotAttenuation = 0.8f,
            ShadowEnabled = false,
            Visible = _live,
        };
        _turntable.AddChild(lamp);
        return lamp;
    }

    /// <summary>Puts a new subject on the turntable (freeing the last) and frames it.</summary>
    public void Show(Node3D? visual)
    {
        if (_subject != null)
        {
            _subject.QueueFree();
            _subject = null;
        }
        Juice = 0;
        _lights = 0;
        _turntable.Rotation = Vector3.Zero;   // framed facing the camera; the caller turns it after
        if (visual == null) return;
        _subject = visual;
        _turntable.AddChild(visual);
        Frame();
    }

    /// <summary>Camera, floor and lamps from the subject's bounds: whatever size it is, it fills the frame.</summary>
    private void Frame()
    {
        if (_subject == null) return;
        var box = Bounds(_subject);
        if (box.Size == Vector3.Zero) box = new Aabb(new Vector3(-0.5f, 0, -0.5f), Vector3.One);
        // the subject stands on the floor and turns about its own middle
        var centre = box.GetCenter();
        _subject.Position -= new Vector3(centre.X, box.Position.Y, centre.Z);
        float height = box.Size.Y;

        // three-quarters from the front left (vehicles face −Z), a little above; the distance is
        // the least that keeps all eight corners of the box in frame, so a tall rider and a long
        // coach both fill it
        var dir = new Vector3(-0.78f, 0.42f, -1f).Normalized();
        var target = new Vector3(0, height * 0.5f, 0);
        var right = Vector3.Up.Cross(dir).Normalized();
        var up = dir.Cross(right);
        float aspect = Size.X / (float)Mathf.Max(1, Size.Y);
        float tanV = Mathf.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f), tanH = tanV * aspect;
        var half = box.Size * 0.5f;
        // the live stage turns it: frame the circle it sweeps, not the box at one angle
        if (_live) half.X = half.Z = new Vector2(half.X, half.Z).Length() * 0.82f;
        float dist = 0.5f;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? -half.X : half.X, ((i & 2) == 0 ? 0 : height) - target.Y, (i & 4) == 0 ? -half.Z : half.Z);
            float along = c.Dot(dir);
            dist = Mathf.Max(dist, along + Mathf.Abs(c.Dot(right)) / tanH);
            dist = Mathf.Max(dist, along + Mathf.Abs(c.Dot(up)) / tanV);
        }
        dist *= 1.06f;   // a little air round it
        _camera.Position = target + dir * dist;
        _camera.LookAt(target, Vector3.Up);

        if (_floor != null)
        {
            float r = Mathf.Max(box.Size.X, box.Size.Z) * 0.75f + 1.5f;
            _floor.Scale = new Vector3(r, 1, r);
            _floor.Position = new Vector3(0, -0.01f, 0);
        }
        // at the nose, a hand's width in from each side, aimed down the road
        float nose = box.Position.Z - centre.Z;
        float side = box.Size.X * 0.32f;
        float lampY = Mathf.Clamp(height * 0.33f, 0.45f, 1.2f);
        foreach (var (lamp, x) in new[] { (_lampL, -side), (_lampR, side) })
        {
            lamp.Position = new Vector3(x, lampY, nose + 0.05f);
            lamp.LookAt(lamp.GlobalPosition + new Vector3(0, -0.28f, -1f), Vector3.Up);
            lamp.SpotRange = Mathf.Max(6f, box.Size.Z * 1.6f);
        }
    }

    public override void _Process(double delta)
    {
        if (!_live || _subject == null) return;
        float dt = (float)delta;
        bool on = Juice > 0.5f;
        // the lamps come up just after the doors start to move, and go a touch quicker than they came
        _lights = Mathf.MoveToward(_lights, on ? 1f : 0f, dt * (on ? 2.4f : 3.5f));
        float flicker = on && _lights < 0.6f ? (Mathf.Sin(_lights * 60f) * 0.5f + 0.5f) : 1f;   // a cold lamp catching
        _lampL.LightEnergy = _lampR.LightEnergy = 3.2f * _lights * flicker;
        Apply(_subject, on);
    }

    /// <summary>Doors and lamps on the rigs that have them; anything else just turns.</summary>
    private static void Apply(Node node, bool on)
    {
        switch (node)
        {
            case CarRig car:
                car.DoorsOpen = on ? (byte)(CarRig.DoorLeft | CarRig.DoorRight | CarRig.DoorRearLeft | CarRig.DoorRearRight) : (byte)0;
                car.Headlights = on;
                car.BrakeLights = on;
                return;
            case HeavyRig heavy:
                heavy.DoorsOpen = on ? (byte)0xFF : (byte)0;
                heavy.Headlights = on;
                heavy.BrakeLights = on;
                break;
        }
        // a truck's trailer sections hang under its rig
        foreach (var child in node.GetChildren())
            if (child is HeavyRig) Apply(child, on);
    }

    /// <summary>The subject's bounds in the turntable's frame, from every mesh in it.</summary>
    private Aabb Bounds(Node3D root)
    {
        var inv = _turntable.GlobalTransform.AffineInverse();
        Aabb? box = null;
        foreach (var g in Descendants<GeometryInstance3D>(root).Prepend(root as GeometryInstance3D).OfType<GeometryInstance3D>())
        {
            if (!g.Visible) continue;
            var b = (inv * g.GlobalTransform) * g.GetAabb();
            box = box is { } a ? a.Merge(b) : b;
        }
        return box ?? new Aabb();
    }

    private static IEnumerable<T> Descendants<T>(Node node) where T : Node
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}

/// <summary>
/// The card thumbnails: each vehicle rendered once on a transparent <see cref="RideStage"/>, a
/// frame or two apiece, kept in memory and as a PNG under <c>user://thumbs/</c> keyed by what the
/// model is built from, so the next session shows them at once. Rendered on demand, the open tab
/// first, so the menu never waits for the whole roster.
/// </summary>
public partial class RideThumbs : Node
{
    public static readonly Vector2I ThumbSize = new(288, 176);
    private const string Dir = "user://thumbs";
    /// <summary>Bump to re-render every cached thumbnail (framing or lighting changed).</summary>
    private const int Version = 4;

    private RideStage _stage = null!;
    private readonly Dictionary<string, ImageTexture> _done = new();
    private readonly List<(string Key, Func<Node3D?> Build, Action<Texture2D> Ready)> _queue = new();
    private (string Key, Action<Texture2D> Ready)? _shooting;
    private int _wait;

    public override void _Ready()
    {
        _stage = RideStage.Create(ThumbSize, live: false);
        AddChild(_stage);
        DirAccess.MakeDirRecursiveAbsolute(Dir);
    }

    /// <summary>
    /// The thumbnail for <paramref name="key"/>: at once if it is in memory or on disk, else
    /// queued (<paramref name="first"/> puts it ahead of the rest) and handed over when drawn.
    /// </summary>
    public void Request(string key, Func<Node3D?> build, Action<Texture2D> ready, bool first = false)
    {
        string file = FileFor(key);
        var tex = _done.GetValueOrDefault(key) ?? (Godot.FileAccess.FileExists(file) ? Load(key, file) : null);
        if (tex != null)
        {
            ready(tex);
            return;
        }
        int at = _queue.FindIndex(q => q.Key == key);
        if (at >= 0)
        {
            var q = _queue[at];
            _queue.RemoveAt(at);
            _queue.Insert(first ? 0 : _queue.Count, (key, q.Build, t => { q.Ready(t); ready(t); }));
            return;
        }
        var item = (key, build, ready);
        if (first) _queue.Insert(0, item);
        else _queue.Add(item);
    }

    private ImageTexture? Load(string key, string file)
    {
        var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(file));
        if (img == null || img.IsEmpty()) return null;
        return _done[key] = ImageTexture.CreateFromImage(img);
    }

    private static string FileFor(string key) => $"{Dir}/{Fnv(key + "|v" + Version):x16}.png";

    /// <summary>Stable across runs (string.GetHashCode is not).</summary>
    private static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037;
        foreach (char c in s) { h ^= c; h *= 1099511628211; }
        return h;
    }

    public override void _Process(double delta)
    {
        if (_shooting is { } shot)
        {
            // two frames: one for the rig's first-frame settle, one drawn with it settled
            if (--_wait > 0) return;
            var img = _stage.GetTexture().GetImage();
            _stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            _stage.Show(null);
            _shooting = null;
            if (img != null && !img.IsEmpty())
            {
                var tex = _done[shot.Key] = ImageTexture.CreateFromImage(img);
                img.SavePng(FileFor(shot.Key));
                shot.Ready(tex);
            }
            return;
        }
        if (_queue.Count == 0) return;
        var (key, build, ready) = _queue[0];
        _queue.RemoveAt(0);
        if (_done.TryGetValue(key, out var have))
        {
            ready(have);
            return;
        }
        var visual = build();
        if (visual == null) return;
        _stage.Show(visual);
        _stage.Yaw = RideStage.ThumbYaw;
        _stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _shooting = (key, ready);
        _wait = 3;
    }
}
