using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>What a piece of a half-built shell is made of: its colour, and nothing else.</summary>
public enum ShellPart : byte
{
    Concrete = 0, FreshConcrete = 1, Formwork = 2, Plywood = 3, Rebar = 4, Tube = 5, Deck = 6,
    Net = 7, Insulation = 8, Frame = 9, Board = 10,
    /// <summary>Collision only, never drawn: a scaffold lift's guard where it has no netting.</summary>
    Invisible = 11,
}

/// <summary>
/// One axis-aligned box of a shell in the SITE frame (#608): x along the plan box's
/// <c>AxisU</c>, z along its <c>AxisV</c>, both from the box's centre; y is the tile's own height.
/// <see cref="Solid"/>: it has collision. Thin dressing (bars, tubes, nets) has none.
/// </summary>
public readonly record struct ShellBox(Vector3 Min, Vector3 Max, ShellPart Part, bool Solid);

/// <summary>A walkable slope, as the quad <see cref="A"/> <see cref="B"/> <see cref="C"/> <see cref="D"/> in the site frame: a flight's ramp.</summary>
public readonly record struct ShellRamp(Vector3 A, Vector3 B, Vector3 C, Vector3 D);

/// <summary>An axis-aligned rectangle in the site frame.</summary>
public readonly record struct Rect2D(float X0, float Z0, float X1, float Z1)
{
    public float Width => X1 - X0;
    public float Depth => Z1 - Z0;
    public bool Contains(float x, float z, float margin = 0f) =>
        x >= X0 - margin && x <= X1 + margin && z >= Z0 - margin && z <= Z1 + margin;
    public bool Overlaps(Rect2D o) => X0 < o.X1 && o.X0 < X1 && Z0 < o.Z1 && o.Z0 < Z1;
}

/// <summary>
/// A building site's half-built structure (#608), as boxes and ramps in the site frame: the
/// slabs cast so far with the stair well cut out of them, the columns between them, the outer
/// walls with their window openings, the stair core with a switchback flight per storey, and by
/// phase the formwork, starter bars, roof parapet, insulation and the scaffolding wrapped round
/// it. <see cref="ShellPlans.Plan"/> makes it; <c>SiteShellBuilder</c> turns it into a mesh and
/// collision faces.
/// </summary>
public sealed class ShellPlan
{
    public List<ShellBox> Boxes { get; } = new();
    public List<ShellRamp> Ramps { get; } = new();
    /// <summary>The height of each slab's top, ground floor first.</summary>
    public List<float> Levels { get; } = new();
    /// <summary>The stair core in the site frame, and whether its flights run along x.</summary>
    public Rect2D Core { get; set; }
    public bool CoreAlongX { get; set; }
    /// <summary>The wings the shell follows (the plan box itself, or <c>PlanOutline.Wings</c>).</summary>
    public IReadOnlyList<Rect2D> Wings { get; set; } = Array.Empty<Rect2D>();
    /// <summary>
    /// The way up, in the site frame: from the ground floor's landing, up both flights of every
    /// storey to the top slab's landing. What <c>--shellwalkcheck</c> walks a body along, and
    /// the line a worker would take.
    /// </summary>
    public List<Vector3> Route { get; } = new();
}

/// <summary>
/// Builds a <see cref="ShellPlan"/> from a <see cref="ConstructionSite"/>: a pure function of the
/// site, its wings and the ground under it, so every peer builds the same shell and its collision.
///
/// <para>
/// <b>Walkable by construction.</b> The slabs stand on the highest ground under the building, the
/// stair core rises through every slab (a 1 m landing on the street side of its well stays slab, so
/// the flight arriving from below steps onto it), each storey's flight is a ramp to stand on under
/// steps to look at (the interiors' rule: a body catches on real risers), and the scaffolding's
/// lifts are at the slabs' heights, so its decks are a step out through any opening.
/// </para>
/// </summary>
public static class ShellPlans
{
    public const float Slab = 0.25f, Wall = 0.25f, Column = 0.32f;
    /// <summary>Columns stand about this far apart, each wing's spans divided evenly.</summary>
    public const float Span = 6f;
    /// <summary>The stair core: flights this wide, a 1 m landing at the street end, 1.2 m at the far end.</summary>
    public const float FlightWidth = 1.2f, NearLanding = 1.0f, FarLanding = 1.2f;
    public const float SillHeight = 0.9f, HeadHeight = 2.25f, WindowWidth = 1.6f;
    /// <summary>Scaffolding: inner standards this far off the wall, outer ones this far, a standard every <see cref="Bay"/>.</summary>
    public const float ScaffoldIn = 0.3f, ScaffoldOut = 1.05f, Bay = 2.5f;
    /// <summary>A crane standing in the building has this much of every slab left open round its mast.</summary>
    public const float CraneHole = 2.6f;

