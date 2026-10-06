using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// Low-poly trucks from <see cref="MeshScratch"/> boxes at their real dimensions (#70): a cab-over
/// cab, hollow above its floor with see-through glass and the cockpit in it (#157), its grille,
/// lamps, mirrors and steps, the chassis rails, the fuel tank, the
/// fifth wheel of a tractor or the swap body and hitch of a rigid. Authored facing +Z with the
/// origin on the ground under the centre of mass (z = cg − metres behind the front); the operator's
/// colours come from <see cref="HeavyLook"/>. Wheels are the rig's.
/// </summary>
public static class TruckMeshBuilder
{
    private static readonly Color Bunk = new(0.22f, 0.24f, 0.3f);

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
        ((ArrayMesh, Vector3, float) Tip, (ArrayMesh, Vector3) Tailgate)? tipper = null;
        ((ArrayMesh, Vector3, float) Drum, (ArrayMesh, Vector3) Chute)? mixer = null;
        float front = cg;                 // authored z of the bumper
        float rear = cg - s.Length;       // and of the back

        float cabLen = tractor ? 2.35f : 2.25f;
        float cabTop = tractor ? 3.35f : 3.25f;

        // ---- the cab: hollow above its floor, glass you see through (#157) ----
        float floorY = tractor ? 1.3f : 1.2f;
        const float wsLow = 2.2f, wsHigh = 3.1f, winLow = 2.15f, winHigh = 3.05f, wall = 0.07f;
        float ceil = cabTop - 0.08f;
        float shell = s.Width - 0.05f, inner = shell * 0.5f - wall;
        // bumper, steps and the lower cab, cut round the front wheels; solid up to the cab floor
        Skirt(m, s, cg, 0f, cabLen, 0.45f, 1.1f, s.Width - 0.02f, look.Lower);
        Along(m, cg, 0f, cabLen, 1.1f, floorY, shell, look.Paint);
        Along(m, cg, wall, cabLen - wall, floorY, floorY + 0.01f, inner * 2f, HeavyCabin.FloorColour);
        // the face round the windscreen, the roof and the back wall
        Along(m, cg, 0f, wall, floorY, wsLow, shell, look.Paint);
        Along(m, cg, 0f, wall, wsHigh, cabTop, shell, look.Paint);
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, 0f, wall, wsLow, wsHigh, 0.13f, look.Paint, sx * (shell * 0.5f - 0.065f));
        FrontPane(m, front - 0.03f, shell * 0.5f - 0.13f, wsLow, wsHigh);
        Along(m, cg, 0f, cabLen, ceil, cabTop, shell, look.Paint);
        Along(m, cg, wall, cabLen - wall, ceil - 0.01f, ceil, inner * 2f, HeavyCabin.Lining);
        Along(m, cg, cabLen - wall, cabLen, floorY, ceil, shell, look.Paint);
        // the side walls with the door's window in each, from the pillar to the door's back edge
        const float doorGlass0 = 0.25f, doorGlass1 = 1.25f;
        foreach (float sx in new[] { -1f, 1f })
        {
            float x = sx * (shell * 0.5f - wall * 0.5f);
            Along(m, cg, wall, cabLen - wall, floorY, winLow, wall, look.Paint, x);
            Along(m, cg, wall, cabLen - wall, winHigh, ceil, wall, look.Paint, x);
            Along(m, cg, wall, doorGlass0, winLow, winHigh, wall, look.Paint, x);
            Along(m, cg, doorGlass1, cabLen - wall, winLow, winHigh, wall, look.Paint, x);
            SidePane(m, sx * shell * 0.5f, cg - doorGlass0, cg - doorGlass1, winLow, winHigh);
        }
        // inside: the engine tunnel down the middle, and a tractor's bunk across the back
        float bunk = tractor ? 0.7f : 0f;
        Along(m, cg, 0.35f, cabLen - wall - bunk, floorY, floorY + 0.14f, 0.6f, HeavyCabin.Dash);
        if (tractor)
        {
            Along(m, cg, cabLen - wall - bunk, cabLen - wall, floorY, floorY + 0.5f, inner * 2f, HeavyCabin.Dash);
            Along(m, cg, cabLen - wall - bunk, cabLen - wall, floorY + 0.5f, floorY + 0.62f, inner * 2f, Bunk);
        }
        // roof: a tall deflector on the long-haul tractor, a low spoiler on the distribution truck
        if (tractor) Along(m, cg, 0.45f, cabLen, cabTop, 3.95f, s.Width - 0.15f, look.Paint);
        else Along(m, cg, 0.9f, cabLen, cabTop, 3.55f, s.Width - 0.3f, look.Paint);
        // grille and sun visor on the face
        m.Box(new Vector3(0, 1.62f, front + 0.02f), new Vector3(s.Width - 0.45f, 0.62f, 0.04f), look.Accent);
        m.Box(new Vector3(0, 3.18f, front + 0.08f), new Vector3(s.Width - 0.2f, 0.07f, 0.2f), look.Accent);
        // the belt stripe
        Sides(m, cg, 0.05f, cabLen - 0.05f, 1.5f, 1.66f, s.Width - 0.05f, look.Accent);
        // mirrors out on their arms: a main and a wide-angle each side, no wider than #70's boxes
        // (the hull is measured from the mesh)
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * (hw - 0.03f), 2.95f, front - 0.25f), new Vector3(sx * (hw + 0.17f), 2.95f, front - 0.36f), 0.025f, Trim, 4);
            m.Tube(new Vector3(sx * (hw + 0.17f), 2.95f, front - 0.36f), new Vector3(sx * (hw + 0.17f), 2.12f, front - 0.36f), 0.02f, Trim, 4);
            string name = sx > 0 ? "MirrorLeft" : "MirrorRight";
            mirrors.Add((name, new Vector3(sx * (hw + 0.17f), 2.62f, front - 0.42f), new Vector2(0.18f, 0.36f)));
            mirrors.Add((name + "Wide", new Vector3(sx * (hw + 0.17f), 2.27f, front - 0.42f), new Vector2(0.18f, 0.2f)));
        }
        var cab = new CabFrame
        {
            Front = front, Floor = floorY, Ceiling = ceil, WsBase = wsLow, WsTop = wsHigh, DashTop = wsLow, InnerHalf = inner,
            Nose = 0.25f, DriverX = hw - 0.65f, HipRise = 0.48f, ColumnTilt = 0.85f, DashToX = -inner,
            Clutch = spec.Box == Transmission.Amt, PassengerSeat = true,
        };
        var cockpit = HeavyCabin.Build(m, cab, HeavyCabin.GaugesFor(spec.LimiterKmh, spec.Redline),
            HeavyDriveline.AirLow, HeavyDriveline.AirMax, mirrors);
        var seats = new[]
        {
            new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, floorY),
            new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip with { X = -cockpit.Seat.Hip.X }), cockpit.Seat.Recline, floorY),
        };
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
        else if (spec.Body == TruckBody.Tipper)
        {
            tipper = Tipper(s, cg, cabLen, load, look);
            Sides(m, cg, cabLen + 0.3f, firstRear - 0.7f, 0.5f, 0.7f, s.Width - 0.2f, Steel);
            // the subframe the body lies on, and the front ram's foot behind the cab
            foreach (float sx in new[] { -1f, 1f })
                Along(m, cg, cabLen + 0.2f, s.Length - 0.15f, 1.02f, 1.22f, 0.14f, Trim, sx * 0.5f);
            m.Tube(new Vector3(0, 1.02f, cg - cabLen - 0.45f), new Vector3(0, 1.4f, cg - cabLen - 0.45f), 0.12f, Steel, 6);
        }
        else if (spec.Body == TruckBody.Mixer)
        {
            mixer = Mixer(s, cg, cabLen, look);
            Sides(m, cg, cabLen + 0.3f, firstRear - 0.7f, 0.5f, 0.7f, s.Width - 0.2f, Steel);
            // the drum's two stands, the water tank behind the cab, the hopper over the drum's mouth,
            // and the ladder up to it at the back
            Along(m, cg, cabLen + 0.35f, cabLen + 0.65f, 1.02f, 2.3f, 1.2f, Trim);
            Along(m, cg, s.Length - 1.2f, s.Length - 0.9f, 1.02f, 3.0f, 1.3f, Trim);
            Along(m, cg, cabLen + 0.05f, cabLen + 0.35f, 1.1f, 2.5f, 1.6f, look.Accent);
            m.Box(new Vector3(0, 3.35f, cg - s.Length + 0.75f), new Vector3(0.9f, 0.45f, 0.7f), Steel);
            foreach (float sx in new[] { -1f, 1f })
                m.Box(new Vector3(sx * 0.3f, 2.05f, cg - s.Length + 0.08f), new Vector3(0.05f, 2.1f, 0.05f), Steel);
            for (int i = 0; i < 6; i++)
                m.Box(new Vector3(0, 1.15f + i * 0.33f, cg - s.Length + 0.08f), new Vector3(0.6f, 0.04f, 0.05f), Steel);
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

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
            Tip = tipper?.Tip,
            Tailgate = tipper?.Tailgate,
            Drum = mixer?.Drum,
            Chute = mixer?.Chute,
        };
    }

    private static readonly Color Gravel = new(0.55f, 0.52f, 0.47f);
    private static readonly Color DrumSteel = new(0.82f, 0.82f, 0.8f);

    /// <summary>How far a tipper's body tips, rad: about 50°, as a rear tipper's front ram lifts it.</summary>
    public const float TipAngle = 0.87f;

    /// <summary>
    /// A rear-tipping body (#613) from behind the cab to the back, built about its hinge low at the
    /// back: a floor, two sides, a headboard with a short canopy over the cab, and the load as a heap
    /// as high as the truck is loaded. Its tailgate is separate, built about its top hinge.
    /// </summary>
    private static ((ArrayMesh, Vector3, float) Tip, (ArrayMesh, Vector3) Tailgate) Tipper(SectionSpec s, float cg, float cabLen, float load, HeavyLook look)
    {
        var bin = new MeshScratch();
        float from = cabLen + 0.25f, to = s.Length - 0.05f, w = s.Width - 0.06f;
        const float floor0 = 1.24f, floor1 = 1.36f, top = 2.85f;
        Along(bin, cg, from, to, floor0, floor1, w, look.Cargo);
        foreach (float sx in new[] { -1f, 1f })
        {
            Along(bin, cg, from, to, floor1, top, 0.06f, look.Paint, sx * (w * 0.5f - 0.03f));
            // the side's top rail and its stiffeners
            Along(bin, cg, from, to, top - 0.1f, top, 0.1f, look.Accent, sx * (w * 0.5f - 0.03f));
            for (float at = from + 0.9f; at < to - 0.3f; at += 1.2f)
                Along(bin, cg, at, at + 0.08f, floor1, top, 0.1f, look.Accent, sx * (w * 0.5f));
        }
        Along(bin, cg, from, from + 0.08f, floor1, top + 0.25f, w, look.Paint);
        Along(bin, cg, from - 0.45f, from + 0.08f, top + 0.15f, top + 0.25f, w - 0.2f, look.Paint);
        if (load > 0.02f)
        {
            // the load: gravel heaped up the middle, as high as the truck is loaded
            float h = floor1 + (top - floor1) * 0.95f * Mathf.Clamp(load, 0f, 1f);
            Along(bin, cg, from + 0.1f, to - 0.1f, floor1, h, w - 0.14f, Gravel);
            Along(bin, cg, from + 0.6f, to - 0.6f, h, h + 0.18f * load, w - 0.9f, Gravel);
        }
        // the hinge low at the back, a little in from the rear bumper
        var hinge = new Vector3(0, 1.15f, cg - (s.Length - 0.25f));
        var gate = new MeshScratch();
        Along(gate, cg, to - 0.06f, to, floor1, top, w, look.Paint);
        Along(gate, cg, to - 0.1f, to, floor1, floor1 + 0.12f, w, look.Accent);
        var gatePivot = new Vector3(0, top, cg - to + 0.03f);
        return ((bin.Build(hinge), CarMeshBuilder.Turned(hinge), TipAngle),
            (gate.Build(gatePivot), CarMeshBuilder.Turned(gatePivot - hinge)));
    }

    /// <summary>
    /// A concrete mixer's drum (#613), built about its axis along its length (the mouth at the back),
    /// as rings of tubes with the spiral's welds striped round it so its turning shows; and its chute,
    /// built about its top pivot under the mouth.
    /// </summary>
    private static ((ArrayMesh, Vector3, float) Drum, (ArrayMesh, Vector3) Chute) Mixer(SectionSpec s, float cg, float cabLen, HeavyLook look)
    {
        var drum = new MeshScratch();
        // the profile from the closed front (+Z, by the cab) to the mouth at the back (−Z)
        (float Z, float R)[] profile = { (2.75f, 0.55f), (2.2f, 0.95f), (1.2f, 1.15f), (-0.4f, 1.15f), (-1.6f, 0.95f), (-2.5f, 0.55f), (-2.85f, 0.38f) };
        for (int i = 0; i + 1 < profile.Length; i++)
        {
            var (z0, r0) = profile[i];
            var (z1, r1) = profile[i + 1];
            drum.Tube(new Vector3(0, 0, z0), new Vector3(0, 0, z1), r0, r1, DrumSteel, 12);
        }
        // stripes round it, a little proud of the shell, turned a step apart along its length
        for (int k = 0; k < 6; k++)
        {
            float a = k * Mathf.Tau / 6f;
            for (int j = 0; j < 3; j++)
            {
                float z = 1.6f - j * 1.3f, r = 1.17f;
                float aj = a + j * 0.5f;
                drum.Box(new Vector3(Mathf.Cos(aj) * r, Mathf.Sin(aj) * r, z), new Vector3(0.12f, 0.12f, 1.1f), look.Accent,
                    new Basis(Vector3.Back, aj));
            }
        }
        float mid = cabLen + 0.75f + 2.85f;
        var pivot = new Vector3(0, 2.45f, cg - mid);
        var chute = new MeshScratch();
        // a trough sloping down and back from under the mouth
        chute.Box(new Vector3(0, -0.25f, -0.75f), new Vector3(0.5f, 0.08f, 1.5f), Steel, new Basis(Vector3.Right, -0.32f));
        foreach (float sx in new[] { -1f, 1f })
            chute.Box(new Vector3(sx * 0.25f, -0.15f, -0.75f), new Vector3(0.04f, 0.22f, 1.5f), Steel, new Basis(Vector3.Right, -0.32f));
        var chutePivot = new Vector3(0, 2.15f, cg - s.Length + 0.25f);
        return ((drum.Build(), CarMeshBuilder.Turned(pivot), 0.2f),
            (chute.Build(), CarMeshBuilder.Turned(chutePivot)));
    }
}
