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

    /// <summary>Half the mast's side, m.</summary>
    public static float MastHalf(CraneSpot c) => 0.8f * Scale(c);

    /// <summary>The top of the base, where the mast stands, over the ground, m.</summary>
    public static float FootHeight(CraneSpot c) => c.Kind == CraneKind.Tower ? 0.8f : 0.5f;

    /// <summary>A climber on the ladder stands this far out from the mast's centre, m (#618).</summary>
    public static float LadderOut(CraneSpot c) => MastHalf(c) + 0.45f;

    /// <summary>How high up the ladder, over the base, the climb ends at the cab's door, m (#618).</summary>
    public static float LadderTop(CraneSpot c) => MastHeight(c) - FootHeight(c) - 2.4f;

    /// <summary>Where the operator stands in the cab, in the pivot's frame (#618): the jib ahead, the windows round.</summary>
    public static readonly Vector3 CabSeat = new(1.9f, -0.6f, -0.6f);

    /// <summary>
    /// The mast's side the ladder goes up, in the site frame (#618): the one facing the site's
    /// front, where the yard and the gate are, snapped to the mast's sides (x or z).
    /// </summary>
    /// <param name="front">The site's front in its own frame (along AxisU, along AxisV).</param>
    public static Vector2 LadderFace(Vector2 front) =>
        Math.Abs(front.X) >= Math.Abs(front.Y) ? new Vector2(Math.Sign(front.X) is 0 ? 1 : Math.Sign(front.X), 0)
        : new Vector2(0, Math.Sign(front.Y));

    /// <summary>The side the radio remote's box hangs on, a quarter turn round from the ladder's (#618).</summary>
    public static Vector2 RemoteFace(Vector2 ladder) => new(-ladder.Y, ladder.X);

    /// <summary>The remote's box over the ground, m: at hand height.</summary>
    public const float RemoteHeight = 1.1f;

    /// <summary>The base and the lattice mast, in the site frame, on the ground at <paramref name="ground"/>.</summary>
    /// <param name="at">The mast's centre in the site frame (x, z).</param>
    /// <param name="face">The ladder's side (<see cref="LadderFace"/>): a tower crane's ladder, and every crane's remote box a side round.</param>
    public static List<ShellBox> Mast(CraneSpot c, Vector2 at, float ground, Vector2 face)
    {
        var boxes = new List<ShellBox>();
        float k = Scale(c), s = 1.6f * k, half = s / 2, top = ground + MastHeight(c);
        float baseSide = c.Kind == CraneKind.Tower ? ConstructionSites.TowerBase : ConstructionSites.SelfErectingBase;
        float b = baseSide / 2;
        // the cross base on its ballast blocks
        float foot = ground + FootHeight(c);
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

        // #618: the ladder up the face towards the yard (a tower crane's: a self-erecting one has no
        // cab), two stiles and a rung every 0.35 m, from the base to the cab's door
        var side = new Vector2(-face.Y, face.X);
        Vector2 On(float outward, float across) => at + face * outward + side * across;
        void Upright(Vector2 p, float w, float y0, float y1, ShellPart part) =>
            Box(boxes, p.X - w, y0, p.Y - w, p.X + w, y1, p.Y + w, part, false);
        if (c.Kind == CraneKind.Tower)
        {
            float ladderTop = foot + LadderTop(c) + 1.2f;
            Upright(On(half + 0.12f, -0.25f), 0.03f, foot, ladderTop, ShellPart.Tube);
            Upright(On(half + 0.12f, 0.25f), 0.03f, foot, ladderTop, ShellPart.Tube);
            for (float y = foot + 0.3f; y < ladderTop; y += 0.35f)
            {
                var a = On(half + 0.12f, -0.25f);
                var z = On(half + 0.12f, 0.25f);
                Box(boxes, Math.Min(a.X, z.X) - 0.02f, y, Math.Min(a.Y, z.Y) - 0.02f, Math.Max(a.X, z.X) + 0.02f, y + 0.04f,
                    Math.Max(a.Y, z.Y) + 0.02f, ShellPart.Tube, false);
            }
        }
        // and the radio remote's box a side round, at hand height (every crane: a remote works any)
        var remote = RemoteFace(face);
        var box = at + remote * (half + 0.12f);
        Box(boxes, box.X - 0.18f, ground + RemoteHeight - 0.22f, box.Y - 0.18f, box.X + 0.18f, ground + RemoteHeight + 0.22f, box.Y + 0.18f,
            ShellPart.Cab, false);
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
