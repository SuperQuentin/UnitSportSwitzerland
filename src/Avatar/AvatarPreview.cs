using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Puts the avatars on a turntable against a plain backdrop, with no terrain in the way.
///
/// <para>
/// <c>godot --path . -- --avatars [seconds] [out.png]</c>
/// </para>
///
/// <para>
/// Exists because a figure that looks right beside a road at fifty metres can be wrong up close
/// in ways nothing else reveals — a knee bending the wrong way, a saddle the rider hovers over.
/// Checking that in the world means flying to a player and hoping the light is useful; this
/// shows all four in a row at a fixed distance, every time.
/// </para>
/// </summary>
public partial class AvatarPreview : Node3D
{
    private double _elapsed;
    private double _seconds = 6;
    private string _output = "";
    private float _viewDegrees = 90;
    private int _focus = -1;
    private float _crank = float.NaN;
    private float _stride = float.NaN;
    private Cyclist? _cyclist;
    private readonly List<Node3D> _turntables = new();

    public static bool Requested(out double seconds, out string output)
    {
        seconds = 6;
        output = "avatars.png";

        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--avatars");
        if (i < 0) return false;

        if (i + 1 < args.Length && double.TryParse(args[i + 1],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double s)) seconds = s;
        if (i + 2 < args.Length && !args[i + 2].StartsWith("--")) output = args[i + 2];
        return true;
    }

    public static AvatarPreview Create(double seconds, string output, float viewDegrees = 90,
        int focus = -1, float crank = float.NaN, float stride = float.NaN) =>
        new()
        {
            Name = "AvatarPreview", _seconds = seconds, _output = output,
            _viewDegrees = viewDegrees, _focus = focus, _crank = crank, _stride = stride,
        };

    public override void _Ready()
    {
        var material = HumanMeshBuilder.Material();

        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(Mathf.DegToRad(-42), Mathf.DegToRad(-35), 0),
            LightEnergy = 1.1f,
        });

        var env = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.44f, 0.50f, 0.56f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.58f, 0.62f),
                AmbientLightEnergy = 0.85f,
            },
        };
        AddChild(env);

        // ground disc, so the figures do not float in a void
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(24, 24) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.34f, 0.38f, 0.31f),
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            },
        };
        AddChild(ground);

        // --stride lays one gait cycle out as a strip. A walk cycle cannot be judged from a
        // single frame any more than a crank can: what matters is whether the planted foot
        // stays put between frames, which needs the frames side by side.
        if (!float.IsNaN(_stride))
        {
            const int steps = 6;
            var strip = new List<Node3D>();
            for (int i = 0; i < steps; i++)
                strip.Add(new MeshInstance3D
                {
                    Mesh = HumanMeshBuilder.BuildStride(
                        HumanPalette.ForRider(i), _stride, i / (float)steps),
                    MaterialOverride = material,
                });

            for (int i = 0; i < steps; i++) Place((i - (steps - 1) * 0.5f) * 1.15f, strip[i]);
            var strideCam = new Camera3D { Position = new Vector3(0, 0.95f, 12.5f), Fov = 34 };
            AddChild(strideCam);
            strideCam.LookAt(new Vector3(0, 0.85f, 0), Vector3.Up);
            strideCam.Current = true;
            return;
        }

        var subjects = new List<Node3D>
        {
            new MeshInstance3D
            {
                Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(0)),
                MaterialOverride = material,
            },
            new MeshInstance3D
            {
                Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(2), HumanPose.Running),
                MaterialOverride = material,
            },
            new MeshInstance3D
            {
                Mesh = BikeMeshBuilder.Build(),
                MaterialOverride = material,
            },
        };

        var cyclist = Cyclist.Create(4);
        cyclist.CadenceRpm = 78;
        subjects.Add(cyclist);
        _cyclist = cyclist;

        subjects.Add(new MeshInstance3D
        {
            Mesh = SkierMeshBuilder.BuildSkier(HumanPalette.ForRider(6), SkiPalette.ForRider(6)),
            MaterialOverride = material,
        });

        Camera3D camera;
        if (_focus >= 0 && _focus < subjects.Count)
        {
            Place(0f, subjects[_focus]);
            // Long lens from far back, i.e. near-orthographic. A close wide-angle view of a
            // bicycle exaggerates whichever end is nearer and makes correct proportions look
            // wrong — which cost an iteration before this was fixed.
            camera = new Camera3D { Position = new Vector3(0, 0.85f, 9.0f), Fov = 13 };
            AddChild(camera);
            camera.LookAt(new Vector3(0, 0.72f, 0), Vector3.Up);
        }
        else
        {
            // evenly spaced whatever the count, so adding a subject never needs a new table
            const float pitch = 1.85f;
            float first = -(subjects.Count - 1) * pitch * 0.5f;
            for (int i = 0; i < subjects.Count; i++) Place(first + i * pitch, subjects[i]);
            camera = new Camera3D { Position = new Vector3(0, 1.15f, 5.6f), Fov = 52 };
            AddChild(camera);
            camera.LookAt(new Vector3(0, 0.85f, 0), Vector3.Up);
        }

        camera.Current = true;
    }

    private void Place(float x, Node3D node)
    {
        var pivot = new Node3D { Position = new Vector3(x, 0, 0) };
        pivot.AddChild(node);
        AddChild(pivot);
        _turntables.Add(pivot);
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;

        // A fixed angle, not a turn. Bicycle and rider geometry is judged side-on — saddle
        // height against hip, hands against the drops, knee over the pedal spindle — and a
        // three-quarter view hides exactly those relationships.
        foreach (var pivot in _turntables)
            pivot.Rotation = new Vector3(0, Mathf.DegToRad(_viewDegrees), 0);

        // --crank parks the cranks so two runs can be compared; without it they spin freely
        if (!float.IsNaN(_crank) && _cyclist != null)
        {
            _cyclist.CadenceRpm = 0;
            _cyclist.SetCrankAngle(_crank);
        }

        if (_elapsed < _seconds) return;

        var image = GetViewport().GetTexture().GetImage();
        var error = image.SavePng(_output);
        GD.Print(error == Error.Ok
            ? $"[avatars] wrote {_output} ({image.GetWidth()}x{image.GetHeight()})"
            : $"[avatars] FAILED to write {_output}: {error}");
        GetTree().Quit();
    }
}
