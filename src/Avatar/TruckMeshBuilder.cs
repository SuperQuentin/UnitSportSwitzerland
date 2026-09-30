using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// Low-poly trucks from <see cref="MeshScratch"/> boxes at their real dimensions (#70): a cab-over
/// cab with its glass, grille, lamps, mirrors and steps, the chassis rails, the fuel tank, the
/// fifth wheel of a tractor or the swap body and hitch of a rigid. Authored facing +Z with the
/// origin on the ground under the centre of mass (z = cg − metres behind the front); the operator's
/// colours come from <see cref="HeavyLook"/>. Wheels are the rig's.
/// </summary>
public static class TruckMeshBuilder
{
    public static HeavyParts Build(HeavySpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        var look = spec.Look;
        float cg = Cg(s, load);
        float hw = s.Width * 0.5f;
        bool tractor = spec.Class == HeavyClass.Tractor;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float front = cg;                 // authored z of the bumper
        float rear = cg - s.Length;       // and of the back

        float cabLen = tractor ? 2.35f : 2.25f;
        float cabTop = tractor ? 3.35f : 3.25f;

        // ---- the cab ----
        // bumper, steps and the lower cab, cut round the front wheels
        Skirt(m, s, cg, 0f, cabLen, 0.45f, 1.1f, s.Width - 0.02f, look.Lower);
        Along(m, cg, 0f, cabLen, 1.1f, cabTop, s.Width - 0.05f, look.Paint);
        // roof: a tall deflector on the long-haul tractor, a low spoiler on the distribution truck
        if (tractor) Along(m, cg, 0.45f, cabLen, cabTop, 3.95f, s.Width - 0.15f, look.Paint);
        else Along(m, cg, 0.9f, cabLen, cabTop, 3.55f, s.Width - 0.3f, look.Paint);
        // windscreen, grille, sun visor on the face
        m.Box(new Vector3(0, 2.6f, front + 0.02f), new Vector3(s.Width - 0.3f, 1.0f, 0.04f), Glass);
        m.Box(new Vector3(0, 1.62f, front + 0.02f), new Vector3(s.Width - 0.45f, 0.62f, 0.04f), look.Accent);
        m.Box(new Vector3(0, 3.18f, front + 0.08f), new Vector3(s.Width - 0.2f, 0.07f, 0.2f), look.Accent);
        // side glass, the belt stripe, and the door's handle line
        Sides(m, cg, 0.25f, 1.25f, 2.15f, 3.05f, s.Width - 0.05f, Glass);
        Sides(m, cg, 0.05f, cabLen - 0.05f, 1.5f, 1.66f, s.Width - 0.05f, look.Accent);
        // mirrors on their arms
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Box(new Vector3(sx * (hw + 0.1f), 2.9f, front - 0.3f), new Vector3(0.2f, 0.05f, 0.05f), Trim);
            m.Box(new Vector3(sx * (hw + 0.22f), 2.55f, front - 0.32f), new Vector3(0.07f, 0.6f, 0.24f), Trim);
        }
        // head lamps low in the bumper corners, amber indicators beside them
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(head, sx * (hw - 0.35f), 0.88f, front + 0.03f, 0.42f, 0.17f, 0.04f, HeadLamp);
            Lamp(m, sx * (hw - 0.08f), 0.88f, front + 0.03f, 0.1f, 0.14f, 0.04f, Amber);
        }

        // ---- the chassis behind the cab ----
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, cabLen - 0.3f, s.Length - 0.05f, 0.72f, 1.02f, 0.12f, Trim, sx * 0.45f);
        // mudguards over the axles behind the cab
        foreach (var a in s.Axles)
        {
            if (a.At < cabLen) continue;
            float r = Tyre.Radius(a.Tyre);
            Along(m, cg, a.At - r - 0.1f, a.At + r + 0.1f, 2f * r + 0.04f, 2f * r + 0.1f, s.Width - 0.04f, Trim);
        }
        // the fuel tank on the left, between the axles
        float firstRear = s.Axles.Where(a => a.Group == 1).Min(a => a.At);
        Along(m, cg, cabLen + 0.1f, firstRear - Tyre.Radius(s.Axles[^1].Tyre) - 0.2f, 0.5f, 1.05f, 0.55f, Steel, hw - 0.35f);

        if (tractor)
        {
            // the fifth wheel: a plate on its mounting, the jaws toward the back
            Along(m, cg, s.HitchAt - 0.7f, s.HitchAt + 0.7f, s.HitchHeight - 0.12f, s.HitchHeight - 0.02f, 1.9f, Steel);
            Along(m, cg, s.HitchAt - 0.4f, s.HitchAt + 0.4f, 1.02f, s.HitchHeight - 0.12f, 1.2f, Trim);
            // the air and electric lines coiled behind the cab
            Along(m, cg, cabLen + 0.05f, cabLen + 0.2f, 1.1f, 2.4f, 0.9f, Trim);
        }
        else
        {
            // the swap body on its frame
            float from = cabLen + 0.2f;
            Along(m, cg, from, s.Length, 1.25f, 4.0f, s.Width, look.Cargo);
            Sides(m, cg, from, s.Length, 1.3f, 1.62f, s.Width, look.Accent);
            Sides(m, cg, from, s.Length, 3.8f, 3.9f, s.Width, look.Accent);
            // the rear doors' seam and hinges
            m.Box(new Vector3(0, 2.6f, rear - 0.01f), new Vector3(0.04f, 2.6f, 0.02f), Trim);
            // side underrun guards between the axles, and the hitch jaw under the back
            Sides(m, cg, cabLen + 0.3f, firstRear - 0.7f, 0.5f, 0.7f, s.Width - 0.2f, Steel);
            Along(m, cg, s.HitchAt - 0.2f, s.HitchAt + 0.12f, s.HitchHeight - 0.12f, s.HitchHeight + 0.08f, 0.35f, Trim);
        }
        // the rear bumper bar and the lamps in it
        Along(m, cg, s.Length - 0.12f, s.Length, 0.45f, 0.62f, s.Width - 0.2f, Steel);
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(tail, sx * (hw - 0.3f), 0.82f, rear - 0.02f, 0.4f, 0.16f, 0.04f, TailLamp);
            Lamp(rev, sx * (hw - 0.62f), 0.82f, rear - 0.02f, 0.14f, 0.12f, 0.04f, White);
        }

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), System.Array.Empty<HeavyDoorLeaf>());
    }
}
