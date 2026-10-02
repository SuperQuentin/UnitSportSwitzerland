using Godot;
using UnitSport.Audio.Cd;

namespace UnitSport.Interiors;

/// <summary>
/// The night club (#370): while the chess type beat plays the church goes dark and a mirror ball
/// over the aisle throws coloured beams and sparkles on the beat. The walls are unlit vertex
/// colours (<c>ps1_interior</c>), so they get it from the shader (the per-instance
/// <c>disco</c>, the global beat <c>world_disco_beat</c>); characters, lit by the ambient, get
/// coloured spots, a pulsing lamp and a darker room (<see cref="World.DayNight.Disco"/>). Built
/// once, hidden when idle; off, nothing of it is drawn.
/// </summary>
public partial class ChurchStage
{
    private static readonly StringName UDisco = "disco", UCenter = "disco_center", GBeat = "world_disco_beat";

    private static readonly Color[] BeamColors =
    {
        new(1f, 0.15f, 0.55f), new(0.2f, 0.5f, 1f), new(0.2f, 1f, 0.45f), new(1f, 0.85f, 0.15f), new(0.7f, 0.25f, 1f), new(1f, 0.4f, 0.1f),
    };

    private Node3D? _disco;
    private Node3D? _ball;
    private Node3D? _beams;
    private readonly List<SpotLight3D> _spots = new();
    private OmniLight3D? _pulse;
    private Vector3 _centerLocal;
    private Vector3 _centerShown = new(float.NaN, 0, 0);
    private bool _darkening;

    private void StartDisco(CdInfo cd)
    {
        _disco ??= BuildDisco();
        _disco.Visible = true;
        _centerShown = new Vector3(float.NaN, 0, 0);
        foreach (var m in _meshes) m.SetInstanceShaderParameter(UDisco, 1f);
    }

    private void StepDisco(float beat)
    {
        RenderingServer.GlobalShaderParameterSet(GBeat, beat);
        // the shader works in world space: the ball's centre follows an origin shift
        var center = _interior.ToGlobal(_centerLocal);
        if (center != _centerShown)
        {
            _centerShown = center;
            foreach (var m in _meshes) m.SetInstanceShaderParameter(UCenter, center);
        }
        float ph = beat - Mathf.Floor(beat);
        float pulse = Mathf.Exp(-ph * 4f);
        if (_ball != null) _ball.Rotation = new Vector3(0, beat * 0.4f, 0);
        if (_beams != null) _beams.Rotation = new Vector3(0, -beat * 0.785f, 0);
        int hue = Mathf.FloorToInt(beat);
        for (int i = 0; i < _spots.Count; i++)
        {
            var spot = _spots[i];
            // each sweeps its own circle, a bar a turn, and takes the next colour on every beat
            float a = beat * Mathf.Pi / 2f + i * Mathf.Tau / _spots.Count;
            spot.Rotation = new Vector3(-1.1f + 0.35f * Mathf.Sin(a * 1.3f), a, 0);
            spot.LightColor = BeamColors[(((hue + i) % BeamColors.Length) + BeamColors.Length) % BeamColors.Length];
            spot.LightEnergy = 3f + 4f * pulse;
        }
        if (_pulse != null)
        {
            _pulse.LightColor = BeamColors[((hue * 2) % BeamColors.Length + BeamColors.Length) % BeamColors.Length];
            _pulse.LightEnergy = 0.4f + 2.2f * pulse;
        }
        // the room around a character goes dark only for whoever is in this church
        bool here = LocalHere();
        if (here) World.DayNight.Disco = 1f;
        else if (_darkening) World.DayNight.Disco = 0f;
        _darkening = here;
    }

    private void StopDisco()
    {
        if (_disco != null) _disco.Visible = false;
        foreach (var m in _meshes)
            if (IsInstanceValid(m)) m.SetInstanceShaderParameter(UDisco, 0f);
        if (_darkening) World.DayNight.Disco = 0f;
        _darkening = false;
    }

    /// <summary>The ball, its beams and the lights, over the middle of the nave, under its ceiling.</summary>
    private Node3D BuildDisco()
    {
        var layout = _interior.Layout;
        RoomPlan? nave = null;
        int floor = 0;
        for (int f = 0; f < layout.Floors.Count && nave == null; f++)
            foreach (var room in layout.Floors[f].Rooms)
                if (room.Type == RoomType.Nave) { nave = room; floor = f; break; }
        float x0 = nave?.X0 ?? 0, x1 = nave?.X1 ?? 0, z0 = nave?.Z0 ?? 0, z1 = nave?.Z1 ?? 0;
        float y0 = layout.FloorY(floor), clear = nave != null ? layout.ClearOf(nave) : layout.StoreyHeight;
        float height = Math.Min(clear - 0.6f, 5.5f);
        _centerLocal = new Vector3((x0 + x1) / 2, y0 + height, (z0 + z1) / 2);

        var disco = new Node3D { Name = "Disco", Position = _centerLocal, Visible = false };
        AddChild(disco);

        // the mirror ball on its wire
        _ball = new Node3D { Name = "Ball" };
        disco.AddChild(_ball);
        var mirror = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.85f, 0.88f, 0.95f),
        };
        _ball.AddChild(new MeshInstance3D
        {
            Name = "Mirror",
            Mesh = new SphereMesh { Radius = 0.35f, Height = 0.7f, RadialSegments = 10, Rings = 6, Material = mirror },
        });
        float wire = Math.Max(0.05f, y0 + clear - _centerLocal.Y);
        disco.AddChild(new MeshInstance3D
        {
            Name = "Wire",
            Position = new Vector3(0, 0.35f + wire / 2, 0),
            Mesh = new BoxMesh { Size = new Vector3(0.02f, wire, 0.02f), Material = mirror },
        });

        // beams: thin glowing cones off the ball, turning against it
        _beams = new Node3D { Name = "Beams" };
        disco.AddChild(_beams);
        float reach = Math.Max(2f, height + 1f);
        for (int i = 0; i < BeamColors.Length; i++)
        {
            var c = BeamColors[i];
            var glow = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = c with { A = 0.12f },
            };
            var pivot = new Node3D { Rotation = new Vector3(-0.55f - (i % 2) * 0.35f, i * Mathf.Tau / BeamColors.Length, 0) };
            _beams.AddChild(pivot);
            // a cone pointing down -Y from the ball, tipped out by the pivot
            pivot.AddChild(new MeshInstance3D
            {
                Position = new Vector3(0, -reach / 2, 0),
                Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.2f, Height = reach, RadialSegments = 8, Rings = 1, Material = glow },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        // the lights a character is lit by
        for (int i = 0; i < 4; i++)
        {
            var spot = new SpotLight3D
            {
                Name = $"Spot{i}", SpotRange = height + 6f, SpotAngle = 22f, LightEnergy = 4f, ShadowEnabled = false,
            };
            disco.AddChild(spot);
            _spots.Add(spot);
        }
        _pulse = new OmniLight3D { Name = "Pulse", OmniRange = Math.Max(8f, (x1 - x0 + z1 - z0) * 0.6f), ShadowEnabled = false };
        disco.AddChild(_pulse);
        return disco;
    }
}
