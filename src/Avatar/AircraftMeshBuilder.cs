using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The flying things: a wingsuit, two canopies, a helicopter and a light plane, built from the
/// same two primitives as the riders (<see cref="MeshScratch"/>: tubes and boxes), one surface
/// each. Authored facing +Z like every avatar mesh — <see cref="MeshScratch.Build"/> turns them
/// to face -Z on the way out, and the <see cref="Flip"/> helper does the same for the positions
/// of separately spinning parts (rotors, propeller) so they land where they were drawn.
///
/// <para>
/// Proportions are real ones, rounded: a paraglider spans ~10 m, a light helicopter's rotor
/// ~10 m, a high-wing four-seater ~11 m. At this fidelity silhouette is everything, and a plane
/// the wrong size next to a real Swiss village is the first thing that would look off.
/// </para>
/// </summary>
public static class AircraftMeshBuilder
{
    /// <summary>Authoring (+Z forward) to node space (-Z forward).</summary>
    public static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    // ---- wingsuit ----------------------------------------------------------------------

    /// <summary>
    /// The spread figure with its three wings: one under each arm to the hip, one between the
    /// legs. Upright, like the pose; the flyer lays it prone.
    /// </summary>
    public static ArrayMesh Wingsuit(HumanPalette palette)
    {
        var s = new MeshScratch();
        HumanMeshBuilder.Append(s, palette, HumanPose.Spread, includeLegs: true, helmet: true);
        var wing = palette.Jersey.Darkened(0.15f);
        // arm wings: from the arm down to the hip, thin enough to read as fabric
        s.Box(new Vector3(-0.43f, 1.17f, -0.03f), new Vector3(0.52f, 0.48f, 0.02f), wing);
        s.Box(new Vector3(0.43f, 1.17f, -0.03f), new Vector3(0.52f, 0.48f, 0.02f), wing);
        // tail wing between the legs
        s.Box(new Vector3(0, 0.55f, -0.03f), new Vector3(0.46f, 0.70f, 0.02f), wing);
        return s.Build();
    }

    // ---- canopies ------------------------------------------------------------------------

    /// <summary>
    /// A rider in a harness under a curved wing of cells, with lines to it. The paraglider is a
    /// long, flat, high-aspect wing well above the pilot; the parachute a short, deep square one.
    /// </summary>
    public static ArrayMesh Canopy(HumanPalette palette, bool paraglider)
    {
        var s = new MeshScratch();
        HumanMeshBuilder.Append(s, palette, HumanPose.Hanging, includeLegs: true, helmet: true);

        int cells = paraglider ? 11 : 7;
        float radius = paraglider ? 6.5f : 4.5f;         // curvature of the arc, m
        float half = Mathf.DegToRad(paraglider ? 50f : 38f);
        float top = paraglider ? 8.5f : 6.0f;            // canopy crown above the feet, m
        float chord = paraglider ? 2.4f : 2.6f;
        float centreY = top - radius;
        var a = paraglider ? new Color(0.95f, 0.45f, 0.10f) : new Color(0.20f, 0.35f, 0.85f);
        var b = paraglider ? new Color(0.98f, 0.85f, 0.20f) : new Color(0.92f, 0.92f, 0.95f);

        float cellAngle = 2 * half / cells;
        for (int i = 0; i < cells; i++)
        {
            float t = -half + cellAngle * (i + 0.5f);
            var centre = new Vector3(radius * Mathf.Sin(t), centreY + radius * Mathf.Cos(t), 0.2f);
            float width = 2 * radius * Mathf.Sin(cellAngle / 2) + 0.02f;   // no gaps between cells
            var basis = new Basis(Vector3.Back, -t);
            s.Box(centre, new Vector3(width, 0.28f, chord), (i & 1) == 0 ? a : b, basis);

            // a line from each shoulder's side to every other cell; the rest would be clutter
            if (i % 2 == 0 || i == cells - 1)
            {
                var anchor = new Vector3(Mathf.Sign(centre.X) * 0.2f, 1.9f, 0.05f);
                var edge = centre - basis.Y * 0.14f;
                s.Tube(anchor, edge, 0.012f, new Color(0.25f, 0.25f, 0.25f), 3);
            }
        }
        return s.Build();
    }

    // ---- helicopter ----------------------------------------------------------------------

    public static readonly Vector3 RotorHub = new(0, 2.45f, 0.1f);

