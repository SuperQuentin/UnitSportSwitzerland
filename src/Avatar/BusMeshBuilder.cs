using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// Low-poly buses (#70): a city bus, the two halves of an articulated one with the bellows between
/// them, a high-deck coach. The walls are built as walls — a dark interior shows through the
/// openings — so the door leaves (on the right, Swiss side) swing out of real holes. The
/// destination display is a Label3D the rig sets. Authored facing +Z, origin on the ground under
/// the section's centre of mass; operator colours from <see cref="HeavyLook"/>.
/// </summary>
public static class BusMeshBuilder
{
    private static readonly Color Interior = new(0.1f, 0.1f, 0.11f);
    private static readonly Color Bellows = new(0.13f, 0.13f, 0.14f);
    private const float Wall = 0.08f;

    public static HeavyParts Build(HeavySpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        var look = spec.Look;
        float cg = Cg(s, load);
        float hw = s.Width * 0.5f;
        bool coach = spec.Class == HeavyClass.Coach;
        bool first = section == 0, last = section == spec.Sections.Length - 1;
        float offset = SectionFront(spec.Sections, section);
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float front = cg, rear = cg - s.Length;

        // heights: floor, top of the skirt, the window band, the roof
        float floor = coach ? 0.38f : 0.3f;
        float belt = coach ? 1.5f : 0.95f;
        float winLow = coach ? 1.6f : 1.05f;
        float winHigh = coach ? 3.3f : 2.6f;
        float roof = s.Height - (coach ? 0.1f : 0.17f);

        // the articulated rear half starts with its bellows
        float from = !first && s.Pivot == Coupling.BusJoint ? 0.9f : 0f;
        if (from > 0f)
            for (int i = 0; i < 6; i++)
                Along(m, cg, i * 0.15f, i * 0.15f + 0.12f, floor + 0.05f, roof - 0.05f, s.Width - (i % 2 == 0 ? 0.08f : 0.2f), Bellows);

        // this section's doors, in its own stations
        var doors = new List<(int Index, float At, float Width)>();
        for (int i = 0; i < look.Doors.Length; i++)
        {
            float at = look.Doors[i].At - offset;
            if (at > from && at < s.Length) doors.Add((i, at, look.Doors[i].Width));
        }
        var doorCuts = doors.Select(d => (d.At - d.Width * 0.5f, d.At + d.Width * 0.5f)).ToList();
        var archCuts = s.Axles.Select(a => (a.At - Tyre.Radius(a.Tyre) - 0.12f, a.At + Tyre.Radius(a.Tyre) + 0.12f)).ToList();
        float archTop = s.Axles.Max(a => 2f * Tyre.Radius(a.Tyre)) + 0.1f;

        // the dark inside, the floor and the roof
        // inside the end walls, not flush with them: coplanar faces z-fight to black at a distance
        Along(m, cg, from + 0.1f, s.Length - 0.1f, floor + 0.02f, roof - 0.02f, s.Width - 2f * Wall - 0.02f, Interior);
        Along(m, cg, from, s.Length, roof, s.Height, s.Width - 0.04f, look.Paint);
        // roof pods: air conditioning (and the engine's cooling on the rear half)
        if (!coach) Along(m, cg, Mathf.Max(from, s.Length * 0.3f), Mathf.Max(from, s.Length * 0.3f) + 2.6f, s.Height - 0.02f, s.Height + 0.14f, 1.9f, look.Paint);

        // the walls: the left whole but for the arches, the right cut at every door too
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * (hw - Wall * 0.5f);
            var lowCuts = side < 0 ? archCuts.Concat(doorCuts).ToList() : archCuts;
            var highCuts = side < 0 ? doorCuts : new List<(float, float)>();
            Cut(m, cg, from, s.Length, floor, archTop, x, lowCuts, look.Lower);
            Cut(m, cg, from, s.Length, archTop, belt, x, highCuts, coach ? look.Paint : look.Lower);
            Cut(m, cg, from, s.Length, belt, winLow, x, highCuts, look.Paint);
            Cut(m, cg, from, s.Length, winLow, winHigh, x, highCuts, look.Paint);
            Cut(m, cg, from, s.Length, winHigh, roof, x, highCuts, look.Accent);
            // the glass, proud of the wall; none in the last metre where the engine sits
            float glassTo = last && !coach ? s.Length - 1.2f : s.Length - 0.3f;
            Cut(m, cg, from + 0.25f, glassTo, winLow + 0.05f, winHigh - 0.05f, side * (hw + 0.005f), highCuts, Glass, 0.02f);
            if (coach)
                // the luggage bays' doors: a dark seam top and bottom
                Cut(m, cg, from + 2.5f, s.Length - 3f, belt - 0.06f, belt, side * (hw + 0.005f), archCuts, Trim, 0.02f);
        }

