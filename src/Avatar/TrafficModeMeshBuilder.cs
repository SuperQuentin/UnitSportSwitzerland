using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The traffic's walkers, riders and tram sections (#1147), in the traffic cars' blocky style
/// (<see cref="TrafficMeshBuilder"/>): a handful of boxes each, vertex-coloured, no texture.
/// Each fits the box its mover collides with (<c>Traffic.Walkers/Riders/Trams</c>' <c>MoverLook</c>
/// sizes): origin at the middle of its bottom, authored +Z forward (built facing -Z, the node's
/// forward, like the cars). Built once per look and shared by every mover that wears it (#221):
/// nothing changes a traffic mesh after it is built.
/// </summary>
public static class TrafficModeMeshBuilder
{
    private static readonly Color Glass = new(0.22f, 0.28f, 0.34f);
    private static readonly Color Dark = new(0.08f, 0.08f, 0.09f);
    private static readonly Color Metal = new(0.42f, 0.43f, 0.45f);
    private static readonly Color Head = new(1f, 0.96f, 0.8f);
    private static readonly Color Tail = new(0.95f, 0.1f, 0.08f);

    private static readonly Color[] Skins =
    {
        new(0.93f, 0.76f, 0.62f), new(0.78f, 0.58f, 0.42f), new(0.52f, 0.36f, 0.24f), new(0.36f, 0.24f, 0.16f),
    };

    private static readonly Color[] Hairs =
    {
        new(0.12f, 0.09f, 0.07f), new(0.42f, 0.28f, 0.14f), new(0.78f, 0.66f, 0.42f), new(0.6f, 0.6f, 0.6f),
    };

    /// <summary>A walker's top and trousers: plain town clothes, mostly muted, a few bright.</summary>
    private static readonly (Color Top, Color Legs)[] Clothes =
    {
        (new(0.15f, 0.22f, 0.4f), new(0.22f, 0.24f, 0.3f)),
        (new(0.75f, 0.12f, 0.12f), new(0.15f, 0.15f, 0.17f)),
        (new(0.85f, 0.85f, 0.82f), new(0.25f, 0.33f, 0.5f)),
        (new(0.25f, 0.4f, 0.25f), new(0.55f, 0.48f, 0.36f)),
        (new(0.12f, 0.12f, 0.13f), new(0.12f, 0.12f, 0.13f)),
        (new(0.9f, 0.62f, 0.15f), new(0.25f, 0.33f, 0.5f)),
        (new(0.55f, 0.5f, 0.45f), new(0.3f, 0.28f, 0.26f)),
        (new(0.5f, 0.25f, 0.55f), new(0.2f, 0.2f, 0.22f)),
    };

    /// <summary>A bike's frame and its rider's jersey.</summary>
    private static readonly (Color Frame, Color Jersey)[] Bikes =
    {
        (new(0.1f, 0.1f, 0.11f), new(0.85f, 0.2f, 0.15f)),
        (new(0.75f, 0.12f, 0.12f), new(0.9f, 0.9f, 0.88f)),
        (new(0.15f, 0.35f, 0.7f), new(0.95f, 0.8f, 0.15f)),
        (new(0.85f, 0.85f, 0.82f), new(0.2f, 0.3f, 0.55f)),
        (new(0.2f, 0.5f, 0.3f), new(0.12f, 0.12f, 0.13f)),
        (new(0.55f, 0.57f, 0.6f), new(0.3f, 0.65f, 0.35f)),
    };

    /// <summary>Tram liveries: body, lower band/skirt, the doors. Geneva first; Zurich, Basel and Bern for variety.</summary>
    public static readonly (string Name, Color Body, Color Band, Color Door)[] Liveries =
    {
        ("Geneva", new(0.93f, 0.93f, 0.92f), new(0.85f, 0.18f, 0.12f), new(0.85f, 0.18f, 0.12f)),
        ("Geneva, older", new(0.86f, 0.32f, 0.14f), new(0.93f, 0.93f, 0.92f), new(0.3f, 0.3f, 0.32f)),
        ("Zurich", new(0.93f, 0.93f, 0.92f), new(0.12f, 0.32f, 0.68f), new(0.12f, 0.32f, 0.68f)),
        ("Basel", new(0.18f, 0.5f, 0.28f), new(0.93f, 0.93f, 0.92f), new(0.93f, 0.93f, 0.92f)),
    };

