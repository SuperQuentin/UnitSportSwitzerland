using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Cars, vans and trains for the traffic, from the same tubes and boxes as everything else
/// (<see cref="MeshScratch"/>). Each vehicle is two meshes: the body, lit like the world, and
/// its lamps, drawn unshaded so headlights and tail lights still shine when night has darkened
/// everything around them.
/// </summary>
public static class TrafficMeshBuilder
{
    private static readonly Color Glass = new(0.22f, 0.28f, 0.34f);
    private static readonly Color Tyre = new(0.08f, 0.08f, 0.09f);
    private static readonly Color Head = new(1f, 0.96f, 0.8f);
    private static readonly Color Tail = new(0.95f, 0.1f, 0.08f);

    /// <summary>Paint colours in the proportions a Swiss car park actually has: mostly grey.</summary>
    public static readonly Color[] Paints =
    {
        new(0.82f, 0.83f, 0.85f), new(0.55f, 0.57f, 0.6f), new(0.15f, 0.16f, 0.18f), new(0.92f, 0.92f, 0.9f),
        new(0.3f, 0.32f, 0.36f), new(0.62f, 0.1f, 0.1f), new(0.12f, 0.22f, 0.45f), new(0.7f, 0.66f, 0.55f),
    };

    // Built once per look and shared by every car or carriage that wears it: there are 16 car
    // looks and a handful of carriages, and a new pair of meshes per spawn was garbage (#221).
    // Nothing draws on or changes a traffic mesh after it is built.
    private static readonly Dictionary<(Color, bool), (ArrayMesh, ArrayMesh)> Cars = new();
    private static readonly Dictionary<(Color, Color, float, bool, bool, bool), (ArrayMesh, ArrayMesh)> Carriages = new();

    /// <summary>A hatchback, 4.2 m, or a van, 5 m and taller. Origin on the road, +Z forward. Shared: do not change it.</summary>
    public static (ArrayMesh Body, ArrayMesh Lamps) Car(Color paint, bool van)
    {
        if (!Cars.TryGetValue((paint, van), out var built)) Cars[(paint, van)] = built = BuildCar(paint, van);
        return built;
    }

    private static (ArrayMesh Body, ArrayMesh Lamps) BuildCar(Color paint, bool van)
    {
        var s = new MeshScratch();
        float len = van ? 5.0f : 4.2f, wid = 1.8f;
        s.Box(new Vector3(0, 0.55f, 0), new Vector3(wid, 0.6f, len), paint);
        if (van)
        {
            s.Box(new Vector3(0, 1.35f, -0.35f), new Vector3(wid - 0.05f, 1.1f, len - 1.3f), paint);
            s.Box(new Vector3(0, 1.3f, len * 0.5f - 0.75f), new Vector3(wid - 0.1f, 0.6f, 0.45f), Glass);
        }
        else
        {
            s.Box(new Vector3(0, 1.12f, -0.25f), new Vector3(wid - 0.15f, 0.56f, 2.2f), Glass);
            s.Box(new Vector3(0, 1.42f, -0.35f), new Vector3(wid - 0.2f, 0.06f, 1.8f), paint);
        }
        foreach (float x in new[] { -0.82f, 0.82f })
            foreach (float z in new[] { -len * 0.32f, len * 0.32f })
                s.Tube(new Vector3(x - 0.12f, 0.33f, z), new Vector3(x + 0.12f, 0.33f, z), 0.33f, Tyre, 8);

        var l = new MeshScratch();
        foreach (float x in new[] { -0.6f, 0.6f })
        {
            l.Box(new Vector3(x, 0.72f, len * 0.5f + 0.01f), new Vector3(0.36f, 0.14f, 0.04f), Head);
            l.Box(new Vector3(x, 0.8f, -len * 0.5f - 0.01f), new Vector3(0.32f, 0.12f, 0.04f), Tail);
        }
        return (s.Build(), l.Build());
    }

