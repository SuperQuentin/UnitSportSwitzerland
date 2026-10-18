using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The flying saucer (#861): a classic lens-shaped hull about 9 m across, a glass dome with two
/// seats under it, a band of rim lamps that chase, a glowing underside, three landing legs that fold
/// into the belly and a hatch whose ramp comes down to the ground. Plus the mothership, the same
/// shape at 120 m with rings of windows: scenery and an event prop, not a mount.
///
/// <para>
/// Authored facing +Z (the ramp side is the front), origin on the ground under the legs' feet, like
/// every <see cref="MeshScratch"/> model; the moving parts (legs, ramp, lamps, the beam) are child
/// nodes placed at <see cref="AircraftMeshBuilder.Flip"/> of their authored hinge, animated by
/// <see cref="UfoRig"/>. Sizes are chosen so a person walks under the belly (2.2 m up) and up the ramp.
/// </para>
/// </summary>
public static class UfoMeshBuilder
{
    /// <summary>Overall radius of the rim, m.</summary>
    public const float RimRadius = 4.6f;
    /// <summary>Height of the flat belly round the hatch with the legs down, m: a person walks under it.</summary>
    public const float Belly = 2.2f;
    /// <summary>The rim's edge: where the chasing lamps sit.</summary>
    public const float RimY = 2.85f;
    /// <summary>The cabin floor under the dome, and the dome's radius.</summary>
    public const float DomeBase = 3.65f, DomeRadius = 1.75f;
    /// <summary>The legs' hinges on the belly (radius), and where their feet stand (radius).</summary>
    public const float LegHingeR = 2.7f, LegFootR = 3.35f;
    /// <summary>The ramp: hinged at the hatch's back edge (authored z), this long, this wide.</summary>
    public const float RampHingeZ = -0.6f, RampLength = 4.0f, RampWidth = 1.3f;
    /// <summary>The ramp's angle down when open, rad: its far end on the ground.</summary>
    public static readonly float RampOpenAngle = Mathf.Asin((Belly - 0.06f) / RampLength);
    /// <summary>The legs' fold, rad: swung in and up under the belly (and telescoped to <see cref="LegTuck"/> of their length).</summary>
    public const float LegFold = -1.8f, LegTuck = 0.7f;
    /// <summary>How many lamps round the rim.</summary>
    public const int RimLamps = 16;

    /// <summary>The ramp's hinge height: just under the belly plate.</summary>
    public const float RampHingeY = Belly - 0.08f;

    /// <summary>The hatch: the beam's apex, authored (on the axis, at the belly).</summary>
    public static readonly Vector3 Hatch = new(0, Belly - 0.05f, 0);

    /// <summary>The foot of the open ramp, authored: where one walks out and gets in.</summary>
    public static Vector3 RampFoot => new(0, 0, RampHingeZ + RampLength * Mathf.Cos(RampOpenAngle) + 0.6f);

    private static readonly Color Silver = new(0.74f, 0.77f, 0.82f);
    private static readonly Color Steel = new(0.46f, 0.49f, 0.55f);
    private static readonly Color Dark = new(0.22f, 0.24f, 0.29f);
    private static readonly Color Seat = new(0.35f, 0.12f, 0.45f);
    private static readonly Color DomeGlass = new(0.55f, 0.95f, 0.85f, 0.32f);

    /// <summary>The lamps' colours round the rim, repeated: the classic carnival chase.</summary>
    public static readonly Color[] LampColours =
    {
        new(0.2f, 1f, 0.9f), new(1f, 0.25f, 0.85f), new(1f, 0.9f, 0.2f), new(0.4f, 1f, 0.3f),
    };

    /// <summary>The underside's and the beam's glow.</summary>
    public static readonly Color GlowColour = new(0.45f, 1f, 0.75f);

    // ---- the hull --------------------------------------------------------------------------

    /// <summary>
    /// A lens through rings round the vertical: (radius, height) pairs from the belly up to the
    /// dome's base, each ring <paramref name="sides"/> points, scaled by <paramref name="scale"/>.
    /// </summary>
    private static void Lens(MeshScratch s, (float R, float Y)[] profile, Color[] bands, Color cap, int sides, float scale)
    {
        var sections = new List<Vector3[]>();
        foreach (var (r, y) in profile)
        {
            var ring = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = Mathf.Tau * i / sides;
                ring[i] = new Vector3(Mathf.Sin(a) * r, y, Mathf.Cos(a) * r) * scale;
            }
            sections.Add(ring);
        }
        // one band colour per ring gap: Loft colours round a ring, so draw each gap as its own loft
        for (int k = 0; k + 1 < sections.Count; k++)
        {
            var colours = new Color[sides];
            System.Array.Fill(colours, bands[k]);
            s.Loft(new[] { sections[k], sections[k + 1] }, colours, cap, averaged: true);
        }
    }

    /// <summary>The saucer's profile, belly to dome base: flat round the hatch, the rim's sharp edge, a shallow crown.</summary>
    private static readonly (float R, float Y)[] Profile =
    {
        (1.7f, Belly), (3.3f, Belly + 0.12f), (4.35f, 2.55f), (RimRadius, RimY - 0.1f), (RimRadius, RimY + 0.08f),
        (4.3f, 3.08f), (3.0f, 3.45f), (DomeRadius + 0.15f, DomeBase),
    };
    private static readonly Color[] ProfileBands = { Dark, Steel, Steel.Darkened(0.1f), new(0.2f, 0.22f, 0.3f), Silver, Silver, Silver.Lightened(0.08f) };

    /// <summary>The hull without its moving parts: body, the cabin under the dome, two seats and the controls.</summary>
    [Core.Showcase("Parts", "UFO hull")]
    public static ArrayMesh Hull()
    {
        var s = new MeshScratch();
        using (s.Smoothing(true))
        {
            Lens(s, Profile, ProfileBands, Dark, 32, 1f);
            // the belly's centre plate round the hatch, a shade darker, and the hatch's frame
            s.Tube(new Vector3(0, Belly + 0.02f, 0), new Vector3(0, Belly - 0.04f, 0), 1.72f, 1.62f, Dark.Darkened(0.2f), 24);
            // the cabin floor under the dome, and a raised collar the glass sits in
            s.Tube(new Vector3(0, DomeBase - 0.02f, 0), new Vector3(0, DomeBase + 0.06f, 0), DomeRadius + 0.18f, DomeRadius + 0.12f, Steel, 32);
        }
        // the two seats side by side, a little behind the middle, and the console ahead of them
        foreach (float x in new[] { -0.55f, 0.55f })
        {
            s.Box(new Vector3(x, DomeBase + 0.28f, -0.25f), new Vector3(0.55f, 0.18f, 0.55f), Seat);
            s.Box(new Vector3(x, DomeBase + 0.72f, -0.52f), new Vector3(0.55f, 0.75f, 0.12f), Seat, new Basis(Vector3.Right, -0.15f));
        }
        s.Box(new Vector3(0, DomeBase + 0.45f, 0.95f), new Vector3(1.7f, 0.5f, 0.35f), Dark, new Basis(Vector3.Right, 0.35f));
        // overhead control grips the pilots hold up (the seated pose has its hands raised)
        foreach (float x in new[] { -0.55f, 0.55f })
        {
            s.Tube(new Vector3(x, DomeBase + 1.62f, -0.22f), new Vector3(x, DomeBase + 1.42f, -0.22f), 0.03f, Steel, 5);
            s.Tube(new Vector3(x - 0.28f, DomeBase + 1.40f, -0.22f), new Vector3(x + 0.28f, DomeBase + 1.40f, -0.22f), 0.035f, Dark, 6);
        }
        s.Tube(new Vector3(-0.55f, DomeBase + 1.62f, -0.22f), new Vector3(0.55f, DomeBase + 1.62f, -0.22f), 0.03f, Steel, 5);
        // the dome glass: a hemisphere of panes (the glass surface)
        Dome(s, new Vector3(0, DomeBase + 0.04f, 0), DomeRadius, 16, 6, DomeGlass);
        // an antenna ball on the dome's crown
        s.Tube(new Vector3(0, DomeBase + DomeRadius, 0), new Vector3(0, DomeBase + DomeRadius + 0.35f, 0), 0.03f, Steel, 5);
        return s.Build();
    }

    /// <summary>A hemisphere of glass panes centred on <paramref name="centre"/>.</summary>
    private static void Dome(MeshScratch s, Vector3 centre, float radius, int around, int up, Color glass)
    {
        Span<Vector3> quad = stackalloc Vector3[4];
        for (int j = 0; j < up; j++)
        {
            float e0 = Mathf.Pi / 2 * j / up, e1 = Mathf.Pi / 2 * (j + 1) / up;
            for (int i = 0; i < around; i++)
            {
                float a0 = Mathf.Tau * i / around, a1 = Mathf.Tau * (i + 1) / around;
                Vector3 P(float a, float e) => centre + new Vector3(Mathf.Sin(a) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(a) * Mathf.Cos(e)) * radius;
                quad[0] = P(a0, e0); quad[1] = P(a1, e0); quad[2] = P(a1, e1); quad[3] = P(a0, e1);
                s.Pane(quad, glass);
            }
        }
    }

    /// <summary>One landing leg, authored at its hinge with the foot outward (+Z) and down: a strut, a knee and a pad.</summary>
    [Core.Showcase("Parts", "UFO landing leg")]
    public static ArrayMesh Leg()
    {
        var s = new MeshScratch();
        float drop = Belly - 0.08f, out_ = LegFootR - LegHingeR;
        var foot = new Vector3(0, -drop, out_);
        s.Box(Vector3.Zero, new Vector3(0.5f, 0.18f, 0.5f), Dark);
        s.Tube(Vector3.Zero, foot * 0.55f, 0.13f, 0.11f, Steel, 8);
        s.Tube(foot * 0.55f, foot + new Vector3(0, 0.12f, 0), 0.08f, 0.07f, Silver, 8);
        s.Tube(foot * 0.55f + new Vector3(0, 0.05f, 0), foot * 0.55f - new Vector3(0, 0.05f, 0), 0.17f, Dark, 8);
        s.Tube(foot + new Vector3(0, 0.1f, 0), foot, 0.36f, 0.4f, Dark, 10);
        return s.Build();
    }

    /// <summary>The ramp, authored at its hinge, running forward (+Z) flat: plates, side rails and a lip.</summary>
    [Core.Showcase("Parts", "UFO ramp")]
    public static ArrayMesh Ramp()
    {
        var s = new MeshScratch();
        float w = RampWidth * 0.5f;
        s.Box(new Vector3(0, -0.05f, RampLength * 0.5f), new Vector3(RampWidth, 0.1f, RampLength), Steel);
        // treads across, for a ramp people walk up
        for (int i = 1; i < 8; i++)
            s.Box(new Vector3(0, 0.005f, RampLength * i / 8f), new Vector3(RampWidth - 0.16f, 0.03f, 0.06f), Dark);
        foreach (float x in new[] { -w, w })
            s.Box(new Vector3(x, 0.02f, RampLength * 0.5f), new Vector3(0.08f, 0.16f, RampLength), Silver);
        return s.Build();
    }

    /// <summary>The ramp's two edge lights, unshaded.</summary>
    [Core.Showcase("Parts", "UFO ramp lights")]
    public static ArrayMesh RampLights()
    {
        var s = new MeshScratch();
        float w = RampWidth * 0.5f;
        foreach (float x in new[] { -w, w })
            s.Box(new Vector3(x, 0.11f, RampLength * 0.5f), new Vector3(0.05f, 0.03f, RampLength - 0.2f), Colors.White);
        return s.Build();
    }

    /// <summary>The open hatch's glowing inside, a bright panel set into the belly over the ramp.</summary>
    [Core.Showcase("Parts", "UFO hatch glow")]
    public static ArrayMesh HatchGlow()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, Belly - 0.06f, RampHingeZ + RampLength * 0.5f), new Vector3(RampWidth - 0.05f, 0.02f, RampLength - 0.1f), Colors.White);
        return s.Build();
    }

    /// <summary>The underside's glowing ring round the hatch.</summary>
    [Core.Showcase("Parts", "UFO underside glow")]
    public static ArrayMesh UnderGlow()
    {
        var s = new MeshScratch();
        s.Ring(new Vector3(0, Belly - 0.02f, 0), Vector3.Up, 2.0f, 3.1f, 0.04f, Colors.White, 32);
        return s.Build();
    }

    /// <summary>One rim lamp: a small faceted bulb.</summary>
    [Core.Showcase("Parts", "UFO rim lamp")]
    public static ArrayMesh Lamp()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, -0.15f, 0), new Vector3(0, 0.15f, 0), 0.22f, 0.22f, Colors.White, 8);
        return s.Build();
    }

    /// <summary>
    /// The tractor beam's cone, apex at the origin, hanging down <see cref="Player.TractorBeamMath.Reach"/>
    /// metres, open at both ends and seen from inside too (its shader is <see cref="UfoRig.BeamMaterial"/>).
    /// </summary>
    public static ArrayMesh BeamCone()
    {
        var s = new MeshScratch();
        using (s.Smoothing(true))
            s.Skirt(Vector3.Zero, Vector3.Down * Player.TractorBeamMath.Reach, Player.TractorBeamMath.ApexRadius,
                Player.TractorBeamMath.RadiusAt(Player.TractorBeamMath.Reach), Colors.White, 24);
        return s.Build();
    }

    // ---- the saucer, put together ------------------------------------------------------------

    /// <summary>
    /// The drawn saucer: <see cref="UfoRig"/> with the hull, the pilot (and a passenger) under the
    /// dome, the legs, the ramp, the lamps, the glow and the beam as named children.
    /// </summary>
    /// <param name="pilot">The figure in the left seat; null for an empty one (parked, or the model viewer's).</param>
    public static UfoRig Create(HumanPalette? pilot) => UfoRig.Create(pilot);

    // the saucer itself, ridden and parked, shows through Rideable's "Rides" set (RideKind.Ufo)
    [Core.Showcase("Aircraft", "UFO, flying (legs up, beam on, shortened)")]
    private static Node3D ShowcaseUfoFlying()
    {
        var rig = UfoRig.Create(HumanPalette.ForRider(5));
        rig.Show(new UfoRig.Look(Hum: 0.8f, Legs: 0f, Ramp: 0f, Beam: true, Lights: true), 0.3f);
        // the beam shortened to a fifth, or its 45 m would frame the saucer as a speck
        if (rig.FindChild("Beam", true, false) is Node3D beam) beam.Scale = new Vector3(0.6f, 0.2f, 0.6f);
        return rig;
    }

    // ---- the mothership ------------------------------------------------------------------------

    /// <summary>The mothership's scale over the saucer's profile: ~120 m across.</summary>
    public const float MothershipScale = 13f;

    /// <summary>
    /// The mothership's hull (#861): the saucer's lens at 120 m, a stepped crown with a tower on
    /// top, and the docking bay's dark mouth in its belly. Scenery and an event prop (#863).
    /// </summary>
    [Core.Showcase("Parts", "Mothership hull")]
    public static ArrayMesh MothershipHull()
    {
        var s = new MeshScratch();
        float k = MothershipScale;
        using (s.Smoothing(true))
        {
            Lens(s, Profile, ProfileBands, Dark, 64, k);
            // a stepped crown instead of the glass dome: decks, then a tower
            s.Tube(new Vector3(0, DomeBase * k - 1f, 0), new Vector3(0, DomeBase * k + 6f, 0), 26f, 21f, Silver.Darkened(0.05f), 48);
            s.Tube(new Vector3(0, DomeBase * k + 6f, 0), new Vector3(0, DomeBase * k + 12f, 0), 16f, 11f, Steel, 40);
            s.Tube(new Vector3(0, DomeBase * k + 12f, 0), new Vector3(0, DomeBase * k + 26f, 0), 5f, 2.5f, Silver, 24);
            // the docking bay: a dark well in the belly a saucer flies into
            s.Tube(new Vector3(0, Belly * k + 0.3f, 0), new Vector3(0, Belly * k - 0.5f, 0), 14f, 13f, Dark.Darkened(0.5f), 40);
            // fins round the rim, every 45°
            for (int i = 0; i < 8; i++)
            {
                float a = Mathf.Tau * (i + 0.5f) / 8;
                var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                s.Box(dir * (RimRadius * k - 1f) + Vector3.Up * (RimY * k), new Vector3(1.5f, 4f, 10f), Steel, new Basis(Vector3.Up, a));
            }
        }
        return s.Build();
    }

    /// <summary>The mothership's windows and lamps, one unshaded mesh: two rings of lit windows, the rim lamps, the bay's ring.</summary>
    [Core.Showcase("Parts", "Mothership lights")]
    public static ArrayMesh MothershipLights()
    {
        var s = new MeshScratch();
        float k = MothershipScale;
        // windows round the crown's lower deck and round the upper shell
        for (int i = 0; i < 48; i++)
        {
            float a = Mathf.Tau * i / 48;
            var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            s.Box(dir * 25.6f + Vector3.Up * (DomeBase * k + 2.5f), new Vector3(1.6f, 1.2f, 0.4f), new Color(1f, 0.92f, 0.6f), new Basis(Vector3.Up, a));
        }
        for (int i = 0; i < 64; i++)
        {
            float a = Mathf.Tau * i / 64;
            var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            s.Box(dir * (RimRadius * k + 0.6f) + Vector3.Up * (RimY * k), new Vector3(1.8f, 1.1f, 0.6f), LampColours[i % LampColours.Length], new Basis(Vector3.Up, a));
        }
        s.Ring(new Vector3(0, Belly * k - 0.2f, 0), Vector3.Up, 14.5f, 17f, 0.4f, GlowColour, 48);
        s.Ring(new Vector3(0, Belly * k + 1.4f, 0), Vector3.Up, 30f, 36f, 0.4f, GlowColour, 64);
        return s.Build();
    }

    /// <summary>The mothership as a node: hull, lights, a slowly turning ring of lamps (<see cref="MothershipRig"/>).</summary>
    [Core.Showcase("Aircraft", "Mothership")]
    public static Node3D Mothership() => MothershipRig.Create();
}
