using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The rounded bodies the lit styles draw (#760: the XP90 Yaris and the XW20 / XW30 Prius; the
/// rest of the roster is #763). Where PS1 stacks boxes, the body is lofted through stations along
/// the car: each side a thick wall whose section bulges at the waist, tucks under at the sill and
/// rolls over at the shoulder to where the side glass stands, its foot lifted round each wheel in a
/// circular arch, its corners rounded in plan; between the walls a crowned block over the nose and
/// over the tail; the doors are the same wall between their pillars, on their hinges. Smooth
/// normals (<see cref="MeshScratch.Loft"/>, averaged), so the cel light rolls over the panels.
///
/// <para>
/// The same seat, glass, doors' centres, mirrors and lamps as the PS1 body: only the skin
/// changes, and the hull is always measured from the PS1 mesh (<c>Car.ParkedBox</c>), so nothing a
/// player collides with depends on the style a client draws.
/// </para>
/// </summary>
public static partial class CarMeshBuilder
{
    /// <summary>The shapes that have a rounded body for the lit styles.</summary>
    internal static bool RoundedShape(BodyShape shape) => shape is BodyShape.TallHatch or BodyShape.Liftback;

    /// <summary>The rounded wall's thickness, m.</summary>
    private const float RoundSkin = 0.07f;
    /// <summary>How far the shoulder rolls in from the widest point: the side glass stands on it (the cabin's width, <c>cw</c>).</summary>
    private const float Shoulder = 0.1f;

    /// <summary>The side's section, foot to shoulder: (share of the wall's height, how far in from the widest).</summary>
    private static readonly (float T, float In)[] SideProfile =
    {
        (0f, 0.06f), (0.1f, 0.018f), (0.32f, 0f), (0.6f, 0.006f), (0.82f, 0.025f), (0.94f, 0.055f), (1f, Shoulder),
    };

    /// <summary>Half the body's width at <paramref name="z"/>: the corners rounded in plan, more at the nose.</summary>
    internal static float PlanHalf(Dims d, float z)
    {
        float hl = d.Length * 0.5f, hw = d.Width * 0.5f;
        float r = z > 0 ? 0.45f : 0.35f;
        float u = Mathf.Clamp((Mathf.Abs(z) - (hl - r)) / r, 0f, 1f);
        return hw - 0.1f * (1f - Mathf.Sqrt(1f - u * u));
    }

    /// <summary>A wheel arch's radius: the wheel's and a hand's clearance.</summary>
    private static float ArchRadius(Dims d) => d.WheelR + 0.07f;

    /// <summary>The foot of the side at z: the sill, or the arch over a wheel (round about the axle).</summary>
    private static float SideFoot(Dims d, float z)
    {
        float y = SillY0, r = ArchRadius(d);
        foreach (float ax in new[] { d.Wheelbase * 0.5f, -d.Wheelbase * 0.5f })
        {
            float dz = z - ax;
            if (Mathf.Abs(dz) < r) y = Mathf.Max(y, d.WheelR + Mathf.Sqrt(r * r - dz * dz));
        }
        return y;
    }

    /// <summary>
    /// The top of the side at z: down the bonnet's stations ahead of the screen, the belt along the
    /// cabin, up to the deck just behind the rear glass's foot and, unless a spoiler sits on it,
    /// rolled off over the last 12 cm.
    /// </summary>
    private static float SideTop(Dims d, float z, bool rollTail)
    {
        float hl = d.Length * 0.5f;
        if (z >= d.WsBase)
        {
            var st = NoseStations(d);
            return z <= st[1].Z
                ? Mathf.Lerp(st[0].Top, st[1].Top, (z - st[0].Z) / (st[1].Z - st[0].Z))
                : Mathf.Lerp(st[1].Top, st[2].Top, Mathf.Clamp((z - st[1].Z) / (st[2].Z - st[1].Z), 0f, 1f));
        }
        if (z > d.RgBase) return d.Belt;
        float y = Mathf.Lerp(d.Belt, d.Deck, Mathf.Clamp((d.RgBase - z) / 0.08f, 0f, 1f));
        return rollTail ? y - 0.05f * Mathf.Clamp((-hl + 0.12f - z) / 0.12f, 0f, 1f) : y;
    }

    /// <summary>One side's wall at z, as a ring: out round the section foot to shoulder, back down inside it.</summary>
    private static Vector3[] WallRing(Dims d, float z, float sx, bool rollTail)
    {
        float hw = PlanHalf(d, z), y0 = SideFoot(d, z), y1 = SideTop(d, z, rollTail);
        int n = SideProfile.Length;
        var ring = new Vector3[n * 2];
        for (int i = 0; i < n; i++)
        {
            var (t, inset) = SideProfile[i];
            float y = Mathf.Lerp(y0, y1, t), x = hw - inset;
            ring[i] = new Vector3(sx * x, y, z);
            ring[2 * n - 1 - i] = new Vector3(sx * (x - RoundSkin), y, z);
        }
        return ring;
    }

    /// <summary>The wall's bands: the lower body's colour low down, paint to the shoulder and over it, trim inside, dark under the foot.</summary>
    private static Color[] WallColours(Color paint, Color lower)
    {
        int n = SideProfile.Length;
        var colours = new Color[n * 2];
        for (int i = 0; i < n - 1; i++) colours[i] = SideProfile[i + 1].T <= 0.32f ? lower : paint;
        colours[n - 1] = paint;
        for (int i = n; i < 2 * n - 1; i++) colours[i] = Cabin;
        colours[2 * n - 1] = Trim;
        return colours;
    }

    /// <summary>
    /// The block between the walls at z (nose or tail): its sides and foot inside the walls and the
    /// wheel wells (dark), its top meeting the shoulders and crowned in the middle.
    /// </summary>
    private static Vector3[] BlockRing(Dims d, float z, bool rollTail)
    {
        float hw = PlanHalf(d, z), top = SideTop(d, z, rollTail);
        float edge = hw - Shoulder, side = hw - 0.09f;
        // over a wheel the block stops above it (the well); beyond the arches it comes down to the sill
        float foot = SideFoot(d, z) > SillY0 + 0.01f || NearArch(d, z) ? SillY1 : SillY0 + 0.02f;
        return new[]
        {
            new Vector3(-side, foot, z), new Vector3(side, foot, z), new Vector3(side, top - 0.06f, z),
            new Vector3(edge, top, z), new Vector3(edge * 0.5f, top + 0.012f, z), new Vector3(0, top + 0.016f, z),
            new Vector3(-edge * 0.5f, top + 0.012f, z), new Vector3(-edge, top, z), new Vector3(-side, top - 0.06f, z),
        };
    }

    private static bool NearArch(Dims d, float z) =>
        Mathf.Abs(z - d.Wheelbase * 0.5f) < ArchRadius(d) + 0.03f || Mathf.Abs(z + d.Wheelbase * 0.5f) < ArchRadius(d) + 0.03f;

    private static Color[] BlockColours(Color top) => new[] { Trim, Trim, top, top, top, top, top, top, Trim };

    /// <summary>
    /// Where the rounded body has a station: its ends and their rounding, the bonnet's stations, the
    /// screen's and rear glass's feet, round each arch (both sides of each lip), and the doors' edges.
    /// </summary>
    private static List<float> RoundStations(Dims d, IEnumerable<(float Z0, float Z1, bool Rear)> doors)
    {
        float hl = d.Length * 0.5f;
        var z = new List<float> { -hl, -hl + 0.03f, -hl + 0.08f, -hl + 0.16f, -hl + 0.26f, -hl + 0.36f,
            hl, hl - 0.04f, hl - 0.1f, hl - 0.18f, hl - 0.3f, hl - 0.45f, d.WsBase, d.RgBase, d.RgBase - 0.08f };
        float r = ArchRadius(d);
        foreach (float ax in new[] { d.Wheelbase * 0.5f, -d.Wheelbase * 0.5f })
        {
            z.Add(ax - r - 0.002f); z.Add(ax + r + 0.002f);
            for (int i = -6; i <= 6; i++) z.Add(ax + r * 0.998f * Mathf.Sin(i / 6f * Mathf.Pi * 0.5f));
        }
        foreach (var door in doors) { z.Add(door.Z0); z.Add(door.Z1); }
        z.RemoveAll(v => v < -hl || v > hl);
        z.Sort();
        // stations closer than a millimetre are one station (the arch's lip keeps its two)
        for (int i = z.Count - 1; i > 0; i--)
            if (z[i] - z[i - 1] < 0.001f) z.RemoveAt(i);
        return z;
    }

    /// <summary>The stations from <paramref name="from"/> to <paramref name="to"/>, both ends included.</summary>
    private static List<float> Between(List<float> stations, float from, float to)
    {
        var z = new List<float> { from };
        foreach (float v in stations)
            if (v > from + 0.0005f && v < to - 0.0005f) z.Add(v);
        z.Add(to);
        return z;
    }

    /// <summary>
    /// The rounded body into <paramref name="s"/> (smooth already): both walls where there is no
    /// door, the nose's and the tail's blocks, a narrow floor pan between the wheels, and a dark
    /// wheel tub over each wheel so the cabin does not look out through the arches.
    /// </summary>
    private static void RoundedBody(MeshScratch s, CarBody body, Dims d, (float Z0, float Z1, bool Rear)[] doors)
    {
        var paint = body.Paint;
        var lower = body.Lower ?? paint;
        float hl = d.Length * 0.5f;
        bool roll = body.Shape != BodyShape.Liftback;
        var stations = RoundStations(d, doors);
        var wallColours = WallColours(paint, lower);

        // the walls, front to back, skipping the doors' spans
        var spans = doors.Select(x => (x.Z0, x.Z1)).OrderBy(x => x.Z0).ToList();
        var open = new List<(float From, float To)>();
        float at = -hl;
        foreach (var (z0, z1) in spans)
        {
            if (z0 - at > 0.001f) open.Add((at, z0));
            at = Mathf.Max(at, z1);
        }
        if (hl - at > 0.001f) open.Add((at, hl));
        foreach (float sx in new[] { -1f, 1f })
            foreach (var (from, to) in open)
                s.Loft(Between(stations, from, to).Select(z => WallRing(d, z, sx, roll)).ToList(), wallColours, paint, averaged: true);

        // the nose and the tail between the walls
        s.Loft(Between(stations, d.WsBase - 0.02f, hl).Select(z => BlockRing(d, z, roll)).ToList(), BlockColours(body.Bonnet ?? paint), paint, averaged: true);
        s.Loft(Between(stations, -hl, d.RgBase + 0.02f).Select(z => BlockRing(d, z, roll)).ToList(), BlockColours(paint), paint, averaged: true);

        // under the cabin: the pan between the wheels, and a tub over each wheel
        float pan = d.Track * 0.5f - d.TyreW * 0.5f - 0.02f;
        s.Box(new Vector3(0, (SillY0 + FloorY) * 0.5f, (d.RgBase + d.WsBase) * 0.5f), new Vector3(pan * 2f, FloorY - SillY0, d.WsBase - d.RgBase), lower);
        float r = ArchRadius(d);
        foreach (float ax in new[] { d.Wheelbase * 0.5f, -d.Wheelbase * 0.5f })
            foreach (float sx in new[] { -1f, 1f })
            {
                // inside the wall's inner face and under the shoulder, where the skin rolls in
                float x0 = pan, x1 = PlanHalf(d, ax) - 0.09f, y0 = d.WheelR * 2f + 0.04f;
                float y1 = Mathf.Min(SideTop(d, ax, roll), d.Belt) - 0.15f;
                if (y1 - y0 > 0.02f)
                    s.Box(new Vector3(sx * (x0 + x1) * 0.5f, (y0 + y1) * 0.5f, ax), new Vector3(x1 - x0, y1 - y0, r * 2f), Trim);
            }
    }

    /// <summary>The roof as a rounded slab, crowned across, its edges rolled down onto the pillars.</summary>
    private static void RoundedRoof(MeshScratch top, Dims d, float cw, Color colour)
    {
        float hx = cw * 0.5f + 0.01f, y1 = d.Roof, y0 = d.Roof - 0.05f;
        Vector3[] Ring(float z) => new[]
        {
            new Vector3(-hx, y0, z), new Vector3(hx, y0, z), new Vector3(hx, y1 - 0.025f, z), new Vector3(hx - 0.06f, y1 - 0.004f, z),
            new Vector3(0, y1, z), new Vector3(-hx + 0.06f, y1 - 0.004f, z), new Vector3(-hx, y1 - 0.025f, z),
        };
        int n = 5;
        var rings = Enumerable.Range(0, n).Select(i => Ring(Mathf.Lerp(d.RgTop, d.WsTop, i / (n - 1f)))).ToList();
        top.Loft(rings, Enumerable.Repeat(colour, 7).ToArray(), colour, averaged: true);
    }

    /// <summary>
    /// A headlamp on the rounded nose: a thin strip lying on the body itself, from a narrow tail up
    /// the bonnet to the nose, across the bonnet's edge and down the rolled shoulder, a few
    /// millimetres proud (a plate standing off the curve cast a hard cel shadow under it).
    /// </summary>
    private static void RoundedLamp(MeshScratch head, Dims d, float sx, float back, float width)
    {
        float hl = d.Length * 0.5f;
        const int n = 7;
        var rings = new List<Vector3[]>(n);
        for (int k = 0; k < n; k++)
        {
            float f = k / (n - 1f), z = Mathf.Lerp(hl - back, hl - 0.015f, f);
            float hw = PlanHalf(d, z), top = SideTop(d, z, true), foot = SideFoot(d, z), edge = hw - Shoulder;
            // inboard on the bonnet (its crown), the bonnet's edge, down the shoulder: the teardrop's
            // inner edge comes out from the shoulder toward the nose
            float x0 = edge - width * Mathf.Lerp(0.3f, 0.75f, f);
            Vector3 P(float x, float y) => new(sx * x, y, z);
            var on = new[]
            {
                P(x0, top + 0.012f * Mathf.Clamp((edge - x0) / (edge * 0.5f), 0f, 1f)),
                P(edge, top),
                P(hw - 0.055f, Mathf.Lerp(foot, top, 0.94f)),
                P(hw - 0.025f, Mathf.Lerp(foot, top, 0.82f)),
            };
            var outward = new[]
            {
                Vector3.Up, new Vector3(sx * 0.45f, 0.9f, 0).Normalized(), new Vector3(sx * 0.8f, 0.6f, 0).Normalized(), new Vector3(sx, 0.2f, 0).Normalized(),
            };
            var ring = new Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                ring[i] = on[i] + outward[i] * 0.006f;
                ring[7 - i] = on[i] + outward[i] * 0.001f;
            }
            rings.Add(ring);
        }
        head.Loft(rings, Enumerable.Repeat(Head, 8).ToArray(), Head);
    }

    /// <summary>
    /// A tail lamp on the rounded tail, from <paramref name="y0"/> to <paramref name="y1"/>: a thin
    /// strip round the rear corner, <paramref name="faceIn"/> in along the tail's face and
    /// <paramref name="sideOn"/> forward along the side, following the corner's curve.
    /// </summary>
    private static void RoundedTailLamp(MeshScratch tail, Dims d, float sx, float y0, float y1, float faceIn, float sideOn, bool rollTail)
    {
        float hl = d.Length * 0.5f;
        var rings = new List<Vector3[]>();
        foreach (float y in new[] { y0, (y0 + y1) * 0.5f, y1 })
        {
            // round the corner in plan at this height: in on the face, the corner, then forward on the side
            var path = new List<Vector3>();
            float faceX = PlanHalf(d, -hl) - SideInset(d, -hl, y, rollTail);
            path.Add(new Vector3(sx * (faceX - faceIn), y, -hl));
            for (int i = 0; i <= 4; i++)
            {
                float z = -hl + sideOn * i / 4f;
                path.Add(new Vector3(sx * (PlanHalf(d, z) - SideInset(d, z, y, rollTail)), y, z));
            }
            // each point out along the path's normal in plan (from its neighbours)
            int n = path.Count;
            var ring = new Vector3[n * 2];
            for (int i = 0; i < n; i++)
            {
                var along = path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)];
                var outward = new Vector3(along.Z, 0, -along.X).Normalized() * sx;
                ring[i] = path[i] + outward * 0.006f;
                ring[2 * n - 1 - i] = path[i] + outward * 0.001f;
            }
            rings.Add(ring);
        }
        tail.Loft(rings, Enumerable.Repeat(Tail, rings[0].Length).ToArray(), Tail);
    }

    /// <summary>How far in from the widest the side is at height <paramref name="y"/>, at z (the section's profile).</summary>
    private static float SideInset(Dims d, float z, float y, bool rollTail)
    {
        float y0 = SideFoot(d, z), y1 = SideTop(d, z, rollTail);
        float t = Mathf.Clamp((y - y0) / Mathf.Max(y1 - y0, 0.01f), 0f, 1f);
        for (int i = 1; i < SideProfile.Length; i++)
            if (t <= SideProfile[i].T)
                return Mathf.Lerp(SideProfile[i - 1].In, SideProfile[i].In, (t - SideProfile[i - 1].T) / (SideProfile[i].T - SideProfile[i - 1].T));
        return Shoulder;
    }

    /// <summary>A rounded door: the wall between its pillars, a few millimetres in from each, built round its hinge.</summary>
    private static void RoundedDoorPanel(MeshScratch m, CarBody body, Dims d, (float Z0, float Z1, bool Rear) span, float sx, Vector3 hinge)
    {
        var stations = RoundStations(d, System.Array.Empty<(float, float, bool)>());
        var rings = Between(stations, span.Z0 + 0.004f, span.Z1 - 0.004f)
            .Select(z => WallRing(d, z, sx, true).Select(p => p - hinge).ToArray()).ToList();
        m.Loft(rings, WallColours(body.Paint, body.Lower ?? body.Paint), body.Paint, averaged: true);
    }
}
