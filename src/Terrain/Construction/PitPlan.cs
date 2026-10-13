using Godot;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A footbridge over a shell's trench (#619), in the site frame: from <see cref="From"/> on the pit's
/// edge to <see cref="To"/> on the wall of the wing it lands on, <see cref="Normal"/> the wall's
/// outward normal (the way back out).
/// </summary>
public readonly record struct PitBridge(Vector2 From, Vector2 To, Vector2 Normal);

/// <summary>
/// What stands in and round a site's pit (#619), in the site frame: the sheet piles lining it, the
/// walers along them, the guard rail round the top, the footbridges over a shell's trench. The
/// ground itself (the hole, the floor, the ramp) is the terrain's: <see cref="PitTerrain"/>.
/// </summary>
public sealed class PitPlan
{
    public List<ShellBox> Boxes { get; } = new();
    /// <summary>The floor's height: the gravel the slab is cast on, a step under the slab's top.</summary>
    public float Floor { get; set; }
    /// <summary>The ramp's top edge, outer then inner corner, on the pit's edge at the ground's height.</summary>
    public Vector3 TopOuter { get; set; }
    public Vector3 TopInner { get; set; }
    /// <summary>The way down the lane, a unit vector, and how far the slope runs before the floor.</summary>
    public Vector2 Down { get; set; }
    public float RampLength { get; set; }
    public float Grade { get; set; }
    /// <summary>Whether the pit has its ramp: not where a slope took it too deep for its lane (0 <see cref="RampLength"/>).</summary>
    public bool HasRamp => RampLength > 0;
    public List<PitBridge> Bridges { get; } = new();
    /// <summary>Walkable slopes, collision only: from the ground onto each footbridge's deck.</summary>
    public List<ShellRamp> Ramps { get; } = new();
    /// <summary>
    /// The way a machine goes in, in the site frame: on the ramp's top (the whole machine in the lane),
    /// down to its foot, along the floor beside the slab. What <c>--pitcheck</c> drives an excavator
    /// along, then backs it up the lane to <see cref="Exit"/>: a 9.5 m machine does not turn in a 4.6 m lane.
    /// </summary>
    public List<Vector3> Route { get; } = new();
    /// <summary>Just out of the ramp's mouth, on the ground beside the pit.</summary>
    public Vector3 Exit { get; set; }
}

/// <summary>
/// Builds a site's <see cref="PitPlan"/> (#619): a pure function of the site, its floor and the
/// ground round it, so every peer lines the same pit.
/// </summary>
public static class PitPlans
{
    /// <summary>The ramp's grade: a site ramp for tracked machines and lorries, 1 in 4.</summary>
    public const float RampGrade = 0.25f;
    /// <summary>The steepest a ramp is made where the lane is short for the pit's depth.</summary>
    public const float MaxGrade = 0.36f;
    /// <summary>Flat lane left at the ramp's foot, m: where a machine comes off it onto the floor.</summary>
    public const float FootRoom = 3f;
    /// <summary>The gravel under the slab: this far below the slab's top, so a machine rolls onto it.</summary>
    public const float Blinding = 0.02f;
    /// <summary>A pit's wall is at least this high on its lowest side: the floor goes lower on a slope.</summary>
    public const float MinWall = 1.2f;
    /// <summary>Sheet piles stand this far out of the ground, a pile this wide, the profile this deep.</summary>
    public const float PileProud = 0.3f, PileWidth = 0.6f, PileDepth = 0.16f;
    public const float BridgeWidth = 1.2f, RailHeight = 1.05f, PostSpacing = 2.5f;
    /// <summary>The check's start, m down the ramp from its mouth: a 9.5 m machine's middle with all of it in the lane.</summary>
    public const float EntryOnRamp = 5.5f;

    /// <summary>
    /// The floor a pit is dug to: the slab of the lowest basement, under the ground floor's slab by a
    /// storey per basement, and at least <see cref="MinWall"/> under the lowest ground round the pit.
    /// </summary>
    public static float FloorOf(ConstructionSite site, float groundFloor, float rimLow) =>
        site.Pit is not { } pit ? groundFloor
            : Math.Min(groundFloor - pit.Basements * site.StoreyHeight, rimLow - MinWall);

