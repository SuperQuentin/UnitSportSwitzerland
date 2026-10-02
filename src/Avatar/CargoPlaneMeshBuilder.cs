using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The Battle Royale cargo plane (#207): a four-engine high-wing military transport in the C-130
/// mould, about 30 m long and 40 m across, its rear ramp down. Authored nose to +Z;
/// <see cref="MeshScratch.Build()"/> turns it half round, so on the node the nose is −Z (Godot's
/// forward). The origin is the middle of the fuselage. Vertex-coloured, built once.
/// </summary>
public static class CargoPlaneMeshBuilder
{
    private static ArrayMesh? _mesh;

    private static readonly Color Hull = new(0.40f, 0.44f, 0.36f), Belly = new(0.52f, 0.55f, 0.50f);
    private static readonly Color Dark = new(0.06f, 0.06f, 0.07f), Glass = new(0.12f, 0.16f, 0.20f);
    private static readonly Color Metal = new(0.30f, 0.31f, 0.32f), Red = new(0.85f, 0.10f, 0.10f);

    /// <summary>Where the jumpers leave from, in node space (the end of the ramp, below and behind).</summary>
    public static readonly Vector3 Ramp = new(0, -2.6f, 15.5f);

    public static ArrayMesh Build()
    {
        if (_mesh != null) return _mesh;
        var s = new MeshScratch();

        // the fuselage: a long tube, a rounded nose, a tail that sweeps up over the ramp
        s.Tube(new Vector3(0, 0, -9), new Vector3(0, 0, 11), 2.2f, Hull, 10);
        s.Tube(new Vector3(0, 0, 11), new Vector3(0, -0.3f, 15), 2.2f, 0.9f, Hull, 10);
        s.Tube(new Vector3(0, 0.2f, -9), new Vector3(0, 1.5f, -15.5f), 2.2f, 0.7f, Hull, 10);
        s.Box(new Vector3(0, -1.9f, 1), new Vector3(3.6f, 0.5f, 19f), Belly);
        // the cockpit windows
        s.Box(new Vector3(0, 1.0f, 13.2f), new Vector3(2.6f, 0.7f, 1.2f), Glass, Basis.FromEuler(new Vector3(-0.45f, 0, 0)));
        foreach (float x in new[] { -1f, 1f }) s.Box(new Vector3(x * 1.75f, 1.0f, 12.2f), new Vector3(0.1f, 0.6f, 1.4f), Glass);
        // the landing gear fairings on the flanks
        foreach (float x in new[] { -1f, 1f }) s.Box(new Vector3(x * 2.2f, -1.5f, 1.5f), new Vector3(1.3f, 1.3f, 7f), Belly);

        // the open rear: dark inside, the ramp lowered behind it
        s.Box(new Vector3(0, -0.4f, -10.5f), new Vector3(3.4f, 2.6f, 3.2f), Dark);
        s.Box(new Vector3(0, -2.3f, -13.6f), new Vector3(3.2f, 0.18f, 4.6f), Metal, Basis.FromEuler(new Vector3(-0.22f, 0, 0)));

        // the high wing, four engines under it, their propellers
        s.Box(new Vector3(0, 2.3f, 1.5f), new Vector3(40f, 0.5f, 4.4f), Hull);
        s.Box(new Vector3(0, 2.05f, 1.5f), new Vector3(6f, 0.3f, 5f), Hull);
        foreach (float x in new[] { -12.5f, -6.5f, 6.5f, 12.5f })
        {
            s.Tube(new Vector3(x, 1.6f, -2.2f), new Vector3(x, 1.6f, 4.6f), 0.65f, 0.55f, Hull, 8);
            s.Tube(new Vector3(x, 1.6f, 4.6f), new Vector3(x, 1.6f, 5.4f), 0.5f, 0.15f, Metal, 8);
            s.Box(new Vector3(x, 1.6f, 5.2f), new Vector3(0.3f, 3.8f, 0.08f), Dark);
            s.Box(new Vector3(x, 1.6f, 5.2f), new Vector3(3.8f, 0.3f, 0.08f), Dark);
        }

        // the tail: a tall fin and the tailplane
        s.Box(new Vector3(0, 5.0f, -13.6f), new Vector3(0.4f, 6.5f, 4.2f), Hull, Basis.FromEuler(new Vector3(0.3f, 0, 0)));
        s.Box(new Vector3(0, 2.4f, -14.6f), new Vector3(14f, 0.3f, 3.0f), Hull);

        // Swiss Air Force marks: a red square with a white cross, on the fin and on each flank
        Mark(s, new Vector3(0.23f, 5.6f, -14.2f), Vector3.Right, 1.6f);
        Mark(s, new Vector3(-0.23f, 5.6f, -14.2f), Vector3.Left, 1.6f);
        Mark(s, new Vector3(2.22f, 0.3f, -5f), Vector3.Right, 1.3f);
        Mark(s, new Vector3(-2.22f, 0.3f, -5f), Vector3.Left, 1.3f);

        return _mesh = s.Build();
    }

    /// <summary>The red square and white cross, flat against a side facing <paramref name="normal"/> (±X).</summary>
    private static void Mark(MeshScratch s, Vector3 at, Vector3 normal, float size)
    {
        s.Box(at, new Vector3(0.04f, size, size), Red);
        var front = at + normal * 0.03f;
        s.Box(front, new Vector3(0.04f, size * 0.6f, size * 0.2f), Colors.White);
        s.Box(front, new Vector3(0.04f, size * 0.2f, size * 0.6f), Colors.White);
    }
}
