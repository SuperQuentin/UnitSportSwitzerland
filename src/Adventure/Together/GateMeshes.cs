using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Together;

/// <summary>
/// The two-handle gate (#1266 part 4, a <c>SealKind.Gate</c> seal): a heavy iron gate in a stone frame across the way to
/// a castle cellar's or a hut storeroom's chest, and its two wheel handles on posts 3.5 m apart. Built once each; the
/// wheel turns and the gate sinks as nodes.
/// </summary>
public static class GateMeshes
{
    private static ArrayMesh? _wheel, _post, _gate;
    private static readonly Color Iron = new("2e3034"), Rust = new("6a3a22"), Stone = new("8a8680"), Wood = new("6a4a2e");

    /// <summary>A hand wheel: rim, four spokes and the hub, in the XY plane (it turns about Z).</summary>
    [Showcase("Adventure", "Gate wheel")]
    public static ArrayMesh Wheel()
    {
        if (_wheel != null) return _wheel;
        var s = new MeshScratch();
        s.Ring(Vector3.Zero, Vector3.Forward, 0.26f, 0.30f, 0.04f, Iron, segments: 20);
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Pi / 2 + 0.3f;
            s.Tube(Vector3.Zero, new Vector3(Mathf.Cos(a) * 0.27f, Mathf.Sin(a) * 0.27f, 0), 0.018f, Iron, sides: 5);
        }
        s.Tube(new Vector3(0, 0, -0.05f), new Vector3(0, 0, 0.05f), 0.05f, Rust, sides: 8);
        return _wheel = s.Build();
    }

    /// <summary>The wheel's post: a squat stone pillar with an iron cap.</summary>
    [Showcase("Adventure", "Gate wheel post")]
    public static ArrayMesh Post()
    {
        if (_post != null) return _post;
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.45f, 0), new Vector3(0.3f, 0.9f, 0.3f), Stone);
        s.Box(new Vector3(0, 0.93f, 0), new Vector3(0.34f, 0.06f, 0.34f), Iron);
        s.Tube(new Vector3(0, 1.0f, -0.12f), new Vector3(0, 1.0f, 0.02f), 0.03f, Rust, sides: 6);
        return _post = s.Build();
    }

    /// <summary>The gate: a stone frame 3 m wide and 2.6 m high, an iron-banded wooden leaf with bars (it sinks as a node).</summary>
    [Showcase("Adventure", "Two-handle gate")]
    public static ArrayMesh Gate()
    {
        if (_gate != null) return _gate;
        var s = new MeshScratch();
        s.Box(new Vector3(-1.55f, 1.3f, 0), new Vector3(0.3f, 2.6f, 0.5f), Stone);
        s.Box(new Vector3(1.55f, 1.3f, 0), new Vector3(0.3f, 2.6f, 0.5f), Stone);
        s.Box(new Vector3(0, 2.75f, 0), new Vector3(3.4f, 0.3f, 0.5f), Stone);
        s.Box(new Vector3(0, 1.3f, 0), new Vector3(2.8f, 2.5f, 0.12f), Wood);
        for (int i = 0; i < 3; i++) s.Box(new Vector3(0, 0.4f + i * 0.9f, 0.07f), new Vector3(2.8f, 0.1f, 0.03f), Iron);
        for (int i = 0; i < 7; i++) s.Box(new Vector3(-1.2f + i * 0.4f, 1.3f, 0.09f), new Vector3(0.05f, 2.4f, 0.03f), Iron);
        return _gate = s.Build();
    }
}