    /// <summary>A railcar or carriage: length along +Z, the cab ends shaped when it leads. Shared: do not change it.</summary>
    public static (ArrayMesh Body, ArrayMesh Lamps) Carriage(Color paint, Color band, float length,
        bool narrow, bool cabFront, bool cabBack)
    {
        var key = (paint, band, length, narrow, cabFront, cabBack);
        if (!Carriages.TryGetValue(key, out var built)) Carriages[key] = built = BuildCarriage(paint, band, length, narrow, cabFront, cabBack);
        return built;
    }

    private static (ArrayMesh Body, ArrayMesh Lamps) BuildCarriage(Color paint, Color band, float length,
        bool narrow, bool cabFront, bool cabBack)
    {
        var s = new MeshScratch();
        float wid = narrow ? 2.65f : 2.95f, roof = narrow ? 3.5f : 4.0f, floor = 1.05f;
        s.Box(new Vector3(0, (floor + roof) * 0.5f, 0), new Vector3(wid, roof - floor, length - 0.6f), paint);
        s.Box(new Vector3(0, floor + 1.05f, 0), new Vector3(wid + 0.02f, 0.75f, length - 2.2f), Glass);   // window band
        s.Box(new Vector3(0, floor + 0.35f, 0), new Vector3(wid + 0.03f, 0.18f, length - 0.8f), band);
        s.Box(new Vector3(0, roof + 0.1f, 0), new Vector3(wid - 0.5f, 0.2f, length - 1.5f), new Color(0.35f, 0.35f, 0.37f));
        // bogies
        foreach (float z in new[] { -length * 0.33f, length * 0.33f })
            s.Box(new Vector3(0, 0.5f, z), new Vector3(wid - 0.5f, 0.6f, 2.6f), Tyre);

        var l = new MeshScratch();
        if (cabFront)
        {
            s.Box(new Vector3(0, floor + 1.4f, length * 0.5f - 0.28f), new Vector3(wid - 0.3f, 0.9f, 0.1f), Glass);
            foreach (float x in new[] { -0.9f, 0f, 0.9f })
                l.Box(new Vector3(x, x == 0 ? roof - 0.35f : floor + 0.25f, length * 0.5f - 0.28f), new Vector3(0.28f, 0.2f, 0.06f), Head);
        }
        if (cabBack)
            foreach (float x in new[] { -0.9f, 0.9f })
                l.Box(new Vector3(x, floor + 0.25f, -length * 0.5f + 0.28f), new Vector3(0.24f, 0.18f, 0.06f), Tail);
        return (s.Build(), l.Build());
    }

    /// <summary>The AI traffic's car, van and train carriages, as World/Traffic builds them, in the model viewer (--models).</summary>
    [Core.Showcase("Traffic")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseTraffic()
    {
        static Node3D WithLamps((ArrayMesh Body, ArrayMesh Lamps) m)
        {
            var node = ModelViewer.Shaded(m.Body);
            node.AddChild(new MeshInstance3D { Mesh = m.Lamps, MaterialOverride = LampMaterial() });
            return node;
        }
        var white = new Color(0.9f, 0.9f, 0.9f);
        var red = new Color(0.78f, 0.1f, 0.1f);
        yield return ("Car", () => WithLamps(Car(Paints[0], van: false)));
        yield return ("Van", () => WithLamps(Car(Paints[1], van: true)));
        yield return ("SBB carriage", () => WithLamps(Carriage(white, red, 24f, false, false, false)));
        yield return ("SBB end car, rear cab", () => WithLamps(Carriage(white, red, 24f, false, false, true)));
        yield return ("Narrow-gauge carriage", () => WithLamps(Carriage(new Color(0.72f, 0.12f, 0.12f), new Color(0.95f, 0.95f, 0.95f), 17f, true, false, false)));
    }

    /// <summary>Unshaded, vertex-coloured: lamps stay bright whatever the light.</summary>
    public static StandardMaterial3D LampMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
    };
}