    /// <summary>A light single-rotor helicopter: cabin, bubble, boom, fin, skids. Rotor separate.</summary>
    public static ArrayMesh Helicopter(Color paint)
    {
        var s = new MeshScratch();
        var dark = new Color(0.12f, 0.13f, 0.15f);
        var glass = new Color(0.45f, 0.62f, 0.75f);

        s.Box(new Vector3(0, 1.30f, 0.1f), new Vector3(1.7f, 1.5f, 2.8f), paint);          // cabin
        s.Box(new Vector3(0, 1.35f, 1.75f), new Vector3(1.5f, 1.2f, 0.7f), glass);         // bubble
        s.Box(new Vector3(0, 2.05f, -0.2f), new Vector3(1.0f, 0.5f, 1.8f), paint);         // engine hump
        s.Tube(new Vector3(0, 1.65f, -1.2f), new Vector3(0, 1.95f, -6.1f), 0.30f, 0.13f, paint, 8);
        s.Box(new Vector3(0, 2.45f, -6.1f), new Vector3(0.08f, 1.2f, 0.7f), paint);        // fin
        s.Box(new Vector3(0, 1.95f, -5.6f), new Vector3(1.6f, 0.06f, 0.45f), paint);       // stabiliser
        s.Tube(RotorHub - new Vector3(0, 0.3f, 0), RotorHub, 0.08f, dark, 6);               // mast

        // skids and their struts
        foreach (float x in new[] { -0.85f, 0.85f })
        {
            s.Tube(new Vector3(x, 0.08f, -1.4f), new Vector3(x, 0.08f, 1.7f), 0.06f, dark, 5);
            s.Tube(new Vector3(x, 0.08f, 1.7f), new Vector3(x, 0.25f, 2.0f), 0.06f, dark, 5);
            s.Tube(new Vector3(x, 0.08f, 0.9f), new Vector3(x * 0.6f, 0.62f, 0.8f), 0.045f, dark, 5);
            s.Tube(new Vector3(x, 0.08f, -0.8f), new Vector3(x * 0.6f, 0.62f, -0.7f), 0.045f, dark, 5);
        }
        return s.Build();
    }

    /// <summary>Two crossed blades, 10.4 m tip to tip, centred on the hub. Spun as its own node.</summary>
    public static ArrayMesh Rotor()
    {
        var s = new MeshScratch();
        var blade = new Color(0.10f, 0.10f, 0.11f);
        s.Box(Vector3.Zero, new Vector3(10.4f, 0.05f, 0.30f), blade);
        s.Box(Vector3.Zero, new Vector3(0.30f, 0.05f, 10.4f), blade);
        s.Box(Vector3.Zero, new Vector3(0.35f, 0.18f, 0.35f), blade);
        return s.Build();
    }

    // ---- plane ---------------------------------------------------------------------------

    public static readonly Vector3 PropHub = new(0, 1.45f, 3.25f);

    /// <summary>A high-wing four-seater on fixed tricycle gear. Propeller separate.</summary>
    public static ArrayMesh Plane(Color paint, Color trim)
    {
        var s = new MeshScratch();
        var dark = new Color(0.12f, 0.12f, 0.13f);
        var glass = new Color(0.45f, 0.62f, 0.75f);

        s.Tube(new Vector3(0, 1.45f, 3.1f), new Vector3(0, 1.50f, 0.2f), 0.48f, 0.62f, paint, 8);
        s.Tube(new Vector3(0, 1.50f, 0.2f), new Vector3(0, 1.85f, -4.4f), 0.62f, 0.14f, paint, 8);
        s.Box(new Vector3(0, 1.95f, 1.2f), new Vector3(1.05f, 0.5f, 1.6f), glass);          // cabin windows
        s.Box(new Vector3(0, 2.25f, 0.9f), new Vector3(11.0f, 0.14f, 1.55f), paint);        // wing
        s.Box(new Vector3(-4.8f, 2.25f, 0.9f), new Vector3(1.4f, 0.15f, 1.56f), trim);      // wing tips
        s.Box(new Vector3(4.8f, 2.25f, 0.9f), new Vector3(1.4f, 0.15f, 1.56f), trim);
        s.Tube(new Vector3(-0.5f, 1.2f, 1.0f), new Vector3(-2.4f, 2.18f, 1.0f), 0.05f, dark, 4);   // struts
        s.Tube(new Vector3(0.5f, 1.2f, 1.0f), new Vector3(2.4f, 2.18f, 1.0f), 0.05f, dark, 4);
        s.Box(new Vector3(0, 1.85f, -4.2f), new Vector3(3.4f, 0.08f, 0.9f), paint);         // stabiliser
        s.Box(new Vector3(0, 2.45f, -4.35f), new Vector3(0.08f, 1.25f, 1.0f), trim);        // fin
        s.Tube(new Vector3(0, 1.45f, 3.1f), PropHub, 0.22f, 0.12f, dark, 6);                // spinner

        // tricycle gear
        foreach (var (leg, wheel) in new[]
        {
            (new Vector3(-1.2f, 1.1f, 0.7f), new Vector3(-1.3f, 0.3f, 0.7f)),
            (new Vector3(1.2f, 1.1f, 0.7f), new Vector3(1.3f, 0.3f, 0.7f)),
            (new Vector3(0, 1.1f, 2.5f), new Vector3(0, 0.3f, 2.55f)),
        })
        {
            s.Tube(leg, wheel, 0.05f, dark, 4);
            s.Box(wheel, new Vector3(0.18f, 0.6f, 0.6f), dark);
        }
        return s.Build();
    }

    /// <summary>A two-blade propeller, 1.9 m, spinning about the forward axis.</summary>
    public static ArrayMesh Propeller()
    {
        var s = new MeshScratch();
        s.Box(Vector3.Zero, new Vector3(0.14f, 1.9f, 0.05f), new Color(0.15f, 0.15f, 0.16f));
        return s.Build();
    }
}