    /// <param name="wings">The building's wings in the site frame (the plan box when null).</param>
    /// <param name="floor">The lowest slab's top (<see cref="FloorOf"/>).</param>
    /// <param name="groundFloor">The ground floor's slab top, where a footbridge lands.</param>
    /// <param name="ground">The drawn ground's height at a point of the site frame.</param>
    public static PitPlan? Plan(ConstructionSite site, IReadOnlyList<Rect2D>? wings, float floor, float groundFloor,
        Func<float, float, float> ground)
    {
        if (site.Pit is not { } pit) return null;
        var plan = new PitPlan { Floor = floor - Blinding };
        void Box(float x0, float y0, float z0, float x1, float y1, float z1, ShellPart part, bool solid) =>
            plan.Boxes.Add(new ShellBox(new Vector3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1)),
                new Vector3(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1)), part, solid));
        var a = pit.Area;

        // ---- the ramp: down the lane from its top end, at the grade, then flat to the far end ----
        var (outer, inner, down) = RampTop(pit);
        float hOuter = ground(outer.X, outer.Y), hInner = ground(inner.X, inner.Y);
        float drop = Math.Max(hOuter, hInner) - plan.Floor;
        // 1 in 4 with room at its foot; steeper where the lane is short, up to MaxGrade, giving up the
        // room at the foot before the grade
        plan.Grade = Math.Max(RampGrade, drop / Math.Max(1f, pit.LaneLength - FootRoom));
        if (plan.Grade > MaxGrade) plan.Grade = Math.Max(MaxGrade, drop / Math.Max(1f, pit.LaneLength - 1f));
        plan.RampLength = drop / plan.Grade;
        // a pit a slope took too deep for its lane: no ramp at all, rather than a wall to drive at
        if (plan.Grade > MaxGrade + 1e-4f) plan.RampLength = 0;
        plan.TopOuter = new Vector3(outer.X, hOuter, outer.Y);
        plan.TopInner = new Vector3(inner.X, hInner, inner.Y);
        plan.Down = down;
        // the opening the ramp comes up through: no pile and no rail across it
        var mouth = (outer + inner) / 2;
        float mouthHalf = pit.LaneWidth / 2;
        bool InMouth(Vector2 p) => plan.HasRamp && (p - mouth).Dot(down) > -0.5f && (p - mouth).Dot(down) < 0.5f
            && Math.Abs((p - mouth).Dot(new Vector2(-down.Y, down.X))) < mouthHalf + 0.05f;

        // ---- footbridges over a shell's trench: the way onto its ground floor --------------------
        if (site.Phase == SitePhase.Shell)
            plan.Bridges.AddRange(Bridges(site, wings));

        // ---- the edges: sheet piles, walers, the guard rail on top --------------------------------
        float depthMin = float.MaxValue;
        var edges = new (Vector2 A, Vector2 B, Vector2 Out)[]
        {
            (new(a.X0, a.Z0), new(a.X1, a.Z0), new(0, -1)), (new(a.X1, a.Z0), new(a.X1, a.Z1), new(1, 0)),
            (new(a.X1, a.Z1), new(a.X0, a.Z1), new(0, 1)), (new(a.X0, a.Z1), new(a.X0, a.Z0), new(-1, 0)),
        };
        foreach (var (ea, eb, n) in edges)
        {
            float len = ea.DistanceTo(eb);
            var t = (eb - ea) / len;
            int piles = Math.Max(1, (int)MathF.Round(len / PileWidth));
            for (int k = 0; k < piles; k++)
            {
                var p0 = ea + t * (len * k / piles);
                var p1 = ea + t * (len * (k + 1) / piles);
                var mid = (p0 + p1) / 2;
                if (InMouth(mid)) continue;
                float g = ground(mid.X + n.X * 0.2f, mid.Y + n.Y * 0.2f);
                depthMin = Math.Min(depthMin, g - plan.Floor);
                // the profile: pans facing in and out by turns, every one in front of the dug face
                float o0 = k % 2 == 0 ? -PileDepth + 0.02f : -0.02f, o1 = o0 + PileDepth + 0.04f;
                // cut down to the ground under a footbridge, so its ramp meets no lip
                bool bridged = plan.Bridges.Any(b => b.From.DistanceTo(mid) < BridgeWidth / 2 + 0.4f);
                Box(p0.X + n.X * o0, plan.Floor - 0.6f, p0.Y + n.Y * o0, p1.X + n.X * o1, bridged ? g - 0.03f : g + PileProud, p1.Y + n.Y * o1,
                    ShellPart.SheetPile, true);
            }
        }
        if (depthMin == float.MaxValue) depthMin = 0;
        // walers: one beam along the piles in a shallow pit, two in a deep one
        int walers = depthMin > 4.5f ? 2 : depthMin > 1.8f ? 1 : 0;
        foreach (var (ea, eb, n) in edges)
            for (int w = 1; w <= walers; w++)
            {
                float y = plan.Floor + depthMin * w / (walers + 1);
                var (s0, s1) = Unmouthed(ea, eb, InMouth);
                foreach (var (q0, q1) in new[] { (ea, s0), (s1, eb) })
                {
                    if (q0.DistanceTo(q1) < 0.5f) continue;
                    Box(q0.X - n.X * 0.16f, y - 0.15f, q0.Y - n.Y * 0.16f, q1.X - n.X * 0.42f, y + 0.15f, q1.Y - n.Y * 0.42f,
                        ShellPart.Frame, false);
                }
            }
        // the guard rail on the piles' line, a post every 2.5 m; open at the ramp's mouth and at the bridges
        bool Open(Vector2 p) => InMouth(p) || plan.Bridges.Any(b => b.From.DistanceTo(p) < BridgeWidth / 2 + 0.1f);
        foreach (var (ea, eb, n) in edges)
        {
            float len = ea.DistanceTo(eb);
            var t = (eb - ea) / len;
            int bays = Math.Max(1, (int)MathF.Ceiling(len / PostSpacing));
            for (int k = 0; k < bays; k++)
            {
                var p0 = ea + t * (len * k / bays) + n * 0.35f;
                var p1 = ea + t * (len * (k + 1) / bays) + n * 0.35f;
                // a bay touching an opening is left out whole: a post standing in the mouth would stop a machine
                if (Open(p0 - n * 0.35f) || Open(p1 - n * 0.35f) || Open((p0 + p1) / 2 - n * 0.35f)) continue;
                float g0 = ground(p0.X, p0.Y), g1 = ground(p1.X, p1.Y);
                float lo = Math.Min(g0, g1), hi = Math.Max(g0, g1);
                Box(p0.X - 0.03f, g0, p0.Y - 0.03f, p0.X + 0.03f, g0 + RailHeight + 0.05f, p0.Y + 0.03f, ShellPart.Tube, false);
                Box(p1.X - 0.03f, g1, p1.Y - 0.03f, p1.X + 0.03f, g1 + RailHeight + 0.05f, p1.Y + 0.03f, ShellPart.Tube, false);
                Box(p0.X - 0.025f, hi + 0.5f, p0.Y - 0.025f, p1.X + 0.025f, hi + 0.55f, p1.Y + 0.025f, ShellPart.Barrier, false);
                Box(p0.X - 0.025f, hi + RailHeight - 0.05f, p0.Y - 0.025f, p1.X + 0.025f, hi + RailHeight, p1.Y + 0.025f, ShellPart.Barrier, false);
                // solid as a sheet, so nobody walks over the edge (the scaffolding's rule)
                Box(p0.X - 0.02f, lo, p0.Y - 0.02f, p1.X + 0.02f, hi + RailHeight, p1.Y + 0.02f, ShellPart.Invisible, true);
            }
        }

        // ---- the bridges: a deck from the ground past the piles to the ground floor's slab -------
        foreach (var b in plan.Bridges)
        {
            var across = new Vector2(-b.Normal.Y, b.Normal.X) * (BridgeWidth / 2);
            // the deck runs level at the slab's top and rests on the ground past the piles, where a
            // slope may stand the ground a step over or under it
            var far = b.From + b.Normal * 0.8f;
            float top = groundFloor, g = ground(far.X, far.Y);
            var c0 = b.To - across;
            var c1 = b.From + across;
            Box(c0.X, top - 0.08f, c0.Y, c1.X, top, c1.Y, ShellPart.Deck, true);
            // its end on the ground: planks drawn level, walked as a slope from the ground's height
            Box(b.From.X - across.X, top - 0.08f, b.From.Y - across.Y, far.X + across.X, top, far.Y + across.Y, ShellPart.Deck, false);
            Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
            plan.Ramps.Add(new ShellRamp(P(far - across, g), P(far + across, g), P(b.From + across, top), P(b.From - across, top)));
            foreach (float side in new[] { -1f, 1f })
            {
                var s0 = b.To + across * side * 1.04f;
                var s1 = b.From + across * side * 1.04f;
                Box(s0.X - 0.02f, top + 0.5f, s0.Y - 0.02f, s1.X + 0.02f, top + 0.54f, s1.Y + 0.02f, ShellPart.Tube, false);
                Box(s0.X - 0.02f, top + RailHeight - 0.04f, s0.Y - 0.02f, s1.X + 0.02f, top + RailHeight, s1.Y + 0.02f, ShellPart.Tube, false);
                Box(s0.X - 0.02f, top, s0.Y - 0.02f, s1.X + 0.02f, top + RailHeight, s1.Y + 0.02f, ShellPart.Invisible, true);
            }
        }

        // ---- the way in, for the check: on the ramp's top, down it, along the floor beside the slab
        if (!plan.HasRamp) return plan;
        float mouthY = (hOuter + hInner) / 2;
        var onTop = mouth + down * EntryOnRamp;
        plan.Route.Add(new Vector3(onTop.X, mouthY - plan.Grade * EntryOnRamp, onTop.Y));
        var foot = mouth + down * plan.RampLength;
        plan.Route.Add(new Vector3(foot.X, plan.Floor, foot.Y));
        var end = mouth + down * Math.Min(pit.LaneLength - 2f, plan.RampLength + 2.5f);
        plan.Route.Add(new Vector3(end.X, plan.Floor, end.Y));
        // and out: backed up the lane, through the mouth onto the ground beside the pit
        var exit = mouth - down * 1f;
        plan.Exit = new Vector3(exit.X, ground(exit.X, exit.Y), exit.Y);
        return plan;
    }

    /// <summary>The ramp's top edge (outer corner on the pit's front edge, inner on the lane's inner edge) and the way down the lane.</summary>
    public static (Vector2 Outer, Vector2 Inner, Vector2 Down) RampTop(SitePit pit)
    {
        var l = pit.Lane;
        if (pit.LaneAlongX)
        {
            float x = pit.TopEnd > 0 ? l.X1 : l.X0;
            // the outer edge is the one on the pit's own edge
            float zo = Math.Abs(l.Z0 - pit.Area.Z0) < 0.01f ? l.Z0 : l.Z1, zi = zo == l.Z0 ? l.Z1 : l.Z0;
            return (new Vector2(x, zo), new Vector2(x, zi), new Vector2(-pit.TopEnd, 0));
        }
        else
        {
            float z = pit.TopEnd > 0 ? l.Z1 : l.Z0;
            float xo = Math.Abs(l.X0 - pit.Area.X0) < 0.01f ? l.X0 : l.X1, xi = xo == l.X0 ? l.X1 : l.X0;
            return (new Vector2(xo, z), new Vector2(xi, z), new Vector2(0, -pit.TopEnd));
        }
    }

    /// <summary>
    /// A shell's footbridges over its trench: one at the middle of each side that is not the street
    /// side (where the ramp's lane is), from the pit's edge to the first wing it meets; none where the
    /// line from there misses every wing (a U's courtyard).
    /// </summary>
    public static List<PitBridge> Bridges(ConstructionSite site, IReadOnlyList<Rect2D>? wings)
    {
        var list = new List<PitBridge>();
        if (site.Pit is not { } pit) return list;
        float hw = site.Box.Width / 2, hd = site.Box.Depth / 2;
        var ws = wings is { Count: > 0 } ? wings : new[] { new Rect2D(-hw, -hd, hw, hd) };
        foreach (var n in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
        {
            if (n.Dot(pit.Side) > 0.5f) continue;
            // from the pit's edge on this side, inward along the side's middle, to the first wall
            float edge = n.X > 0 ? pit.Area.X1 : n.X < 0 ? -pit.Area.X0 : n.Y > 0 ? pit.Area.Z1 : -pit.Area.Z0;
            float best = float.MinValue;
            Rect2D hit = default;
            foreach (var w in ws)
            {
                // the wing's wall facing this way, if the side's middle line crosses it
                bool crosses = n.X != 0 ? w.Z0 + 1f < 0 && w.Z1 - 1f > 0 : w.X0 + 1f < 0 && w.X1 - 1f > 0;
                if (!crosses) continue;
                float wall = n.X > 0 ? w.X1 : n.X < 0 ? -w.X0 : n.Y > 0 ? w.Z1 : -w.Z0;
                if (wall > best) { best = wall; hit = w; }
            }
            if (best == float.MinValue || edge - best < 0.5f) continue;
            // it lands in the middle of a bay of that wall, where the shell puts a doorway: the bay
            // nearest the side's middle (the shell's columns divide each wing's sides evenly)
            float lo = n.X != 0 ? hit.Z0 : hit.X0, hi = n.X != 0 ? hit.Z1 : hit.X1;
            int bays = Math.Max(1, (int)MathF.Round((hi - lo) / ShellPlans.Span));
            float at = Enumerable.Range(0, bays).Select(k => lo + (hi - lo) * (k + 0.5f) / bays).OrderBy(Math.Abs).First();
            var along = n.X != 0 ? new Vector2(0, 1) : new Vector2(1, 0);
            list.Add(new PitBridge(n * edge + along * at, n * best + along * at, n));
        }
        return list;
    }

    /// <summary>Where an edge leaves the ramp's mouth: the two ends of the part of A..B in it (both B when it misses).</summary>
    private static (Vector2, Vector2) Unmouthed(Vector2 a, Vector2 b, Func<Vector2, bool> inMouth)
    {
        float len = a.DistanceTo(b);
        var t = (b - a) / len;
        float s0 = -1, s1 = -1;
        for (float s = 0; s <= len; s += 0.1f)
            if (inMouth(a + t * s))
            {
                if (s0 < 0) s0 = s;
                s1 = s;
            }
        return s0 < 0 ? (b, b) : (a + t * Math.Max(0, s0 - 0.1f), a + t * Math.Min(len, s1 + 0.1f));
    }
}
