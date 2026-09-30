using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Turns an <see cref="InteriorLayout"/> into one vertex-coloured triangle soup plus a collision
/// soup, in the interior's local frame. Pure arrays, so it runs off the main thread.
///
/// <para>
/// Every room is drawn as its own box <i>from the inside</i>, its walls set in by
/// <see cref="InteriorGenerator.WallInset"/>: two neighbours' faces are then a wall's thickness
/// apart and a doorway only has to cut the same hole in both and line it with reveals. Nobody
/// ever sees a wall from outside the building — it is floating in the dark under the terrain —
/// so there is no outer skin at all.
/// </para>
///
/// <para>
/// Colours go through <c>SrgbToLinear</c> (baked vertex colours are never converted by Godot).
/// Alpha 0 marks glass: the shader draws it as the sky of the hour (<c>world_sky</c>), unlit.
/// </para>
/// </summary>
public static class InteriorMeshBuilder
{
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, Vector3[] Collision);

    private sealed class Scratch
    {
        public readonly List<Vector3> V = new();
        public readonly List<Color> C = new();
        public readonly List<Vector3> Col = new();

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col, bool collide)
        {
            V.Add(a); V.Add(b); V.Add(c);
            C.Add(col); C.Add(col); C.Add(col);
            if (collide) { Col.Add(a); Col.Add(b); Col.Add(c); }
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, bool collide = true)
        {
            Tri(a, b, c, col, collide);
            Tri(a, c, d, col, collide);
        }

        /// <summary>Quad with a darker lower edge — cheap ambient occlusion where wall meets floor.</summary>
        public void WallQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, float y0, float height)
        {
            Color Shade(Vector3 p) =>
                (col * Mathf.Lerp(0.78f, 1.0f, Mathf.Clamp((p.Y - y0) / Math.Max(0.1f, height * 0.5f), 0, 1))) with { A = col.A };
            V.Add(a); V.Add(b); V.Add(c); V.Add(a); V.Add(c); V.Add(d);
            C.Add(Shade(a)); C.Add(Shade(b)); C.Add(Shade(c)); C.Add(Shade(a)); C.Add(Shade(c)); C.Add(Shade(d));
            Col.Add(a); Col.Add(b); Col.Add(c); Col.Add(a); Col.Add(c); Col.Add(d);
        }

        /// <summary>Box from min to max corner, rotated by <paramref name="basis"/> about <paramref name="pivot"/>.</summary>
        public void Box(Vector3 min, Vector3 max, Color col, bool collide, Basis basis, Vector3 pivot)
        {
            Vector3 P(float x, float y, float z) => pivot + basis * (new Vector3(x, y, z) - pivot);
            var p000 = P(min.X, min.Y, min.Z); var p100 = P(max.X, min.Y, min.Z);
            var p010 = P(min.X, max.Y, min.Z); var p110 = P(max.X, max.Y, min.Z);
            var p001 = P(min.X, min.Y, max.Z); var p101 = P(max.X, min.Y, max.Z);
            var p011 = P(min.X, max.Y, max.Z); var p111 = P(max.X, max.Y, max.Z);
            var side = col * 0.86f; side.A = col.A;
            Quad(p010, p110, p111, p011, col, collide);          // top
            Quad(p000, p100, p110, p010, side, collide);         // -z
            Quad(p001, p011, p111, p101, side, collide);         // +z
            Quad(p000, p010, p011, p001, side * 0.95f, collide); // -x
            Quad(p100, p101, p111, p110, side * 0.95f, collide); // +x
            if (min.Y > 0.01f) Quad(p000, p001, p101, p100, side * 0.7f, collide);
        }

        public void Box(Vector3 min, Vector3 max, Color col, bool collide = true) =>
            Box(min, max, col, collide, Basis.Identity, Vector3.Zero);
    }

    private static Color C(float r, float g, float b) => new Color(r, g, b).SrgbToLinear();

    private static readonly Color Glass = new(0.80f, 0.88f, 0.96f, 0f);
    private static readonly Color Rail = C(0.30f, 0.24f, 0.18f);
    private static readonly Color StairWood = C(0.55f, 0.40f, 0.26f);
    private static readonly Color Concrete = C(0.60f, 0.60f, 0.58f);

    private static (Color Floor, Color Wall, Color Ceiling) Palette(RoomType t) => t switch
    {
        RoomType.Living or RoomType.Dining => (C(0.56f, 0.40f, 0.26f), C(0.90f, 0.86f, 0.76f), C(0.95f, 0.94f, 0.90f)),
        RoomType.Kitchen => (C(0.72f, 0.70f, 0.66f), C(0.86f, 0.88f, 0.82f), C(0.95f, 0.95f, 0.93f)),
        RoomType.Bedroom => (C(0.62f, 0.52f, 0.46f), C(0.82f, 0.86f, 0.90f), C(0.95f, 0.95f, 0.95f)),
        RoomType.Bathroom or RoomType.WC => (C(0.78f, 0.80f, 0.82f), C(0.70f, 0.82f, 0.88f), C(0.95f, 0.96f, 0.97f)),
        RoomType.Office or RoomType.Classroom => (C(0.46f, 0.48f, 0.52f), C(0.88f, 0.88f, 0.86f), C(0.93f, 0.93f, 0.93f)),
        RoomType.Shop => (C(0.80f, 0.78f, 0.74f), C(0.94f, 0.93f, 0.90f), C(0.96f, 0.96f, 0.96f)),
        RoomType.Nave => (C(0.66f, 0.62f, 0.56f), C(0.90f, 0.88f, 0.82f), C(0.80f, 0.76f, 0.70f)),
        RoomType.Workshop or RoomType.Garage or RoomType.Storage => (Concrete, C(0.72f, 0.72f, 0.70f), C(0.64f, 0.64f, 0.64f)),
        RoomType.Barn => (C(0.46f, 0.38f, 0.28f), C(0.50f, 0.38f, 0.26f), C(0.40f, 0.31f, 0.22f)),
        RoomType.Lobby => (C(0.70f, 0.66f, 0.60f), C(0.86f, 0.84f, 0.80f), C(0.94f, 0.94f, 0.92f)),
        RoomType.Porch => (C(0.58f, 0.56f, 0.52f), C(0.84f, 0.82f, 0.76f), C(0.70f, 0.66f, 0.60f)),
        RoomType.Belfry => (C(0.46f, 0.36f, 0.26f), C(0.70f, 0.67f, 0.61f), C(0.40f, 0.31f, 0.22f)),
        _ => (C(0.52f, 0.38f, 0.25f), C(0.88f, 0.84f, 0.76f), C(0.95f, 0.94f, 0.90f)), // hall, landing
    };

    public static MeshData Build(InteriorLayout l)
    {
        var s = new Scratch();
        float h = l.StoreyHeight;
        float clear = h - InteriorGenerator.Slab;

        List<RectPlan> HolesOf(int f) => f < l.Floors.Count ? l.Floors[f].Holes : new List<RectPlan>();
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var floor = l.Floors[f];
            float y0 = f * h;
            var above = HolesOf(f + 1);
            foreach (var room in floor.Rooms)
                Room(s, room, y0, l.ClearOf(room), floor.Holes, HolesOf(f + room.Span));
            if (floor.Flight is { } flight) Flight(s, flight, y0, h);
            foreach (var r in floor.Rails)
                s.Box(new Vector3(Math.Min(r.X0, r.X1) - 0.03f, y0, Math.Min(r.Z0, r.Z1)),
                    new Vector3(Math.Max(r.X0, r.X1) + 0.03f, y0 + 1.0f, Math.Max(r.Z0, r.Z1)), Rail);
            // the shaft between this ceiling and the next floor
            foreach (var hole in above)
            {
                float a = y0 + clear, b = y0 + h;
                var wall = StairWood * 0.8f;
                s.Quad(new(hole.X0, a, hole.Z0), new(hole.X1, a, hole.Z0), new(hole.X1, b, hole.Z0), new(hole.X0, b, hole.Z0), wall);
                s.Quad(new(hole.X0, a, hole.Z1), new(hole.X1, a, hole.Z1), new(hole.X1, b, hole.Z1), new(hole.X0, b, hole.Z1), wall);
                s.Quad(new(hole.X0, a, hole.Z0), new(hole.X0, a, hole.Z1), new(hole.X0, b, hole.Z1), new(hole.X0, b, hole.Z0), wall);
                s.Quad(new(hole.X1, a, hole.Z0), new(hole.X1, a, hole.Z1), new(hole.X1, b, hole.Z1), new(hole.X1, b, hole.Z0), wall);
            }
        }

        foreach (var p in l.Furniture)
            Furniture(s, p, p.Floor * h + p.Lift);

        return new MeshData(s.V.ToArray(), s.C.ToArray(), s.Col.ToArray());
    }

    /// <summary>
    /// A front door's leaf, in its hinge's frame: x from the hinge across the opening (toward -x
    /// when <paramref name="mirrored"/>), y from <paramref name="bottom"/> up, z
    /// 0..<paramref name="thickness"/> toward the street. A knob on the room side near the free edge,
    /// and the facade's handle and colour (<see cref="BuildingFootprint"/>) on the street side, so
    /// the leaf seen swinging through the portal is the one on the facade.
    /// </summary>
    public static MeshData Leaf(float width, float height, float thickness, BuildingKind kind,
        bool mirrored = false, float bottom = 0f)
    {
        var s = new Scratch();
        void Box(float x0, float y0, float z0, float x1, float y1, float z1, Color col) =>
            s.Box(new Vector3(mirrored ? -x1 : x0, y0, z0), new Vector3(mirrored ? -x0 : x1, y1, z1), col, false);

        var wood = BuildingFootprint.DoorLeafColorFor(kind);
        Box(0.01f, bottom, 0, width - 0.01f, height - 0.01f, thickness, wood);
        // two raised panels, so it reads as a door and not a plank
        var panel = wood * 0.85f;
        panel.A = 1;
        float pw = width - 0.3f, ph = height - bottom;
        Box(0.15f, bottom + 0.2f, -0.01f, 0.15f + pw, bottom + ph * 0.45f, 0, panel);
        Box(0.15f, bottom + ph * 0.55f, -0.01f, 0.15f + pw, height - 0.2f, 0, panel);
        var knob = C(0.80f, 0.70f, 0.30f);
        Box(width - 0.14f, 1.0f, -0.06f, width - 0.06f, 1.08f, 0, knob);
        // where BuildingMeshBuilder.AppendDoor puts it: 0.7 of the half-width from the middle
        // (a pair's leaf: 0.15 of it, both handles near the meeting stiles)
        float hx = width * 0.85f;
        Box(hx - 0.04f, 1.0f, thickness, hx + 0.04f, 1.08f, thickness + 0.05f, BuildingFootprint.DoorHandleColor);
        return new MeshData(s.V.ToArray(), s.C.ToArray(), s.Col.ToArray());
    }

    // ---- rooms -------------------------------------------------------------------------------

    private static void Room(Scratch s, RoomPlan r, float y0, float clear, List<RectPlan> holes, List<RectPlan> ceilingHoles)
    {
        var (floorCol, wallCol, ceilCol) = Palette(r.Type);
        const float t = InteriorGenerator.WallInset;
        var inner = new RectPlan(r.X0 + t, r.Z0 + t, r.X1 - t, r.Z1 - t);
        float top = y0 + clear;

        foreach (var piece in Subtract(inner, holes))
            s.Quad(new(piece.X0, y0, piece.Z0), new(piece.X1, y0, piece.Z0),
                new(piece.X1, y0, piece.Z1), new(piece.X0, y0, piece.Z1), floorCol);
        foreach (var piece in Subtract(inner, ceilingHoles))
            s.Quad(new(piece.X0, top, piece.Z0), new(piece.X0, top, piece.Z1),
                new(piece.X1, top, piece.Z1), new(piece.X1, top, piece.Z0), ceilCol);

        for (int side = 0; side < 4; side++)
            Wall(s, r, (Side)side, inner, y0, clear, wallCol);
    }

    /// <summary>Maps (along, height, depth-out-of-the-room) on one wall to interior space.</summary>
    private static Vector3 OnWall(Side side, RectPlan inner, float along, float y, float outward) => side switch
    {
        Side.Front => new Vector3(along, y, inner.Z0 - outward),
        Side.Back => new Vector3(along, y, inner.Z1 + outward),
        Side.Left => new Vector3(inner.X0 - outward, y, along),
        _ => new Vector3(inner.X1 + outward, y, along),
    };

    private static void Wall(Scratch s, RoomPlan r, Side side, RectPlan inner, float y0, float clear, Color col)
    {
        bool along = side is Side.Front or Side.Back;
        float a = along ? inner.X0 : inner.Z0, b = along ? inner.X1 : inner.Z1;
        float top = y0 + clear;
        var cuts = r.Openings.Where(o => o.Side == side)
            .Select(o => (S0: Math.Max(a, o.Center - o.Width / 2), S1: Math.Min(b, o.Center + o.Width / 2), O: o))
            .Where(c => c.S1 - c.S0 > 0.05f)
            .OrderBy(c => c.S0).ToList();

        Vector3 P(float u, float y) => OnWall(side, inner, u, y, 0);
        void Panel(float u0, float u1, float ya, float yb)
        {
            if (u1 - u0 < 1e-3f || yb - ya < 1e-3f) return;
            s.WallQuad(P(u0, ya), P(u1, ya), P(u1, yb), P(u0, yb), col, y0, clear);
        }

        float cursor = a;
        foreach (var (s0, s1, o) in cuts)
        {
            if (s0 < cursor) continue; // overlapping cuts: keep the first
            Panel(cursor, s0, y0, top);
            float ob = y0 + o.Bottom, ot = Math.Min(top, y0 + o.Top);
            Panel(s0, s1, y0, ob);
            Panel(s0, s1, ot, top);

            // reveals: to the shared wall line for a doorway (the neighbour closes the rest),
            // deeper for an exterior opening so it reads as a real wall's thickness
            float depth = o.Kind is OpeningKind.Door or OpeningKind.Arch ? InteriorGenerator.WallInset : InteriorGenerator.WallInset + 0.22f;
            var reveal = col * 0.9f;
            reveal.A = 1;
            Vector3 R(float u, float y, float d) => OnWall(side, inner, u, y, d);
            s.Quad(R(s0, ob, 0), R(s0, ot, 0), R(s0, ot, depth), R(s0, ob, depth), reveal);
            s.Quad(R(s1, ob, 0), R(s1, ob, depth), R(s1, ot, depth), R(s1, ot, 0), reveal);
            s.Quad(R(s0, ot, 0), R(s1, ot, 0), R(s1, ot, depth), R(s0, ot, depth), reveal);
            if (o.Bottom > 0.01f)
                s.Quad(R(s0, ob, 0), R(s0, ob, depth), R(s1, ob, depth), R(s1, ob, 0), reveal);
            else
                // threshold: floors stop at the wall face, so the doorway needs its own
                s.Quad(R(s0, y0, 0), R(s0, y0, depth), R(s1, y0, depth), R(s1, y0, 0), Palette(r.Type).Floor);

            if (o.Kind == OpeningKind.Window)
            {
                s.Quad(R(s0, ob, depth), R(s1, ob, depth), R(s1, ot, depth), R(s0, ot, depth), Glass);
                // a mullion and a transom, so it reads as a window and not a hole
                float mid = (s0 + s1) / 2;
                var frame = C(0.92f, 0.92f, 0.90f);
                s.Quad(R(mid - 0.03f, ob, depth - 0.01f), R(mid + 0.03f, ob, depth - 0.01f),
                    R(mid + 0.03f, ot, depth - 0.01f), R(mid - 0.03f, ot, depth - 0.01f), frame, false);
                float tr = ob + (ot - ob) * 0.7f;
                s.Quad(R(s0, tr - 0.03f, depth - 0.01f), R(s1, tr - 0.03f, depth - 0.01f),
                    R(s1, tr + 0.03f, depth - 0.01f), R(s0, tr + 0.03f, depth - 0.01f), frame, false);
            }
            // the front door's leaf is not baked: it swings (DoorLeaf, see Leaf below)
            cursor = s1;
        }
        Panel(cursor, b, y0, top);
    }

    /// <summary>Rectangle minus a set of rectangles, as a list of rectangles (guillotine cuts).</summary>
    private static List<RectPlan> Subtract(RectPlan r, List<RectPlan> holes)
    {
        var pieces = new List<RectPlan> { r };
        foreach (var h in holes)
        {
            var next = new List<RectPlan>();
            foreach (var p in pieces)
            {
                if (!p.Overlaps(h, 0)) { next.Add(p); continue; }
                if (h.Z0 > p.Z0) next.Add(new RectPlan(p.X0, p.Z0, p.X1, h.Z0));
                if (h.Z1 < p.Z1) next.Add(new RectPlan(p.X0, h.Z1, p.X1, p.Z1));
                float z0 = Math.Max(p.Z0, h.Z0), z1 = Math.Min(p.Z1, h.Z1);
                if (h.X0 > p.X0) next.Add(new RectPlan(p.X0, z0, h.X0, z1));
                if (h.X1 < p.X1) next.Add(new RectPlan(h.X1, z0, p.X1, z1));
            }
            pieces = next;
        }
        return pieces;
    }

    // ---- stairs ------------------------------------------------------------------------------

    /// <summary>
    /// Solid steps to look at, a ramp to stand on. A CharacterBody climbing real risers catches on
    /// every nosing; the ramp runs from one tread before the first step to the top landing, which
    /// keeps it at or just under the nosings the whole way up.
    /// </summary>
    private static void Flight(Scratch s, FlightPlan f, float y0, float h)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(h / 0.19f));
        float dir = Math.Sign(f.ZTop - f.ZBottom);
        float run = Math.Abs(f.ZTop - f.ZBottom);
        float tread = run / steps;
        float x0 = f.X0 + 0.02f, x1 = f.X1 - 0.02f;
        for (int i = 0; i < steps; i++)
        {
            float za = f.ZBottom + dir * tread * i, zb = f.ZBottom + dir * tread * (i + 1);
            var shade = i % 2 == 0 ? StairWood : StairWood * 0.93f;
            shade.A = 1;
            s.Box(new Vector3(x0, y0, Math.Min(za, zb)), new Vector3(x1, y0 + h * (i + 1) / steps, Math.Max(za, zb)), shade, false);
        }
        float zs = f.ZBottom - dir * tread;
        var a = new Vector3(f.X0, y0, zs);
        var b = new Vector3(f.X1, y0, zs);
        var c = new Vector3(f.X1, y0 + h, f.ZTop);
        var d = new Vector3(f.X0, y0 + h, f.ZTop);
        s.Col.Add(a); s.Col.Add(b); s.Col.Add(c);
        s.Col.Add(a); s.Col.Add(c); s.Col.Add(d);
    }

    // ---- furniture ---------------------------------------------------------------------------

    private static void Furniture(Scratch s, FurniturePlan p, float y0)
    {
        // authored with its back to -Z, centred on the origin, then turned and moved
        var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
        var at = new Vector3(p.X, y0, p.Z);
        float w = p.W / 2, d = p.D / 2, H = p.H;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col, bool collide = false)
        {
            var min = new Vector3(xa, ya, za);
            var max = new Vector3(xb, yb, zb);
            s.Box(at + min, at + max, col, collide, basis, at);
        }
        // one collision box for the whole piece: cheaper than per-part, and what a player hits
        // anyway; hung and wall-mounted pieces are overhead, and the chancel step has its own
        bool solid = p.Type is not (FurnitureType.Rug or FurnitureType.Plant or FurnitureType.Bell
            or FurnitureType.Cross or FurnitureType.Dais);
        if (solid) CollisionBox(s, at, basis, new Vector3(-w, 0, -d), new Vector3(w, H, d));

        var wood = C(0.52f, 0.36f, 0.22f);
        var darkWood = C(0.34f, 0.22f, 0.14f);
        var white = C(0.92f, 0.92f, 0.90f);
        var metal = C(0.62f, 0.64f, 0.66f);
        var dark = C(0.12f, 0.12f, 0.13f);
        switch (p.Type)
        {
            case FurnitureType.Bed:
            case FurnitureType.SingleBed:
                B(-w, 0, -d, w, 0.32f, d, wood);
                B(-w + 0.04f, 0.32f, -d + 0.04f, w - 0.04f, 0.5f, d - 0.04f, white);
                B(-w + 0.08f, 0.5f, -d + 0.1f, w - 0.08f, 0.58f, -d + 0.45f, white);
                B(-w + 0.02f, 0.44f, -d + 0.55f, w - 0.02f, 0.54f, d - 0.02f, C(0.36f, 0.48f, 0.70f));
                B(-w, 0, -d, w, 0.95f, -d + 0.06f, darkWood);
                break;
            case FurnitureType.Wardrobe:
                B(-w, 0, -d, w, H, d, wood);
                B(-0.1f, 1.0f, d, -0.05f, 1.15f, d + 0.03f, metal);
                B(0.05f, 1.0f, d, 0.1f, 1.15f, d + 0.03f, metal);
                break;
            case FurnitureType.Nightstand:
            case FurnitureType.Crate:
                B(-w, 0, -d, w, H, d, p.Type == FurnitureType.Crate ? C(0.62f, 0.48f, 0.30f) : wood);
                break;
            case FurnitureType.HayBale:
                B(-w, 0, -d, w, H, d, C(0.84f, 0.74f, 0.40f));
                break;
            case FurnitureType.Sofa:
                var cloth = C(0.44f, 0.30f, 0.26f);
                B(-w, 0, -d, w, 0.42f, d, cloth);
                B(-w, 0.42f, -d, w, H, -d + 0.22f, cloth * 0.9f);
                B(-w, 0.42f, -d, -w + 0.18f, 0.62f, d, cloth * 0.9f);
                B(w - 0.18f, 0.42f, -d, w, 0.62f, d, cloth * 0.9f);
                break;
            case FurnitureType.CoffeeTable:
            case FurnitureType.Table:
            case FurnitureType.Desk:
            case FurnitureType.Workbench:
                var top = p.Type == FurnitureType.Workbench ? C(0.60f, 0.50f, 0.36f)
                    : p.Type == FurnitureType.Desk ? C(0.78f, 0.74f, 0.68f) : wood;
                B(-w, H - 0.05f, -d, w, H, d, top);
                foreach (var (lx, lz) in new[] { (-w + 0.05f, -d + 0.05f), (w - 0.1f, -d + 0.05f), (-w + 0.05f, d - 0.1f), (w - 0.1f, d - 0.1f) })
                    B(lx, 0, lz, lx + 0.05f, H - 0.05f, lz + 0.05f, darkWood);
                if (p.Type == FurnitureType.Desk && p.W > 1.3f)
                    B(-0.25f, H, -d + 0.05f, 0.25f, H + 0.35f, -d + 0.1f, dark); // a monitor
                break;
            case FurnitureType.Counter:
                B(-w, 0, -d, w, 0.86f, d, white);
                B(-w, 0.86f, -d, w, 0.9f, d, C(0.30f, 0.30f, 0.32f));
                B(-w, 1.45f, -d, w, 2.1f, -d + 0.35f, white); // wall cupboards
                break;
            case FurnitureType.Stove:
                B(-w, 0, -d, w, H, d, white * 0.95f);
                B(-w + 0.08f, H, -d + 0.08f, w - 0.08f, H + 0.01f, d - 0.08f, dark);
                break;
            case FurnitureType.Fridge:
            case FurnitureType.Altar:
                B(-w, 0, -d, w, H, d, white);
                if (p.Type == FurnitureType.Altar) B(-w - 0.02f, H - 0.05f, -d - 0.02f, w + 0.02f, H + 0.01f, d + 0.02f, C(0.70f, 0.14f, 0.16f));
                break;
            case FurnitureType.Toilet:
                B(-w, 0, -d + 0.15f, w, 0.42f, d, white);
                B(-w, 0.3f, -d, w, 0.75f, -d + 0.18f, white);
                break;
            case FurnitureType.Sink:
                B(-0.08f, 0, -d, 0.08f, 0.75f, -d + 0.2f, white);
                B(-w, 0.72f, -d, w, H, d, white);
                B(-0.02f, H, -d + 0.02f, 0.02f, H + 0.2f, -d + 0.06f, metal);
                break;
            case FurnitureType.Bathtub:
                B(-w, 0, -d, w, H, d, white);
                B(-w + 0.06f, H, -d + 0.06f, w - 0.06f, H + 0.005f, d - 0.06f, C(0.72f, 0.84f, 0.90f));
                break;
            case FurnitureType.Shelf:
            case FurnitureType.Rack:
                var frame = p.Type == FurnitureType.Rack ? metal : wood;
                B(-w, 0, -d, -w + 0.04f, H, d, frame);
                B(w - 0.04f, 0, -d, w, H, d, frame);
                int boards = Math.Max(2, (int)(H / 0.45f));
                for (int i = 0; i <= boards; i++)
                {
                    float y = H * i / boards;
                    B(-w, y, -d, w, y + 0.03f, d, frame);
                    if (i < boards)
                    {
                        // goods/books: a few coloured blocks per board, varied by position
                        for (int k = 0; k < 3; k++)
                        {
                            float x = -w + 0.1f + (2 * w - 0.2f) * k / 3f;
                            float hue = (float)((p.X * 7.1 + p.Z * 3.3 + i * 0.37 + k * 0.23) % 1.0);
                            var goods = Color.FromHsv(Math.Abs(hue), 0.45f, 0.7f).SrgbToLinear();
                            B(x, y + 0.03f, -d + 0.05f, x + (2 * w - 0.3f) / 3.5f, y + 0.03f + Math.Min(0.3f, H / boards - 0.08f), d - 0.05f, goods);
                        }
                    }
                }
                break;
            case FurnitureType.ShopCounter:
                B(-w, 0, -d, w, H, d, C(0.62f, 0.44f, 0.30f));
                B(-w, H, -d, w, H + 0.03f, d, dark);
                break;
            case FurnitureType.Pew:
                B(-w, 0.42f, -d + 0.1f, w, 0.47f, d, wood);
                B(-w, 0.42f, -d, w, H, -d + 0.08f, wood);
                B(-w, 0, -d, -w + 0.06f, 0.42f, d, darkWood);
                B(w - 0.06f, 0, -d, w, 0.42f, d, darkWood);
                break;
            case FurnitureType.Car:
                var paint = C(0.62f, 0.14f, 0.12f);
                B(-w, 0.25f, -d, w, 0.85f, d, paint);
                B(-w + 0.1f, 0.85f, -d + 1.0f, w - 0.1f, H, d - 1.4f, C(0.40f, 0.52f, 0.62f));
                foreach (float wz in new[] { -d + 0.7f, d - 0.7f })
                {
                    B(-w - 0.02f, 0, wz - 0.32f, -w + 0.2f, 0.62f, wz + 0.32f, dark);
                    B(w - 0.2f, 0, wz - 0.32f, w + 0.02f, 0.62f, wz + 0.32f, dark);
                }
                break;
            case FurnitureType.Blackboard:
                B(-w, 0.9f, -d, w, H, d, C(0.16f, 0.30f, 0.22f));
                B(-w, 0.86f, -d, w, 0.9f, d + 0.06f, wood);
                break;
            case FurnitureType.Tv:
                B(-w, 0, -d, w, 0.45f, d, darkWood);
                B(-w * 0.8f, 0.45f, -0.04f, w * 0.8f, H, 0.04f, dark);
                break;
            case FurnitureType.Rug:
                B(-w, 0.001f, -d, w, 0.02f, d, C(0.62f, 0.24f, 0.22f));
                break;
            case FurnitureType.Plant:
                B(-0.15f, 0, -0.15f, 0.15f, 0.3f, 0.15f, C(0.62f, 0.34f, 0.22f));
                B(-w, 0.3f, -d, w, H, d, C(0.24f, 0.46f, 0.22f));
                break;
            case FurnitureType.Chair:
                B(-w, 0.42f, -d, w, 0.46f, d, wood);
                B(-w, 0.46f, -d, w, 0.9f, -d + 0.05f, wood);
                break;
            case FurnitureType.Dais:
            {
                var stone = C(0.62f, 0.58f, 0.52f);
                B(-w, 0, -d, w, H, d, stone);
                B(-w, 0, -d - 0.4f, w, H * 0.5f, -d, stone * 0.92f); // a half step in front
                // stood on, not bumped into: the top, and a ramp over the step, which a body
                // walks up where it would catch on a riser
                Vector3 P(float x, float y, float z) => at + basis * new Vector3(x, y, z);
                void Col(Vector3 a, Vector3 b, Vector3 c, Vector3 dd)
                {
                    s.Col.Add(a); s.Col.Add(b); s.Col.Add(c);
                    s.Col.Add(a); s.Col.Add(c); s.Col.Add(dd);
                }
                Col(P(-w, H, -d), P(w, H, -d), P(w, H, d), P(-w, H, d));
                Col(P(-w, 0, -d - 0.9f), P(w, 0, -d - 0.9f), P(w, H, -d), P(-w, H, -d));
                Col(P(-w, 0, -d), P(-w, H, -d), P(-w, H, d), P(-w, 0, d));
                Col(P(w, 0, -d), P(w, 0, d), P(w, H, d), P(w, H, -d));
                break;
            }
            case FurnitureType.Lectern:
                B(-0.08f, 0, -0.08f, 0.08f, H - 0.12f, 0.08f, darkWood);
                B(-w, 0, -d, w, 0.06f, d, darkWood);
                B(-w, H - 0.14f, -d, w, H, d, wood);
                B(-w * 0.8f, H, -d + 0.08f, w * 0.8f, H + 0.03f, d - 0.12f, white); // an open book
                break;
            case FurnitureType.Cross:
            {
                var gilt = C(0.78f, 0.64f, 0.30f);
                B(-0.07f, 0, -d, 0.07f, H, d, gilt);
                B(-w, H * 0.62f, -d, w, H * 0.62f + 0.14f, d, gilt);
                break;
            }
            case FurnitureType.Bell:
            {
                var bronze = C(0.55f, 0.42f, 0.22f);
                // a yoke up to the ceiling beam, then the bell flaring out to its lip
                B(-w * 0.9f, H * 0.92f, -0.08f, w * 0.9f, H * 1.05f + 0.25f, 0.08f, darkWood);
                B(-w * 0.28f, H * 0.78f, -d * 0.28f, w * 0.28f, H * 0.92f, d * 0.28f, bronze);
                B(-w * 0.4f, H * 0.5f, -d * 0.4f, w * 0.4f, H * 0.78f, d * 0.4f, bronze);
                B(-w * 0.46f, H * 0.2f, -d * 0.46f, w * 0.46f, H * 0.5f, d * 0.46f, bronze);
                B(-w * 0.5f, H * 0.08f, -d * 0.5f, w * 0.5f, H * 0.2f, d * 0.5f, bronze * 0.9f);
                B(-0.06f, 0, -0.06f, 0.06f, H * 0.12f, 0.06f, dark); // the clapper
                break;
            }
        }
    }

    private static void CollisionBox(Scratch s, Vector3 at, Basis basis, Vector3 min, Vector3 max)
    {
        Vector3 P(float x, float y, float z) => at + basis * new Vector3(x, y, z);
        var q = new[]
        {
            (P(min.X, max.Y, min.Z), P(max.X, max.Y, min.Z), P(max.X, max.Y, max.Z), P(min.X, max.Y, max.Z)),
            (P(min.X, min.Y, min.Z), P(max.X, min.Y, min.Z), P(max.X, max.Y, min.Z), P(min.X, max.Y, min.Z)),
            (P(min.X, min.Y, max.Z), P(max.X, min.Y, max.Z), P(max.X, max.Y, max.Z), P(min.X, max.Y, max.Z)),
            (P(min.X, min.Y, min.Z), P(min.X, min.Y, max.Z), P(min.X, max.Y, max.Z), P(min.X, max.Y, min.Z)),
            (P(max.X, min.Y, min.Z), P(max.X, min.Y, max.Z), P(max.X, max.Y, max.Z), P(max.X, max.Y, min.Z)),
        };
        foreach (var (a, b, c, d) in q)
        {
            s.Col.Add(a); s.Col.Add(b); s.Col.Add(c);
            s.Col.Add(a); s.Col.Add(c); s.Col.Add(d);
        }
    }
}
