using Godot;
using UnitSport.Avatar;
using UnitSport.Items;

namespace UnitSport.Build;

/// <summary>
/// How the gadgets look and what is solid about them (#275). Each is drawn in its placed object's
/// frame: origin on the ground (a ladder: at its top), local +Z toward whoever set it down (the
/// hideout's door, the ladder's climbing side). <see cref="MeshScratch.Build()"/> turns a mesh half
/// round, so everything is authored through <see cref="A"/>, which turns it back.
/// </summary>
public static class GadgetMeshes
{
    private static readonly Dictionary<string, ArrayMesh> Cache = new();

    /// <summary>A point in the final frame, as MeshScratch wants it authored (it negates X and Z on Build).</summary>
    private static Vector3 A(float x, float y, float z) => new(-x, y, -z);

    private static readonly Color Wood = new(0.50f, 0.34f, 0.19f), DarkWood = new(0.36f, 0.24f, 0.14f),
        Steel = new(0.55f, 0.58f, 0.62f), Rubber = new(0.12f, 0.12f, 0.13f), Hay = new(0.86f, 0.74f, 0.36f),
        HayDark = new(0.74f, 0.62f, 0.28f), Rope = new(0.78f, 0.69f, 0.47f);

    /// <summary>The factory registered for every gadget kind in <see cref="PlacedObjects"/>.</summary>
    public static Node3D Visual(PlacedObject o)
    {
        var body = new StaticBody3D();
        var mesh = new MeshInstance3D { Mesh = MeshFor(o), MaterialOverride = ItemDefs.Material };
        body.AddChild(mesh);
        foreach (var (shape, at) in Colliders(o.Kind)) body.AddChild(new CollisionShape3D { Shape = shape, Transform = at });
        if (o.Kind == PlacedKind.LaunchPad)
            body.AddChild(new OmniLight3D { LightColor = new Color(0.3f, 0.9f, 1f), LightEnergy = 1.2f, OmniRange = 4f, Position = new Vector3(0, 0.6f, 0) });
        if (o.Kind == PlacedKind.Zipline && PlacedObjects.Instance is { } placed && Gadgets.ZipStart(o.Payload) is { } s)
        {
            // the high post is part of the same object: solid as well
            var high = o.WorldTransform(placed.Origin).AffineInverse() * placed.Origin.ToWorld(s.E, s.N, s.Alt);
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.2f, Gadgets.PostHeight, 0.2f) }, Position = high + Vector3.Up * Gadgets.PostHeight / 2 });
        }
        return body;
    }

    /// <summary>A hay hideout going up in flames (#359): fire, smoke and a flickering light, gone after 8 s.</summary>
    public static void Burn(Node parent, Transform3D at)
    {
        var root = new Node3D { Name = "HayFire", TopLevel = true };
        parent.AddChild(root);
        root.GlobalTransform = at;
        var flame = new CpuParticles3D
        {
            Amount = 60, Lifetime = 1.1f, Direction = Vector3.Up, Spread = 25f, InitialVelocityMin = 1.5f, InitialVelocityMax = 3.5f,
            Gravity = new Vector3(0, 1.5f, 0), EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(1.1f, 0.6f, 1.1f),
            Position = Vector3.Up * 0.8f, ScaleAmountMin = 0.6f, ScaleAmountMax = 1.2f,
            Mesh = new BoxMesh { Size = Vector3.One * 0.25f, Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.55f, 0.12f),
            } },
        };
        var smoke = new CpuParticles3D
        {
            Amount = 30, Lifetime = 4f, Direction = Vector3.Up, Spread = 15f, InitialVelocityMin = 1f, InitialVelocityMax = 2f,
            Gravity = new Vector3(0.4f, 1f, 0), Position = Vector3.Up * 1.8f, ScaleAmountMin = 1.5f, ScaleAmountMax = 3f,
            Mesh = new BoxMesh { Size = Vector3.One * 0.5f, Material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.25f, 0.24f, 0.22f, 0.6f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            } },
        };
        var light = new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.25f), LightEnergy = 3f, OmniRange = 9f, Position = Vector3.Up * 1.2f };
        root.AddChild(flame);
        root.AddChild(smoke);
        root.AddChild(light);
        // the bale itself, blackening, for as long as it burns
        root.AddChild(new MeshInstance3D { Mesh = Mesh(PlacedKind.HayHideout), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.1f, 0.08f) } });
        var tree = parent.GetTree();
        tree.CreateTimer(5.0).Timeout += () => { if (GodotObject.IsInstanceValid(flame)) flame.Emitting = false; };
        tree.CreateTimer(8.0).Timeout += () => { if (GodotObject.IsInstanceValid(root)) root.QueueFree(); };
    }

    /// <summary>The mesh of a placed gadget: a zipline is drawn per object (its cable runs to the other post).</summary>
    public static ArrayMesh MeshFor(PlacedObject o)
    {
        if (o.Kind == PlacedKind.Zipline && PlacedObjects.Instance is { } placed && Gadgets.ZipStart(o.Payload) is { } s)
        {
            var high = o.WorldTransform(placed.Origin).AffineInverse() * placed.Origin.ToWorld(s.E, s.N, s.Alt);
            return Zipline(high);
        }
        if (o.Kind == PlacedKind.RopeLadder) return Ladder(Gadgets.LadderLength(o.Payload) ?? Gadgets.LadderMax);
        return Mesh(o.Kind);
    }

    /// <summary>A gadget that looks the same wherever it is: cached per kind.</summary>
    public static ArrayMesh Mesh(PlacedKind kind)
    {
        string key = kind.ToString();
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var s = new MeshScratch();
        switch (kind)
        {
            case PlacedKind.Zipline:
                Post(s, Vector3.Zero);
                break;
            case PlacedKind.Trampoline:
                // three stacked tyres for a rim, a rubber mat across, plank legs under
                for (int i = 0; i < 4; i++)
                {
                    float a = i * Mathf.Pi / 2 + Mathf.Pi / 4;
                    s.Box(A(Mathf.Cos(a) * 0.9f, 0.2f, Mathf.Sin(a) * 0.9f), new Vector3(0.14f, 0.4f, 0.14f), DarkWood);
                }
                s.Ring(A(0, 0.42f, 0), Vector3.Up, 0.95f, 1.25f, 0.18f, Rubber, 20);
                s.Tube(A(0, 0.40f, 0), A(0, 0.46f, 0), 1.0f, new Color(0.18f, 0.20f, 0.24f), 20);
                s.Ring(A(0, 0.47f, 0), Vector3.Up, 0.3f, 0.36f, 0.01f, new Color(0.9f, 0.8f, 0.2f), 16);   // the target mark
                break;
            case PlacedKind.LaunchPad:
                s.Tube(A(0, 0, 0), A(0, 0.22f, 0), 1.0f, 1.0f, new Color(0.30f, 0.33f, 0.37f), 16);
                s.Tube(A(0, 0.22f, 0), A(0, 0.28f, 0), 0.8f, new Color(0.15f, 0.55f, 0.65f), 16);
                s.Ring(A(0, 0.29f, 0), Vector3.Up, 0.55f, 0.65f, 0.02f, new Color(0.5f, 1f, 1f), 16);
                // chevrons pointing up the launch (+Z, away from the battery)
                for (int i = 0; i < 2; i++)
                    foreach (int side in new[] { -1, 1 })
                        s.Box(A(side * 0.16f, 0.30f, -0.15f + i * 0.3f), new Vector3(0.32f, 0.02f, 0.08f), new Color(0.98f, 0.8f, 0.15f),
                            new Basis(Vector3.Up, side * 0.6f));
                s.Box(A(0, 0.2f, -1.15f), new Vector3(0.5f, 0.4f, 0.3f), new Color(0.16f, 0.26f, 0.52f));   // the car battery
                break;
            case PlacedKind.CamoNet:
            {
                foreach (int x in new[] { -1, 1 })
                    foreach (int z in new[] { -1, 1 })
                        s.Tube(A(x * 1.8f, 0, z * 1.8f), A(x * 1.8f, 2.05f, z * 1.8f), 0.04f, DarkWood);
                // patches of green and brown, sagging a little toward the middle
                var rng = new Random(275);
                for (int i = 0; i < 8; i++)
                    for (int j = 0; j < 8; j++)
                    {
                        float x = -1.9f + (i + 0.5f) * 0.475f, z = -1.9f + (j + 0.5f) * 0.475f;
                        float sag = 0.25f * (1 - (x * x + z * z) / 7.2f);
                        var c = rng.Next(3) switch
                        {
                            0 => new Color(0.30f, 0.38f, 0.20f), 1 => new Color(0.42f, 0.36f, 0.22f), _ => new Color(0.24f, 0.30f, 0.16f),
                        };
                        s.Box(A(x, 2.0f - sag, z), new Vector3(0.44f, 0.03f, 0.44f), c, new Basis(Vector3.Up, (float)rng.NextDouble() * 0.4f));
                    }
                break;
            }
            case PlacedKind.HayHideout:
                foreach (var (centre, size) in HayBoxes())
                {
                    s.Box(A(centre.X, centre.Y, centre.Z), size, Hay);
                    // straw bands, so it reads as bales
                    s.Box(A(centre.X, centre.Y, centre.Z), size + new Vector3(0.02f, -size.Y * 0.8f, 0.02f), HayDark);
                }
                break;
        }
        return Cache[key] = s.Build();
    }

    /// <summary>A post with its pulley, foot at <paramref name="foot"/> (final frame).</summary>
    private static void Post(MeshScratch s, Vector3 foot)
    {
        s.Tube(A(foot.X, foot.Y, foot.Z), A(foot.X, foot.Y + Gadgets.PostHeight, foot.Z), 0.09f, 0.07f, Wood, 8);
        s.Box(A(foot.X, foot.Y + Gadgets.PostHeight - 0.1f, foot.Z), new Vector3(0.2f, 0.12f, 0.2f), Steel);
    }

    /// <summary>Both posts and the cable between their tops; <paramref name="high"/> is the high post's foot in the low post's frame.</summary>
    public static ArrayMesh Zipline(Vector3 high)
    {
        var s = new MeshScratch();
        Post(s, Vector3.Zero);
        Post(s, high);
        var top = Vector3.Up * Gadgets.PostHeight;
        s.Tube(A(top.X, top.Y - 0.05f, top.Z), A(high.X, high.Y + Gadgets.PostHeight - 0.05f, high.Z), 0.018f, new Color(0.25f, 0.26f, 0.28f), 5);
        return s.Build();
    }

    /// <summary>Two ropes and wooden rungs hanging <paramref name="length"/> metres down from hooks at the origin.</summary>
    public static ArrayMesh Ladder(float length)
    {
        string key = $"ladder{length:F1}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var s = new MeshScratch();
        foreach (float x in new[] { -0.25f, 0.25f })
        {
            s.Tube(A(x, 0.05f, 0.05f), A(x, -length, 0.05f), 0.025f, Rope, 5);
            s.Box(A(x, 0.02f, -0.05f), new Vector3(0.06f, 0.06f, 0.2f), Steel);   // the hook over the edge
        }
        for (float y = -0.3f; y > -length; y -= 0.3f)
            s.Box(A(0, y, 0.05f), new Vector3(0.56f, 0.04f, 0.05f), Wood);
        return Cache[key] = s.Build();
    }

    /// <summary>The hideout's bales: three walls (a door in front, +Z, and a slit at eye height behind) and a roof.</summary>
    private static IEnumerable<(Vector3 Centre, Vector3 Size)> HayBoxes()
    {
        const float w = 2.2f, h = 1.5f, t = 0.4f;
        yield return (new Vector3(-w / 2 + t / 2, h / 2, 0), new Vector3(t, h, w));                   // left
        yield return (new Vector3(w / 2 - t / 2, h / 2, 0), new Vector3(t, h, w));                    // right
        yield return (new Vector3(0, 0.5f, -w / 2 + t / 2), new Vector3(w - 2 * t, 1.0f, t));         // back, below the slit
        yield return (new Vector3(0, 1.375f, -w / 2 + t / 2), new Vector3(w - 2 * t, 0.25f, t));      // back, above the slit
        foreach (int side in new[] { -1, 1 })
            yield return (new Vector3(side * 0.6f, h / 2, w / 2 - t / 2), new Vector3(0.2f, h, t));   // front, either side of the door
        yield return (new Vector3(0, 1.25f, w / 2 - t / 2), new Vector3(1.0f, 0.5f, t));              // over the door
        yield return (new Vector3(0, h + 0.2f, 0), new Vector3(w + 0.2f, t, w + 0.2f));               // roof
    }

    public static IEnumerable<(Shape3D Shape, Transform3D At)> Colliders(PlacedKind kind)
    {
        switch (kind)
        {
            case PlacedKind.Zipline:
                yield return (new BoxShape3D { Size = new Vector3(0.2f, Gadgets.PostHeight, 0.2f) }, new Transform3D(Basis.Identity, Vector3.Up * Gadgets.PostHeight / 2));
                break;
            case PlacedKind.Trampoline:
                yield return (new CylinderShape3D { Radius = 1.2f, Height = 0.46f }, new Transform3D(Basis.Identity, Vector3.Up * 0.23f));
                break;
            case PlacedKind.LaunchPad:
                yield return (new CylinderShape3D { Radius = 1.0f, Height = 0.28f }, new Transform3D(Basis.Identity, Vector3.Up * 0.14f));
                break;
            case PlacedKind.CamoNet:
                foreach (int x in new[] { -1, 1 })
                    foreach (int z in new[] { -1, 1 })
                        yield return (new BoxShape3D { Size = new Vector3(0.1f, 2.05f, 0.1f) }, new Transform3D(Basis.Identity, new Vector3(x * 1.8f, 1.02f, z * 1.8f)));
                break;
            case PlacedKind.HayHideout:
                foreach (var (centre, size) in HayBoxes())
                    yield return (new BoxShape3D { Size = size }, new Transform3D(Basis.Identity, centre));
                break;
        }
    }
}
