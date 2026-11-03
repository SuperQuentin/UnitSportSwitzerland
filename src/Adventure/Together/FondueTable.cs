using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Together;

/// <summary>
/// The shared pot on screen (#1266 part 5.5): a red caquelon on its réchaud with a blue flame, the cheese surface's
/// shader (<c>shaders/fondue_cheese.gdshader</c>: <c>split</c> and <c>bubble</c>, set on a change only), the forks
/// standing in it as rigid nodes, and a lost bread cube sinking (a tween). Made by the together service while the local
/// player sits at a pot. Nothing rebuilds a mesh.
/// </summary>
public sealed partial class FondueTable : Node3D
{
    private static readonly StringName SplitP = "split", BubbleP = "bubble";
    private static ArrayMesh? _caquelon, _fork, _bread;
    private static ShaderMaterial? _cheeseMat;
    private ShaderMaterial _cheese = null!;
    private float _split = -1f, _bubble = -1f;

    /// <summary>What the cheese shows now (checks).</summary>
    public float ShownSplit => _split;

    public override void _Ready()
    {
        Name = "FondueTable";
        AddChild(new MeshInstance3D { Name = "Caquelon", Mesh = Caquelon(), MaterialOverride = HumanMeshBuilder.Material() });
        _cheese = (ShaderMaterial)(_cheeseMat ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fondue_cheese.gdshader") }).Duplicate();
        AddChild(new MeshInstance3D
        {
            Name = "Cheese", MaterialOverride = _cheese, Position = new Vector3(0, 0.44f, 0),
            Mesh = new CylinderMesh { TopRadius = 0.115f, BottomRadius = 0.115f, Height = 0.004f, RadialSegments = 20, Rings = 1 },
        });
        // the réchaud's flame: a small blue cone, glowing
        AddChild(new MeshInstance3D
        {
            Name = "Flame", Position = new Vector3(0, 0.2f, 0),
            Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.035f, Height = 0.07f, RadialSegments = 8 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.55f, 1f, 0.8f), EmissionEnabled = true, Emission = new Color(0.3f, 0.5f, 1f),
                EmissionEnergyMultiplier = 2f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Tau / 4 + 0.4f;
            AddChild(new MeshInstance3D
            {
                Name = $"Fork{i}", Mesh = Fork(), MaterialOverride = HumanMeshBuilder.Material(),
                Position = new Vector3(Mathf.Cos(a) * 0.06f, 0.42f, Mathf.Sin(a) * 0.06f),
                Rotation = new Vector3(Mathf.Sin(a) * 0.5f, 0, -Mathf.Cos(a) * 0.5f),
            });
        }
    }

    /// <summary>The pot's state: the cheese's look follows the split meter and the heat.</summary>
    public void Show(PotView v)
    {
        float split = v.Splitted ? 1f : Mathf.Clamp(v.Split * 0.6f, 0f, 0.6f);
        float bubble = v.Cooking ? Mathf.Clamp(0.25f + v.Split * 0.6f + (v.Wine ? 0f : 0.2f), 0f, 1f) : 0.05f;
        if (Mathf.Abs(split - _split) > 0.01f) { _split = split; _cheese.SetShaderParameter(SplitP, split); }
        if (Mathf.Abs(bubble - _bubble) > 0.02f) { _bubble = bubble; _cheese.SetShaderParameter(BubbleP, bubble); }
    }

    /// <summary>A bread cube drops in and sinks (one small node, freed after its tween).</summary>
    public void SinkBread()
    {
        var cube = new MeshInstance3D { Name = "LostBread", Mesh = Bread(), MaterialOverride = HumanMeshBuilder.Material(), Position = new Vector3(0.03f, 0.47f, -0.02f) };
        AddChild(cube);
        var t = cube.CreateTween();
        t.TweenProperty(cube, "position:y", 0.436f, 0.25f);
        t.TweenProperty(cube, "position:y", 0.40f, 1.6f);
        t.TweenCallback(Callable.From(cube.QueueFree));
    }

    /// <summary>The red caquelon on its black réchaud stand (the cheese is its own node, under a shader).</summary>
    [Showcase("Adventure", "Caquelon")]
    public static ArrayMesh Caquelon()
    {
        if (_caquelon != null) return _caquelon;
        var s = new MeshScratch();
        var red = new Color("b8241c");
        var glaze = new Color("e8dcc8");
        var iron = new Color("23221f");
        // the réchaud: a ring on three legs, the burner in the middle
        s.Ring(new Vector3(0, 0.30f, 0), Vector3.Up, 0.12f, 0.14f, 0.015f, iron);
        for (int i = 0; i < 3; i++)
        {
            float a = i * Mathf.Tau / 3;
            s.Tube(new Vector3(Mathf.Cos(a) * 0.13f, 0.30f, Mathf.Sin(a) * 0.13f), new Vector3(Mathf.Cos(a) * 0.17f, 0f, Mathf.Sin(a) * 0.17f), 0.008f, iron);
        }
        s.Tube(new Vector3(0, 0.14f, 0), new Vector3(0, 0.19f, 0), 0.04f, new Color("8a8a86"), sides: 10);
        // the pot: a wide glazed bowl with a long handle
        s.Tube(new Vector3(0, 0.31f, 0), new Vector3(0, 0.36f, 0), 0.09f, 0.125f, red, sides: 16);
        s.Tube(new Vector3(0, 0.36f, 0), new Vector3(0, 0.44f, 0), 0.125f, 0.13f, red, sides: 16);
        s.Ring(new Vector3(0, 0.44f, 0), Vector3.Up, 0.115f, 0.132f, 0.012f, glaze);
        s.Tube(new Vector3(0.12f, 0.40f, 0), new Vector3(0.30f, 0.44f, 0), 0.016f, red, sides: 8);
        return _caquelon = s.Build();
    }

    /// <summary>A long fondue fork: a wooden grip and a two-pronged steel shaft.</summary>
    [Showcase("Adventure", "Fondue fork")]
    public static ArrayMesh Fork()
    {
        if (_fork != null) return _fork;
        var s = new MeshScratch();
        var steel = new Color("c4c8cc");
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.18f, 0), 0.0025f, steel, sides: 5);
        s.Tube(new Vector3(0.004f, -0.03f, 0), new Vector3(0.004f, 0f, 0), 0.0015f, steel, sides: 4);
        s.Tube(new Vector3(-0.004f, -0.03f, 0), new Vector3(-0.004f, 0f, 0), 0.0015f, steel, sides: 4);
        s.Tube(new Vector3(0, 0.18f, 0), new Vector3(0, 0.27f, 0), 0.007f, new Color("8a5a30"), sides: 6);
        return _fork = s.Build();
    }

    /// <summary>A cube of bread, crust and crumb.</summary>
    [Showcase("Adventure", "Fondue bread")]
    public static ArrayMesh Bread()
    {
        if (_bread != null) return _bread;
        var s = new MeshScratch();
        s.Box(Vector3.Zero, new Vector3(0.022f, 0.02f, 0.022f), new Color("e8cc96"));
        s.Box(new Vector3(0, 0.009f, 0), new Vector3(0.023f, 0.004f, 0.023f), new Color("a8682c"));
        return _bread = s.Build();
    }
}