    public static int WalkerLooks => Clothes.Length * Skins.Length;
    public static int RiderLooks => Bikes.Length * Skins.Length;

    private static readonly Dictionary<int, ArrayMesh> Walkers = new(), Riders = new();
    private static readonly Dictionary<(int, float, bool, bool, bool, bool), (ArrayMesh, ArrayMesh)> Trams = new();

    /// <summary>A walker, 0.5 x 1.7 x 0.4 m, in look <paramref name="look"/> (any int, wrapped). Shared: do not change it.</summary>
    public static ArrayMesh Walker(int look)
    {
        look = (int)((uint)look % (uint)WalkerLooks);
        if (!Walkers.TryGetValue(look, out var mesh)) Walkers[look] = mesh = BuildWalker(look);
        return mesh;
    }

    private static ArrayMesh BuildWalker(int look)
    {
        var (top, legs) = Clothes[look % Clothes.Length];
        var skin = Skins[look / Clothes.Length % Skins.Length];
        var hair = Hairs[(look * 7 + 3) % Hairs.Length];
        var s = new MeshScratch();
        foreach (float x in new[] { -0.09f, 0.09f })
        {
            s.Box(new Vector3(x, 0.05f, 0.03f), new Vector3(0.15f, 0.1f, 0.26f), Dark);                // shoe
            s.Box(new Vector3(x, 0.47f, 0f), new Vector3(0.14f, 0.78f, 0.16f), legs);                 // leg, into the hips
            s.Box(new Vector3(x * 2.28f, 1.1f, 0f), new Vector3(0.08f, 0.56f, 0.12f), top);            // arm
            s.Box(new Vector3(x * 2.28f, 0.79f, 0f), new Vector3(0.09f, 0.1f, 0.1f), skin);           // hand
        }
        s.Box(new Vector3(0f, 1.1f, 0f), new Vector3(0.34f, 0.6f, 0.22f), top);                       // torso
        s.Box(new Vector3(0f, 1.5f, 0.01f), new Vector3(0.2f, 0.24f, 0.2f), skin);                   // head
        s.Box(new Vector3(0f, 1.645f, -0.01f), new Vector3(0.22f, 0.09f, 0.22f), hair);              // hair
        return s.Build();
    }

    /// <summary>A bike and its rider, 0.6 x 1.7 x 1.8 m, in look <paramref name="look"/> (any int, wrapped). Shared: do not change it.</summary>
    public static ArrayMesh Rider(int look)
    {
        look = (int)((uint)look % (uint)RiderLooks);
        if (!Riders.TryGetValue(look, out var mesh)) Riders[look] = mesh = BuildRider(look);
        return mesh;
    }