        if (first)
        {
            // the face: windscreen down to the bumper, the display above, lamps low at the corners
            float wsLow = coach ? 0.95f : 0.85f;
            float wsHigh = s.Height - (coach ? 0.42f : 0.42f);
            Along(m, cg, 0f, 0.06f, floor, s.Height, s.Width - 0.04f, look.Paint);
            m.Box(new Vector3(0, (wsLow + wsHigh) * 0.5f, front + 0.02f), new Vector3(s.Width - 0.2f, wsHigh - wsLow, 0.04f), Glass);
            float dispY = wsHigh + 0.16f;
            m.Box(new Vector3(0, dispY, front + 0.02f), new Vector3(s.Width - 0.5f, 0.24f, 0.04f), Trim);
            m.Box(new Vector3(0, (floor + wsLow) * 0.5f, front + 0.02f), new Vector3(s.Width - 0.1f, wsLow - floor, 0.04f), look.Lower);
            foreach (float sx in new[] { -1f, 1f })
            {
                Lamp(head, sx * (hw - 0.32f), floor + 0.35f, front + 0.05f, 0.4f, 0.16f, 0.04f, HeadLamp);
                Lamp(m, sx * (hw - 0.07f), floor + 0.35f, front + 0.05f, 0.1f, 0.14f, 0.04f, Amber);
                // mirrors out on their arms, a bus's long stalks
                m.Box(new Vector3(sx * (hw + 0.15f), s.Height - 0.55f, front + 0.25f), new Vector3(0.3f, 0.05f, 0.05f), Trim);
                m.Box(new Vector3(sx * (hw + 0.3f), s.Height - 0.85f, front + 0.3f), new Vector3(0.07f, 0.5f, 0.2f), Trim);
            }
        }
        else Along(m, cg, from, from + 0.06f, floor, s.Height, s.Width - 0.04f, look.Paint);

        if (last)
        {
            Along(m, cg, s.Length - 0.06f, s.Length, floor, s.Height, s.Width - 0.04f, look.Paint);
            // the rear: a window on the coach, the engine's grille on a city bus
            if (coach) m.Box(new Vector3(0, 2.4f, rear - 0.02f), new Vector3(s.Width - 0.4f, 1.1f, 0.04f), Glass);
            else m.Box(new Vector3(0, 0.95f, rear - 0.02f), new Vector3(1.6f, 0.7f, 0.04f), Trim);
            foreach (float sx in new[] { -1f, 1f })
            {
                Lamp(tail, sx * (hw - 0.12f), 1.25f, rear - 0.03f, 0.16f, 0.9f, 0.04f, TailLamp);
                Lamp(rev, sx * (hw - 0.35f), 0.7f, rear - 0.03f, 0.18f, 0.12f, 0.04f, White);
            }
        }
        else Along(m, cg, s.Length - 0.06f, s.Length, floor, s.Height, s.Width - 0.04f, look.Paint);

        // the door leaves: two per opening on a city bus, one on the coach, glass in a frame
        var leaves = new List<HeavyDoorLeaf>();
        float doorTop = coach ? 2.35f : winHigh + 0.05f;
        foreach (var (index, at, width) in doors)
        {
            int n = coach ? 1 : 2;
            for (int i = 0; i < n; i++)
            {
                float leafW = width / n;
                // hinged at the opening's outer edges, each leaf swinging out
                float hingeAt = i == 0 ? at - width * 0.5f : at + width * 0.5f;
                float centreAt = i == 0 ? hingeAt + leafW * 0.5f : hingeAt - leafW * 0.5f;
                var leaf = new MeshScratch();
                float lx = -(hw + 0.01f);
                leaf.Box(new Vector3(lx, (floor + doorTop) * 0.5f, cg - centreAt), new Vector3(0.05f, doorTop - floor, leafW - 0.03f), look.Paint);
                leaf.Box(new Vector3(lx - 0.02f, (floor + 0.35f + doorTop - 0.12f) * 0.5f, cg - centreAt), new Vector3(0.02f, doorTop - floor - 0.47f, leafW - 0.16f), Glass);
                var pivot = new Vector3(lx, 0f, cg - hingeAt);
                leaves.Add(new HeavyDoorLeaf(index, leaf.Build(pivot), new Vector3(-pivot.X, 0f, -pivot.Z), i == 0 ? 1.35f : -1.35f));
            }
        }

        var parts = new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), leaves.ToArray());
        if (first && look.Destinations.Length > 0)
        {
            float dispY = s.Height - 0.42f + 0.16f;
            parts = parts with { Display = (new Vector3(0f, dispY, -front - 0.05f), s.Width - 0.6f) };
        }
        return parts;
    }

    /// <summary>A wall panel at <paramref name="x"/> between two stations, with holes where <paramref name="cuts"/> are.</summary>
    private static void Cut(MeshScratch m, float cg, float fromAt, float toAt, float y0, float y1, float x, List<(float, float)> cuts, Color colour, float thickness = Wall)
    {
        var sorted = cuts.OrderBy(c => c.Item1).ToList();
        float at = fromAt;
        foreach (var (c0, c1) in sorted)
        {
            if (c1 <= at) continue;
            if (c0 > at) Along(m, cg, at, Mathf.Min(c0, toAt), y0, y1, thickness, colour, x);
            at = Mathf.Max(at, c1);
            if (at >= toAt) return;
        }
        if (at < toAt) Along(m, cg, at, toAt, y0, y1, thickness, colour, x);
    }
}
