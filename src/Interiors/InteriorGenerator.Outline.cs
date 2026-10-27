using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Interiors that follow the building's real outline (#1290), angled facades included, close
/// enough rather than exact.
///
/// <para>
/// A plan is made of rectangles, as ever; afterwards each room may take a polygon
/// (<see cref="RoomPlan.Shape"/>) and keeps its rectangle as its core. Two fits, chosen by how the
/// plan was made:
/// </para>
/// <list type="bullet">
/// <item><b>Extend</b>: a plan inside the outline (a house on its biggest front rectangle, flats
/// on their wings) grows each facade room out to it, from every point of its facade wall
/// straight out until it leaves the building or meets another room. An angled facade comes out as
/// one straight angled wall; a corner both walls grow into follows the outline round. A street
/// door's stretch stays where it is (a recessed entrance). A growth deeper than
/// <see cref="DeepAt"/> off a room others open off is a room of its own (the arm of an L).</item>
/// <item><b>Clip</b>: a plan on the whole box (a hall, a barn, a works) has each room cut by the
/// outline.</item>
/// </list>
/// <para>
/// Rooms holding a stair, a shaft, a lift or a ramp keep their rectangle. Anything that does not
/// validate as well as the plan did before goes back to the rectangles.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>A growth deeper than this, off a room others open off, is a room of its own.</summary>
    public const float DeepAt = 4.0f;
    /// <summary>Less growth than this is none: the wall stays.</summary>
    private const float MinReach = 0.15f;
    private const float MaxReach = 60f;
    /// <summary>How far past a wall a point is tested as "outside" it.</summary>
    private const float Step = 0.02f;

    /// <summary>Off only for <c>--outlinesurvey --no-outlines</c>: plans as before #1290, to compare.</summary>
    internal static bool FollowOutlines = true;

    /// <summary>The building's roof on the outline's grid in the plan frame, or null when it is its box.</summary>
    internal static bool[,]? OutlineOf(Footprint fp, Building b) =>
        PlanOutline.FineGrid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);

    /// <summary>The outline's rings, a stub in to each of the plan's ways in from the box's side.</summary>
    internal static List<Vector2[]> RingsFor(InteriorLayout l, bool[,] grid) =>
        PlanOutline.Rings(grid, l.Width, l.Depth, l.AllEntrances().Select((e, i) =>
        (
            Side: e.InZ > 0.5f ? Side.Front : e.InZ < -0.5f ? Side.Back : e.InX > 0.5f ? Side.Left : Side.Right,
            At: Math.Abs(e.InZ) > 0.5f ? e.X : e.Z,
            e.Width,
            Main: i == 0,
            // only a doorway on the box's side needs a way in to the building
            OnBox: e.Level == 0 && (Math.Abs(Math.Abs(e.Z) - l.Depth / 2) < 0.05f || Math.Abs(Math.Abs(e.X) - l.Width / 2) < 0.05f)
        )).Where(d => d.OnBox).Select(d => (d.Side, d.At, d.Width, d.Main)));

    // ---- a cored plan on the biggest front rectangle --------------------------------------------

    /// <summary>
    /// A plan (<paramref name="plan"/>: a house's or an office's <see cref="TryCored"/>, a barn's
    /// <see cref="SingleRoom"/>) made on the biggest rectangle of the outline that stands behind the
    /// main door, then moved into the building's frame and grown out to the outline later: an
    /// L-shaped house no longer has rooms in its yard. Its other doors are fitted there too. False
    /// (the plan untouched) when no such rectangle is worth it.
    /// </summary>
    private static bool TryOnCore(InteriorLayout l, Footprint fp, Building b, Func<bool> plan)
    {
        float margin = l.EntryWidth / 2 + 0.35f;
        if (PlanOutline.FrontCore(b, fp.Center, fp.AxisU, fp.Width, fp.Depth, l.EntryX - margin, l.EntryX + margin, 4.5f) is not { } core)
            return false;
        float cw = core.X1 - core.X0, cd = core.Z1 - core.Z0;
        // the box itself, or nearly: the plain plan, clipped later if need be
        if (cw * cd >= PlanOutline.Boxy * fp.Width * fp.Depth || cw * cd < 25f) return false;
        float cx = (core.X0 + core.X1) / 2, cz = (core.Z0 + core.Z1) / 2;
        var (w, d, x) = (l.Width, l.Depth, l.EntryX);
        (l.Width, l.Depth, l.EntryX) = (cw, cd, x - cx);
        bool ok = plan();
        // the ways in written out, the main one too: its doorway is the core's front, which may
        // stand back from the box's (a recessed entrance in a bent front)
        if (ok) Entrances(l, fp with { Center = fp.Center + fp.AxisU * cx + fp.AxisV * cz, Width = cw, Depth = cd });
        (l.Width, l.Depth, l.EntryX) = (w, d, x);
        if (!ok)
        {
            l.Floors.Clear();
            l.Entrances.Clear();
            l.InnerDoors.Clear();
            l.Lifts.Clear();
            l.Below = 0;
            return false;
        }
        Shift(l, cx, cz);
        return true;
    }

    /// <summary>Everything of a plan moved by (dx, dz) in its own frame.</summary>
    private static void Shift(InteriorLayout l, float dx, float dz)
    {
        static float Along(Side s, float dx, float dz) => s is Side.Front or Side.Back ? dx : dz;
        void Rect(RectPlan r) { r.X0 += dx; r.X1 += dx; r.Z0 += dz; r.Z1 += dz; }
        foreach (var floor in l.Floors)
        {
            foreach (var r in floor.Rooms)
            {
                r.X0 += dx; r.X1 += dx; r.Z0 += dz; r.Z1 += dz;
                if (r.Shape != null) r.SetShape(r.Corners().Select(p => p + new Vector2(dx, dz)).ToArray());
                foreach (var o in r.Openings) if (o.Edge == null) o.Center += Along(o.Side, dx, dz);
            }
            floor.Holes.ForEach(Rect);
            floor.Rails.ForEach(Rect);
            floor.Guards.ForEach(Rect);
            foreach (var g in floor.Landings) { g.X0 += dx; g.X1 += dx; g.Z0 += dz; g.Z1 += dz; }
            foreach (var fl in floor.AllFlights())
            {
                float across = fl.AlongX ? dz : dx, run = fl.AlongX ? dx : dz;
                fl.X0 += across; fl.X1 += across; fl.ZBottom += run; fl.ZTop += run;
            }
        }
        foreach (var lift in l.Lifts)
        {
            lift.X0 += dx; lift.X1 += dx; lift.Z0 += dz; lift.Z1 += dz;
            lift.DoorCenter += Along(lift.DoorSide, dx, dz);
        }
        foreach (var d in l.InnerDoors) d.Center += Along(d.Side, dx, dz);
        foreach (var e in l.Entrances) { e.X += dx; e.Z += dz; }
        foreach (var p in l.Furniture) { p.X += dx; p.Z += dz; }
        l.EntryX += dx;
    }

    // ---- fitting a finished plan ----------------------------------------------------------------

    /// <summary>
    /// The plan's rooms fitted to the outline: extended out to it when the plan lies inside it, cut
    /// by it when the plan is the box. Kept only if it validates as well as before.
    /// </summary>
    internal static void FitOutline(InteriorLayout l, bool[,] grid)
    {
        OutlineFailure = null;
        if (l.Floors.Count == 0) return;
        var rings = RingsFor(l, grid);
        if (rings.Count == 0) return;
        var before = InteriorValidator.Validate(l);
        // what the fit may change, kept whole to go back to
        string saved = l.ToJson();
        bool inside = l.Floors.All(fl => fl.Rooms.All(r => RectInside(rings, r.X0, r.Z0, r.X1, r.Z1, 0.3f)));
        bool ok = inside ? Extend(l, rings) : Clip(l, rings);
        if (ok)
        {
            var after = InteriorValidator.Validate(l);
            if (after.Count > before.Count) ok = Unfit($"{(inside ? "grown" : "cut")}, it does not validate: {string.Join("; ", after.Except(before).Take(3))}");
            else if (l.Floors.SelectMany(fl => fl.Rooms).FirstOrDefault(r => !Drawable(r)) is { } bad) ok = Unfit($"the {bad.Type}'s shape does not triangulate");
        }
        if (ok) return;
        var back = InteriorLayout.FromJson(saved)!;
        l.Floors = back.Floors;
        l.InnerDoors = back.InnerDoors;
        l.Entrances = back.Entrances;
    }

    /// <summary>
    /// Why the last fit on this thread went back to the rectangles, or null if it did not (for
    /// checks: <c>--shapedcheck</c> prints it).
    /// </summary>
    [ThreadStatic] internal static string? OutlineFailure;

    private static bool Unfit(string why)
    {
        OutlineFailure = why;
        return false;
    }

    /// <summary>Whether a rectangle, shrunk by <paramref name="by"/>, lies inside the rings.</summary>
    private static bool RectInside(List<Vector2[]> rings, float x0, float z0, float x1, float z1, float by)
    {
        x0 += by; z0 += by; x1 -= by; z1 -= by;
        if (x1 <= x0 || z1 <= z0) return true;
        foreach (var q in new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1), new Vector2((x0 + x1) / 2, (z0 + z1) / 2) })
            if (!RoomShapes.Contains(rings, q)) return false;
        // a notch of the outline reaching into it between the corners
        foreach (var r in rings)
            foreach (var v in r)
                if (v.X > x0 && v.X < x1 && v.Y > z0 && v.Y < z1) return false;
        return true;
    }

    /// <summary>Whether a room may take a shape: no stair, shaft, lift, ramp or shelter in it.</summary>
    private static bool Shapeable(InteriorLayout l, int f, RoomPlan r)
    {
        if (r.Type is RoomType.Stairwell or RoomType.Elevator or RoomType.Ramp or RoomType.Shelter or RoomType.Vault) return false;
        var rect = new RectPlan(r.X0, r.Z0, r.X1, r.Z1);
        var floor = l.Floors[f];
        if (floor.Holes.Any(h => h.Overlaps(rect)) || floor.AllFlights().Any(fl => fl.Area().Overlaps(rect))
            || floor.Landings.Any(g => rect.Overlaps(new RectPlan(g.X0, g.Z0, g.X1, g.Z1)))) return false;
        int top = f + r.Span;
        if (top < l.Floors.Count && l.Floors[top].Holes.Any(h => h.Overlaps(rect))) return false;
        return !l.Lifts.Any(lift => rect.Overlaps(new RectPlan(lift.X0, lift.Z0, lift.X1, lift.Z1)));
    }

    /// <summary>A shape that triangulates into its own area: simple, so its floor is whole.</summary>
    private static bool Drawable(RoomPlan r)
    {
        if (r.Shape == null) return true;
        var c = r.Corners();
        float area = RoomShapes.Area(c);
        if (area <= 0) return false;
        var tris = RoomShapes.Triangulate(c);
        float sum = 0;
        for (int i = 0; i + 2 < tris.Count; i += 3)
            sum += Math.Abs(RoomShapes.Area(new[] { c[tris[i]], c[tris[i + 1]], c[tris[i + 2]] }));
        return Math.Abs(sum - area) < 0.02f * area + 0.01f;
    }

    // ---- extend ------------------------------------------------------------------------------

    /// <summary>One wall of a rectangle, walked counter-clockwise: from its start, the room on the left.</summary>
    private readonly record struct WallLine(Side Side, Vector2 Origin, Vector2 Along, Vector2 Out, float Length)
    {
        public Vector2 At(float u, float d = 0) => Origin + Along * u + Out * d;
        /// <summary>The opening coordinate (<see cref="OpeningPlan.Center"/>) of a point u along it.</summary>
        public float Coord(float u) => Side is Side.Front or Side.Back ? At(u).X : At(u).Y;
        public float U(float coord) => Side switch
        {
            Side.Front => coord - Origin.X,
            Side.Right => coord - Origin.Y,
            Side.Back => Origin.X - coord,
            _ => Origin.Y - coord,
        };
    }

    private static WallLine[] Lines(RoomPlan r) =>
    [
        new(Side.Front, new(r.X0, r.Z0), new(1, 0), new(0, -1), r.Width),
        new(Side.Right, new(r.X1, r.Z0), new(0, 1), new(1, 0), r.Depth),
        new(Side.Back, new(r.X1, r.Z1), new(-1, 0), new(0, 1), r.Width),
        new(Side.Left, new(r.X0, r.Z1), new(0, -1), new(-1, 0), r.Depth),
    ];

    /// <summary>A stretch of a wall and how far the room grows out over it, at each end; and what it met there.</summary>
    private readonly record struct Reach(float U0, float D0, float U1, float D1, int Ring, int Edge);

    private static bool Extend(InteriorLayout l, List<Vector2[]> rings)
    {
        var claims = l.Floors.Select(_ => new List<Vector2[]>()).ToList();
        var fresh = new HashSet<RoomPlan>();
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var floor = l.Floors[f];
            int count = floor.Rooms.Count;
            // the planned rooms, then the arms' rooms they made: those grow too, but make no arms
            for (int i = 0; i < floor.Rooms.Count; i++)
            {
                var r = floor.Rooms[i];
                // a room with a stair keeps its rectangle, but may still have an arm's rooms off it
                bool shapeable = Shapeable(l, f, r);
                bool arms = i < count;
                if (!shapeable && !(arms && Splits(l, r) && r.Type is not (RoomType.Elevator or RoomType.Ramp))) continue;
                if (Grow(l, f, i, rings, claims[f], arms, fresh, shapeable) is not { } shape) continue;
                r.SetShape(shape);
                for (int g = f; g < Math.Min(l.Floors.Count, f + r.Span); g++) claims[g].Add(shape);
            }
        }
        EdgeWindows(l, rings, fresh);
        return true;
    }

    /// <summary>The other rooms' rectangles on a floor (with those rising into it from below), as rings.</summary>
    private static List<Vector2[]> Others(InteriorLayout l, int f, RoomPlan self)
    {
        var o = new List<Vector2[]>();
        for (int g = 0; g <= f; g++)
            foreach (var q in l.Floors[g].Rooms)
                if (q != self && g + q.Span > f)
                    o.Add([new(q.X0, q.Z0), new(q.X1, q.Z0), new(q.X1, q.Z1), new(q.X0, q.Z1)]);
        return o;
    }

    /// <summary>
    /// Room <paramref name="index"/> of floor <paramref name="f"/> grown out to the outline: its
    /// polygon, or null if it does not grow. A deep stretch off a room others open off becomes a
    /// room of its own, appended to the floor, with a door into this one.
    /// </summary>
    /// <param name="shapeable">False for a room with a stair: it only gets its arms' rooms, never a shape.</param>
    private static Vector2[]? Grow(InteriorLayout l, int f, int index, List<Vector2[]> rings, List<Vector2[]> claims, bool arms,
        HashSet<RoomPlan> fresh, bool shapeable = true)
    {
        var floor = l.Floors[f];
        var r = floor.Rooms[index];
        var allOthers = Others(l, f, r);
        allOthers.AddRange(claims);
        // everything a ray from a wall can meet: the outline first, so its rings keep their indices
        var allWalls = new List<IReadOnlyList<Vector2>>(rings);
        allWalls.AddRange(allOthers);

        var lines = Lines(r);
        var reaches = new List<Reach>[4];
        for (int s = 0; s < 4; s++)
        {
            var w = lines[s];
            // a way in or out of the building keeps its wall: a recessed entrance
            var keep = r.Openings
                .Where(o => o.Edge == null && o.Side == w.Side && (o.Kind == OpeningKind.Entry || o.Kind is OpeningKind.Door or OpeningKind.Arch && o.Other < 0))
                .Select(o => (Lo: Math.Min(w.U(o.Center - o.Width / 2), w.U(o.Center + o.Width / 2)) - 0.3f,
                              Hi: Math.Max(w.U(o.Center - o.Width / 2), w.U(o.Center + o.Width / 2)) + 0.3f))
                .ToList();
            var cuts = new List<float> { 0, w.Length };
            foreach (var (lo, hi) in keep) { cuts.Add(lo); cuts.Add(hi); }
            // only what stands in front of this wall, out to the box's side, can be met (a big block has
            // hundreds of rooms a floor); the far ones are left empty so every ring keeps its index
            float reach = w.Out.X > 0.5f ? l.Width / 2 - w.Origin.X : w.Out.X < -0.5f ? w.Origin.X + l.Width / 2
                : w.Out.Y > 0.5f ? l.Depth / 2 - w.Origin.Y : w.Origin.Y + l.Depth / 2;
            var sweep = new[] { w.At(0), w.At(w.Length), w.At(0, reach), w.At(w.Length, reach) };
            float sx0 = sweep.Min(p => p.X) - 0.05f, sx1 = sweep.Max(p => p.X) + 0.05f, sz0 = sweep.Min(p => p.Y) - 0.05f, sz1 = sweep.Max(p => p.Y) + 0.05f;
            bool Near(IReadOnlyList<Vector2> ring) =>
                ring.Max(p => p.X) >= sx0 && ring.Min(p => p.X) <= sx1 && ring.Max(p => p.Y) >= sz0 && ring.Min(p => p.Y) <= sz1;
            var walls = allWalls.Select((ring, i) => i < rings.Count || Near(ring) ? ring : Array.Empty<Vector2>()).ToList();
            var others = allOthers.Where(Near).ToList();
            bool Blocked(Vector2 p) => !RoomShapes.Contains(rings, p) || others.Any(o => RoomShapes.Contains(o, p));
            // where anything could change: every corner seen from the wall, and every edge crossing its line
            foreach (var ring in walls)
                for (int k = 0; k < ring.Count; k++)
                {
                    var v = ring[k];
                    cuts.Add((v - w.Origin).Dot(w.Along));
                    var nv = ring[(k + 1) % ring.Count];
                    float da = (v - w.Origin).Dot(w.Out), db = (nv - w.Origin).Dot(w.Out);
                    if (da * db < 0)
                    {
                        var x = v + (nv - v) * (da / (da - db));
                        cuts.Add((x - w.Origin).Dot(w.Along));
                    }
                }
            cuts = cuts.Where(u => u >= 0 && u <= w.Length).OrderBy(u => u).ToList();
            var list = reaches[s] = new List<Reach>();
            float last = float.NaN;
            foreach (float u in cuts)
            {
                if (!float.IsNaN(last) && u - last > 0.01f)
                {
                    float ua = last, ub = u, um = (ua + ub) / 2;
                    var p = w.At(um, Step);
                    if (keep.Any(k => um > k.Lo && um < k.Hi) || Blocked(p)) list.Add(new Reach(ua, 0, ub, 0, -1, -1));
                    else
                    {
                        float t = RoomShapes.Cast(walls, p, w.Out, MaxReach, out int ring, out int edge);
                        if (ring < 0 || t + Step < MinReach) list.Add(new Reach(ua, 0, ub, 0, -1, -1));
                        else
                        {
                            var e = walls[ring];
                            var a = e[edge];
                            var b = e[(edge + 1) % e.Count];
                            // past where the edge crosses the wall line it would be behind it: none
                            float Depth(float uu) => LineHit(w.At(uu), w.Out, a, b) is { } h ? Math.Clamp(h, 0, MaxReach) : t + Step;
                            list.Add(new Reach(ua, Depth(ua), ub, Depth(ub), ring, edge));
                        }
                    }
                }
                last = u;
            }
        }

        // ---- the arm of an L: a deep growth off a room others open off gets rooms of its own ----
        // (as rectangles, which grow out to the outline in their turn), and this room is grown again
        if (arms && Splits(l, r))
        {
            bool added = false;
            for (int s = 0; s < 4; s++)
                foreach (var run in Runs(reaches[s]).ToList())
                {
                    if (run.Max(x => Math.Max(x.D0, x.D1)) < DeepAt || run[^1].U1 - run[0].U0 < 2.6f) continue;
                    var (bu0, bu1, bd) = RunCore(run);
                    added |= bd >= 2.4f && bu1 - bu0 >= 2.4f && Arm(l, f, index, lines[s], bu0, bu1, bd, fresh);
                }
            if (added) return Grow(l, f, index, rings, claims, false, fresh, shapeable);
        }
        if (!shapeable) return null;

        // a room grows across a slanted facade's sliver, not down a whole wing: deeper than that
        // it is a room no one would build (a WC 25 m long). A hall alone on its floor fills the building.
        if (!(floor.Rooms.Count == 1 || HallLike(r.Type)))
            for (int s = 0; s < 4; s++)
            {
                var list = reaches[s];
                for (int k = 0; k < list.Count; k++)
                    if (list[k].D0 > DeepAt || list[k].D1 > DeepAt)
                        list[k] = list[k] with { D0 = Math.Min(list[k].D0, DeepAt), D1 = Math.Min(list[k].D1, DeepAt), Ring = -1 };
            }

        if (reaches.All(list => list.All(x => x.D0 < MinReach && x.D1 < MinReach))) return null;

        // ---- the polygon, wall by wall, round the corners ---------------------------------------
        var pts = new List<Vector2>();
        for (int s = 0; s < 4; s++)
        {
            var w = lines[s];
            var list = reaches[s];
            var prevLine = lines[(s + 3) % 4];
            var prevList = reaches[(s + 3) % 4];
            var end = prevList[^1];
            var start = list[0];
            var corner = w.Origin;
            // where both walls grow and both met the same ring of the outline, follow it round
            List<Vector2>? fill = null;
            if (end.D1 >= MinReach && start.D0 >= MinReach && end.Ring >= 0 && end.Ring == start.Ring && end.Ring < rings.Count)
                fill = Round(rings[end.Ring], end.Edge, start.Edge, corner, prevLine.Out, w.Out, allOthers);
            if (fill != null) pts.AddRange(fill);
            else pts.Add(corner);
            foreach (var x in list)
            {
                pts.Add(w.At(x.U0, x.D0));
                pts.Add(w.At(x.U1, x.D1));
            }
        }
        var shape = RoomShapes.Tidy(pts);
        if (shape.Count < 3 || RoomShapes.Area(shape) < r.Area + 0.1f) return null;

        // the walls it grew past are gone, and their windows with them
        for (int s = 0; s < 4; s++)
        {
            var w = lines[s];
            foreach (var x in reaches[s].Where(x => x.D0 >= MinReach || x.D1 >= MinReach))
                r.Openings.RemoveAll(o => o.Edge == null && o.Side == w.Side && o.Kind == OpeningKind.Window
                                          && w.U(o.Center) + o.Width / 2 > x.U0 && w.U(o.Center) - o.Width / 2 < x.U1);
        }
        return shape.ToArray();
    }

    /// <summary>Distance along a ray to the line through a and b (not only the segment), or null if parallel.</summary>
    private static float? LineHit(Vector2 o, Vector2 d, Vector2 a, Vector2 b)
    {
        var e = b - a;
        float den = d.X * e.Y - d.Y * e.X;
        if (Math.Abs(den) < 1e-9f) return null;
        var w = a - o;
        return (w.X * e.Y - w.Y * e.X) / den;
    }

    /// <summary>The stretches of a wall that grow, each as its run of reaches.</summary>
    private static IEnumerable<List<Reach>> Runs(List<Reach> list)
    {
        var run = new List<Reach>();
        foreach (var x in list)
        {
            if (x.D0 >= MinReach || x.D1 >= MinReach) { run.Add(x); continue; }
            if (run.Count > 0) yield return run;
            run = new List<Reach>();
        }
        if (run.Count > 0) yield return run;
    }

    /// <summary>
    /// The outline's corners from where one wall's growth met it to where the next one's did, if
    /// they all lie in the corner beyond the room and in no other room: the corner of the room
    /// follows the building round. Null when it would not.
    /// </summary>
    private static List<Vector2>? Round(Vector2[] ring, int from, int to, Vector2 corner, Vector2 out0, Vector2 out1, List<Vector2[]> others)
    {
        var o = new List<Vector2>();
        // one facade edge cutting the corner: straight across, nothing between
        if (from == to) return o;
        int n = ring.Length;
        // the vertices after the edge one growth met, up to the start of the edge the other met
        for (int k = (from + 1) % n, guard = 0; guard < 8; k = (k + 1) % n, guard++)
        {
            var v = ring[k];
            if ((v - corner).Dot(out0) < -0.02f || (v - corner).Dot(out1) < -0.02f) return null;
            if (others.Any(q => RoomShapes.Contains(q, v))) return null;
            o.Add(v);
            if (k == to) return o;
        }
        return null;
    }

    /// <summary>
    /// Rooms a deep growth gets rooms of its own off: in a block of flats the ones others open off
    /// (#576), elsewhere any but a wet room or one shut by its use.
    /// </summary>
    private static bool Splits(InteriorLayout l, RoomPlan r) => l.Type is BuildingType.Apartments or BuildingType.MixedUse
        ? r.Type is RoomType.Hall or RoomType.Living or RoomType.Kitchen or RoomType.Dining or RoomType.Corridor
        : r.Type is not (RoomType.Bathroom or RoomType.WC or RoomType.Vault or RoomType.Shelter or RoomType.Elevator
            or RoomType.Stairwell or RoomType.Ramp or RoomType.TechRoom);

    /// <summary>A room that is a hall: it goes on round a corner as itself, not as rooms off it.</summary>
    private static bool HallLike(RoomType t) => t is RoomType.Barn or RoomType.Garage or RoomType.WarehouseHall
        or RoomType.ProductionHall or RoomType.IkeaMarket or RoomType.Nave or RoomType.TruckBay or RoomType.Workshop;

    /// <summary>The biggest rectangle of a run's growth that stands on the wall: its stretch and depth.</summary>
    private static (float U0, float U1, float D) RunCore(List<Reach> run)
    {
        var us = run.Select(x => x.U0).Append(run[^1].U1).ToList();
        float best = 0, bu0 = run[0].U0, bu1 = run[^1].U1, bd = 0;
        for (int i = 0; i < us.Count; i++)
            for (int j = i + 1; j < us.Count; j++)
            {
                float d = run.Where(x => x.U1 > us[i] + 1e-4f && x.U0 < us[j] - 1e-4f).Min(x => Math.Min(x.D0, x.D1));
                if ((us[j] - us[i]) * d > best) { best = (us[j] - us[i]) * d; bu0 = us[i]; bu1 = us[j]; bd = d; }
            }
        return (bu0, bu1, bd);
    }

    /// <summary>The side of a rectangle that faces the way <paramref name="v"/> points.</summary>
    private static Side Facing(Vector2 v) =>
        Math.Abs(v.X) > Math.Abs(v.Y) ? (v.X > 0 ? Side.Right : Side.Left) : (v.Y > 0 ? Side.Back : Side.Front);

    /// <summary>
    /// Rooms for the arm of an L (or any deep stretch of building) off room <paramref name="index"/>'s
    /// wall <paramref name="w"/>, filling the rectangle u0..u1 along it and <paramref name="depth"/>
    /// out: a shallow one side by side, each with a door into the room; a deep one a corridor
    /// straight out with rooms either side of it, every room a door onto it. Bedrooms, studies and
    /// box rooms in a home, offices in an office, storage under ground. False if not one door fits.
    /// </summary>
    private static bool Arm(InteriorLayout l, int f, int index, WallLine w, float u0, float u1, float depth, HashSet<RoomPlan> fresh)
    {
        var floor = l.Floors[f];
        var r = floor.Rooms[index];
        float top = Math.Min(2.1f, l.ClearOf(r) - 0.1f);
        const float doorW = 0.9f, corridor = 1.3f, room = 4.5f, least = 2.6f;
        var toWall = Facing(-w.Out);
        var along = Facing(w.Along);
        var back = Facing(-w.Along);
        RoomType TypeOf(float area) =>
            f < l.Below ? RoomType.Storage
            : r.Type is RoomType.Office ? RoomType.Office
            : area >= 9f ? RoomType.Bedroom : area >= 5f ? RoomType.Study : RoomType.Storage;
        int Add(float ua, float da, float ub, float db, RoomType type)
        {
            var a = w.At(ua, da);
            var b = w.At(ub, db);
            var q = new RoomPlan
            {
                X0 = Math.Min(a.X, b.X), Z0 = Math.Min(a.Y, b.Y), X1 = Math.Max(a.X, b.X), Z1 = Math.Max(a.Y, b.Y),
                Type = type, Unit = r.Unit, Clear = r.Clear,
            };
            floor.Rooms.Add(q);
            fresh.Add(q);
            return floor.Rooms.Count - 1;
        }
        static float Coord(Side s, Vector2 p) => s is Side.Front or Side.Back ? p.X : p.Y;
        void Door(int a, Side sa, int b, Vector2 at, OpeningKind kind = OpeningKind.Door)
        {
            var sb = (Side)(((int)sa + 2) % 4);
            floor.Rooms[a].Openings.Add(new OpeningPlan { Side = sa, Center = Coord(sa, at), Width = doorW, Top = top, Kind = kind, Other = b });
            floor.Rooms[b].Openings.Add(new OpeningPlan { Side = sb, Center = Coord(sb, at), Width = doorW, Top = top, Kind = kind, Other = a });
        }
        // a door in this room's wall at u, clear of the cuts already in it, and of a stair or a shaft
        // inside it (a stair hall's wall)
        bool Free(float u)
        {
            if (u - doorW / 2 < u0 + 0.3f || u + doorW / 2 > u1 - 0.3f) return false;
            if (r.Openings.Any(o => o.Edge == null && o.Side == w.Side && o.Kind != OpeningKind.Window
                                    && Math.Abs(w.U(o.Center) - u) < (o.Width + doorW) / 2 + 0.3f)) return false;
            var a = w.At(u - doorW / 2, -1.2f);
            var b = w.At(u + doorW / 2, 0);
            var inside = new RectPlan(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            return !floor.AllFlights().Any(fl => fl.Area().Overlaps(inside)) && !floor.Holes.Any(h => h.Overlaps(inside))
                   && !floor.Landings.Any(g => inside.Overlaps(new RectPlan(g.X0, g.Z0, g.X1, g.Z1)));
        }
        float? Near(float want, float lo, float hi)
        {
            for (float step = 0; step <= (hi - lo) / 2; step += 0.1f)
                foreach (float u in new[] { want - step, want + step })
                    if (u - doorW / 2 >= lo + 0.2f && u + doorW / 2 <= hi - 0.2f && Free(u)) return u;
            return null;
        }

        float width = u1 - u0;
        int made = floor.Rooms.Count;
        if (HallLike(r.Type))
        {
            // a hall goes on into the next wing, through an opening as wide as the wing allows
            float aw = Math.Min(width - 0.6f, 4f);
            if (aw < doorW) return false;
            int q = Add(u0, 0, u1, depth, r.Type);
            var c = w.At((u0 + u1) / 2);
            var sb = (Side)(((int)toWall + 2) % 4);
            floor.Rooms[q].Openings.Add(new OpeningPlan { Side = toWall, Center = Coord(toWall, c), Width = aw, Top = top, Kind = OpeningKind.Arch, Other = index });
            r.Openings.Add(new OpeningPlan { Side = sb, Center = Coord(sb, c), Width = aw, Top = top, Kind = OpeningKind.Arch, Other = q });
        }
        else if (depth < 1.5f * room || width < least + corridor)
        {
            // shallow, or too narrow for a corridor: rooms side by side, each off this room
            int n = Math.Max(1, (int)MathF.Round(width / room));
            while (n > 1 && width / n < least) n--;
            for (int k = 0; k < n; k++)
            {
                float a = u0 + width * k / n, b = u0 + width * (k + 1) / n;
                if (Near((a + b) / 2, a, b) is not { } du) continue;
                int q = Add(a, 0, b, depth, TypeOf((b - a) * depth));
                Door(q, toWall, index, w.At(du));
            }
        }
        else
        {
            // a corridor straight out, in the middle if rooms fit both sides of it, else at one side
            bool both = width >= 2 * least + corridor;
            float c0 = both ? u0 + (width - corridor) / 2 : u0, c1 = c0 + corridor;
            if (Near((c0 + c1) / 2, c0 - 0.2f, c1 + 0.2f) is not { } cu) return false;
            int hall = Add(c0, 0, c1, depth, r.Type is RoomType.Office ? RoomType.Corridor : RoomType.Hall);
            Door(hall, toWall, index, w.At(cu), OpeningKind.Arch);
            int m = Math.Max(1, (int)MathF.Round(depth / room));
            foreach (var (a, b, side) in both
                         ? new[] { (u0, c0, along), (c1, u1, back) }
                         : new[] { (c1, u1, back) })
                for (int k = 0; k < m; k++)
                {
                    float da = depth * k / m, db = depth * (k + 1) / m;
                    int q = Add(a, da, b, db, TypeOf((b - a) * (db - da)));
                    // its door onto the corridor, half way along it
                    var at = w.At(side == along ? b : a, (da + db) / 2);
                    Door(q, side, hall, at);
                }
        }
        if (floor.Rooms.Count == made) return false;
        // this room's wall there is inside now: its windows go
        r.Openings.RemoveAll(o => o.Kind == OpeningKind.Window && o.Edge == null && o.Side == w.Side
                                  && w.U(o.Center) + o.Width / 2 > u0 && w.U(o.Center) - o.Width / 2 < u1);
        return true;
    }

    // ---- clip --------------------------------------------------------------------------------

    private static bool Clip(InteriorLayout l, List<Vector2[]> rings)
    {
        var outers = rings.Where(r => RoomShapes.Area(r) > 0).ToList();
        var holes = rings.Where(r => RoomShapes.Area(r) < 0).ToList();
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var gone = new List<int>();
            for (int i = 0; i < l.Floors[f].Rooms.Count; i++)
            {
                var r = l.Floors[f].Rooms[i];
                if (RectInside(rings, r.X0, r.Z0, r.X1, r.Z1, 0.01f)) continue;
                if (!Shapeable(l, f, r)) return Unfit($"floor {f} {r.Type} holds a stair or a shaft and leaves the outline");
                // a courtyard in the room: cutting it out would need a hole in the floor
                if (holes.Any(h => h.Any(v => v.X > r.X0 && v.X < r.X1 && v.Y > r.Z0 && v.Y < r.Z1))) return Unfit($"floor {f} {r.Type}: a courtyard inside it");
                var pieces = new List<List<Vector2>>();
                foreach (var o in outers)
                {
                    var p = RoomShapes.ClipToRect(o, r.X0, r.Z0, r.X1, r.Z1, out bool split);
                    if (p.Count < 3 || RoomShapes.Area(p) < 0.25f) continue;
                    if (split) return Unfit($"floor {f} {r.Type}: the outline meets it twice");
                    pieces.Add(p);
                }
                // a room out in the yard goes (if nothing is reached only through it, which the
                // validator tells); one cut in two would need two rooms
                if (pieces.Count == 0 || pieces.Count == 1 && RoomShapes.Area(pieces[0]) < 1.5f) { gone.Add(i); continue; }
                if (pieces.Count != 1) return Unfit($"floor {f} {r.Type}: cut in {pieces.Count}");
                var shape = pieces[0];
                // every cut in its walls still on a wall; a window that is not goes, anything else undoes it
                foreach (var o in r.Openings.ToList())
                {
                    if (o.Edge != null) continue;
                    var (at, _, along) = r.WallFrame(o);
                    bool on = RoomShapes.EdgeDistance(shape, at - along * (o.Width / 2)) < 0.03f
                              && RoomShapes.EdgeDistance(shape, at + along * (o.Width / 2)) < 0.03f;
                    if (on) continue;
                    // another street door than the main one, out where the building is not: its door
                    // has no doorway then, and reads as locked (#498)
                    if (o.Kind == OpeningKind.Entry && l.Entrances.Count > 1
                        && l.Entrances.FindIndex(e => new Vector2(e.X, e.Z).DistanceTo(at) < 0.05f) is var ei && ei > 0)
                    {
                        l.Entrances.RemoveAt(ei);
                        r.Openings.Remove(o);
                        continue;
                    }
                    if (o.Kind != OpeningKind.Window) return Unfit($"floor {f} {r.Type}: a {o.Kind} off the outline");
                    r.Openings.Remove(o);
                }
                r.SetShape(shape);
            }
            for (int k = gone.Count - 1; k >= 0; k--)
                if (!Drop(l, f, gone[k])) return Unfit($"floor {f}: {l.Floors[f].Rooms[gone[k]].Type} out in the yard is a way in");
        }
        EdgeWindows(l, rings);
        return true;
    }

    /// <summary>
    /// A room taken out of its floor: the doorways into it walled up, the later rooms' indices one
    /// down. False for a room that is a way in, or holds a flat's front door.
    /// </summary>
    private static bool Drop(InteriorLayout l, int f, int k)
    {
        var rooms = l.Floors[f].Rooms;
        if (rooms[k].Openings.Any(o => o.Kind == OpeningKind.Entry) || l.InnerDoors.Any(d => d.Floor == f && d.Room == k)) return false;
        rooms.RemoveAt(k);
        foreach (var r in rooms)
        {
            r.Openings.RemoveAll(o => o.Other == k && o.Kind is OpeningKind.Door or OpeningKind.Arch or OpeningKind.Window);
            foreach (var o in r.Openings) if (o.Other > k) o.Other--;
        }
        foreach (var d in l.InnerDoors) if (d.Floor == f && d.Room > k) d.Room--;
        return true;
    }

    // ---- windows on the new facades ------------------------------------------------------------

    /// <summary>
    /// Windows on every edge of a shaped room that is a facade and not on its rectangle's wall
    /// lines (those kept theirs), and on every facade of a room an arm made (<paramref name="fresh"/>,
    /// which the planner's windows never saw), spaced as <see cref="AddWindows"/> spaces them.
    /// Above ground only.
    /// </summary>
    private static void EdgeWindows(InteriorLayout l, List<Vector2[]> rings, HashSet<RoomPlan>? fresh = null)
    {
        float clear = l.StoreyHeight - Slab;
        for (int f = l.Below; f < l.Floors.Count; f++)
        {
            var rooms = l.Floors[f].Rooms;
            foreach (var r in rooms)
            {
                bool made = fresh?.Contains(r) == true;
                if (r.Shape == null && !made || r.Type is RoomType.Vault or RoomType.Shelter or RoomType.Elevator or RoomType.Ramp) continue;
                var c = r.Corners();
                for (int k = 0; k < c.Length; k++)
                {
                    var a = c[k];
                    var b = c[(k + 1) % c.Length];
                    float len = a.DistanceTo(b);
                    bool onRect = OnRectLine(r, a, b);
                    if (len < 1.5f || onRect && !made) continue;
                    var dir = (b - a) / len;
                    var outward = new Vector2(dir.Y, -dir.X);
                    var probe = (a + b) / 2 + outward * 0.15f;
                    if (RoomShapes.Contains(rings, probe) || rooms.Any(q => q != r && q.Holds(probe.X, probe.Y))) continue;
                    var (width, bottom, top, spacing) = WindowSize(r.Type, false, clear, len);
                    if (top - bottom < 0.4f || len < width + 0.8f) continue;
                    int count = Math.Max(1, (int)(len / spacing));
                    var side = Facing(outward);
                    for (int i = 0; i < count; i++)
                    {
                        var at = a + dir * (len * (i + 0.5f) / count);
                        // on the rectangle's own wall: addressed by its side, clear of the doors in it
                        if (onRect)
                        {
                            float coord = side is Side.Front or Side.Back ? at.X : at.Y;
                            if (Blocked(r, side, coord, width)) continue;
                            r.Openings.Add(new OpeningPlan { Side = side, Center = coord, Width = width, Bottom = bottom, Top = top, Kind = OpeningKind.Window });
                        }
                        else
                            r.Openings.Add(new OpeningPlan
                            {
                                Edge = k, Center = len * (i + 0.5f) / count, Width = width, Bottom = bottom, Top = top, Kind = OpeningKind.Window,
                            });
                    }
                }
            }
        }
    }

    /// <summary>Whether an edge lies on one of the rectangle's own wall lines.</summary>
    private static bool OnRectLine(RoomPlan r, Vector2 a, Vector2 b)
    {
        const float e = 0.02f;
        bool H(float z) => Math.Abs(a.Y - z) < e && Math.Abs(b.Y - z) < e && Math.Min(a.X, b.X) >= r.X0 - e && Math.Max(a.X, b.X) <= r.X1 + e;
        bool V(float x) => Math.Abs(a.X - x) < e && Math.Abs(b.X - x) < e && Math.Min(a.Y, b.Y) >= r.Z0 - e && Math.Max(a.Y, b.Y) <= r.Z1 + e;
        return H(r.Z0) || H(r.Z1) || V(r.X0) || V(r.X1);
    }

    // ---- small helpers for the rest of the generator --------------------------------------------

    /// <summary>The strip in front of an opening on a shaped room's edge, as a box round it.</summary>
    private static RectPlan Across(RoomPlan r, OpeningPlan o, float half, float deep)
    {
        var (at, outward, along) = r.WallFrame(o);
        var p = new[] { at - along * half, at + along * half, at - along * half - outward * deep, at + along * half - outward * deep };
        return new RectPlan(p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
    }

    /// <summary>Whether a piece's footprint stands inside a shaped room, <paramref name="inset"/> off its walls.</summary>
    private static bool InShape(RoomPlan r, RectPlan p, float inset)
    {
        var c = r.Corners();
        foreach (var q in new[] { new Vector2(p.X0, p.Z0), new Vector2(p.X1, p.Z0), new Vector2(p.X1, p.Z1), new Vector2(p.X0, p.Z1) })
            if (!RoomShapes.Contains(c, q) || RoomShapes.EdgeDistance(c, q) < inset - 1e-3f) return false;
        // an angled wall's corner poking in between the piece's corners
        foreach (var v in c)
            if (v.X > p.X0 + 1e-3f && v.X < p.X1 - 1e-3f && v.Y > p.Z0 + 1e-3f && v.Y < p.Z1 - 1e-3f) return false;
        return true;
    }
}