    private static ArrayMesh BuildRider(int look)
    {
        var (frame, jersey) = Bikes[look % Bikes.Length];
        var skin = Skins[look / Bikes.Length % Skins.Length];
        var legs = new Color(0.12f, 0.12f, 0.14f);
        var s = new MeshScratch();
        // the bike: wheels as thin square-ish discs, a diamond frame, bars, saddle
        float r = 0.34f, wheelZ = 0.54f;
        var rear = new Vector3(0f, r, -wheelZ);
        var front = new Vector3(0f, r, wheelZ);
        foreach (var hub in new[] { rear, front })
            s.Tube(hub - new Vector3(0.025f, 0, 0), hub + new Vector3(0.025f, 0, 0), r, Dark, 8);
        var crank = new Vector3(0f, 0.3f, -0.04f);
        var seat = new Vector3(0f, 0.86f, -0.24f);
        var head = new Vector3(0f, 0.88f, 0.38f);
        Bar(s, rear, crank, 0.035f, frame);
        Bar(s, rear, seat, 0.03f, frame);
        Bar(s, crank, seat, 0.04f, frame);
        Bar(s, crank, head, 0.045f, frame);
        Bar(s, seat, head, 0.04f, frame);
        Bar(s, head, front, 0.04f, Metal);                                                            // fork
        Bar(s, head, head + new Vector3(0f, 0.16f, 0.03f), 0.035f, Metal);                            // stem
        var bars = head + new Vector3(0f, 0.16f, 0.03f);
        s.Box(bars, new Vector3(0.5f, 0.035f, 0.035f), Dark);                                       // handlebar
        s.Box(seat + new Vector3(0f, 0.04f, 0.02f), new Vector3(0.12f, 0.05f, 0.22f), Dark);        // saddle
        s.Box(crank, new Vector3(0.3f, 0.04f, 0.1f), Metal);                                         // pedals
        // the rider: sitting, leaning to the bars
        var hip = seat + new Vector3(0f, 0.12f, 0f);
        var shoulder = new Vector3(0f, 1.36f, 0.12f);
        foreach (float x in new[] { -0.09f, 0.09f })
        {
            var knee = new Vector3(x, 0.74f, 0.12f);
            var foot = new Vector3(x * 1.4f, 0.33f, -0.02f);
            Limb(s, hip + new Vector3(x, 0f, 0f), knee, 0.13f, 0.14f, legs);
            Limb(s, knee, foot, 0.11f, 0.12f, legs);
            s.Box(foot + new Vector3(0f, -0.02f, 0.04f), new Vector3(0.11f, 0.08f, 0.22f), Dark);   // shoe
            var hand = bars + new Vector3(x * 2.4f, 0.03f, 0f);
            Limb(s, shoulder + new Vector3(x * 2f, -0.03f, 0f), hand, 0.08f, 0.09f, jersey);         // arm
            s.Box(hand, new Vector3(0.08f, 0.07f, 0.08f), skin);                                     // glove
        }
        Limb(s, hip, shoulder, 0.34f, 0.2f, jersey);                                                  // torso
        var neck = shoulder + new Vector3(0f, 0.14f, 0.06f);
        s.Box(neck, new Vector3(0.19f, 0.21f, 0.2f), skin);                                          // head
        s.Box(neck + new Vector3(0f, 0.13f, -0.01f), new Vector3(0.23f, 0.1f, 0.27f), jersey.Darkened(0.3f)); // helmet
        return s.Build();
    }

    /// <summary>
    /// One tram section (#1147): <paramref name="length"/> m along Z, 2.4 m wide, the body 3.05 m high with its roof gear to
    /// 3.4 m, in livery <paramref name="livery"/>. A cab at the front (+Z) and/or back; <paramref name="pantograph"/> on its
    /// roof; <paramref name="bellows"/>: a gangway's bellows reaching forward over the 0.6 m gap to the section ahead.
    /// Its lamps (head lights at a cab end that leads, tail lights at the back cab) are the second mesh, unshaded. Shared.
    /// </summary>
    public static (ArrayMesh Body, ArrayMesh Lamps) TramSection(int livery, float length, bool cabFront, bool cabBack,
        bool pantograph, bool bellows)
    {
        livery = (int)((uint)livery % (uint)Liveries.Length);
        var key = (livery, length, cabFront, cabBack, pantograph, bellows);
        if (!Trams.TryGetValue(key, out var built)) Trams[key] = built = BuildTramSection(livery, length, cabFront, cabBack, pantograph, bellows);
        return built;
    }

