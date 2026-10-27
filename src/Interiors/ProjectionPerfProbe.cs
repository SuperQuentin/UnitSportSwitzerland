using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --projectionperf</c> (#1250, windowed: it measures the GPU): what the projection POC
/// (<see cref="CinemaProjectionPoc"/>) would cost in a real hall. The model viewer's Town cinema (its real
/// interior mesh and material), the camera in the seats facing the screen, vsync off; 300 frames in each of:
/// no projector, a projector spot without shadows, with shadows, and with shadows over 20 figures walking
/// in the beam. Prints each phase's mean GPU and CPU frame time (the renderer's own measures) and the
/// shadowed projector's extra.
/// </summary>
public partial class ProjectionPerfProbe : Node3D
{
    public static bool ParseArgs() => CmdArgs.All.Contains("--projectionperf");

    private const int Frames = 300, Warmup = 60;
    private static readonly string[] Phases = { "no projector", "spot, no shadow", "spot + shadow", "spot + shadow + 20 walkers" };

    private SpotLight3D _spot = null!;
    private readonly List<Node3D> _walkers = new();
    private int _phase, _frame;
    private double _gpu, _cpu;
    private readonly double[] _gpuMean = new double[4], _cpuMean = new double[4];

    public override void _Ready()
    {
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);

        var id = new TileId(2600, 1200);
        var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, 18, 34, 9, BuildingKind.Commercial) } };
        Venues.Override = new List<VenueRecord>
        {
            new("n1", VenueType.Cinema, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", "Town"),
        };
        var layout = InteriorGenerator.Generate(tile, 0, null, null)!;
        Venues.Override = null;
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout),
            Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        AddChild(node);
        InteriorNode.ShowSlide(0);
        node.ShowCinema(CinemaBooths.Level(CinemaBooths.Lights.Dim), ~0, glow: true, real: false);

        var screen = layout.Furniture.First(f => f.Type == FurnitureType.MovieScreen);
        var target = new Vector3(screen.X, layout.FloorY(screen.Floor) + 2.6f, screen.Z);
        // the shaded screen the projector lights, a hand in front of the pasted picture
        var shader = GD.Load<Shader>("res://shaders/projection_screen.gdshader");
        var lit = new ShaderMaterial { Shader = shader };
        AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(screen.W - 3f, (screen.W - 3f) / 2.39f) }, MaterialOverride = lit, Position = target + new Vector3(0, 0, 0.6f) });

        var from = target + new Vector3(0, 2.4f, 20f);
        _spot = new SpotLight3D { LightEnergy = 9f, SpotRange = 30f, SpotAttenuation = 0f, SpotAngle = 17f, Visible = false, ShadowBias = 0.03f };
        AddChild(_spot);
        _spot.LookAtFromPosition(from, target, Vector3.Up);

        var walkerMat = new ShaderMaterial { Shader = shader };
        for (int i = 0; i < 20; i++)
        {
            var w = new ProjectionWalker(walkerMat) { Position = target + new Vector3(-4 + (i % 5) * 2f, -2.6f, 3f + i / 5 * 1.6f), Visible = false };
            AddChild(w);
            _walkers.Add(w);
        }
        var cam = new Camera3D { Current = true, Near = 0.05f };
        AddChild(cam);
        cam.LookAtFromPosition(target + new Vector3(0, 1.0f, 14f), target, Vector3.Up);
        Apply();
    }

    private void Apply()
    {
        _spot.Visible = _phase >= 1;
        _spot.ShadowEnabled = _phase >= 2;
        foreach (var w in _walkers) w.Visible = _phase >= 3;
    }

    public override void _Process(double delta)
    {
        var vp = GetViewport().GetViewportRid();
        _frame++;
        if (_frame > Warmup)
        {
            _gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp);
            _cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp) + RenderingServer.GetFrameSetupTimeCpu();
        }
        if (_frame < Warmup + Frames) return;
        _gpuMean[_phase] = _gpu / Frames;
        _cpuMean[_phase] = _cpu / Frames;
        GD.Print($"[projection] {Phases[_phase]}: GPU {_gpuMean[_phase]:F3} ms, CPU {_cpuMean[_phase]:F3} ms");
        (_frame, _gpu, _cpu) = (0, 0, 0);
        if (++_phase < Phases.Length) { Apply(); return; }
        GD.Print($"[projection] the shadowed projector costs {_gpuMean[2] - _gpuMean[0]:F3} ms GPU, {_cpuMean[2] - _cpuMean[0]:F3} ms CPU; with 20 walkers in the beam {_gpuMean[3] - _gpuMean[0]:F3} ms GPU");
        GD.Print("[projection] RESULT: ok");
        GetTree().Quit(0);
    }
}
