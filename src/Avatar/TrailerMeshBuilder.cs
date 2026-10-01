using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// Low-poly trailers (#70): a curtainsider, a tanker, a timber trailer whose logs are the load, and
/// a drawbar trailer's dolly and its swap body. Authored facing +Z, origin on the ground under the
/// section's centre of mass (which moves with the load, as the physics' does).
/// </summary>
public static class TrailerMeshBuilder
{
    private static readonly Color Log = new(0.5f, 0.34f, 0.2f);

    public static HeavyParts Build(TrailerSpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        float cg = Cg(s, load);
        float hw = s.Width * 0.5f;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float front = cg, rear = cg - s.Length;
        bool lampsAtRear = section == spec.Sections.Length - 1;

        if (s.Pivot == Coupling.Drawbar)
            Dolly(m, s, cg);
        else
        {
            float floor = spec.Body == TrailerBody.Tanker ? 1.1f : 1.2f;
            // the frame: two rails, the kingpin plate under a semi's nose
            foreach (float sx in new[] { -1f, 1f })
                Along(m, cg, 0.3f, s.Length - 0.1f, floor - 0.28f, floor, 0.14f, spec.Frame, sx * 0.45f);
            if (s.Pivot == Coupling.FifthWheel)
            {
                Along(m, cg, 0f, s.PivotAt + 0.8f, floor - 0.08f, floor, s.Width - 0.3f, spec.Frame);
                // landing legs, cranked up
                foreach (float sx in new[] { -1f, 1f })
                    m.Tube(new Vector3(sx * 0.95f, 0.35f, cg - (s.PivotAt + 1.1f)), new Vector3(sx * 0.95f, floor, cg - (s.PivotAt + 1.1f)), 0.06f, spec.Frame);
            }
            switch (spec.Body)
            {
                case TrailerBody.Curtainsider:
                case TrailerBody.SwapBody:
                    Box(m, spec, s, cg, floor);
                    break;
                case TrailerBody.Tanker:
                    Tank(m, spec, s, cg);
                    break;
                case TrailerBody.Timber:
                    Timber(m, spec, s, cg, floor, load);
                    break;
            }
            // mudguards over the axle group, the side underrun guards, the rear bar
            float a0 = s.Axles.Min(a => a.At), a1 = s.Axles.Max(a => a.At);
            float r = Tyre.Radius(s.Axles[0].Tyre);
            Along(m, cg, a0 - r - 0.12f, a1 + r + 0.12f, 2f * r + 0.03f, 2f * r + 0.09f, s.Width - 0.04f, HeavyMesh.Trim);
            Sides(m, cg, Mathf.Max(s.PivotAt, 0f) + 1.8f, a0 - r - 0.25f, 0.55f, 0.75f, s.Width - 0.25f, Steel);
            Along(m, cg, s.Length - 0.12f, s.Length, 0.45f, 0.6f, s.Width - 0.3f, Steel);
        }

        if (lampsAtRear)
            foreach (float sx in new[] { -1f, 1f })
            {
                Lamp(tail, sx * (hw - 0.3f), 0.78f, rear - 0.02f, 0.4f, 0.15f, 0.04f, TailLamp);
                Lamp(rev, sx * (hw - 0.6f), 0.78f, rear - 0.02f, 0.14f, 0.12f, 0.04f, White);
            }
        // side markers along a long trailer, amber
        if (s.Length > 6f)
            for (float at = 2f; at < s.Length - 1f; at += 3f)
                Sides(m, cg, at, at + 0.08f, 0.8f, 0.88f, s.Width - 0.2f, Amber);

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), System.Array.Empty<HeavyDoorLeaf>());
    }

    /// <summary>A curtainsider's curtains, or a swap body's walls, straps and bands.</summary>
    private static void Box(MeshScratch m, TrailerSpec spec, SectionSpec s, float cg, float floor)
    {
        bool curtain = spec.Body == TrailerBody.Curtainsider;
        Along(m, cg, 0f, s.Length, floor, s.Height, s.Width, spec.Paint);
        // top and bottom rails
        Sides(m, cg, 0f, s.Length, s.Height - 0.12f, s.Height, s.Width, curtain ? spec.Accent : spec.Accent);
        Sides(m, cg, 0f, s.Length, floor, floor + (curtain ? 0.14f : 0.32f), s.Width, curtain ? spec.Frame : spec.Accent);
        if (curtain)
            // the tensioning straps, every 1.3 m
            for (float at = 0.6f; at < s.Length - 0.3f; at += 1.3f)
                Sides(m, cg, at, at + 0.05f, floor + 0.14f, s.Height - 0.12f, s.Width, spec.Paint.Darkened(0.25f));
        // the rear doors' seam
        m.Box(new Vector3(0, (floor + s.Height) * 0.5f, cg - s.Length - 0.01f), new Vector3(0.04f, s.Height - floor - 0.2f, 0.02f), HeavyMesh.Trim);
    }

    private static void Tank(MeshScratch m, TrailerSpec spec, SectionSpec s, float cg)
    {
        const float radius = 1.15f, y = 2.28f;
        m.Tube(new Vector3(0, y, cg - 0.25f), new Vector3(0, y, cg - s.Length + 0.25f), radius, spec.Paint, 14);
        // the stripe along each flank, the walkway and its rail on top, the ladder at the back
        Sides(m, cg, 0.5f, s.Length - 0.5f, y - 0.1f, y + 0.1f, 2f * radius + 0.02f, spec.Accent);
        Along(m, cg, 1.0f, s.Length - 1.0f, y + radius - 0.02f, y + radius + 0.04f, 0.55f, Steel);
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * 0.2f, 1.2f, cg - s.Length + 0.2f), new Vector3(sx * 0.2f, y + radius, cg - s.Length + 0.35f), 0.025f, Steel, 4);
            for (float hatch = 1.8f; hatch < s.Length - 1f; hatch += 2.2f)
                m.Tube(new Vector3(0, y + radius - 0.05f, cg - hatch), new Vector3(0, y + radius + 0.12f, cg - hatch), 0.28f, Steel, 8);
        }
        // the cradles the tank rests on
        foreach (float at in new[] { 1.5f, s.Length * 0.5f, s.Length - 1.5f })
            Along(m, cg, at - 0.15f, at + 0.15f, 1.05f, 1.4f, 1.9f, spec.Frame);
    }

    private static void Timber(MeshScratch m, TrailerSpec spec, SectionSpec s, float cg, float floor, float load)
    {
        Along(m, cg, 0.2f, s.Length, floor, floor + 0.12f, s.Width - 0.1f, spec.Frame);
        // bolsters and their stakes
        for (float at = 1.2f; at < s.Length; at += 2.3f)
        {
            Along(m, cg, at - 0.1f, at + 0.1f, floor + 0.12f, floor + 0.3f, s.Width, spec.Frame);
            foreach (float sx in new[] { -1f, 1f })
                m.Tube(new Vector3(sx * (s.Width * 0.5f - 0.06f), floor + 0.3f, cg - at), new Vector3(sx * (s.Width * 0.5f - 0.06f), s.Height, cg - at), 0.055f, spec.Frame, 5);
        }
        // the logs: four a layer, up to three layers, as many as the load
        int logs = Mathf.RoundToInt(Mathf.Clamp(load, 0f, 1f) * 12f);
        const float r = 0.27f;
        for (int i = 0; i < logs; i++)
        {
            int layer = i / 4, col = i % 4;
            float x = (col - 1.5f) * (2f * r + 0.03f);
            float yy = floor + 0.3f + r + layer * 2f * r * 0.92f;
            // the layers staggered a little, the logs not all cut to one length
            float z0 = cg - 0.3f - (i % 3) * 0.12f, z1 = cg - s.Length + 0.25f + (i % 2) * 0.2f;
            m.Tube(new Vector3(x, yy, z0), new Vector3(x, yy, z1), r * (0.9f + 0.1f * (i % 3)), spec.Paint, 7);
        }
    }

    /// <summary>The drawbar trailer's dolly: its A-frame drawbar to the eye, the axle, the turntable.</summary>
    private static void Dolly(MeshScratch m, SectionSpec s, float cg)
    {
        float axle = s.Axles[0].At;
        float r = Tyre.Radius(s.Axles[0].Tyre);
        var eye = new Vector3(0, 0.95f, cg);
        foreach (float sx in new[] { -1f, 1f })
            m.Tube(eye, new Vector3(sx * 0.6f, 0.8f, cg - (axle - 0.7f)), 0.05f, HeavyMesh.Trim, 5);
        m.Box(eye, new Vector3(0.14f, 0.14f, 0.2f), Steel);
        Along(m, cg, axle - 0.75f, axle + 0.5f, 0.72f, 0.9f, 1.7f, HeavyMesh.Trim);
        Along(m, cg, s.HitchAt - 0.65f, s.HitchAt + 0.65f, s.HitchHeight - 0.1f, s.HitchHeight, 1.9f, Steel);
        m.Tube(new Vector3(-0.95f, r, cg - axle), new Vector3(0.95f, r, cg - axle), 0.07f, HeavyMesh.Trim, 6);
        Along(m, cg, axle - r - 0.1f, axle + r + 0.1f, 2f * r + 0.03f, 2f * r + 0.09f, s.Width - 0.04f, HeavyMesh.Trim);
    }
}