    private static (ArrayMesh, ArrayMesh) BuildTramSection(int livery, float length, bool cabFront, bool cabBack,
        bool pantograph, bool bellows)
    {
        var (_, body, band, door) = Liveries[livery];
        const float wid = 2.4f, floor = 0.32f, roof = 3.05f, winLow = 1.05f, winHigh = 2.35f;
        float half = length * 0.5f;
        var s = new MeshScratch();
        var l = new MeshScratch();
        // body (cab ends inset: the nose's own box closes them), skirt, window band, lower band, roof
        float front = cabFront ? half - 0.7f : half, back = cabBack ? -half + 0.7f : -half;
        s.Box(new Vector3(0f, (floor + roof) * 0.5f, (front + back) * 0.5f), new Vector3(wid, roof - floor, front - back), body);
        s.Box(new Vector3(0f, 0.2f, (front + back) * 0.5f), new Vector3(wid - 0.3f, 0.3f, front - back - 0.4f), Dark);
        s.Box(new Vector3(0f, (winLow + winHigh) * 0.5f, (front + back) * 0.5f),
            new Vector3(wid + 0.02f, winHigh - winLow, front - back - 0.5f), Glass);
        s.Box(new Vector3(0f, 0.55f, (front + back) * 0.5f), new Vector3(wid + 0.02f, 0.3f, front - back - 0.2f), band);
        s.Box(new Vector3(0f, roof + 0.06f, (front + back) * 0.5f), new Vector3(wid - 0.4f, 0.12f, front - back - 0.4f), Metal);
        // doors: a pair-door each side, its window upper half; window pillars between them
        float doorZ = length > 8f ? 0.18f * length : 0f;
        foreach (float side in new[] { -1f, 1f })
        {
            float x = side * (wid * 0.5f + 0.025f);
            foreach (float z in length > 8f ? new[] { -doorZ, doorZ } : new[] { 0f })
            {
                s.Box(new Vector3(x, (floor + winHigh) * 0.5f + 0.05f, z), new Vector3(0.05f, winHigh - floor, 1.3f), door);
                s.Box(new Vector3(x + side * 0.035f, (winLow + winHigh) * 0.5f + 0.1f, z), new Vector3(0.03f, winHigh - winLow - 0.2f, 1.1f), Glass);
            }
            for (float z = back + 1.0f; z < front - 0.7f; z += 1.6f)
            {
                bool atDoor = length > 8f ? Mathf.Abs(Mathf.Abs(z) - doorZ) < 1.0f : Mathf.Abs(z) < 1.0f;
                if (!atDoor)
                    s.Box(new Vector3(side * (wid * 0.5f + 0.02f), (winLow + winHigh) * 0.5f, z), new Vector3(0.04f, winHigh - winLow, 0.12f), body);
            }
        }
        // the cab ends: a low nose, a raked windscreen over it, a destination board above
        foreach (var (cab, dir) in new[] { (cabFront, 1f), (cabBack, -1f) })
        {
            if (!cab) continue;
            float end = dir * half;
            s.Box(new Vector3(0f, (floor + winLow) * 0.5f + 0.05f, end - dir * 0.375f), new Vector3(wid - 0.1f, winLow - floor + 0.1f, 0.75f), body);
            s.Box(new Vector3(0f, 0.55f, end - dir * 0.28f), new Vector3(wid - 0.06f, 0.3f, 0.6f), band);
            s.Box(new Vector3(0f, (winLow + roof - 0.45f) * 0.5f, end - dir * 0.45f), new Vector3(wid - 0.15f, roof - 0.45f - winLow, 0.1f), Glass,
                new Basis(Vector3.Right, -dir * 0.15f));
            s.Box(new Vector3(0f, roof - 0.235f, end - dir * 0.635f), new Vector3(wid - 0.1f, 0.43f, 0.17f), Dark);
            foreach (float x in new[] { -0.85f, 0.85f })
                l.Box(new Vector3(x, 0.9f, end + dir * 0.005f), new Vector3(0.28f, 0.16f, 0.04f), dir > 0 ? Head : Tail);
            l.Box(new Vector3(0f, roof - 0.25f, end - dir * 0.53f), new Vector3(1.1f, 0.16f, 0.04f), new Color(1f, 0.75f, 0.2f));
        }
        if (bellows)
            s.Box(new Vector3(0f, (floor + roof) * 0.5f, half + 0.3f), new Vector3(wid - 0.3f, roof - floor - 0.25f, 0.64f), Dark);
        // roof gear: a box on every section; the pantograph, folded low, on the one that carries it
        s.Box(new Vector3(0f, roof + 0.2f, -length * 0.15f), new Vector3(1.4f, 0.25f, Mathf.Min(2.4f, length * 0.35f)), Metal);
        if (pantograph)
        {
            float pz = length * 0.2f, base0 = roof + 0.1f;
            s.Box(new Vector3(0f, base0 + 0.04f, pz), new Vector3(1.0f, 0.08f, 0.9f), Dark);
            var knee = new Vector3(0f, base0 + 0.17f, pz + 0.7f);
            var top = new Vector3(0f, 3.34f, pz + 0.1f);
            foreach (float x in new[] { -0.3f, 0.3f })
            {
                Bar(s, new Vector3(x, base0 + 0.08f, pz - 0.3f), knee + new Vector3(x, 0, 0), 0.03f, Dark);
                Bar(s, knee + new Vector3(x, 0, 0), top + new Vector3(x * 0.5f, 0, 0), 0.03f, Dark);
            }
            s.Box(top, new Vector3(1.5f, 0.05f, 0.08f), Metal);
        }
        return (s.Build(), l.Build());
    }