    /// <param name="wings">The building's wings in the site frame, or null for the whole plan box.</param>
    /// <param name="groundHigh">The highest ground under the building: the ground floor's slab top.</param>
    /// <param name="groundLow">The lowest: the plinth reaches down to it.</param>
    public static ShellPlan Plan(ConstructionSite site, IReadOnlyList<Rect2D>? wings, float groundHigh, float groundLow)
    {
        var plan = new ShellPlan();
        float hw = site.Box.Width / 2, hd = site.Box.Depth / 2;
        var ws = wings is { Count: > 0 } ? wings : new[] { new Rect2D(-hw, -hd, hw, hd) };
        plan.Wings = ws;
        double R(string q) => Fnv.Unit(site.Key + "|shell|" + q);
        void Box(float x0, float y0, float z0, float x1, float y1, float z1, ShellPart part, bool solid) =>
            plan.Boxes.Add(new ShellBox(new Vector3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1)),
                new Vector3(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1)), part, solid));

        float storey = site.StoreyHeight;
        int top = site.Phase == SitePhase.Foundations ? 0 : site.BuiltStoreys;
        for (int k = 0; k <= top; k++) plan.Levels.Add(groundHigh + k * storey);
        float L(int k) => plan.Levels[k];

        // ---- the stair core, in the biggest wing, its flights along that wing's long side ----
        var main = ws.OrderByDescending(w => w.Width * w.Depth).First();
        bool alongX = main.Width >= main.Depth;
        float coreLen = site.Small ? 5.0f : 6.0f, coreWid = 2 * FlightWidth + 0.2f;
        float mx = (main.X0 + main.X1) / 2, mz = (main.Z0 + main.Z1) / 2;
        // the near end (the landing) faces the street, so the way up starts on the way in
        float toFront = alongX ? site.Front.Dot(site.Box.AxisU) : site.Front.Dot(site.Box.AxisV);
        int dir = toFront >= 0 ? 1 : -1;
        var core = alongX
            ? new Rect2D(mx - coreLen / 2, mz - coreWid / 2, mx + coreLen / 2, mz + coreWid / 2)
            : new Rect2D(mx - coreWid / 2, mz - coreLen / 2, mx + coreWid / 2, mz + coreLen / 2);
        plan.Core = core;
        plan.CoreAlongX = alongX;
        // in the core's own frame: a runs along the flights from the near end (+dir side), b across
        float near = alongX ? (dir > 0 ? core.X1 : core.X0) : (dir > 0 ? core.Z1 : core.Z0);
        float aLen = coreLen, b0 = alongX ? core.Z0 : core.X0;
        // a point a metres in from the near end, b across from b0, as site (x, z)
        (float X, float Z) C(float a, float b) => alongX ? (near - dir * a, b0 + b) : (b0 + b, near - dir * a);
        void CoreBox(float a0, float y0, float bb0, float a1, float y1, float bb1, ShellPart part, bool solid)
        {
            var (x0, z0) = C(a0, bb0);
            var (x1, z1) = C(a1, bb1);
            Box(x0, y0, z0, x1, y1, z1, part, solid);
        }
        // the well: open in every slab above the ground's, but for the near landing
        var (wx0, wz0) = C(NearLanding, 0);
        var (wx1, wz1) = C(aLen, coreWid);
        var well = new Rect2D(Math.Min(wx0, wx1), Math.Min(wz0, wz1), Math.Max(wx0, wx1), Math.Max(wz0, wz1));

        // ---- cranes standing in the building: an opening round each mast ----------------------
        var holes = new List<Rect2D>();
        foreach (var crane in site.Cranes.Where(c => c.Inside))
        {
            var d = crane.Base - site.Box.Center;
            float cx = d.Dot(site.Box.AxisU), cz = d.Dot(site.Box.AxisV);
            holes.Add(new Rect2D(cx - CraneHole / 2, cz - CraneHole / 2, cx + CraneHole / 2, cz + CraneHole / 2));
        }

        // ---- the ground slab, on a plinth down to the lowest ground --------------------------
        foreach (var w in ws)
            Box(w.X0, Math.Min(groundLow, groundHigh - Slab) - 0.3f, w.Z0, w.X1, L(0), w.Z1, ShellPart.Concrete, true);

        // ---- the columns' grid, per wing, and the outer bays along it ------------------------
        var bays = new List<(float X0, float Z0, float X1, float Z1, float Nx, float Nz)>();
        var columns = new List<(float X, float Z)>();
        foreach (var w in ws)
        {
            int nx = Math.Max(1, (int)MathF.Round(w.Width / Span)), nz = Math.Max(1, (int)MathF.Round(w.Depth / Span));
            float c = Column / 2;
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= nz; j++)
                {
                    float x = Mathf.Lerp(w.X0 + c, w.X1 - c, (float)i / nx), z = Mathf.Lerp(w.Z0 + c, w.Z1 - c, (float)j / nz);
                    if (core.Contains(x, z, 0.4f) || holes.Any(h => h.Contains(x, z, 0.4f))) continue;
                    if (columns.Any(p => MathF.Abs(p.X - x) < 0.5f && MathF.Abs(p.Z - z) < 0.5f)) continue;
                    columns.Add((x, z));
                }
            // the four edges, cut into bays at the columns, outward normal (Nx, Nz); an edge against
            // another wing is inside the building and gets no wall
            void Edge(float x0, float z0, float x1, float z1, int n, float nxo, float nzo)
            {
                for (int k = 0; k < n; k++)
                {
                    float ax = Mathf.Lerp(x0, x1, (float)k / n), az = Mathf.Lerp(z0, z1, (float)k / n);
                    float bx = Mathf.Lerp(x0, x1, (float)(k + 1) / n), bz = Mathf.Lerp(z0, z1, (float)(k + 1) / n);
                    float mxp = (ax + bx) / 2 + nxo * 0.4f, mzp = (az + bz) / 2 + nzo * 0.4f;
                    if (ws.Any(o => o != w && o.Contains(mxp, mzp))) continue;
                    bays.Add((ax, az, bx, bz, nxo, nzo));
                }
            }
            Edge(w.X0, w.Z0, w.X1, w.Z0, nx, 0, -1);
            Edge(w.X0, w.Z1, w.X1, w.Z1, nx, 0, 1);
            Edge(w.X0, w.Z0, w.X0, w.Z1, nz, -1, 0);
            Edge(w.X1, w.Z0, w.X1, w.Z1, nz, 1, 0);
        }

        // ---- every storey that is built: slab over it, columns, walls, the flight up ----------
        for (int s = 0; s < top; s++)
        {
            float floor = L(s), ceiling = L(s + 1) - Slab;
            foreach (var (x, z) in columns)
                Box(x - Column / 2, floor, z - Column / 2, x + Column / 2, ceiling, z + Column / 2, ShellPart.Concrete, true);

            // the outer walls: closed bays are a wall with a window in it; a shell's top storey
            // has more of its bays still open, a topped-out building has them all closed
            bool topStorey = s == top - 1;
            double closed = site.Phase == SitePhase.ToppedOut ? 1.0 : topStorey ? 0.45 : 0.85;
            for (int k = 0; k < bays.Count; k++)
            {
                if (R($"bay{s}.{k}") >= closed) continue;
                bool door = s == 0 && R($"door{k}") < 0.15;
                WallBay(bays[k], floor, ceiling, door, site.Phase == SitePhase.ToppedOut && R($"ins{s}.{k}") < 0.55 && s < top * 0.7f);
            }

            // the switchback: up the low side of the core to the far landing, back up the high side
            float mid = floor + storey / 2, upper = L(s + 1);
            Flight(NearLanding, aLen - FarLanding, 0, FlightWidth, floor, mid);
            CoreBox(aLen - FarLanding, mid - 0.2f, 0, aLen, mid, coreWid, ShellPart.Concrete, true);
            Flight(aLen - FarLanding, NearLanding, coreWid - FlightWidth, coreWid, mid, upper);

            // the slab over this storey, the well and any mast cut out of it
            foreach (var w in ws)
                foreach (var r in Subtract(w, holes.Append(well)))
                    Box(r.X0, upper - Slab, r.Z0, r.X1, upper, r.Z1, ShellPart.Concrete, true);
            // the top slab has no flight going on up: a rail where the one below drops away
            if (s + 1 == top)
                CoreBox(NearLanding - 0.05f, upper, 0, NearLanding + 0.05f, upper + 1.05f, FlightWidth, ShellPart.Tube, true);
        }
        // the spine between the two flights, the full height of the core, so nobody steps off one
        // flight onto the other (the Swiss concrete stair's middle wall)
        if (top > 0)
            CoreBox(NearLanding, L(0), FlightWidth, aLen - FarLanding, L(top), coreWid - FlightWidth, ShellPart.Concrete, true);

        // the way up: the landing, up the low lane, across the far landing, up the high lane,
        // and back across the next landing to the foot of the next flight
        {
            float laneA = FlightWidth / 2, laneB = coreWid - FlightWidth / 2;
            Vector3 P(float a, float b, float y)
            {
                var (x, z) = C(a, b);
                return new Vector3(x, y, z);
            }
            plan.Route.Add(P(0.5f, laneA, L(0)));
            for (int s = 0; s < top; s++)
            {
                float mid = L(s) + storey / 2;
                plan.Route.Add(P(NearLanding + 0.3f, laneA, L(s)));
                plan.Route.Add(P(aLen - FarLanding - 0.2f, laneA, mid));
                plan.Route.Add(P(aLen - 0.6f, laneA, mid));
                plan.Route.Add(P(aLen - 0.6f, laneB, mid));
                plan.Route.Add(P(aLen - FarLanding - 0.2f, laneB, mid));
                plan.Route.Add(P(0.5f, laneB, L(s + 1)));
                plan.Route.Add(P(0.5f, laneA, L(s + 1)));
            }
        }

        // the core's walls: both long sides and the far end, open to the landing
        float coreTop = site.Phase switch
        {
            SitePhase.Foundations => L(0) + 1.4f,
            SitePhase.Shell => L(top) + 1.6f,
            _ => L(top) + 2.6f,
        };
        CoreBox(0, L(0), -Wall, aLen + Wall, coreTop, 0, ShellPart.Concrete, true);
        CoreBox(0, L(0), coreWid, aLen + Wall, coreTop, coreWid + Wall, ShellPart.Concrete, true);
        CoreBox(aLen, L(0), 0, aLen + Wall, coreTop, coreWid, ShellPart.Concrete, true);
        if (site.Phase == SitePhase.ToppedOut)
            // the stair house's roof
            CoreBox(0, coreTop, -Wall, aLen + Wall, coreTop + Slab, coreWid + Wall, ShellPart.Concrete, true);
        else
        {
            // the core's next lift, in formwork
            float f0 = coreTop - (site.Phase == SitePhase.Foundations ? 1.4f : 1.6f);
            CoreBox(-0.02f, f0, -Wall - 0.08f, aLen + Wall + 0.08f, coreTop + 0.4f, -Wall, ShellPart.Formwork, true);
            CoreBox(-0.02f, f0, coreWid + Wall, aLen + Wall + 0.08f, coreTop + 0.4f, coreWid + Wall + 0.08f, ShellPart.Formwork, true);
        }

        // ---- the top: what this phase is busy with ----------------------------------------------
        float roof = L(top);
        switch (site.Phase)
        {
            case SitePhase.Foundations:
            case SitePhase.Shell:
                // starter bars up out of every column, and the next storey's walls in formwork
                foreach (var (x, z) in columns)
                    for (int q = 0; q < 4; q++)
                    {
                        float ox = (q & 1) == 0 ? -0.09f : 0.09f, oz = (q & 2) == 0 ? -0.09f : 0.09f;
                        Box(x + ox - 0.015f, roof, z + oz - 0.015f, x + ox + 0.015f, roof + 1.2f, z + oz + 0.015f, ShellPart.Rebar, false);
                    }
                for (int k = 0; k < bays.Count; k++)
                {
                    var b = bays[k];
                    if (R($"form{k}") < 0.35)
                    {
                        // two faces of panels with the wall's gap between them, standing on the edge
                        Panel(b, roof, roof + 2.7f, -Wall - 0.1f, -Wall, ShellPart.Formwork);
                        Panel(b, roof, roof + 2.7f, 0f, 0.1f, ShellPart.Formwork);
                    }
                    else
                        // starter bars along the edge, for the walls still to come
                        for (float t = 0.3f; t < BayLength(b) - 0.2f; t += 0.6f)
                        {
                            var (x, z) = Along(b, t, -Wall / 2);
                            Box(x - 0.015f, roof, z - 0.015f, x + 0.015f, roof + 0.9f, z + 0.015f, ShellPart.Rebar, false);
                        }
                }
                break;
            case SitePhase.ToppedOut:
                // the roof's parapet, round its outer edges
                foreach (var b in bays) Panel(b, roof, roof + 1.0f, -Wall, 0f, ShellPart.Concrete);
                break;
        }

        // ---- the scaffolding, round every outer wall, a lift at every slab -------------------
        if (site.Phase != SitePhase.Foundations)
            Scaffolding(site, bays, plan, R);

        return plan;

        // a closed bay: the wall under the sill, over the head, and either side of the window
        void WallBay((float X0, float Z0, float X1, float Z1, float Nx, float Nz) b, float floor, float ceiling, bool door, bool insulated)
        {
            float len = BayLength(b), win = Math.Min(door ? 1.3f : WindowWidth, len - 1.0f);
            float w0 = (len - win) / 2, w1 = w0 + win;
            float sill = door ? 0f : SillHeight;
            if (!door) WallPiece(b, 0, len, floor, floor + sill, insulated);
            WallPiece(b, 0, len, floor + HeadHeight, ceiling, insulated);
            WallPiece(b, 0, w0, floor + sill, floor + HeadHeight, insulated);
            WallPiece(b, w1, len, floor + sill, floor + HeadHeight, insulated);
            if (site.Phase == SitePhase.ToppedOut && !door)
            {
                // a window frame in the opening, glass to come
                var (x0, z0) = Along(b, w0, -Wall * 0.5f);
                var (x1, z1) = Along(b, w1, -Wall * 0.5f);
                Box(x0 - 0.03f, floor + sill, z0 - 0.03f, x1 + 0.03f, floor + sill + 0.06f, z1 + 0.03f, ShellPart.Frame, false);
                Box(x0 - 0.03f, floor + HeadHeight - 0.06f, z0 - 0.03f, x1 + 0.03f, floor + HeadHeight, z1 + 0.03f, ShellPart.Frame, false);
            }
        }

        void WallPiece((float X0, float Z0, float X1, float Z1, float Nx, float Nz) b, float t0, float t1, float y0, float y1, bool insulated)
        {
            if (t1 - t0 < 0.05f || y1 - y0 < 0.05f) return;
            var (x0, z0) = Along(b, t0, -Wall);
            var (x1, z1) = Along(b, t1, 0f);
            Box(x0, y0, z0, x1, y1, z1, ShellPart.Concrete, true);
            if (!insulated) return;
            var (i0, k0) = Along(b, t0, 0f);
            var (i1, k1) = Along(b, t1, 0.14f);
            Box(i0, y0, k0, i1, y1, k1, ShellPart.Insulation, false);
        }

        void Panel((float X0, float Z0, float X1, float Z1, float Nx, float Nz) b, float y0, float y1, float out0, float out1, ShellPart part)
        {
            var (x0, z0) = Along(b, 0, out0);
            var (x1, z1) = Along(b, BayLength(b), out1);
            Box(x0, y0, z0, x1, y1, z1, part, true);
        }

        // one flight of the switchback: steps to look at over a ramp to stand on, from a0 to a1
        // along the core, b0..b1 across it, rising from ya to yb
        void Flight(float a0, float a1, float bb0, float bb1, float ya, float yb)
        {
            int steps = Math.Max(1, (int)MathF.Ceiling((yb - ya) / 0.18f));
            float run = a1 - a0;
            for (int i = 0; i < steps; i++)
            {
                float sa = a0 + run * i / steps, sb = a0 + run * (i + 1) / steps;
                float ytop = ya + (yb - ya) * (i + 1) / steps;
                CoreBox(Math.Min(sa, sb), Math.Max(ya - 0.25f, ytop - 0.4f), bb0, Math.Max(sa, sb), ytop, bb1, ShellPart.FreshConcrete, false);
            }
            // the ramp runs from one tread before the first step to the top: at or just under the nosings
            float before = a0 - run / steps;
            var (ax, az) = C(before, bb0);
            var (bx, bz) = C(before, bb1);
            var (cx, cz) = C(a1, bb1);
            var (dx, dz) = C(a1, bb0);
            plan.Ramps.Add(new ShellRamp(new Vector3(ax, ya, az), new Vector3(bx, ya, bz), new Vector3(cx, yb, cz), new Vector3(dx, yb, dz)));
        }
    }

    private static float BayLength((float X0, float Z0, float X1, float Z1, float Nx, float Nz) b) =>
        MathF.Sqrt((b.X1 - b.X0) * (b.X1 - b.X0) + (b.Z1 - b.Z0) * (b.Z1 - b.Z0));

    /// <summary>A point t metres along a bay from its start, <paramref name="outward"/> metres out of the wall line.</summary>
    private static (float X, float Z) Along((float X0, float Z0, float X1, float Z1, float Nx, float Nz) b, float t, float outward)
    {
        float len = BayLength(b);
        float ux = (b.X1 - b.X0) / len, uz = (b.Z1 - b.Z0) / len;
        return (b.X0 + ux * t + b.Nx * outward, b.Z0 + uz * t + b.Nz * outward);
    }

    /// <summary>
    /// Standards, a deck and a guard at every slab's height, on the outside of every outer bay;
    /// the guard is solid so nobody walks off a lift, the tubes are not.
    /// </summary>
    private static void Scaffolding(ConstructionSite site, List<(float X0, float Z0, float X1, float Z1, float Nx, float Nz)> bays,
        ShellPlan plan, Func<string, double> roll)
    {
        void Box(float x0, float y0, float z0, float x1, float y1, float z1, ShellPart part, bool solid) =>
            plan.Boxes.Add(new ShellBox(new Vector3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1)),
                new Vector3(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1)), part, solid));
        float bottom = plan.Levels[0] - 0.3f, last = plan.Levels[^1];
        for (int k = 0; k < bays.Count; k++)
        {
            var b = bays[k];
            float len = BayLength(b);
            // a deck reaches round the corner: half its width past both ends of the bay
            float t0 = -ScaffoldOut, t1 = len + ScaffoldOut;
            int posts = Math.Max(1, (int)MathF.Ceiling(len / Bay));
            for (int p = 0; p <= posts; p++)
            {
                float t = len * p / posts;
                foreach (float o in new[] { ScaffoldIn, ScaffoldOut })
                {
                    var (x, z) = Along(b, t, o);
                    Box(x - 0.025f, bottom, z - 0.025f, x + 0.025f, last + 1.1f, z + 0.025f, ShellPart.Tube, false);
                }
            }
            bool netted = roll($"net{k}") < (site.Phase == SitePhase.ToppedOut ? 0.6 : 0.3);
            for (int lift = 1; lift < plan.Levels.Count; lift++)
            {
                float y = plan.Levels[lift];
                var (dx0, dz0) = Along(b, t0, ScaffoldIn);
                var (dx1, dz1) = Along(b, t1, ScaffoldOut);
                Box(dx0, y - 0.05f, dz0, dx1, y, dz1, ShellPart.Deck, true);
                // the guard: a rail at knee and hip height on the outside, solid as a sheet
                var (gx0, gz0) = Along(b, t0, ScaffoldOut);
                var (gx1, gz1) = Along(b, t1, ScaffoldOut + 0.04f);
                Box(gx0, y + 0.5f, gz0, gx1, y + 0.54f, gz1, ShellPart.Tube, false);
                Box(gx0, y + 1.0f, gz0, gx1, y + 1.04f, gz1, ShellPart.Tube, false);
                Box(gx0, y, gz0, gx1, y + 0.15f, gz1, ShellPart.Board, false);
                Box(gx0, y, gz0, gx1, y + 1.05f, gz1, netted ? ShellPart.Net : ShellPart.Invisible, true);
            }
        }
    }

    /// <summary>A rectangle less some holes, as the strips left between them: few boxes, no overlaps.</summary>
    public static List<Rect2D> Subtract(Rect2D r, IEnumerable<Rect2D> holes)
    {
        var cut = holes.Where(h => h.Overlaps(r))
            .Select(h => new Rect2D(Math.Max(h.X0, r.X0), Math.Max(h.Z0, r.Z0), Math.Min(h.X1, r.X1), Math.Min(h.Z1, r.Z1))).ToList();
        if (cut.Count == 0) return new List<Rect2D> { r };
        var xs = cut.SelectMany(h => new[] { h.X0, h.X1 }).Append(r.X0).Append(r.X1).Distinct().OrderBy(x => x).ToList();
        var result = new List<Rect2D>();
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            float x0 = xs[i], x1 = xs[i + 1];
            if (x1 - x0 < 1e-3f) continue;
            float xm = (x0 + x1) / 2;
            var blocked = cut.Where(h => h.X0 <= xm && h.X1 >= xm).OrderBy(h => h.Z0).ToList();
            float z = r.Z0;
            foreach (var h in blocked)
            {
                if (h.Z0 > z + 1e-3f) result.Add(new Rect2D(x0, z, x1, h.Z0));
                z = Math.Max(z, h.Z1);
            }
            if (r.Z1 > z + 1e-3f) result.Add(new Rect2D(x0, z, x1, r.Z1));
        }
        return result;
    }
}
