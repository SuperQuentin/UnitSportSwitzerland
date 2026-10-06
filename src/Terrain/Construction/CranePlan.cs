using Godot;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A crane's parts as boxes (#610): what stands still in the site frame (its base and lattice
/// mast), and what slews, in the frame of the pivot at the mast's top: y up, the jib along −Z
/// (where a yaw of 0 faces) and the counter-jib along +Z. A top-slewing flat-top crane; a
/// self-erecting one is the same, smaller, with no cab.
/// </summary>
public static class CranePlans
{
    /// <summary>The jib's underside over the pivot, m: what the trolley runs under.</summary>
    public const float JibBottom = 0.6f;

    private static float Scale(CraneSpot c) => c.Kind == CraneKind.Tower ? 1f : 0.62f;

    /// <summary>The mast's top over the ground at its foot: the hook's highest point plus the jib.</summary>
    public static float MastHeight(CraneSpot c) => c.HookHeight + 1.2f + JibBottom;

    private static void Box(List<ShellBox> to, float x0, float y0, float z0, float x1, float y1, float z1, ShellPart part, bool solid) =>
        to.Add(new ShellBox(new Vector3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1)),
            new Vector3(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1)), part, solid));

    /// <summary>The base and the lattice mast, in the site frame, on the ground at <paramref name="ground"/>.</summary>
    /// <param name="at">The mast's centre in the site frame (x, z).</param>
    public static List<ShellBox> Mast(CraneSpot c, Vector2 at, float ground)
    {
        var boxes = new List<ShellBox>();
        float k = Scale(c), s = 1.6f * k, half = s / 2, top = ground + MastHeight(c);
        float baseSide = c.Kind == CraneKind.Tower ? ConstructionSites.TowerBase : ConstructionSites.SelfErectingBase;
        float b = baseSide / 2;
        // the cross base on its ballast blocks
        float foot = ground + (c.Kind == CraneKind.Tower ? 0.8f : 0.5f);
        Box(boxes, at.X - b, ground - 0.1f, at.Y - b, at.X + b, foot, at.Y + b, ShellPart.Concrete, true);
        // the four chords, and the bracing every 2 m, as a lattice reads from the street
        float chord = 0.14f * k;
        foreach (var (dx, dz) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            Box(boxes, at.X + dx * half - chord / 2, foot, at.Y + dz * half - chord / 2,
                at.X + dx * half + chord / 2, top, at.Y + dz * half + chord / 2, ShellPart.CraneYellow, false);
        float rung = 0.07f * k;
        for (float y = foot + 1f; y < top - 0.5f; y += 2f * k)
        {
            Box(boxes, at.X - half, y, at.Y - half - rung / 2, at.X + half, y + rung, at.Y - half + rung / 2, ShellPart.CraneYellow, false);
            Box(boxes, at.X - half, y, at.Y + half - rung / 2, at.X + half, y + rung, at.Y + half + rung / 2, ShellPart.CraneYellow, false);
            Box(boxes, at.X - half - rung / 2, y, at.Y - half, at.X - half + rung / 2, y + rung, at.Y + half, ShellPart.CraneYellow, false);
            Box(boxes, at.X + half - rung / 2, y, at.Y - half, at.X + half + rung / 2, y + rung, at.Y + half, ShellPart.CraneYellow, false);
        }
        // what a body or a helicopter meets: the mast as one solid box (drawn as its lattice)
        Box(boxes, at.X - half, foot, at.Y - half, at.X + half, top, at.Y + half, ShellPart.Invisible, true);
        return boxes;
    }

    /// <summary>Everything that slews, in the pivot's frame: slewing unit, cab, jib, counter-jib and its ballast.</summary>
    public static List<ShellBox> Jib(CraneSpot c)
    {
        var boxes = new List<ShellBox>();
        float k = Scale(c), j = c.JibLength, counter = Math.Max(5f, 0.3f * j);
        float w = 0.7f * k, h = 1.4f * k, bottom = JibBottom * k, chord = 0.15f * k;
        // the slewing unit on the mast's top
        Box(boxes, -1.0f * k, -0.2f, -1.0f * k, 1.0f * k, bottom, 1.0f * k, ShellPart.CraneYellow, false);
        if (c.Kind == CraneKind.Tower)
        {
            // the cab, beside the jib's root, windows on three sides
            Box(boxes, 1.1f, -0.6f, -1.5f, 2.7f, 1.8f, 0.6f, ShellPart.Cab, false);
            Box(boxes, 1.08f, 0.5f, -1.52f, 2.72f, 1.6f, -0.9f, ShellPart.Window, false);
            Box(boxes, 2.68f, 0.5f, -1.4f, 2.72f, 1.6f, 0.4f, ShellPart.Window, false);
        }
        // the jib: two bottom chords, a top chord, posts and cross bars, a lattice from the ground
        Box(boxes, -w - chord / 2, bottom, -j, -w + chord / 2, bottom + chord, -1f, ShellPart.CraneYellow, false);
        Box(boxes, w - chord / 2, bottom, -j, w + chord / 2, bottom + chord, -1f, ShellPart.CraneYellow, false);
        Box(boxes, -chord / 2, bottom + h - chord, -j, chord / 2, bottom + h, -1f, ShellPart.CraneYellow, false);
        for (float z = -1f; z > -j; z -= 2.4f * k)
        {
            Box(boxes, -w - 0.03f, bottom, z - 0.04f, -w + 0.03f, bottom + h - chord, z + 0.04f, ShellPart.CraneYellow, false);
            Box(boxes, w - 0.03f, bottom, z - 0.04f, w + 0.03f, bottom + h - chord, z + 0.04f, ShellPart.CraneYellow, false);
            Box(boxes, -w, bottom + h - chord - 0.06f, z - 0.04f, w, bottom + h - chord, z + 0.04f, ShellPart.CraneYellow, false);
        }
        // the counter-jib: chords, a walkway, and the ballast at its end
        Box(boxes, -w - chord / 2, bottom, 1f, -w + chord / 2, bottom + chord, counter, ShellPart.CraneYellow, false);
        Box(boxes, w - chord / 2, bottom, 1f, w + chord / 2, bottom + chord, counter, ShellPart.CraneYellow, false);
        Box(boxes, -w, bottom + chord, 1f, w, bottom + chord + 0.05f, counter, ShellPart.Deck, false);
        Box(boxes, -1.1f * k, bottom - 1.6f * k, counter - 2.6f * k, 1.1f * k, bottom + 1.0f * k, counter - 0.3f, ShellPart.Concrete, false);
        return boxes;
    }

    /// <summary>The trolley, its top at the pivot's frame origin of its own node.</summary>
    public static List<ShellBox> Trolley(CraneSpot c)
    {
        var boxes = new List<ShellBox>();
        float k = Scale(c);
        Box(boxes, -0.6f * k, -0.35f, -0.6f * k, 0.6f * k, 0f, 0.6f * k, ShellPart.CraneYellow, false);
        return boxes;
    }

    /// <summary>The hook block, its top at its node's origin.</summary>
    public static List<ShellBox> Hook()
    {
        var boxes = new List<ShellBox>();
        Box(boxes, -0.25f, -0.7f, -0.2f, 0.25f, 0f, 0.2f, ShellPart.HookBlock, false);
        Box(boxes, -0.06f, -0.95f, -0.06f, 0.06f, -0.7f, 0.06f, ShellPart.Rope, false);
        return boxes;
    }

    /// <summary>The ropes from the trolley to the hook, one metre long: the node scales them to the drop.</summary>
    public static List<ShellBox> Rope()
    {
        var boxes = new List<ShellBox>();
        Box(boxes, -0.17f, -1f, -0.02f, -0.13f, 0f, 0.02f, ShellPart.Rope, false);
        Box(boxes, 0.13f, -1f, -0.02f, 0.17f, 0f, 0.02f, ShellPart.Rope, false);
        return boxes;
    }

    /// <summary>What hangs on the hook, under it: a concrete skip, or a bundle of formwork panels.</summary>
    public static List<ShellBox> Load(int kind)
    {
        var boxes = new List<ShellBox>();
        if (kind == 0)
        {
            Box(boxes, -0.65f, -1.35f, -0.65f, 0.65f, -0.15f, 0.65f, ShellPart.Concrete, false);
            Box(boxes, -0.7f, -0.35f, -0.7f, 0.7f, -0.15f, 0.7f, ShellPart.Skip, false);
        }
        else
            Box(boxes, -1.35f, -1.15f, -0.6f, 1.35f, -0.25f, 0.6f, ShellPart.Formwork, false);
        // the slings up to the hook
        Box(boxes, -0.03f, -0.25f, -0.03f, 0.03f, 0f, 0.03f, ShellPart.Rope, false);
        return boxes;
    }

    /// <summary>The red obstacle lamps, in the pivot's frame: the jib's tip, the counter-jib's end, the top.</summary>
    public static Vector3[] Lamps(CraneSpot c)
    {
        float k = Scale(c), counter = Math.Max(5f, 0.3f * c.JibLength), top = (JibBottom + 1.4f) * k + 0.15f;
        return new[] { new Vector3(0, top, -c.JibLength), new Vector3(0, top, counter), new Vector3(0, top + 0.1f, 0) };
    }

    /// <summary>The jib and counter-jib as one box, in the pivot's frame: what a helicopter hits.</summary>
    public static (Vector3 Center, Vector3 Size) JibBounds(CraneSpot c)
    {
        float k = Scale(c), counter = Math.Max(5f, 0.3f * c.JibLength), h = 1.4f * k + JibBottom * k;
        return (new Vector3(0, h / 2, (counter - c.JibLength) / 2), new Vector3(1.6f * k, h, c.JibLength + counter));
    }
}