    /// <summary>A thin square bar from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static void Bar(MeshScratch s, Vector3 a, Vector3 b, float thick, Color colour) => Limb(s, a, b, thick, thick, colour);

    /// <summary>A box from <paramref name="a"/> to <paramref name="b"/>, <paramref name="width"/> across X, <paramref name="depth"/> the other way.</summary>
    private static void Limb(MeshScratch s, Vector3 a, Vector3 b, float width, float depth, Color colour)
    {
        var along = b - a;
        var y = along.Normalized();
        var z = Vector3.Right.Cross(y);
        if (z.LengthSquared() < 1e-6f) z = Vector3.Back;
        z = z.Normalized();
        var x = y.Cross(z).Normalized();
        s.Box((a + b) * 0.5f, new Vector3(width, along.Length(), depth), colour, new Basis(x, y, z));
    }

    /// <summary>The traffic's walkers, riders and tram sections (#1147), as World/Traffic builds them, in the model viewer (--models).</summary>
    [Core.Showcase("Traffic")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseTrafficModes()
    {
        static Node3D WithLamps((ArrayMesh Body, ArrayMesh Lamps) m)
        {
            var node = ModelViewer.Shaded(m.Body);
            node.AddChild(new MeshInstance3D { Mesh = m.Lamps, MaterialOverride = TrafficMeshBuilder.LampMaterial() });
            return node;
        }
        // a few walkers and riders side by side: their looks at a glance
        static Node3D Row(Func<int, ArrayMesh> make, int count, int step, float spacing)
        {
            var root = new Node3D();
            for (int i = 0; i < count; i++)
            {
                var n = ModelViewer.Shaded(make(i * step));
                n.Position = new Vector3((i - (count - 1) * 0.5f) * spacing, 0f, 0f);
                root.AddChild(n);
            }
            return root;
        }
        yield return ("Walker", () => ModelViewer.Shaded(Walker(0)));
        yield return ("Walkers, looks", () => Row(Walker, 6, 5, 0.8f));
        yield return ("Rider", () => ModelViewer.Shaded(Rider(0)));
        yield return ("Riders, looks", () => Row(Rider, 4, 7, 1.0f));
        for (int i = 0; i < Liveries.Length; i++)
        {
            int livery = i;
            yield return ($"Tram, {Liveries[i].Name}", () => Tram(livery, [9f, 7f, 9f]));
        }
        yield return ("Tram section, front cab", () => WithLamps(TramSection(0, 9f, true, false, false, false)));

        static Node3D Tram(int livery, float[] lengths)
        {
            // laid out as Traffic.AddTram places them: the head first, 0.6 m gangways, -Z forward
            var root = new Node3D();
            float back = 0f, total = 0.6f * (lengths.Length - 1);
            foreach (float len in lengths) total += len;
            for (int i = 0; i < lengths.Length; i++)
            {
                var n = WithLamps(TramSection(livery, lengths[i], i == 0, i == lengths.Length - 1, i == lengths.Length / 2, i > 0));
                n.Position = new Vector3(0f, 0f, back + lengths[i] * 0.5f - total * 0.5f);
                back += lengths[i] + 0.6f;
                root.AddChild(n);
            }
            return root;
        }
    }
}
