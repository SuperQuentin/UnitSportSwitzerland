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
public static partial class InteriorMeshBuilder
{
    /// <summary>The static soup, its collision, and the figures that move (a church's rat and congregation, #370).</summary>
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, Vector3[] Collision, Figure[]? Figures = null);

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
        RoomType.Laundry or RoomType.Cellar or RoomType.Pantry => (Concrete, C(0.80f, 0.80f, 0.77f), C(0.70f, 0.70f, 0.68f)),
        RoomType.Shelter => (C(0.52f, 0.53f, 0.52f), C(0.66f, 0.67f, 0.66f), C(0.58f, 0.59f, 0.58f)),
        RoomType.GuestRoom => (C(0.60f, 0.48f, 0.38f), C(0.88f, 0.84f, 0.74f), C(0.94f, 0.93f, 0.90f)),
        RoomType.HomeCinema => (C(0.30f, 0.14f, 0.14f), C(0.22f, 0.20f, 0.24f), C(0.14f, 0.14f, 0.16f)),
        RoomType.Carnotzet => (C(0.50f, 0.38f, 0.30f), C(0.80f, 0.74f, 0.62f), C(0.42f, 0.30f, 0.20f)),
        RoomType.MusicRoom => (C(0.34f, 0.32f, 0.36f), C(0.30f, 0.30f, 0.34f), C(0.26f, 0.26f, 0.30f)),
        RoomType.Playroom => (C(0.60f, 0.70f, 0.52f), C(0.96f, 0.86f, 0.62f), C(0.95f, 0.95f, 0.92f)),
        RoomType.Study => (C(0.42f, 0.28f, 0.18f), C(0.70f, 0.78f, 0.70f), C(0.92f, 0.92f, 0.88f)),
        RoomType.BankHall => (C(0.82f, 0.80f, 0.76f), C(0.84f, 0.82f, 0.74f), C(0.94f, 0.94f, 0.92f)),
        RoomType.Vault => (C(0.40f, 0.41f, 0.43f), C(0.50f, 0.52f, 0.55f), C(0.44f, 0.45f, 0.47f)),
        // #497 industrial sites: sealed concrete under a painted sheet-metal shell
        RoomType.WarehouseHall or RoomType.ProductionHall => (C(0.58f, 0.58f, 0.57f), C(0.72f, 0.74f, 0.76f), C(0.52f, 0.53f, 0.55f)),
        RoomType.TruckBay or RoomType.ServiceBay => (C(0.50f, 0.50f, 0.50f), C(0.78f, 0.79f, 0.80f), C(0.54f, 0.55f, 0.56f)),
        // the one industrial room meant to look expensive
        RoomType.Showroom => (C(0.86f, 0.86f, 0.87f), C(0.94f, 0.94f, 0.94f), C(0.96f, 0.96f, 0.96f)),
        RoomType.ControlRoom or RoomType.Dispatch => (C(0.46f, 0.48f, 0.52f), C(0.86f, 0.87f, 0.84f), C(0.93f, 0.93f, 0.93f)),
        RoomType.LockerRoom => (C(0.56f, 0.58f, 0.60f), C(0.74f, 0.80f, 0.82f), C(0.92f, 0.92f, 0.92f)),
        RoomType.BreakRoom => (C(0.72f, 0.68f, 0.62f), C(0.90f, 0.88f, 0.80f), C(0.95f, 0.95f, 0.93f)),
        RoomType.PartsStore => (Concrete, C(0.76f, 0.76f, 0.73f), C(0.66f, 0.66f, 0.64f)),
        RoomType.PaintBooth => (C(0.62f, 0.64f, 0.66f), C(0.92f, 0.93f, 0.94f), C(0.90f, 0.91f, 0.92f)),
        // #501: sealed grey concrete, a pale shell and the lit ceiling of a big-box shop floor
        RoomType.IkeaMarket => (C(0.56f, 0.56f, 0.57f), C(0.93f, 0.93f, 0.92f), C(0.96f, 0.96f, 0.95f)),
        // #557 apartment blocks: a brushed-steel cabin, a bare concrete car park and boiler room
        RoomType.Elevator => (C(0.36f, 0.36f, 0.38f), C(0.70f, 0.72f, 0.74f), C(0.86f, 0.86f, 0.84f)),
        RoomType.CarPark => (C(0.50f, 0.50f, 0.49f), C(0.74f, 0.74f, 0.72f), C(0.62f, 0.62f, 0.61f)),
        RoomType.TechRoom => (Concrete, C(0.78f, 0.78f, 0.75f), C(0.68f, 0.68f, 0.66f)),
        RoomType.Corridor => (C(0.62f, 0.58f, 0.52f), C(0.88f, 0.86f, 0.80f), C(0.94f, 0.94f, 0.92f)),
        // #571: a terrazzo floor and pale painted walls, as every Swiss Treppenhaus
        RoomType.Stairwell => (C(0.70f, 0.68f, 0.64f), C(0.92f, 0.90f, 0.84f), C(0.95f, 0.95f, 0.93f)),
        _ => (C(0.52f, 0.38f, 0.25f), C(0.88f, 0.84f, 0.76f), C(0.95f, 0.94f, 0.90f)), // hall, landing
    };

    public static MeshData Build(InteriorLayout l)
    {
        Core.ShowcaseTrace.Mark();
        var s = new Scratch();
        float h = l.StoreyHeight;
        float clear = h - InteriorGenerator.Slab;

        List<RectPlan> HolesOf(int f) => f < l.Floors.Count ? l.Floors[f].Holes : new List<RectPlan>();
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var floor = l.Floors[f];
            float y0 = l.FloorY(f);
            var above = HolesOf(f + 1);
            foreach (var room in floor.Rooms)
                Room(s, room, y0, l.ClearOf(room), floor.Holes, HolesOf(f + room.Span));
            foreach (var flight in floor.AllFlights()) Flight(s, flight, y0, h);
            // a stairwell's half landings (#571): a stone slab, stood on, its underside seen from below
            foreach (var g in floor.Landings)
                s.Box(new Vector3(g.X0 + 0.02f, y0 + h * g.Level - 0.22f, g.Z0), new Vector3(g.X1 - 0.02f, y0 + h * g.Level, g.Z1 - 0.02f), StairStone);
            foreach (var r in floor.Rails)
            {
                var lo = new Vector3(Math.Min(r.X0, r.X1) - 0.03f, y0, Math.Min(r.Z0, r.Z1));
                var hi = new Vector3(Math.Max(r.X0, r.X1) + 0.03f, y0 + 1.0f, Math.Max(r.Z0, r.Z1));
                // in a stairwell (#571) the guard is the parapet's own: plaster with a handrail on it
                bool well = floor.Rooms.Any(q => q.Type == RoomType.Stairwell
                    && (lo.X + hi.X) / 2 > q.X0 && (lo.X + hi.X) / 2 < q.X1 && lo.Z >= q.Z0 - 0.01f && hi.Z <= q.Z1 + 0.01f);
                if (!well) { s.Box(lo, hi, Rail); continue; }
                s.Box(lo with { Z = lo.Z - 0.04f }, hi with { Y = y0 + 0.9f, Z = hi.Z + 0.04f }, Parapet);
                s.Box(new Vector3(lo.X - 0.02f, y0 + 0.9f, lo.Z - 0.06f), new Vector3(hi.X + 0.02f, y0 + 0.96f, hi.Z + 0.06f), Handrail, false);
            }
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

        foreach (var lift in l.Lifts)
            for (int f = lift.Bottom; f <= lift.Top && f < l.Floors.Count; f++)
                LiftDressing(s, lift, l.FloorY(f));

        var figures = new List<Figure>();
        foreach (var p in l.Furniture)
        {
            // a loose floor pallet is its own node, to be forked up and carried off (#583):
            // InteriorManager.AddPallets draws it from PalletPiece
            if (IsLoosePallet(p)) continue;
            Furniture(s, p, l.FloorY(p.Floor) + p.Lift, figures);
        }

        return new MeshData(s.V.ToArray(), s.C.ToArray(), s.Col.ToArray(), figures.ToArray());
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

    /// <summary>
    /// A garage's roll-up door, <paramref name="width"/> across and hanging <paramref name="height"/>
    /// down from its top edge at the origin (so squashing Y rolls it into the lintel), between
    /// <paramref name="z0"/> and <paramref name="z1"/>: sheet-metal slats, each a lit upper face over
    /// a shaded lower one, and a dark bottom rail. The facade's baked shut door has the same slats.
    /// </summary>
    public static MeshData RollUpLeaf(float width, float height, float z0, float z1)
    {
        var s = new Scratch();
        float hw = width / 2, mid = (z0 + z1) / 2;
        var metal = C(0.80f, 0.82f, 0.85f);
        var shade = metal * 0.8f;
        shade.A = 1;
        int slats = Math.Max(4, Mathf.RoundToInt(height / 0.22f));
        float sh = height / slats;
        for (int i = 0; i < slats; i++)
        {
            float top = -i * sh, split = top - sh * 0.35f, bottom = top - sh;
            s.Box(new Vector3(-hw, split, z0), new Vector3(hw, top, z1), metal, false);
            s.Box(new Vector3(-hw, bottom, z0), new Vector3(hw, split, mid), shade, false);
        }
        s.Box(new Vector3(-hw, -height, z0), new Vector3(hw, -height + 0.06f, z1 + 0.01f), C(0.2f, 0.2f, 0.22f), false);
        return new MeshData(s.V.ToArray(), s.C.ToArray(), s.Col.ToArray());
    }

    // ---- rooms -------------------------------------------------------------------------------

    /// <summary>
    /// How far each wall, floor and ceiling piece runs past its neat edge. Pieces that only touch
    /// (a panel beside a doorway, the lintel over it, two rooms' reveals meeting at the shared wall
    /// line) leave T-junctions, and those rasterise as hairline cracks onto the dark void the
    /// interior floats in; the interior's far-from-origin coordinates make it worse. Overlapping by
    /// a few millimetres closes them; the overlap is coplanar and the same colour, so it is unseen.
    /// </summary>
    private const float Seam = 0.006f;

    private static void Room(Scratch s, RoomPlan r, float y0, float clear, List<RectPlan> holes, List<RectPlan> ceilingHoles)
    {
        var (floorCol, wallCol, ceilCol) = Palette(r.Type);
        const float t = InteriorGenerator.WallInset;
        var inner = new RectPlan(r.X0 + t, r.Z0 + t, r.X1 - t, r.Z1 - t);
        float top = y0 + clear;

        foreach (var cut in Subtract(inner, holes))
        {
            var piece = cut.Grow(Seam);
            s.Quad(new(piece.X0, y0, piece.Z0), new(piece.X1, y0, piece.Z0),
                new(piece.X1, y0, piece.Z1), new(piece.X0, y0, piece.Z1), floorCol);
        }
        foreach (var cut in Subtract(inner, ceilingHoles))
        {
            var piece = cut.Grow(Seam);
            s.Quad(new(piece.X0, top, piece.Z0), new(piece.X0, top, piece.Z1),
                new(piece.X1, top, piece.Z1), new(piece.X1, top, piece.Z0), ceilCol);
        }

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
            u0 -= Seam; u1 += Seam; ya -= Seam; yb += Seam;
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
            // (a doorway's reveal runs a hair past that line, into the neighbour's, so the two meet
            // without a crack)
            float depth = o.Kind is OpeningKind.Door or OpeningKind.Arch ? InteriorGenerator.WallInset + Seam : InteriorGenerator.WallInset + 0.22f;
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
            if (r.Type == RoomType.Shelter && o.Kind == OpeningKind.Door) BlastDoor(s, side, inner, s0, s1, ob, ot, b);
            cursor = s1;
        }
        Panel(cursor, b, y0, top);
    }

    private static readonly Color BlastSteel = C(0.36f, 0.40f, 0.36f);

    /// <summary>
    /// A shelter's door (#213): a thick steel frame round the opening and the armoured leaf, a
    /// hand's width thick, standing open flat against the wall beside it with its wheel and its
    /// two lever bolts. Baked: nobody shuts a shelter in peacetime. The generator keeps the strip
    /// it stands on free of furniture (<c>InteriorGenerator.BlastLeaf</c>).
    /// </summary>
    private static void BlastDoor(Scratch s, Side side, RectPlan inner, float s0, float s1, float ob, float ot, float wallEnd)
    {
        Vector3 R(float u, float y, float d) => OnWall(side, inner, u, y, d);
        void Block(float u0, float u1, float y0, float y1, float d0, float d1, Color col, bool collide = true)
        {
            var a = R(u0, y0, d0);
            var b = R(u1, y1, d1);
            s.Box(new Vector3(Math.Min(a.X, b.X), y0, Math.Min(a.Z, b.Z)), new Vector3(Math.Max(a.X, b.X), y1, Math.Max(a.Z, b.Z)), col, collide);
        }
        // the frame, standing proud of the wall into the room
        Block(s0 - 0.12f, s0, ob, ot + 0.12f, -0.08f, 0, BlastSteel);
        Block(s1, s1 + 0.12f, ob, ot + 0.12f, -0.08f, 0, BlastSteel);
        Block(s0 - 0.12f, s1 + 0.12f, ot, ot + 0.12f, -0.08f, 0, BlastSteel);
        // the leaf, swung open against the wall past the hinge jamb
        float u0 = s1 + 0.14f, u1 = Math.Min(wallEnd - 0.02f, u0 + (s1 - s0) + 0.1f);
        if (u1 - u0 < 0.4f) return;
        Block(u0, u1, ob + 0.02f, ot + 0.08f, -0.2f, -0.02f, (BlastSteel * 0.92f) with { A = 1 });
        float mid = (u0 + u1) / 2, hy = ob + (ot - ob) * 0.55f;
        var dark = C(0.16f, 0.17f, 0.16f);
        Block(mid - 0.18f, mid + 0.18f, hy - 0.18f, hy + 0.18f, -0.25f, -0.2f, dark, false);   // the wheel
        Block(u1 - 0.18f, u1 - 0.06f, hy + 0.35f, hy + 0.47f, -0.27f, -0.2f, dark, false);     // lever bolts
        Block(u1 - 0.18f, u1 - 0.06f, hy - 0.47f, hy - 0.35f, -0.27f, -0.2f, dark, false);
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

    private static readonly Color StairStone = C(0.72f, 0.70f, 0.66f);
    private static readonly Color StairNosing = C(0.50f, 0.48f, 0.45f);
    private static readonly Color Parapet = C(0.90f, 0.88f, 0.82f);
    private static readonly Color Handrail = C(0.40f, 0.27f, 0.17f);

    /// <summary>
    /// Steps to look at, a ramp to stand on. A CharacterBody climbing real risers catches on every
    /// nosing; the ramp runs from one tread before the first step to the top, which keeps it at or
    /// just under the nosings the whole way up. A house's flight is wooden steps on a solid
    /// block down to its floor. A stairwell's half flight (#571) is stone treads with a dark
    /// nosing on a slab whose underside follows the steps, since the flight below passes under it,
    /// and carries a parapet with a handrail along the open well when <see cref="FlightPlan.Parapet"/> says.
    /// </summary>
    private static void Flight(Scratch s, FlightPlan f, float y0, float h)
    {
        // built in the flight's own frame, across its lane and along its run; a flight along X
        // (#577) is the same with the two swapped, which the two-sided interior shaders and
        // collision do not mind
        Vector3 V(float across, float y, float along) => f.AlongX ? new Vector3(along, y, across) : new Vector3(across, y, along);
        void B(float a0, float ya, float l0, float a1, float yb, float l1, Color col, bool collide)
        {
            var p = V(a0, ya, l0);
            var q = V(a1, yb, l1);
            s.Box(p.Min(q), p.Max(q), col, collide);
        }
        float ya = y0 + h * f.From, yb = y0 + h * f.To, rise = yb - ya;
        bool stone = f.Half;
        int steps = Math.Max(1, (int)MathF.Ceiling(rise / (stone ? 0.18f : 0.19f)));
        float dir = Math.Sign(f.ZTop - f.ZBottom);
        float run = Math.Abs(f.ZTop - f.ZBottom);
        float tread = run / steps;
        float x0 = f.X0 + 0.02f, x1 = f.X1 - 0.02f;
        for (int i = 0; i < steps; i++)
        {
            float za = f.ZBottom + dir * tread * i, zb = f.ZBottom + dir * tread * (i + 1);
            float top = ya + rise * (i + 1) / steps;
            float z0 = Math.Min(za, zb), z1 = Math.Max(za, zb);
            if (!stone)
            {
                var shade = i % 2 == 0 ? StairWood : StairWood * 0.93f;
                shade.A = 1;
                B(x0, y0, z0, x1, top, z1, shade, false);
                continue;
            }
            // the tread and its riser on a slab 0.35 m thick under the step line
            B(x0, Math.Max(ya - 0.2f, top - 0.38f), z0, x1, top, z1, StairStone, false);
            float nose = dir > 0 ? z0 : z1;
            B(x0, top - 0.03f, nose - 0.02f, x1, top + 0.005f, nose + 0.02f, StairNosing, false);
        }
        if (stone && f.Parapet != 0)
        {
            // a solid parapet in the well, following the flight, and a handrail along its top: each
            // flight's half of the well, so the two of a turn stand side by side; solid, so nobody
            // steps off one flight onto the other
            float p0 = f.Parapet > 0 ? f.X1 : f.X0 - InteriorGenerator.StairEye / 2;
            float p1 = f.Parapet > 0 ? f.X1 + InteriorGenerator.StairEye / 2 : f.X0;
            float ra = ya + rise / steps, rb = yb;
            Prism(s, f.AlongX, p0, p1, f.ZBottom, f.ZTop, ra - 0.4f, ra + 0.9f, rb - 0.4f, rb + 0.9f, Parapet, true);
            Prism(s, f.AlongX, p0 - 0.03f, p1 + 0.03f, f.ZBottom, f.ZTop, ra + 0.9f, ra + 0.96f, rb + 0.9f, rb + 0.96f, Handrail, false);
        }
        float zs = f.ZBottom - dir * tread;
        var a = V(f.X0, ya, zs);
        var b = V(f.X1, ya, zs);
        var c = V(f.X1, yb, f.ZTop);
        var d = V(f.X0, yb, f.ZTop);
        s.Col.Add(a); s.Col.Add(b); s.Col.Add(c);
        s.Col.Add(a); s.Col.Add(c); s.Col.Add(d);
    }

    /// <summary>
    /// A box sloped along z (or along x, <paramref name="alongX"/>, the two swapped): x from <paramref name="x0"/> to <paramref name="x1"/>, at
    /// z = <paramref name="za"/> from <paramref name="lowA"/> to <paramref name="highA"/>, at
    /// z = <paramref name="zb"/> from <paramref name="lowB"/> to <paramref name="highB"/>.
    /// </summary>
    private static void Prism(Scratch s, bool alongX, float x0, float x1, float za, float zb,
        float lowA, float highA, float lowB, float highB, Color col, bool collide)
    {
        Vector3 V(float x, float y, float z) => alongX ? new Vector3(z, y, x) : new Vector3(x, y, z);
        var a0 = V(x0, lowA, za); var a1 = V(x1, lowA, za);
        var a2 = V(x1, highA, za); var a3 = V(x0, highA, za);
        var b0 = V(x0, lowB, zb); var b1 = V(x1, lowB, zb);
        var b2 = V(x1, highB, zb); var b3 = V(x0, highB, zb);
        var side = col * 0.9f; side.A = col.A;
        s.Quad(a0, b0, b3, a3, side, collide);       // -x face
        s.Quad(a1, a2, b2, b1, side, collide);       // +x face
        s.Quad(a3, b3, b2, a2, col, collide);        // top
        s.Quad(a0, a1, b1, b0, side * 0.8f, false);  // underside
        s.Quad(a0, a3, a2, a1, side, false);         // ends
        s.Quad(b0, b1, b2, b3, side, false);
    }

    // ---- elevators (#557) ----------------------------------------------------------------------

    /// <summary>
    /// One leaf of an elevator's sliding pair, <paramref name="w"/> wide, centred on its origin in x,
    /// standing on y = 0, thin in z: brushed steel with a darker edge where the two meet.
    /// </summary>
    public static MeshData LiftDoorPanel(float w, float h)
    {
        var s = new Scratch();
        s.Box(new Vector3(-w / 2, 0.005f, -0.02f), new Vector3(w / 2, h, 0.02f), LiftSteel * 0.95f, false);
        s.Box(new Vector3(w / 2 - 0.015f, 0.005f, -0.022f), new Vector3(w / 2, h, 0.022f), LiftSteel * 0.6f, false);
        s.Box(new Vector3(-w / 2, 0.005f, -0.022f), new Vector3(-w / 2 + 0.015f, h, 0.022f), LiftSteel * 0.6f, false);
        return new MeshData(s.V.ToArray(), s.C.ToArray(), Array.Empty<Vector3>());
    }

    private static readonly Color LiftSteel = C(0.66f, 0.68f, 0.70f);
    private static readonly Color LiftButton = C(0.95f, 0.78f, 0.30f);

    /// <summary>
    /// What an elevator has on one floor besides its cabin room: the steel frame round the doors
    /// and the call button on the landing, and in the cabin the button panel and a handrail. The
    /// sliding doors themselves move, so they are nodes (<c>InteriorNode</c>), not baked.
    /// </summary>
    private static void LiftDressing(Scratch s, LiftPlan lift, float y0)
    {
        float half = lift.DoorWidth / 2, top = y0 + lift.DoorTop;
        void Slab(float a0, float a1, float ya, float yb, float off0, float off1, Color col)
        {
            var p = lift.WallPoint(a0, ya, off0);
            var q = lift.WallPoint(a1, yb, off1);
            s.Box(new Vector3(Math.Min(p.X, q.X), ya, Math.Min(p.Z, q.Z)), new Vector3(Math.Max(p.X, q.X), yb, Math.Max(p.Z, q.Z)), col);
        }
        const float wall = InteriorGenerator.WallInset;
        Slab(-half - 0.10f, -half, y0, top + 0.10f, wall, wall + 0.04f, LiftSteel);
        Slab(half, half + 0.10f, y0, top + 0.10f, wall, wall + 0.04f, LiftSteel);
        Slab(-half - 0.10f, half + 0.10f, top, top + 0.10f, wall, wall + 0.04f, LiftSteel);
        // the floor indicator over the doors, and the call button beside them
        Slab(-0.15f, 0.15f, top + 0.16f, top + 0.28f, wall, wall + 0.03f, C(0.08f, 0.08f, 0.09f));
        Slab(-0.05f, 0.05f, top + 0.19f, top + 0.25f, wall + 0.03f, wall + 0.035f, C(0.95f, 0.30f, 0.12f));
        float ca = lift.CallAlong, cy = y0 + LiftPlan.ButtonHeight;
        Slab(ca - 0.06f, ca + 0.06f, cy - 0.11f, cy + 0.11f, wall, wall + 0.02f, LiftSteel * 0.9f);
        Slab(ca - 0.025f, ca + 0.025f, cy - 0.025f, cy + 0.025f, wall + 0.02f, wall + 0.035f, LiftButton);

        // inside: the panel on the wall beside the doors, a button a floor, the bottom one lowest
        var pc = lift.PanelPoint(y0 + LiftPlan.ButtonHeight);
        var (nx, nz) = lift.PanelNormal;
        var normal = new Vector3(nx, 0, nz);
        var across = new Vector3(-nz, 0, nx);
        int buttons = lift.Top - lift.Bottom + 1;
        float panelH = LiftPlan.PanelHeight(buttons);
        var pmin = pc - across * 0.13f - new Vector3(0, panelH / 2, 0);
        var pmax = pc + across * 0.13f + new Vector3(0, panelH / 2, 0) + normal * 0.02f;
        s.Box(pmin.Min(pmax), pmin.Max(pmax), LiftSteel * 0.85f, false);
        for (int b = 0; b < buttons; b++)
        {
            var at = lift.ButtonPoint(y0, b);
            var bmin = at - across * 0.025f - new Vector3(0, 0.025f, 0);
            var bmax = at + across * 0.025f + new Vector3(0, 0.025f, 0) + normal * 0.015f;
            s.Box(bmin.Min(bmax), bmin.Max(bmax), b == 0 ? C(0.30f, 0.80f, 0.40f) : LiftButton, false);
        }
        // a handrail on the back wall
        float hx0 = lift.X0 + 0.25f, hx1 = lift.X1 - 0.25f, hz0 = lift.Z0 + 0.25f, hz1 = lift.Z1 - 0.25f;
        var (a, b2) = lift.DoorSide switch
        {
            Side.Front => (new Vector3(hx0, y0 + 0.88f, lift.Z1 - wall - 0.08f), new Vector3(hx1, y0 + 0.93f, lift.Z1 - wall - 0.04f)),
            Side.Back => (new Vector3(hx0, y0 + 0.88f, lift.Z0 + wall + 0.04f), new Vector3(hx1, y0 + 0.93f, lift.Z0 + wall + 0.08f)),
            Side.Left => (new Vector3(lift.X1 - wall - 0.08f, y0 + 0.88f, hz0), new Vector3(lift.X1 - wall - 0.04f, y0 + 0.93f, hz1)),
            _ => (new Vector3(lift.X0 + wall + 0.04f, y0 + 0.88f, hz0), new Vector3(lift.X0 + wall + 0.08f, y0 + 0.93f, hz1)),
        };
        s.Box(a, b2, LiftSteel, false);
    }

    // ---- furniture ---------------------------------------------------------------------------

    private static readonly Color LockerSteel = C(0.22f, 0.30f, 0.24f);
    private static readonly Color SafeSteel = C(0.26f, 0.27f, 0.29f);

    /// <summary>
    /// The door of a gun locker or safe, in its hinge's frame: x from the hinge (the piece's left
    /// front edge seen from in front) across, y up, z 0..thickness outward. Swung about y by the
    /// loot service (negative angles open it toward the room). Collision-free: the piece's own box
    /// already stops a player.
    /// </summary>
    public static MeshData LockDoor(FurniturePlan p)
    {
        var s = new Scratch();
        bool safe = p.Type != FurnitureType.GunLocker;
        var steel = safe ? SafeSteel : LockerSteel;
        float w = p.W, H = p.H, t = safe ? 0.07f : 0.03f;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            s.Box(new Vector3(xa, ya, za), new Vector3(xb, yb, zb), col, false);
        B(0.005f, 0.01f, 0, w - 0.005f, H - 0.01f, t, steel);
        var chrome = C(0.75f, 0.76f, 0.78f);
        if (safe)
        {
            // the dial and its handle
            B(w * 0.5f - 0.07f, H * 0.55f - 0.07f, t, w * 0.5f + 0.07f, H * 0.55f + 0.07f, t + 0.02f, C(0.10f, 0.10f, 0.11f));
            B(w * 0.5f - 0.01f, H * 0.55f + 0.035f, t + 0.02f, w * 0.5f + 0.01f, H * 0.55f + 0.06f, t + 0.025f, chrome);
            B(w - 0.12f, H * 0.3f, t, w - 0.08f, H * 0.45f, t + 0.04f, chrome);
        }
        else
        {
            for (int k = 0; k < 4; k++)   // vents
                B(0.15f, H - 0.25f - k * 0.06f, t, w - 0.15f, H - 0.23f - k * 0.06f, t + 0.005f, (steel * 0.6f) with { A = 1f });
            B(w - 0.1f, H * 0.48f, t, w - 0.06f, H * 0.58f, t + 0.03f, chrome);
            B(w * 0.5f - 0.05f, H * 0.62f - 0.05f, t, w * 0.5f + 0.05f, H * 0.62f + 0.05f, t + 0.02f, C(0.10f, 0.10f, 0.11f));   // the dial
        }
        return new MeshData(s.V.ToArray(), s.C.ToArray(), Array.Empty<Vector3>());
    }

    // ---- pallets a forklift can lift (#583) ------------------------------------------------------

    /// <summary>
    /// A pallet standing loose on the floor — not one on a rack beam, which <see cref="FurnitureType.PalletRack"/>
    /// draws itself — and so one a forklift can lift. <see cref="Build"/> leaves it out of the
    /// merged mesh; it is its own node (<c>InteriorManager.AddPallets</c>).
    /// </summary>
    public static bool IsLoosePallet(FurniturePlan p) => p.Type == FurnitureType.Pallet && p.Lift == 0f;

    /// <summary>The load byte a plan's pallet carries off with it: its goods roll and its deck.</summary>
    public static byte PalletLoad(FurniturePlan p) => Items.Pallets.LoadOf(Hash(p, 3), p.D);

    /// <summary>
    /// A pallet on its own, from its load byte: centred on the origin on its underside, its runners
    /// along X (the way the tines go in). The same mesh in the hall, on the forks and set down
    /// anywhere, so a pallet looks the same wherever it is. No collision soup: its node has a box.
    /// </summary>
    public static MeshData PalletPiece(byte load)
    {
        var s = new Scratch();
        var plan = new FurniturePlan
        {
            Type = FurnitureType.Pallet, W = Items.Pallets.Length, D = Items.Pallets.Depth(load), H = Items.Pallets.Height,
        };
        Furniture(s, plan, 0f, new List<Figure>(), load);
        // whole alpha: a goods shade (sacks' 0.94) is a figure shader's effect code where it is drawn outside
        var colors = s.C.ToArray();
        for (int i = 0; i < colors.Length; i++) colors[i].A = 1f;
        return new MeshData(s.V.ToArray(), colors, Array.Empty<Vector3>());
    }

    /// <summary>PAUSA's block letters, 3x5, top row first.</summary>
    private static readonly string[] PausaFont =
    {
        "111101111100100",   // P
        "010101111101101",   // A
        "101101101101111",   // U
        "111100111001111",   // S
        "010101111101101",   // A
    };

    /// <summary>
    /// A PAUSA vending machine (#273), front to +Z: a tall red cabinet, a white band with the
    /// wordmark in red block letters, a glass front over four rows of spirals with goods in them,
    /// the keypad column on the right (LCD, keys, coin slot) and the pickup flap at the bottom.
    /// Vertex-coloured boxes only, like every other piece. In the Selecta style, not its name or logo.
    /// </summary>
    private static void Vending(FurniturePlan p, float w, float d, float H, Action<Vector3, Vector3, Color> box)
    {
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            box(new Vector3(xa, ya, za), new Vector3(xb, yb, zb), col);
        var red = C(0.80f, 0.08f, 0.10f);
        var deep = C(0.52f, 0.05f, 0.07f);
        var white = C(0.95f, 0.95f, 0.93f);
        var dark = C(0.08f, 0.08f, 0.09f);
        var steel = C(0.62f, 0.64f, 0.66f);
        float f = d;   // the front face

        B(-w, 0.06f, -d, w, H, d, red);                              // the cabinet
        foreach (float fx in new[] { -w + 0.04f, w - 0.12f })      // feet
            B(fx, 0, -d + 0.05f, fx + 0.08f, 0.06f, d - 0.05f, dark);
        B(-w, H - 0.27f, f, w, H - 0.04f, f + 0.012f, white);        // the white band
        // the wordmark, centred on the band
        const float px = 0.034f;
        float x0 = -(PausaFont.Length * 4 - 1) * px / 2, yTop = H - 0.07f;
        for (int l = 0; l < PausaFont.Length; l++)
            for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                    if (PausaFont[l][row * 3 + col] == '1')
                    {
                        float x = x0 + (l * 4 + col) * px, y = yTop - (row + 1) * px;
                        B(x, y, f + 0.012f, x + px, y + px, f + 0.02f, red);
                    }

        // the window: four rows of six spirals, the goods in them, glass over it all
        float wx0 = -w + 0.06f, wx1 = w * 0.38f, wy0 = 0.46f, wy1 = H - 0.33f;
        B(wx0 - 0.02f, wy0 - 0.02f, f, wx1 + 0.02f, wy1 + 0.02f, f + 0.01f, deep);    // the frame
        B(wx0, wy0, f + 0.01f, wx1, wy1, f + 0.012f, C(0.16f, 0.16f, 0.18f));          // the dark back
        float rowH = (wy1 - wy0) / 4, colW = (wx1 - wx0) / 6;
        for (int r = 0; r < 4; r++)
        {
            float y = wy1 - (r + 1) * rowH;
            B(wx0, y, f + 0.012f, wx1, y + 0.012f, f + 0.06f, steel);                   // the shelf
            for (int c = 0; c < 6; c++)
            {
                float x = wx0 + c * colW;
                float hue = (float)Math.Abs((p.X * 3.7 + p.Z * 1.3 + r * 0.29 + c * 0.17) % 1.0);
                var goods = Color.FromHsv(hue, 0.65f, 0.85f).SrgbToLinear();
                B(x + 0.012f, y + 0.02f, f + 0.02f, x + colW - 0.012f, y + rowH * 0.62f, f + 0.045f, goods);   // the packet
                for (int k = 0; k < 3; k++)                                                                  // the spiral's turns
                    B(x + colW * (0.2f + 0.3f * k), y + 0.012f, f + 0.012f, x + colW * (0.2f + 0.3f * k) + 0.008f, y + rowH * 0.7f, f + 0.055f, steel);
                B(x + colW * 0.3f, y + 0.002f, f + 0.06f, x + colW * 0.7f, y + 0.014f, f + 0.065f, white);  // the code label
            }
        }
        B(wx0, wy0, f + 0.065f, wx1, wy1, f + 0.07f, Glass);

        // the keypad column: LCD, twelve keys, the coin slot, the return button
        float kx0 = w * 0.46f, kx1 = w - 0.06f;
        B(kx0, 0.5f, f, kx1, H - 0.33f, f + 0.01f, C(0.20f, 0.20f, 0.22f));
        B(kx0 + 0.03f, 1.22f, f + 0.01f, kx1 - 0.03f, 1.32f, f + 0.018f, C(0.30f, 0.52f, 0.36f));   // the LCD
        float kw = (kx1 - kx0 - 0.08f) / 3;
        for (int k = 0; k < 12; k++)
        {
            float kx = kx0 + 0.04f + (k % 3) * kw, ky = 1.15f - (k / 3) * 0.055f;
            B(kx + 0.006f, ky - 0.04f, f + 0.01f, kx + kw - 0.006f, ky, f + 0.022f, k == 9 ? C(0.85f, 0.70f, 0.15f) : steel);
        }
        B((kx0 + kx1) / 2 - 0.006f, 0.82f, f + 0.01f, (kx0 + kx1) / 2 + 0.006f, 0.9f, f + 0.02f, dark);   // the coin slot
        B((kx0 + kx1) / 2 - 0.035f, 0.66f, f + 0.01f, (kx0 + kx1) / 2 + 0.035f, 0.74f, f + 0.03f, dark);  // the change cup

        // the pickup flap, and its push plate
        B(-w + 0.08f, 0.12f, f, w * 0.38f, 0.38f, f + 0.012f, C(0.14f, 0.14f, 0.15f));
        B(-w + 0.1f, 0.3f, f + 0.012f, w * 0.36f, 0.36f, f + 0.02f, C(0.30f, 0.30f, 0.32f));
    }

    /// <param name="load">A pallet's load byte (<see cref="Items.Pallets"/>), drawn instead of its plan's own roll; -1 for none.</param>
    private static void Furniture(Scratch s, FurniturePlan p, float y0, List<Figure> figures, int load = -1)
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
        // what sits on a pallet or a rack beam: boxes, a drum, or sacks, by a roll of its own, so no
        // two bays of a warehouse hold the same thing (#497)
        void Goods(float cx, float y, float half, float h, float roll)
        {
            if (h < 0.12f) return;
            var goods = Items.Pallets.GoodsOf(roll);
            if (goods == Items.PalletGoods.Cartons)
            {
                // cartons, two or three of them, stacked a little untidily
                int n = roll < 0.22f ? 2 : 3;
                for (int i = 0; i < n; i++)
                {
                    float y0 = y + h * i / n, y1 = y + h * (i + 1) / n - 0.01f;
                    float skew = (roll * 10f % 1f - 0.5f) * half * 0.3f * (i % 2 == 0 ? 1 : -1);
                    var box = new Color(0.62f + 0.1f * roll, 0.50f, 0.32f).SrgbToLinear();
                    B(cx - half * 0.92f + skew, y0, -half * 0.8f, cx + half * 0.92f + skew, y1, half * 0.8f, box);
                }
            }
            else if (goods == Items.PalletGoods.Drums)
            {
                // a pair of drums
                var col = roll < 0.58f ? C(0.20f, 0.36f, 0.58f) : C(0.62f, 0.44f, 0.14f);
                foreach (float bx in new[] { cx - half * 0.45f, cx + half * 0.45f })
                    B(bx - half * 0.4f, y, -half * 0.4f, bx + half * 0.4f, y + h, half * 0.4f, col);
            }
            else if (goods == Items.PalletGoods.Sacks)
            {
                // sacks, in two crossed courses
                var sack = C(0.78f, 0.74f, 0.62f);
                B(cx - half * 0.95f, y, -half * 0.85f, cx + half * 0.95f, y + h * 0.5f, half * 0.85f, sack);
                B(cx - half * 0.8f, y + h * 0.5f, -half * 0.95f, cx + half * 0.8f, y + h, half * 0.95f, sack * 0.94f);
            }
            else
            {
                // a shrink-wrapped block, the pale grey-blue of stretch film
                B(cx - half * 0.95f, y, -half * 0.88f, cx + half * 0.95f, y + h, half * 0.88f, C(0.72f, 0.76f, 0.78f));
            }
        }
        // one collision box for the whole piece: cheaper than per-part, and what a player hits
        // anyway; hung and wall-mounted pieces are overhead, and the chancel step has its own
        bool solid = p.Type is not (FurnitureType.Rug or FurnitureType.Plant or FurnitureType.Bell
            or FurnitureType.Cross or FurnitureType.Dais or FurnitureType.AcousticFoam or FurnitureType.CinemaScreen
            or FurnitureType.GuitarStand
            // #497: paint on the floor, a beam overhead, and signs flat on the wall
            or FurnitureType.FloorMarking or FurnitureType.Gantry or FurnitureType.SafetySign
            or FurnitureType.Banner or FurnitureType.Whiteboard or FurnitureType.TimeClock);
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
            case FurnitureType.BlahajBin:
            {
                // a wire bin, heaped over the rim with plush sharks (#501). The heap is what sells
                // it, so the sharks are built individually rather than as one mound: six or seven
                // of them, each turned its own way, laid out from the bin's own place in the plan
                // so every peer builds the same bin and a rebuild does not reshuffle it.
                var wire = C(0.34f, 0.36f, 0.40f);
                var shark = C(0.49f, 0.78f, 0.94f);
                var belly = C(0.90f, 0.95f, 0.98f);
                // the basket: a rim, four corner posts and a slatted floor
                B(-w, H - 0.06f, -d, w, H, -d + 0.05f, wire);
                B(-w, H - 0.06f, d - 0.05f, w, H, d, wire);
                B(-w, H - 0.06f, -d, -w + 0.05f, H, d, wire);
                B(w - 0.05f, H - 0.06f, -d, w, H, d, wire);
                B(-w + 0.04f, 0.08f, -d + 0.04f, w - 0.04f, 0.13f, d - 0.04f, wire * 0.8f);
                foreach (var (px, pz) in new[] { (-w, -d), (w - 0.05f, -d), (-w, d - 0.05f), (w - 0.05f, d - 0.05f) })
                    B(px, 0, pz, px + 0.05f, H, pz + 0.05f, wire);

                int sharks = 5 + (int)(Hash(p, 1) * 3);      // 5..7
                for (int i = 0; i < sharks; i++)
                {
                    // stacked in two layers, the upper one proud of the rim: a full bin
                    float sx = (Hash(p, i * 4 + 2) - 0.5f) * (p.W - 0.46f);
                    float sz = (Hash(p, i * 4 + 3) - 0.5f) * (p.D - 0.46f);
                    float sy = 0.13f + (i % 2) * 0.17f + Hash(p, i * 4 + 4) * 0.05f;
                    bool lengthwise = Hash(p, i * 4 + 5) < 0.5f;
                    const float bl = 0.19f, bw = 0.075f, bh = 0.085f;   // half-extents of one shark
                    float ax = lengthwise ? bl : bw, az = lengthwise ? bw : bl;
                    // body, pale belly under it, and the tail standing up at one end
                    B(sx - ax, sy, sz - az, sx + ax, sy + bh * 2, sz + az, shark);
                    B(sx - ax * 0.86f, sy, sz - az * 0.86f, sx + ax * 0.86f, sy + bh * 0.6f, sz + az * 0.86f, belly);
                    float tx = lengthwise ? sx - ax : sx, tz = lengthwise ? sz : sz - az;
                    B(tx - 0.035f, sy + bh * 0.5f, tz - 0.035f, tx + 0.035f, sy + bh * 2.3f, tz + 0.035f, shark * 0.9f);
                }
                break;
            }
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
            case FurnitureType.GunLocker:
            case FurnitureType.Safe:
            case FurnitureType.VaultSafe:
            {
                // an open-fronted steel box: the door is its own node (LockDoor), swung by the loot service
                bool safe = p.Type != FurnitureType.GunLocker;
                var steel = safe ? SafeSteel : LockerSteel;
                var inside = (steel * 0.45f) with { A = 1f };
                const float t = 0.04f;
                B(-w, 0, -d, w, t, d, steel);              // floor
                B(-w, H - t, -d, w, H, d, steel);          // roof
                B(-w, 0, -d, -w + t, H, d, steel);         // sides
                B(w - t, 0, -d, w, H, d, steel);
                B(-w, 0, -d, w, H, -d + t, inside);        // back, darker: the inside is in shadow
                if (safe)
                {
                    B(-w + t, H * 0.5f, -d + t, w - t, H * 0.5f + 0.02f, d - 0.06f, inside);   // a shelf
                    B(-w + 0.1f, t, -d + 0.1f, -w + 0.3f, t + 0.12f, d - 0.15f, C(0.30f, 0.45f, 0.30f));   // a cash box
                    B(0.0f, H * 0.5f + 0.02f, -d + 0.1f, w - 0.1f, H * 0.5f + 0.1f, d - 0.15f, C(0.62f, 0.58f, 0.40f));
                }
                else
                {
                    B(-w + t, H - 0.45f, -d + t, w - t, H - 0.43f, d - 0.06f, inside);         // the ammunition shelf
                    for (int k = 0; k < 3; k++)
                        B(-w + 0.08f + k * 0.15f, H - 0.43f, -d + 0.1f, -w + 0.2f + k * 0.15f, H - 0.33f, d - 0.15f, C(0.70f, 0.16f, 0.12f));
                    foreach (float gx in new[] { -0.12f, 0.12f })
                    {
                        B(gx - 0.035f, 0.06f, -d + 0.08f, gx + 0.035f, 0.55f, -d + 0.2f, C(0.40f, 0.27f, 0.16f));   // stock
                        B(gx - 0.015f, 0.55f, -d + 0.1f, gx + 0.015f, 1.3f, -d + 0.16f, dark);                   // barrel
                    }
                }
                break;
            }
            case FurnitureType.WashingMachine:
            case FurnitureType.Dryer:
            {
                B(-w, 0, -d, w, H, d, white);
                B(-w, H - 0.12f, d - 0.01f, w, H - 0.02f, d + 0.01f, C(0.70f, 0.72f, 0.74f));    // control strip
                float r = Math.Min(w, H * 0.3f) * 0.75f, cy = H * 0.45f;
                B(-r, cy - r, d - 0.01f, r, cy + r, d + 0.02f, metal);
                B(-r * 0.7f, cy - r * 0.7f, d + 0.02f, r * 0.7f, cy + r * 0.7f, d + 0.03f,
                    p.Type == FurnitureType.Dryer ? dark : C(0.40f, 0.56f, 0.70f));               // the porthole
                break;
            }
            case FurnitureType.IroningBoard:
                B(-w, H - 0.03f, -d, w, H, d, C(0.70f, 0.78f, 0.86f));
                B(-w * 0.5f, 0, -0.02f, -w * 0.5f + 0.03f, H - 0.03f, 0.02f, metal);
                B(w * 0.5f - 0.03f, 0, -0.02f, w * 0.5f, H - 0.03f, 0.02f, metal);
                break;
            case FurnitureType.BunkBed:
                foreach (float by in new[] { 0.25f, 1.25f })
                {
                    B(-w, by, -d, w, by + 0.06f, d, BlastSteel);
                    B(-w + 0.03f, by + 0.06f, -d + 0.03f, w - 0.03f, by + 0.2f, d - 0.03f, C(0.42f, 0.46f, 0.34f));   // army blanket
                }
                foreach (var (lx, lz) in new[] { (-w, -d), (w - 0.05f, -d), (-w, d - 0.05f), (w - 0.05f, d - 0.05f) })
                    B(lx, 0, lz, lx + 0.05f, H, lz + 0.05f, BlastSteel);
                break;
            case FurnitureType.WaterTank:
                B(-w, 0, -d, w, H, d, C(0.30f, 0.44f, 0.62f));
                B(-0.06f, H, -0.06f, 0.06f, H + 0.08f, 0.06f, white);
                break;
            case FurnitureType.WineRack:
                B(-w, 0, -d, -w + 0.04f, H, d, darkWood);
                B(w - 0.04f, 0, -d, w, H, d, darkWood);
                for (float y = 0.05f; y < H - 0.1f; y += 0.22f)
                {
                    B(-w, y, -d, w, y + 0.03f, d, darkWood);
                    for (float x = -w + 0.1f; x < w - 0.08f; x += 0.16f)   // bottle ends
                        B(x, y + 0.04f, d - 0.04f, x + 0.08f, y + 0.12f, d, (int)((x + y) * 10) % 3 == 0 ? C(0.40f, 0.12f, 0.16f) : C(0.18f, 0.30f, 0.16f));
                }
                break;
            case FurnitureType.Barrel:
            {
                var oak = C(0.48f, 0.32f, 0.18f);
                B(-w * 0.85f, 0, -d * 0.85f, w * 0.85f, H, d * 0.85f, oak);
                B(-w, H * 0.2f, -d, w, H * 0.8f, d, (oak * 1.05f) with { A = 1 });
                foreach (float hy in new[] { 0.12f, 0.5f, 0.85f })
                    B(-w - 0.01f, H * hy, -d - 0.01f, w + 0.01f, H * hy + 0.04f, d + 0.01f, dark);
                break;
            }
            case FurnitureType.DrumKit:
            {
                var shell = C(0.62f, 0.10f, 0.12f);
                var chrome = C(0.82f, 0.82f, 0.84f);
                B(-0.3f, 0, -0.1f, 0.3f, 0.55f, 0.3f, shell);                       // bass drum
                B(-0.3f, 0.01f, 0.3f, 0.3f, 0.54f, 0.31f, white);                    // its head
                B(-0.6f, 0.45f, 0.1f, -0.3f, 0.62f, 0.4f, shell);                   // snare
                B(0.3f, 0.35f, 0.0f, 0.62f, 0.62f, 0.32f, shell);                    // floor tom
                B(-0.25f, 0.6f, -0.05f, -0.02f, 0.78f, 0.18f, shell);                // rack toms
                B(0.02f, 0.6f, -0.05f, 0.25f, 0.78f, 0.18f, shell);
                foreach (var (cx, cz, cy) in new[] { (-0.7f, -0.2f, 0.95f), (0.65f, -0.35f, 1.05f) })
                {
                    B(cx - 0.01f, 0, cz - 0.01f, cx + 0.01f, cy, cz + 0.01f, chrome);
                    B(cx - 0.22f, cy, cz - 0.22f, cx + 0.22f, cy + 0.015f, cz + 0.22f, C(0.80f, 0.66f, 0.30f));   // cymbal
                }
                B(-0.15f, 0, -0.55f, 0.15f, 0.48f, -0.3f, dark);                    // the stool
                break;
            }
            case FurnitureType.Piano:
                B(-w, 0, -d, w, H, d, dark);
                B(-w, 0.68f, d, w, 0.74f, d + 0.25f, dark);                            // key bed
                B(-w + 0.04f, 0.74f, d + 0.02f, w - 0.04f, 0.76f, d + 0.24f, white);   // keys
                B(-w * 0.6f, 0, d + 0.3f, w * 0.6f, 0.48f, d + 0.6f, darkWood);       // bench
                break;
            case FurnitureType.Keyboard:
                B(-w, H - 0.1f, -d, w, H, d, dark);
                B(-w + 0.04f, H, -d + 0.08f, w - 0.04f, H + 0.015f, d - 0.04f, white);
                B(-w + 0.1f, 0, -0.03f, -w + 0.14f, H - 0.1f, 0.03f, metal);            // X stand
                B(w - 0.14f, 0, -0.03f, w - 0.1f, H - 0.1f, 0.03f, metal);
                break;
            case FurnitureType.GuitarStand:
            {
                var body = (p.X * 3.1f + p.Z) % 2 > 1 ? C(0.80f, 0.42f, 0.14f) : C(0.12f, 0.12f, 0.14f);
                B(-0.18f, 0.08f, -0.06f, 0.18f, 0.5f, 0.06f, body);
                B(-0.13f, 0.5f, -0.05f, 0.13f, 0.65f, 0.05f, body);
                B(-0.025f, 0.65f, -0.02f, 0.025f, H - 0.12f, 0.02f, darkWood);       // neck
                B(-0.05f, H - 0.12f, -0.025f, 0.05f, H, 0.025f, dark);               // head
                B(-0.15f, 0, -0.1f, 0.15f, 0.08f, 0.1f, metal);
                break;
            }
            case FurnitureType.Amplifier:
                B(-w, 0, -d, w, H, d, dark);
                B(-w + 0.05f, H * 0.2f, d, w - 0.05f, H * 0.85f, d + 0.01f, C(0.24f, 0.24f, 0.26f));
                break;
            case FurnitureType.AcousticFoam:
                // wedge tiles in a checker, from knee height up, flat on the wall
                for (float y = 0.4f; y < H - 0.25f; y += 0.3f)
                    for (float x = -w; x < w - 0.25f; x += 0.3f)
                    {
                        bool up = ((int)((x + w) / 0.3f) + (int)(y / 0.3f)) % 2 == 0;
                        B(x + 0.01f, y + 0.01f, -d, x + 0.29f, y + 0.29f, up ? d : d - 0.03f, up ? C(0.20f, 0.20f, 0.24f) : C(0.16f, 0.16f, 0.19f));
                    }
                break;
            case FurnitureType.CinemaScreen:
                B(-w, 0.7f, -d, w, H, -d + 0.04f, C(0.06f, 0.06f, 0.07f));
                B(-w + 0.05f, 0.75f, -d + 0.04f, w - 0.05f, H - 0.05f, -d + 0.05f, C(0.88f, 0.90f, 0.95f));
                break;
            case FurnitureType.Armchair:
            {
                var fabric = (p.X + p.Z) % 2 > 1 ? C(0.36f, 0.42f, 0.30f) : C(0.50f, 0.30f, 0.24f);
                B(-w, 0, -d, w, 0.42f, d, fabric);
                B(-w, 0.42f, -d, w, H, -d + 0.2f, (fabric * 0.9f) with { A = 1 });
                B(-w, 0.42f, -d, -w + 0.15f, 0.62f, d, (fabric * 0.9f) with { A = 1 });
                B(w - 0.15f, 0.42f, -d, w, 0.62f, d, (fabric * 0.9f) with { A = 1 });
                break;
            }
            case FurnitureType.Bookcase:
                B(-w, 0, -d, w, H, -d + 0.03f, darkWood);
                B(-w, 0, -d, -w + 0.04f, H, d, darkWood);
                B(w - 0.04f, 0, -d, w, H, d, darkWood);
                for (int i = 0; i <= 5; i++)
                {
                    float y = H * i / 5;
                    B(-w, y, -d, w, y + 0.03f, d, darkWood);
                    if (i == 5) break;
                    for (float x = -w + 0.06f; x < w - 0.08f; x += 0.06f)   // book spines
                    {
                        float hue = (float)Math.Abs((x * 13.7 + y * 5.1 + p.X) % 1.0);
                        B(x, y + 0.03f, -d + 0.04f, x + 0.05f, y + 0.03f + 0.22f + 0.08f * hue, d - 0.03f,
                            Color.FromHsv(hue, 0.5f, 0.55f).SrgbToLinear());
                    }
                }
                break;
            case FurnitureType.ToyBox:
                B(-w, 0, -d, w, H, d, C(0.90f, 0.70f, 0.20f));
                B(-w - 0.01f, H - 0.06f, -d - 0.01f, w + 0.01f, H, d + 0.01f, C(0.24f, 0.46f, 0.80f));
                B(-0.08f, H, -0.08f, 0.08f, H + 0.16f, 0.08f, C(0.84f, 0.20f, 0.18f));   // a block left on top
                break;
            case FurnitureType.TellerDesk:
            {
                // customers' side to +Z: a tall marble front with a counter, glass above it
                var marble = C(0.84f, 0.82f, 0.78f);
                B(-w, 0, -d, w, H, d, marble);
                B(-w - 0.02f, H, -d, w + 0.02f, H + 0.05f, d + 0.06f, darkWood);
                for (float x = -w; x <= w + 0.01f; x += Math.Max(0.6f, (2 * w) / Math.Max(1, (int)(2 * w / 1.2f))))
                    B(x - 0.03f, H + 0.05f, d - 0.03f, x + 0.03f, H + 0.85f, d + 0.03f, metal);
                B(-w, H + 0.05f, d - 0.01f, w, H + 0.85f, d + 0.0f, Glass);
                B(-w, H + 0.85f, d - 0.03f, w, H + 0.9f, d + 0.03f, metal);
                B(-0.35f, H + 0.06f, -d + 0.1f, 0.35f, H + 0.4f, -d + 0.15f, dark);   // a terminal
                break;
            }
            case FurnitureType.VendingMachine:
                Vending(p, w, d, H, (a, b, col) => B(a.X, a.Y, a.Z, b.X, b.Y, b.Z, col));
                break;
            case FurnitureType.ShopCounter:

                B(-w, 0, -d, w, H, d, C(0.62f, 0.44f, 0.30f));
                B(-w, H, -d, w, H + 0.03f, d, dark);
                break;
            case FurnitureType.Pew:
            case FurnitureType.FrontPew:
                B(-w, 0.42f, -d + 0.1f, w, 0.47f, d, wood);
                B(-w, 0.42f, -d, w, H, -d + 0.08f, wood);
                B(-w, 0, -d, -w + 0.06f, 0.42f, d, darkWood);
                B(w - 0.06f, 0, -d, w, 0.42f, d, darkWood);
                if (p.Type == FurnitureType.FrontPew) Congregation(p, w, d, y0, figures);
                break;
            case FurnitureType.Car:
                // a showroom row of identical red cars reads as a bug, not a style (#497)
                var paint = CarPaint(Hash(p, 11));
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
            case FurnitureType.PastorRat:
                figures.Add(Rat(p, y0, dark));
                break;
            case FurnitureType.ChurchRadio:
                ChurchRadio(w, d, H, (a, b, col) => B(a.X, a.Y, a.Z, b.X, b.Y, b.Z, col));
                break;
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

            // ---- industrial sites (#497) ------------------------------------------------------
            case FurnitureType.PalletRack:
            {
                // uprights, beam levels, and a loaded pallet on each: the one piece a warehouse is
                var upright = C(0.58f, 0.30f, 0.18f);   // the orange of a Swiss racking frame
                var beam = C(0.26f, 0.42f, 0.72f);
                int levels = Math.Max(2, (int)(H / 1.6f));
                foreach (float ux in new[] { -w + 0.05f, w - 0.05f })
                {
                    B(ux - 0.05f, 0, -d, ux + 0.05f, H, -d + 0.1f, upright);
                    B(ux - 0.05f, 0, d - 0.1f, ux + 0.05f, H, d, upright);
                    // the bracing between the two legs, as a ladder of short rungs; one per level is
                    // all that reads at PS1 distances, and a 50-bay hall is 50 of these
                    for (int k = 0; k < levels; k++)
                    {
                        float y = H * (k + 0.5f) / levels;
                        B(ux - 0.035f, y, -d + 0.08f, ux + 0.035f, y + 0.05f, d - 0.08f, upright * 0.85f);
                    }
                }
                for (int i = 0; i < levels; i++)
                {
                    float y = H * (i + 1) / (levels + 0.4f);
                    B(-w, y, -d, w, y + 0.09f, -d + 0.09f, beam);
                    B(-w, y, d - 0.09f, w, y + 0.09f, d, beam);
                    // one or two pallets on the level, loaded with what this site stores
                    int slots = p.W > 2.2f ? 2 : 1;
                    for (int k = 0; k < slots; k++)
                    {
                        float cx = -w + (2 * w) * (k + 0.5f) / slots;
                        float pw = Math.Min(0.95f, (2 * w) / slots * 0.82f) / 2;
                        if (Hash(p, i * 7 + k) < 0.3f) continue;  // a gap on the shelf
                        B(cx - pw, y + 0.09f, -d + 0.08f, cx + pw, y + 0.21f, d - 0.08f, wood * 0.9f);
                        float lh = Math.Min(H / (levels + 0.4f) - 0.4f, 0.75f);
                        Goods(cx, y + 0.21f, pw, lh, Hash(p, i * 13 + k + 1));
                    }
                }
                break;
            }
            case FurnitureType.Pallet:
            {
                // the pallet itself, then what is stacked on it
                var pine = C(0.70f, 0.56f, 0.36f);
                for (int k = 0; k < 3; k++)
                {
                    float z = -d + (2 * d - 0.14f) * k / 2f;
                    B(-w, 0, z, w, 0.08f, z + 0.14f, pine * 0.85f);
                }
                B(-w, 0.08f, -d, w, 0.14f, d, pine);
                Goods(0, 0.14f, w * 0.9f, Math.Max(0.2f, H - 0.14f), load >= 0 ? Items.Pallets.Roll((byte)load) : Hash(p, 3));
                break;
            }
            case FurnitureType.BarrelStack:
            {
                // four drums on a pallet, banded
                var blue = C(0.20f, 0.36f, 0.58f);
                B(-w, 0, -d, w, 0.14f, d, wood * 0.85f);
                foreach (float bx in new[] { -w * 0.5f, w * 0.5f })
                    foreach (float bz in new[] { -d * 0.5f, d * 0.5f })
                    {
                        var col = Hash(p, (int)(bx * 10 + bz * 3)) < 0.5f ? blue : C(0.62f, 0.44f, 0.14f);
                        B(bx - w * 0.42f, 0.14f, bz - d * 0.42f, bx + w * 0.42f, H, bz + d * 0.42f, col);
                        B(bx - w * 0.45f, H * 0.4f, bz - d * 0.45f, bx + w * 0.45f, H * 0.48f, bz + d * 0.45f, col * 0.85f);
                    }
                break;
            }
            case FurnitureType.SackStack:
            {
                // cement or feed sacks, courses crossed as they are really stacked
                var sack = C(0.78f, 0.74f, 0.62f);
                int courses = Math.Max(2, (int)(H / 0.18f));
                for (int i = 0; i < courses; i++)
                {
                    float y = H * i / courses, y1 = H * (i + 1) / courses - 0.015f;
                    bool across = i % 2 == 0;
                    int n = 2;
                    for (int k = 0; k < n; k++)
                    {
                        float t0 = -1f + 2f * k / n, t1 = -1f + 2f * (k + 1) / n;
                        var col = sack * (0.92f + 0.08f * Hash(p, i * 5 + k));
                        col.A = 1;
                        if (across) B(w * t0 + 0.02f, y, -d + 0.03f, w * t1 - 0.02f, y1, d - 0.03f, col);
                        else B(-w + 0.03f, y, d * t0 + 0.02f, w - 0.03f, y1, d * t1 - 0.02f, col);
                    }
                }
                break;
            }
            case FurnitureType.Conveyor:
            {
                // legs, side rails and a belt, with rollers showing at the ends
                float belt = H - 0.1f;
                foreach (float lx in new[] { -w + 0.12f, w - 0.12f })
                {
                    B(lx - 0.05f, 0, -d + 0.08f, lx + 0.05f, belt, -d + 0.18f, metal * 0.8f);
                    B(lx - 0.05f, 0, d - 0.18f, lx + 0.05f, belt, d - 0.08f, metal * 0.8f);
                }
                B(-w, belt - 0.14f, -d, w, belt, -d + 0.07f, metal);
                B(-w, belt - 0.14f, d - 0.07f, w, belt, d, metal);
                B(-w, belt - 0.07f, -d + 0.07f, w, belt, d - 0.07f, C(0.18f, 0.18f, 0.20f));
                // a box or two riding on it
                for (int k = 0; k < 2; k++)
                {
                    float h = Hash(p, k);
                    if (h < 0.35f) continue;
                    float cx = -w * 0.5f + w * k;
                    B(cx - 0.22f, belt, -0.22f, cx + 0.22f, belt + 0.3f, 0.22f, C(0.66f, 0.52f, 0.32f));
                }
                break;
            }
            case FurnitureType.Machine:
            {
                // a press or a lathe: a base, a body, a head over the work, and a control box
                var body = C(0.26f, 0.44f, 0.40f);   // machine green
                B(-w, 0, -d, w, 0.14f, d, dark);
                B(-w * 0.9f, 0.14f, -d * 0.9f, w * 0.9f, H * 0.6f, d * 0.9f, body);
                B(-w * 0.7f, H * 0.6f, -d * 0.5f, w * 0.7f, H, d * 0.5f, body * 0.9f);
                B(-w * 0.45f, H * 0.6f, -d * 0.95f, w * 0.45f, H * 0.78f, -d * 0.5f, metal);  // the ram
                B(w * 0.55f, H * 0.35f, -d - 0.08f, w * 0.95f, H * 0.6f, -d * 0.6f, C(0.80f, 0.70f, 0.20f)); // the panel
                B(-w * 0.2f, H, -0.06f, w * 0.2f, H + 0.3f, 0.06f, metal * 0.8f);  // the extract duct
                break;
            }
            case FurnitureType.Gantry:
            {
                // a crane beam under the roof, with a hoist block hanging off it
                B(-w, H - 0.25f, -d, w, H, d, C(0.80f, 0.68f, 0.16f));
                B(-w, H - 0.32f, -d * 0.55f, w, H - 0.25f, d * 0.55f, C(0.80f, 0.68f, 0.16f) * 0.9f);
                float hx = -w + 2 * w * Hash(p, 1);
                B(hx - 0.22f, H - 0.62f, -0.18f, hx + 0.22f, H - 0.32f, 0.18f, dark);
                B(hx - 0.03f, H - 1.3f, -0.03f, hx + 0.03f, H - 0.62f, 0.03f, metal * 0.8f);
                B(hx - 0.12f, H - 1.45f, -0.08f, hx + 0.12f, H - 1.3f, 0.08f, metal);
                break;
            }
            case FurnitureType.ToolChest:
            {
                var red = C(0.62f, 0.16f, 0.14f);
                B(-w, 0, -d, w, H, d, red);
                int drawers = Math.Max(3, (int)(H / 0.2f));
                for (int i = 0; i < drawers; i++)
                {
                    float y = H * (i + 0.15f) / drawers;
                    B(-w + 0.04f, y, -d - 0.015f, w - 0.04f, y + H / drawers * 0.6f, -d, red * 0.82f);
                    B(-w * 0.3f, y + H / drawers * 0.2f, -d - 0.03f, w * 0.3f, y + H / drawers * 0.35f, -d - 0.01f, metal);
                }
                B(-w, H, -d, w, H + 0.03f, d, metal);
                break;
            }
            case FurnitureType.CarLift:
            {
                // a low drive-on ramp: two runways on a cross beam, with ramps at the near end
                B(-w, 0, -d, w, 0.04f, d, C(0.80f, 0.70f, 0.18f) * 0.7f);   // the painted bay under it
                foreach (float rx in new[] { -w * 0.55f, w * 0.55f })
                {
                    B(rx - w * 0.3f, 0.04f, -d + 0.4f, rx + w * 0.3f, H, d - 0.2f, metal * 0.85f);
                    // the ramp up onto the runway
                    B(rx - w * 0.3f, 0.04f, -d, rx + w * 0.3f, H * 0.55f, -d + 0.4f, metal * 0.7f);
                }
                B(-w, 0.04f, d - 0.25f, w, H * 0.8f, d - 0.05f, C(0.26f, 0.28f, 0.30f));  // the crossbeam
                break;
            }
            case FurnitureType.TyreStack:
            {
                int tyres = Math.Max(3, (int)(H / 0.22f));
                for (int i = 0; i < tyres; i++)
                {
                    float y = H * i / tyres;
                    float r = w * (i % 2 == 0 ? 1f : 0.94f);
                    B(-r, y, -d * (r / w), r, H * (i + 1) / tyres - 0.015f, d * (r / w),
                        i % 2 == 0 ? dark : dark * 1.4f);
                }
                break;
            }
            case FurnitureType.OilDrum:
            {
                var col = Hash(p, 0) < 0.45f ? C(0.18f, 0.34f, 0.56f) : C(0.56f, 0.18f, 0.14f);
                B(-w, 0, -d, w, H, d, col);
                B(-w * 1.04f, H * 0.28f, -d * 1.04f, w * 1.04f, H * 0.36f, d * 1.04f, col * 0.82f);
                B(-w * 1.04f, H * 0.64f, -d * 1.04f, w * 1.04f, H * 0.72f, d * 1.04f, col * 0.82f);
                B(-w * 0.9f, H, -d * 0.9f, w * 0.9f, H + 0.02f, d * 0.9f, col * 1.1f);
                break;
            }
            case FurnitureType.JerryCan:
                B(-w, 0, -d, w, H, d, C(0.58f, 0.18f, 0.14f));
                B(-w * 0.3f, H, -d * 0.5f, w * 0.3f, H + 0.05f, d * 0.5f, dark);   // the spout
                B(-w * 0.8f, H * 0.78f, -d - 0.02f, w * 0.8f, H * 0.86f, -d, dark); // the handle bar
                break;
            case FurnitureType.Compressor:
            {
                // a horizontal receiver on feet with the pump and motor on top
                B(-w, 0, -d, w, 0.1f, d, dark);
                B(-w, 0.1f, -d * 0.75f, w, H * 0.6f, d * 0.75f, C(0.20f, 0.32f, 0.52f));
                B(-w * 0.4f, H * 0.6f, -d * 0.6f, w * 0.1f, H, d * 0.6f, metal * 0.8f);
                B(w * 0.15f, H * 0.6f, -d * 0.5f, w * 0.8f, H * 0.92f, d * 0.5f, dark);
                break;
            }
            case FurnitureType.Locker:
            {
                // a bank of two or three doors, each with a vent and a handle
                var steel = C(0.42f, 0.52f, 0.60f);
                B(-w, 0, -d, w, H, d, steel);
                int doors = Math.Max(2, (int)(p.W / 0.4f));
                for (int i = 0; i < doors; i++)
                {
                    float x0 = -w + 2 * w * i / doors + 0.015f, x1 = -w + 2 * w * (i + 1) / doors - 0.015f;
                    B(x0, 0.03f, -d - 0.012f, x1, H - 0.03f, -d, steel * 0.9f);
                    for (int k = 0; k < 3; k++)
                        B(x0 + 0.03f, H - 0.16f - k * 0.05f, -d - 0.02f, x1 - 0.03f, H - 0.13f - k * 0.05f, -d - 0.012f, dark);
                    B(x1 - 0.07f, H * 0.5f, -d - 0.03f, x1 - 0.03f, H * 0.5f + 0.1f, -d - 0.012f, metal);
                }
                break;
            }
            // ---- #557: apartment blocks ------------------------------------------------------
            case FurnitureType.Pillar:
                // bare concrete, a darker band at its foot where the bumpers rub
                B(-w, 0, -d, w, H, d, C(0.66f, 0.66f, 0.64f));
                B(-w - 0.01f, 0, -d - 0.01f, w + 0.01f, 0.5f, d + 0.01f, C(0.86f, 0.70f, 0.14f));
                for (int i = 0; i < 3; i++)
                    B(-w - 0.012f, 0.08f + i * 0.16f, -d - 0.012f, w + 0.012f, 0.16f + i * 0.16f, d + 0.012f, dark);
                break;
            case FurnitureType.StorageCage:
            {
                // a tenant's compartment: slatted wooden walls with gaps, a door in front, boxes
                // and a pair of skis inside
                var lath = C(0.70f, 0.56f, 0.38f);
                foreach (float cx in new[] { -w, w - 0.05f })
                    for (float z = -d; z < d - 0.02f; z += 0.14f)
                        B(cx, 0, z, cx + 0.05f, H, Math.Min(z + 0.09f, d), lath);
                for (float x = -w; x < w - 0.02f; x += 0.14f)
                    B(x, 0, -d, Math.Min(x + 0.09f, w), H, -d + 0.04f, lath * 0.9f);
                for (float x = -w; x < w - 0.02f; x += 0.14f)
                    B(x, 0.05f, d - 0.04f, Math.Min(x + 0.09f, w), H - 0.05f, d, lath);
                B(-w, 0.05f, d - 0.05f, w, 0.12f, d, lath * 0.75f);
                B(-w, H - 0.12f, d - 0.05f, w, H - 0.05f, d, lath * 0.75f);
                B(w - 0.25f, H * 0.5f, d, w - 0.18f, H * 0.5f + 0.12f, d + 0.04f, dark);
                B(-w + 0.15f, 0, -d + 0.15f, -w + 0.7f, 0.5f, -d + 0.6f, C(0.62f, 0.50f, 0.32f));
                B(-w + 0.2f, 0.5f, -d + 0.2f, -w + 0.6f, 0.8f, -d + 0.55f, C(0.66f, 0.54f, 0.36f));
                if (Hash(p, 3) > 0.5f)
                    B(w - 0.45f, 0, -d + 0.1f, w - 0.35f, 1.7f, -d + 0.2f, C(0.80f, 0.16f, 0.14f));
                break;
            }
            case FurnitureType.Mailboxes:
            {
                // a bank of letterboxes, a slot and a name tag each
                var steel = C(0.58f, 0.62f, 0.64f);
                B(-w, H - 0.75f, -d, w, H, d, steel);
                int cols = Math.Max(2, (int)(p.W / 0.32f));
                for (int c = 0; c < cols; c++)
                    for (int r = 0; r < 3; r++)
                    {
                        float x0 = -w + 2 * w * c / cols + 0.02f, x1 = -w + 2 * w * (c + 1) / cols - 0.02f;
                        float ya = H - 0.75f + 0.25f * r + 0.02f, yb = ya + 0.21f;
                        B(x0, ya, d, x1, yb, d + 0.01f, steel * 0.86f);
                        B(x0 + 0.03f, yb - 0.05f, d + 0.01f, x1 - 0.03f, yb - 0.035f, d + 0.02f, dark);
                        B(x0 + 0.04f, ya + 0.03f, d + 0.01f, x0 + 0.12f, ya + 0.06f, d + 0.02f, white);
                    }
                break;
            }
            case FurnitureType.BikeRack:
            {
                // a floor rail and two or three bikes in it, each a frame on two thin wheels
                B(-w, 0, -d, w, 0.06f, -d + 0.08f, metal);
                int bikes = Hash(p, 5) < 0.5f ? 2 : 3;
                for (int i = 0; i < bikes; i++)
                {
                    float x = -w + 2 * w * (i + 0.5f) / bikes;
                    var bikeCol = (Hash(p, i) * 3) switch { < 1 => C(0.16f, 0.30f, 0.62f), < 2 => C(0.70f, 0.12f, 0.12f), _ => C(0.18f, 0.18f, 0.18f) };
                    foreach (float wz in new[] { -d + 0.05f, d - 0.3f })
                        B(x - 0.02f, 0, wz, x + 0.02f, 0.62f, wz + 0.26f, dark);
                    B(x - 0.025f, 0.35f, -d + 0.2f, x + 0.025f, 0.42f, d - 0.15f, bikeCol);
                    B(x - 0.025f, 0.42f, d - 0.3f, x + 0.025f, 0.85f, d - 0.25f, bikeCol);
                    B(x - 0.22f, 0.85f, d - 0.3f, x + 0.22f, 0.88f, d - 0.25f, dark);
                    B(x - 0.06f, 0.8f, -d + 0.25f, x + 0.06f, 0.84f, -d + 0.45f, dark);
                }
                break;
            }
            case FurnitureType.Bench:
                foreach (float lx in new[] { -w + 0.12f, w - 0.12f })
                    B(lx - 0.04f, 0, -d + 0.04f, lx + 0.04f, H - 0.05f, d - 0.04f, metal * 0.8f);
                B(-w, H - 0.05f, -d, w, H, d, wood);
                break;
            case FurnitureType.HardHatRack:
            {
                // pegs on a board, with hats and a high-vis on them
                B(-w, H - 0.5f, -d, w, H - 0.42f, d, wood * 0.9f);
                int pegs = Math.Max(2, (int)(p.W / 0.3f));
                for (int i = 0; i < pegs; i++)
                {
                    float x = -w + 2 * w * (i + 0.5f) / pegs;
                    B(x - 0.02f, H - 0.48f, -d - 0.08f, x + 0.02f, H - 0.44f, -d, metal);
                    float h = Hash(p, i);
                    if (h < 0.25f) continue;
                    var hat = h < 0.6f ? C(0.90f, 0.72f, 0.10f) : C(0.92f, 0.94f, 0.96f);
                    B(x - 0.11f, H - 0.62f, -d - 0.13f, x + 0.11f, H - 0.48f, -d - 0.01f, hat);
                    if (h > 0.8f) // a vest on the peg below
                        B(x - 0.13f, H - 1.1f, -d - 0.1f, x + 0.13f, H - 0.62f, -d - 0.02f, C(0.85f, 0.88f, 0.12f));
                }
                break;
            }
            case FurnitureType.FireExtinguisher:
            {
                // on its wall bracket, at the height the rules want it
                float head = H + 0.6f;
                B(-w * 0.1f, 0.55f, -d - 0.03f, w * 0.1f, head, -d, metal * 0.7f);
                B(-w * 0.8f, 0.6f, -d - 0.02f, w * 0.8f, head - 0.08f, d * 0.8f, C(0.70f, 0.12f, 0.10f));
                B(-w * 0.3f, head - 0.08f, -d * 0.2f, w * 0.3f, head, d * 0.2f, dark);
                B(-w * 0.9f, head - 0.2f, -d - 0.04f, -w * 0.2f, head - 0.14f, -d * 0.2f, dark); // the hose
                break;
            }
            case FurnitureType.SafetySign:
            {
                // a pictogram board: a bright field with a dark figure on it, flat to the wall
                float h = Hash(p, 0);
                var field = h < 0.45f ? C(0.14f, 0.34f, 0.66f)      // blue: what you must wear
                    : h < 0.8f ? C(0.88f, 0.76f, 0.10f)             // yellow: what to watch for
                    : C(0.72f, 0.14f, 0.12f);                       // red: what you must not do
                B(-w, H * 0.2f, -d, w, H, d, white);
                B(-w * 0.86f, H * 0.26f, -d - 0.012f, w * 0.86f, H * 0.94f, -d, field);
                B(-w * 0.3f, H * 0.4f, -d - 0.02f, w * 0.3f, H * 0.82f, -d - 0.012f, dark);
                break;
            }
            case FurnitureType.TimeClock:
                B(-w, 0.9f, -d, w, 0.9f + H, d, C(0.80f, 0.80f, 0.78f));
                B(-w * 0.7f, 0.95f, -d - 0.012f, w * 0.7f, 0.9f + H * 0.7f, -d, dark);
                break;
            case FurnitureType.Whiteboard:
                B(-w, 1.0f, -d, w, 1.0f + H, d, C(0.30f, 0.32f, 0.34f));
                B(-w + 0.05f, 1.05f, -d - 0.015f, w - 0.05f, 1.0f + H - 0.05f, -d, white);
                B(-w * 0.6f, 1.0f + H * 0.6f, -d - 0.025f, w * 0.1f, 1.0f + H * 0.64f, -d - 0.016f, C(0.20f, 0.36f, 0.66f));
                B(-w * 0.5f, 1.0f + H * 0.38f, -d - 0.025f, w * 0.4f, 1.0f + H * 0.42f, -d - 0.016f, C(0.66f, 0.22f, 0.20f));
                break;
            case FurnitureType.Banner:
            {
                // a hanging cloth banner: the dealership's colours, not the works'
                var col = Hash(p, 0) < 0.5f ? C(0.14f, 0.30f, 0.60f) : C(0.68f, 0.14f, 0.16f);
                B(-w, H - 2.2f, -d, w, H, d, col);
                B(-w, H - 0.08f, -d - 0.02f, w, H, d, metal * 0.8f);
                B(-w * 0.55f, H - 1.3f, -d - 0.012f, w * 0.55f, H - 0.5f, -d, white);
                break;
            }
            case FurnitureType.DeskCounter:
            {
                // a service counter: a worktop on a plinth, with a raised ledge facing the customer
                B(-w, 0, -d + 0.08f, w, H - 0.14f, d, C(0.80f, 0.78f, 0.74f));
                B(-w, H - 0.14f, -d, w, H - 0.04f, d, wood);
                B(-w, H - 0.04f, -d, w, H, -d + 0.1f, wood * 0.85f);
                B(-w * 0.55f, H, -d + 0.2f, -w * 0.1f, H + 0.22f, d * 0.4f, dark);  // a till
                break;
            }
            case FurnitureType.ShowroomPlinth:
                // a low polished dais a car stands on, with a lit edge reveal
                B(-w, 0, -d, w, H - 0.04f, d, C(0.22f, 0.23f, 0.26f));
                B(-w + 0.06f, H - 0.04f, -d + 0.06f, w - 0.06f, H, d - 0.06f, C(0.88f, 0.88f, 0.90f));
                break;
            case FurnitureType.Forklift:
            {
                // counterbalance truck: body, cage, mast and forks
                var orange = C(0.86f, 0.44f, 0.08f);
                B(-w, 0.14f, -d + 0.2f, w, 0.7f, d, orange);
                B(-w * 0.8f, 0.7f, d * 0.1f, w * 0.8f, 1.05f, d * 0.8f, dark);   // the seat
                foreach (float cx in new[] { -w + 0.08f, w - 0.08f })
                {
                    B(cx - 0.05f, 1.05f, d * 0.05f, cx + 0.05f, H, d * 0.15f, metal * 0.8f);
                    B(cx - 0.05f, 1.05f, d * 0.75f, cx + 0.05f, H, d * 0.85f, metal * 0.8f);
                }
                B(-w * 0.9f, H - 0.07f, d * 0.05f, w * 0.9f, H, d * 0.85f, metal * 0.7f);  // the cage roof
                foreach (float mx in new[] { -w * 0.6f, w * 0.6f })
                    B(mx - 0.05f, 0.14f, -d + 0.08f, mx + 0.05f, H - 0.25f, -d + 0.2f, metal * 0.85f);
                foreach (float fx in new[] { -w * 0.45f, w * 0.45f })
                    B(fx - 0.06f, 0.05f, -d - 0.5f, fx + 0.06f, 0.14f, -d + 0.08f, metal);
                foreach (float wz in new[] { -d + 0.45f, d - 0.35f })
                {
                    B(-w - 0.02f, 0, wz - 0.18f, -w + 0.14f, 0.42f, wz + 0.18f, dark);
                    B(w - 0.14f, 0, wz - 0.18f, w + 0.02f, 0.42f, wz + 0.18f, dark);
                }
                break;
            }
            case FurnitureType.TruckProp:
            {
                // a rigid lorry: cab over the front axle, box body behind it
                var cab = CarPaint(Hash(p, 5));
                B(-w, 0.45f, -d, w, H * 0.8f, -d + 2.0f, cab);
                B(-w * 0.9f, H * 0.42f, -d + 0.1f, w * 0.9f, H * 0.72f, -d + 0.22f, C(0.40f, 0.52f, 0.62f)); // screen
                B(-w, 0.75f, -d + 2.0f, w, H, d, C(0.88f, 0.88f, 0.86f));   // the box
                B(-w, 0.55f, -d + 2.0f, w, 0.75f, d, dark);                 // the chassis under it
                foreach (float wz in new[] { -d + 1.1f, d - 1.6f, d - 0.6f })
                {
                    B(-w - 0.03f, 0, wz - 0.45f, -w + 0.22f, 0.9f, wz + 0.45f, dark);
                    B(w - 0.22f, 0, wz - 0.45f, w + 0.03f, 0.9f, wz + 0.45f, dark);
                }
                break;
            }
            case FurnitureType.FloorMarking:
            {
                // paint, not a thing: a hazard-yellow lane edge or bay outline
                var hazard = C(0.86f, 0.74f, 0.12f);
                float line = Math.Min(0.09f, Math.Min(w, d) * 0.5f);
                if (p.D <= 0.2f)   // a single lane line
                    B(-w, 0.001f, -d, w, 0.012f, d, hazard);
                else
                {
                    B(-w, 0.001f, -d, w, 0.012f, -d + line, hazard);
                    B(-w, 0.001f, d - line, w, 0.012f, d, hazard);
                    B(-w, 0.001f, -d, -w + line, 0.012f, d, hazard);
                    B(w - line, 0.001f, -d, w, 0.012f, d, hazard);
                }
                break;
            }
        }
    }

    /// <summary>
    /// A stable 0..1 roll for one detail of one piece: which colour a drum is, whether a rack slot is
    /// empty, which way a shark in a bin lies (#501). From the piece's own place in the plan, so every peer builds the same warehouse and a
    /// rebuild does not reshuffle it.
    /// </summary>
    private static float Hash(FurniturePlan p, int salt) =>
        Occasions.OccasionHash.Unit((int)(p.X * 100), (int)(p.Z * 100), salt, (uint)p.Type);

    /// <summary>Body colour for a car or a lorry cab, from a roll: the Swiss car park, roughly.</summary>
    private static Color CarPaint(float roll) => roll switch
    {
        < 0.20f => C(0.72f, 0.73f, 0.75f),   // silver, still the commonest
        < 0.36f => C(0.16f, 0.17f, 0.19f),   // black
        < 0.50f => C(0.90f, 0.90f, 0.89f),   // white
        < 0.62f => C(0.34f, 0.36f, 0.40f),   // graphite
        < 0.74f => C(0.58f, 0.14f, 0.12f),   // red
        < 0.84f => C(0.16f, 0.30f, 0.54f),   // blue
        < 0.91f => C(0.18f, 0.36f, 0.26f),   // racing green
        < 0.96f => C(0.72f, 0.56f, 0.18f),   // sand
        _ => C(0.78f, 0.52f, 0.10f),         // the one bright one on the forecourt
    };

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
