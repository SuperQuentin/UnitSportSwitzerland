using Godot;
using UnitSport.Player;

namespace UnitSport.Avatar;

/// <summary>What the steamer is made of: its meshes, its seats and the deck you walk on (#303).</summary>
public sealed record SteamerParts(ArrayMesh Hull, ArrayMesh Wheel, ArrayMesh[] Gates, ArrayMesh[] Planks, ArrayMesh Lever,
    SeatAnchor[] Seats, VehicleDeck Deck);

/// <summary>
/// The CGN Belle Époque paddle steamer (#303), authored like every machine (+Z forward, +X the
/// ship's left, the origin on the keel under the centre of mass) and flipped to node space by
/// <see cref="MeshScratch.Build()"/>. Low-poly: a white hull lofted from the lines it floats on
/// (<see cref="SteamerLines"/>) with a black boot-top at the waterline and an antifouling bottom,
/// the paddle boxes with their gilt fans, the red wheels (their own nodes, they turn), the buff
/// funnel with its black top, the open bow, the forward saloon under the upper deck, the engine
/// casing, the wheelhouse on the bridge, the awning aft, the stairs. CGN's colours, no names or
/// logos. Drawn in the figure material, so every style restyles it.
///
/// <para>
/// The deck (<see cref="VehicleDeck"/>, #162) is filled from the same numbers: the main deck, the
/// saloon's walls and doorways, the casing, the paddle boxes, the stairs as ramps meeting the upper
/// deck flush, the railings as solid boxes, every seat a block, the gangway gates (doors 0 port,
/// 1 starboard) with their planks and buttons, the posts and rails to hold, and the floor plan
/// aboard (the hull's outline, the planks).
/// </para>
/// Stations along the ship (<c>at</c>) are metres aft of the stem at the main deck.
/// </summary>
public static class SteamerMeshBuilder
{
    /// <summary>The stem at the main deck, authored z; the deck is <see cref="SteamerLines.Length"/> long.</summary>
    public const float Bow = -SteamerLines.StemZ;
    public const float DeckY = SteamerLines.Depth;
    /// <summary>The upper deck's top, and its slab's underside (the saloon's and the covered decks' ceiling).</summary>
    public const float UpperY = 5.6f, UnderUpper = 5.45f;
    public const float RailHeight = 1.05f;

    public const float SaloonFrom = 13f, SaloonTo = 29f, SaloonHalf = 3.4f;
    public const float CasingFrom = 31f, CasingTo = 43f, CasingHalf = 1.6f;
    public const float UpperFrom = 13f, UpperTo = 62f, UpperHalf = 4.15f;
    public const float HouseFrom = 30.6f, HouseTo = 34f, HouseHalf = 1.9f, HouseTop = 8.35f;
    public const float FunnelAt = 37.6f, FunnelTop = 12.4f, FunnelRadius = 0.95f;
    public const float AwningFrom = 40f, AwningY = 8.0f;
    /// <summary>The gangway openings, both sides, just aft of the paddle boxes.</summary>
    public const float GangFrom = 43.2f, GangTo = 45.2f;
    /// <summary>The gangway's plank, open: from the deck's edge this far out and down (onto a quay alongside).</summary>
    public const float PlankOut = 1.3f, PlankDrop = 0.3f;
    /// <summary>Where the plank starts: the floor slab's edge amidships (4.3 + 0.05 m), flush with it.</summary>
    public const float PlankEdge = 4.35f;
    /// <summary>The stairs: from the main deck here up to the upper deck's aft edge, either side.</summary>
    public const float StairFoot = 68.5f, StairX = 2.9f, StairWidth = 1.1f;
    /// <summary>The wheels' middle, as a station, and the paddle boxes either side of it.</summary>
    public static float WheelAt => Bow - SteamerLines.WheelZ;
    public static float BoxFrom => WheelAt - 4.75f;
    public static float BoxTo => WheelAt + 4.75f;
    public const float BoxOut = 7.95f, BoxBottom = 2.2f, BoxTop = 5.75f;

    /// <summary>The helmsman: on a tall stool at the wheel, mid-wheelhouse.</summary>
    public static readonly Vector3 HelmHip = new(0, UpperY + 0.78f, Bow - 32.25f);
    public static readonly Vector3 HelmWheel = new(0, UpperY + 1.0f, Bow - 31.65f);
    public const float HelmRadius = 0.5f;
    /// <summary>The telegraph's pedestal, to port of the wheel; its handle's pivot on top.</summary>
    public static readonly Vector3 TelegraphAt = new(0.85f, UpperY + 1.05f, Bow - 31.55f);
    /// <summary>The whistle on the funnel's steam pipe.</summary>
    public static readonly Vector3 WhistleAt = new(0, FunnelTop - 0.9f, Bow - FunnelAt + 1.25f);

    public static readonly Color White = new(0.94f, 0.94f, 0.91f);
    public static readonly Color Boot = new(0.08f, 0.08f, 0.09f);
    public static readonly Color Bottom = new(0.42f, 0.13f, 0.1f);
    public static readonly Color Teak = new(0.7f, 0.53f, 0.34f);
    public static readonly Color Varnish = new(0.45f, 0.25f, 0.12f);
    public static readonly Color Buff = new(0.91f, 0.76f, 0.4f);
    public static readonly Color Gilt = new(0.86f, 0.68f, 0.24f);
    public static readonly Color WheelRed = new(0.55f, 0.11f, 0.08f);
    public static readonly Color Canvas = new(0.96f, 0.95f, 0.9f);
    public static readonly Color Velvet = new(0.5f, 0.1f, 0.12f);
    public static readonly Color Carpet = new(0.36f, 0.08f, 0.1f);
    public static readonly Color Brass = new(0.8f, 0.64f, 0.3f);
    public static readonly Color Glass = new(0.55f, 0.68f, 0.75f, 0.35f);
    private static readonly Color Dark = new(0.12f, 0.12f, 0.13f);
    private static readonly Color ButtonBody = new(0.95f, 0.78f, 0.1f);
    private static readonly Color ButtonLamp = new(0.25f, 0.85f, 0.3f);

    /// <summary>Authored z of a station.</summary>
    public static float Z(float at) => Bow - at;

    /// <summary>The main deck's half-width at authored z: the hull's beam amidships, a fine bow, a rounded counter stern.</summary>
    public static float DeckHalf(float z)
    {
        const float mid = 4.3f;
        if (z > 18f) { float u = (z - 18f) / (Bow - 18f); return Mathf.Max(0.25f, mid * Mathf.Sqrt(Mathf.Max(0f, 1f - 0.97f * u * u))); }
        if (z < -22f) { float u = (-22f - z) / (Bow - 22f); return mid * Mathf.Sqrt(Mathf.Max(0f, 1f - 0.72f * u * u)); }
        return mid;
    }

    private static float HalfAt(float at) => DeckHalf(Z(at));

    /// <summary>The stations the deck's edge is built on: close together at the ends, where it curves, and at every opening.</summary>
    private static float[] EdgeStations()
    {
        var list = new List<float>();
        for (float at = 0.4f; at < 20f; at += 1f) list.Add(at);
        for (float at = 20f; at < 60f; at += 4f) list.Add(at);
        for (float at = 60f; at < 75.6f; at += 1.2f) list.Add(at);
        list.Add(75.6f);
        list.AddRange(new[] { BoxFrom, BoxTo, GangFrom, GangTo, SaloonFrom, SaloonTo, UpperTo, StairFoot });
        list.Sort();
        var clean = new List<float>();
        // none inside a gangway: its gate and plank are one piece
        foreach (float a in list)
            if ((clean.Count == 0 || a - clean[^1] > 0.05f) && !(a > GangFrom + 0.01f && a < GangTo - 0.01f)) clean.Add(a);
        return clean.ToArray();
    }

    private static readonly Dictionary<int, SteamerParts> _parts = new();

    /// <summary>The parts, built once (the meshes are shared by every steamer drawn).</summary>
    public static SteamerParts Parts()
    {
        if (_parts.TryGetValue(0, out var known)) return known;
        return _parts[0] = Build();
    }

    private static SteamerParts Build()
    {
        var m = new MeshScratch();
        var dk = new DeckBuilder(Bow);
        var seats = new List<SeatAnchor>();
        var stations = EdgeStations();

        Hull(m);
        MainDeck(m, dk, stations);
        PaddleBoxes(m, dk);
        Saloon(m, dk, seats);
        Casing(m, dk);
        UpperDeck(m, dk, seats);
        Wheelhouse(m, dk, seats);
        Funnel(m, dk);
        Stairs(m, dk);
        Fittings(m, dk);

        // the floor plan aboard: the deck's edge just outside the rail, out along each plank
        foreach (int side in new[] { 1, -1 })
        {
            var edge = side > 0 ? stations : Enumerable.Reverse(stations).ToArray();
            foreach (float at in edge)
            {
                float x = HalfAt(at) + 0.12f;
                if (at >= GangFrom - 0.01f && at <= GangTo + 0.01f)
                {
                    bool first = side > 0 ? at <= GangFrom + 0.01f : at >= GangTo - 0.01f;
                    if (first) { dk.PlanAt(side * x, at); dk.PlanAt(side * (HalfAt(at) + PlankOut + 0.2f), at); }
                    else { dk.PlanAt(side * (HalfAt(at) + PlankOut + 0.2f), at); dk.PlanAt(side * x, at); }
                    continue;
                }
                dk.PlanAt(side * x, at);
            }
        }
        var deck = dk.Build(0, new Aabb(new Vector3(-BoxOut, DeckY - 0.6f, -Bow - 0.2f), new Vector3(BoxOut * 2f, FunnelTop + 0.5f - (DeckY - 0.6f), 2f * Bow + 0.4f)));

        var gates = new ArrayMesh[2];
        var planks = new ArrayMesh[2];
        for (int door = 0; door < 2; door++)
        {
            float side = door == 0 ? 1f : -1f;
            var g = new MeshScratch();
            float x = side * (HalfAt((GangFrom + GangTo) * 0.5f) - 0.05f);
            Panel(g, x, GangFrom + 0.05f, x, GangTo - 0.05f, DeckY, RailHeight - 0.08f, White);
            g.Box(new Vector3(x, DeckY + RailHeight - 0.04f, Z((GangFrom + GangTo) * 0.5f)), new Vector3(0.1f, 0.08f, GangTo - GangFrom - 0.1f), Varnish);
            gates[door] = g.Build();
            var p = new MeshScratch();
            float edge = HalfAt((GangFrom + GangTo) * 0.5f);
            var low = new Vector3(side * (PlankEdge + PlankOut), DeckY - PlankDrop, 0);
            var high = new Vector3(side * PlankEdge, DeckY, 0);
            var mid = (low + high) * 0.5f;
            float run = (high - low).Length();
            float tilt = Mathf.Atan2(PlankDrop, PlankOut) * side;
            float zc = Z((GangFrom + GangTo) * 0.5f);
            p.Box(new Vector3(mid.X, mid.Y - 0.05f, zc), new Vector3(run, 0.08f, GangTo - GangFrom - 0.3f), Teak, new Basis(Vector3.Back, -tilt));
            foreach (float e in new[] { -1f, 1f })
                p.Tube(new Vector3(low.X, low.Y + 0.9f, zc + e * 0.8f), new Vector3(high.X, high.Y + 0.9f, zc + e * 0.8f), 0.025f, White, 5);
            planks[door] = p.Build();
        }
        return new SteamerParts(m.Build(), WheelMesh(), gates, planks, LeverMesh(), seats.ToArray(), deck);
    }

    // ---- the hull --------------------------------------------------------------------------

    /// <summary>The hull's stations, authored z (+ toward the bow), stern to stem.</summary>
    public static readonly float[] HullStations = BuildStations();

    private static float[] BuildStations()
    {
        var zs = new List<float> { -Bow, -37.2f, -36f, -35f, -33f, -30f };
        for (float z = -26f; z <= 26f; z += 4f) zs.Add(z);
        zs.AddRange(new[] { 30f, 33f, 35f, 36.3f, 37.3f, Bow });
        return zs.ToArray();
    }

    /// <summary>
    /// The hull's section at authored z, one side, keel to deck: (half-width, height over the keel)
    /// of the keel, the turn of the bilge, the waterline's boot-top, the topsides and the deck's edge.
    /// What is drawn (<see cref="Hull"/>) and what a parked ship collides as (#378).
    /// </summary>
    public static Vector2[] HullSection(float z)
    {
        float wl = SteamerLines.Draught, half = SteamerLines.WaterlineHalf;
        float t = Mathf.Clamp((z + half) / (2f * half), 0f, 1f);
        float hw = SteamerLines.Beam * 0.5f * SteamerLines.HalfBeam(t);
        float keel = SteamerLines.KeelRise(t);
        if (z > half)
        {
            float f = (z - half) / (Bow - half);
            hw = Mathf.Lerp(hw, 0.05f, f);
            keel = Mathf.Lerp(keel, DeckY - 0.4f, f);
        }
        else if (z < -half)
        {
            float f = (-half - z) / (Bow - half);
            hw = Mathf.Lerp(hw, 0.4f, f);
            keel = Mathf.Lerp(keel, 2.3f, f);
        }
        float dh = DeckHalf(z);
        float ends = z > half ? (z - half) / (Bow - half) : z < -half ? (-half - z) / (Bow - half) : 0f;
        float bilgeY = keel + SteamerLines.Bilge * 0.55f * (1f - ends);
        float lowY = Mathf.Max(bilgeY + 0.05f, wl - 0.25f);
        float highY = Mathf.Max(lowY + 0.05f, wl + 0.3f);
        float deckY = Mathf.Max(DeckY, highY + 0.05f);
        float highX = Mathf.Lerp(hw, dh, (highY - wl) / Mathf.Max(0.1f, deckY - wl));
        return new[] { new Vector2(0, keel), new Vector2(hw * 0.82f, bilgeY), new Vector2(hw, lowY), new Vector2(highX, highY), new Vector2(dh, deckY) };
    }

    /// <summary>
    /// The hull lofted stern to stem: keel, the turn of the bilge, the waterline's black boot-top,
    /// white topsides flaring to the deck's edge, the teak deck. Under the water it is the columns'
    /// shape (<see cref="SteamerLines.HalfBeam"/>, the keel's rise, the bilge); past the waterline's
    /// ends the stem rakes forward and the counter stern overhangs.
    /// </summary>
    private static void Hull(MeshScratch m)
    {
        var rings = new List<Vector3[]>();
        foreach (float z in HullStations)
        {
            var p = HullSection(z);
            rings.Add(new[]
            {
                new Vector3(0, p[0].Y, z),
                new Vector3(p[1].X, p[1].Y, z),
                new Vector3(p[2].X, p[2].Y, z),
                new Vector3(p[3].X, p[3].Y, z),
                new Vector3(p[4].X, p[4].Y, z),
                new Vector3(-p[4].X, p[4].Y, z),
                new Vector3(-p[3].X, p[3].Y, z),
                new Vector3(-p[2].X, p[2].Y, z),
                new Vector3(-p[1].X, p[1].Y, z),
            });
        }
        var zs = HullStations;
        m.Loft(rings, new[] { Bottom, Bottom, Boot, White, Teak, White, Boot, Bottom, Bottom }, White);
        // a gilt line under the deck's edge, standing a centimetre proud, and the rubbing strake
        for (int i = 0; i + 1 < zs.Length; i++)
        {
            float z0 = zs[i], z1 = zs[i + 1];
            foreach (float side in new[] { 1f, -1f })
            {
                Strip(m, new Vector3(side * (DeckHalf(z0) + 0.012f), DeckY - 0.32f, z0), new Vector3(side * (DeckHalf(z1) + 0.012f), DeckY - 0.32f, z1), 0.05f, Gilt);
                Strip(m, new Vector3(side * (DeckHalf(z0) + 0.03f), DeckY - 0.08f, z0), new Vector3(side * (DeckHalf(z1) + 0.03f), DeckY - 0.08f, z1), 0.12f, Varnish);
            }
        }
    }

    /// <summary>A thin band between two points along the hull's side, <paramref name="height"/> tall.</summary>
    private static void Strip(MeshScratch m, Vector3 a, Vector3 b, float height, Color colour)
    {
        var run = b - a;
        float length = run.Length();
        if (length < 0.01f) return;
        m.Box((a + b) * 0.5f, new Vector3(0.03f, height, length), colour, new Basis(Vector3.Up, Mathf.Atan2(run.X, run.Z)));
    }

    /// <summary>A wall panel from plan point a to b (authored x, station), from y up <paramref name="height"/>.</summary>
    private static void Panel(MeshScratch m, float x0, float at0, float x1, float at1, float y, float height, Color colour, float thick = 0.06f)
    {
        var a = new Vector3(x0, 0, Z(at0));
        var b = new Vector3(x1, 0, Z(at1));
        var run = b - a;
        float length = run.Length();
        if (length < 0.01f) return;
        m.Box(((a + b) * 0.5f) with { Y = y + height * 0.5f }, new Vector3(thick, height, length), colour, new Basis(Vector3.Up, Mathf.Atan2(run.X, run.Z)));
    }

    // ---- the main deck ---------------------------------------------------------------------

    private static void MainDeck(MeshScratch m, DeckBuilder dk, float[] stations)
    {
        // the floor: a slab per stretch, as wide as the wider end (the rail keeps anyone off the overhang)
        for (int i = 0; i + 1 < stations.Length; i++)
        {
            float a0 = stations[i], a1 = stations[i + 1];
            dk.Along(a0, a1, DeckY - 0.15f, DeckY, 2f * Mathf.Max(HalfAt(a0), HalfAt(a1)) + 0.1f);
        }
        dk.Along(0f, stations[0], DeckY - 0.15f, DeckY, 2f * HalfAt(stations[0]));
        // (the slab under a gangway is as wide as amidships, PlankEdge from the middle)
        dk.Along(stations[^1], SteamerLines.Length, DeckY - 0.15f, DeckY, 2f * HalfAt(stations[^1]));

        // the bulwark rail round the deck: solid panels with a varnished cap, a gap at each gangway,
        // the paddle boxes' inner walls amidships
        foreach (float side in new[] { 1f, -1f })
            for (int i = 0; i + 1 < stations.Length; i++)
            {
                float a0 = stations[i], a1 = stations[i + 1];
                float mid = (a0 + a1) * 0.5f;
                if (mid > BoxFrom && mid < BoxTo) continue;
                bool gate = mid > GangFrom && mid < GangTo;
                float x0 = side * (HalfAt(a0) - 0.05f), x1 = side * (HalfAt(a1) - 0.05f);
                if (gate)
                {
                    // the gangway's gate: a shut door to the walk; open, a plank down to a quay alongside
                    int door = side > 0 ? 0 : 1;
                    dk.Wall(x0, a0, x1, a1, DeckY, DeckY + RailHeight, 0.1f, DeckPart.DoorShut, door);
                    // its top edge is the floor slab's edge, flush: a lip of a centimetre stops the walk
                    dk.RampAcross(mid, side * (PlankEdge + PlankOut), DeckY - PlankDrop, side * PlankEdge, DeckY, a1 - a0 - 0.3f, DeckPart.DoorStep, door);
                    continue;
                }
                Panel(m, x0, a0, x1, a1, DeckY, RailHeight - 0.08f, White);
                Panel(m, x0, a0, x1, a1, DeckY + RailHeight - 0.08f, 0.08f, Varnish, 0.11f);
                dk.Wall(x0, a0, x1, a1, DeckY, DeckY + RailHeight, 0.1f);
                if (i % 2 == 0) dk.Hold(side * (HalfAt(mid) - 0.3f), mid);
            }
        // the bow closed between the two rails, the counter's rail across the stern
        float bow = stations[0], stern = stations[^1];
        Panel(m, HalfAt(bow) - 0.05f, bow, -(HalfAt(bow) - 0.05f), bow, DeckY, RailHeight, White);
        dk.Wall(HalfAt(bow) - 0.05f, bow, -(HalfAt(bow) - 0.05f), bow, DeckY, DeckY + RailHeight, 0.1f);
        Panel(m, HalfAt(stern) - 0.05f, stern, -(HalfAt(stern) - 0.05f), stern, DeckY, RailHeight - 0.08f, White);
        Panel(m, HalfAt(stern) - 0.05f, stern, -(HalfAt(stern) - 0.05f), stern, DeckY + RailHeight - 0.08f, 0.08f, Varnish, 0.11f);
        dk.Wall(HalfAt(stern) - 0.05f, stern, -(HalfAt(stern) - 0.05f), stern, DeckY, DeckY + RailHeight, 0.1f);

        // the gangways' buttons, inside and out, just ahead of each opening at hand height
        for (int door = 0; door < 2; door++)
        {
            float side = door == 0 ? 1f : -1f;
            float at = GangFrom - 0.25f, edge = HalfAt(at);
            foreach (bool outside in new[] { false, true })
            {
                float x = side * (outside ? edge + 0.07f : edge - 0.17f);
                var normal = new Vector3(outside ? side : -side, 0, 0);
                var p = new Vector3(x, DeckY + 0.95f, Z(at));
                m.Box(p, new Vector3(0.024f, 0.12f, 0.08f), ButtonBody);
                m.Box(p + normal * 0.012f + new Vector3(0, 0.02f, 0), new Vector3(0.004f, 0.04f, 0.04f), ButtonLamp);
                dk.Button(door, p, normal);
            }
        }
    }

    // ---- the paddle boxes and wheels --------------------------------------------------------

    private static void PaddleBoxes(MeshScratch m, DeckBuilder dk)
    {
        float z0 = Z(BoxFrom), z1 = Z(BoxTo), zc = Z(WheelAt);
        foreach (float side in new[] { 1f, -1f })
        {
            // the box's profile, front to back: an arch over the wheel on a straight bottom, extruded outboard
            Vector3[] Profile(float x)
            {
                var ring = new List<Vector3> { new(x, BoxBottom, z0) };
                for (int i = 0; i <= 8; i++)
                {
                    float u = -1f + 2f * i / 8f;
                    float y = 3.4f + (BoxTop - 3.4f) * Mathf.Sqrt(Mathf.Max(0f, 1f - u * u));
                    ring.Add(new Vector3(x, y, Mathf.Lerp(z0, z1, (u + 1f) * 0.5f)));
                }
                ring.Add(new Vector3(x, BoxBottom, z1));
                return ring.ToArray();
            }
            var colours = new Color[11];
            for (int i = 0; i < colours.Length; i++) colours[i] = White;
            colours[^1] = Dark;   // the open bottom over the wheel
            m.Loft(new[] { Profile(side * (SteamerLines.Beam * 0.5f - 0.1f)), Profile(side * BoxOut) }, colours, White);
            // the gilt fan on its face, rays from the axle to the arch, and a gilt rim along it
            float face = side * (BoxOut + 0.015f);
            for (int r = 0; r < 9; r++)
            {
                float u = -0.8f + 1.6f * r / 8f;
                float y = 3.4f + (BoxTop - 3.4f) * Mathf.Sqrt(1f - u * u) - 0.25f;
                var tip = new Vector3(face, y, Mathf.Lerp(z0, z1, (u + 1f) * 0.5f));
                var hub = new Vector3(face, SteamerLines.WheelY, zc);
                var run = tip - hub;
                m.Box((tip + hub) * 0.5f, new Vector3(0.02f, run.Length(), 0.12f), Gilt, new Basis(Vector3.Right, Mathf.Atan2(run.Z, run.Y)));
            }
            m.Box(new Vector3(face, SteamerLines.WheelY, zc), new Vector3(0.03f, 0.7f, 0.7f), Gilt);
            m.Box(new Vector3(face, BoxBottom + 0.2f, zc), new Vector3(0.02f, 0.18f, z0 - z1 - 0.2f), Varnish);
            dk.Box(new Vector3(side * (SteamerLines.Beam * 0.5f + BoxOut) * 0.5f - side * 0.05f, (BoxBottom + BoxTop) * 0.5f, zc),
                new Vector3(BoxOut - SteamerLines.Beam * 0.5f + 0.1f, BoxTop - BoxBottom, z0 - z1));
        }
    }

    /// <summary>One wheel, authored round its axle (+X along it): two red rims on spokes, the floats between them.</summary>
    private static ArrayMesh WheelMesh()
    {
        var m = new MeshScratch();
        float r = SteamerLines.WheelRadius;
        const int spokes = 12;
        m.Tube(new Vector3(-1.05f, 0, 0), new Vector3(1.05f, 0, 0), 0.22f, Dark, 8);
        foreach (float x in new[] { -0.85f, 0.85f })
        {
            m.Ring(new Vector3(x, 0, 0), Vector3.Right, r - 0.16f, r, 0.08f, WheelRed, 16);
            m.Ring(new Vector3(x, 0, 0), Vector3.Right, 0.2f, 0.32f, 0.1f, WheelRed, 8);
            for (int i = 0; i < spokes; i++)
            {
                float a = Mathf.Tau * i / spokes;
                var dir = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a));
                m.Tube(new Vector3(x, 0, 0) + dir * 0.3f, new Vector3(x, 0, 0) + dir * (r - 0.1f), 0.035f, WheelRed, 4);
            }
        }
        for (int i = 0; i < spokes; i++)
        {
            float a = Mathf.Tau * (i + 0.5f) / spokes;
            var dir = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a));
            m.Box(dir * (r - 0.32f), new Vector3(1.85f, 0.55f, 0.07f), Varnish, new Basis(Vector3.Right, a));
        }
        return m.Build();
    }

    // ---- the saloon --------------------------------------------------------------------------

    /// <summary>
    /// The forward lower saloon: a deckhouse on the main deck under the upper deck, big windows down
    /// both sides, a door forward onto the open bow and two aft onto the covered side decks, red
    /// velvet seats in rows on a red carpet.
    /// </summary>
    private static void Saloon(MeshScratch m, DeckBuilder dk, List<SeatAnchor> seats)
    {
        float h0 = DeckY, h1 = UnderUpper;
        float winLow = DeckY + 0.95f, winHigh = DeckY + 2.05f;
        foreach (float side in new[] { 1f, -1f })
        {
            float x = side * SaloonHalf;
            m.Box(new Vector3(x, (h0 + winLow) * 0.5f, Z((SaloonFrom + SaloonTo) * 0.5f)), new Vector3(0.1f, winLow - h0, SaloonTo - SaloonFrom), White);
            m.Box(new Vector3(x, (winHigh + h1) * 0.5f, Z((SaloonFrom + SaloonTo) * 0.5f)), new Vector3(0.1f, h1 - winHigh, SaloonTo - SaloonFrom), White);
            // the windows: varnished pillars, panes between
            for (float at = SaloonFrom; at < SaloonTo + 0.01f; at += 1.6f)
            {
                m.Box(new Vector3(x, (winLow + winHigh) * 0.5f, Z(at)), new Vector3(0.11f, winHigh - winLow, 0.16f), Varnish);
                if (at + 1.6f <= SaloonTo + 0.01f)
                    m.Pane(new[]
                    {
                        new Vector3(x + side * 0.01f, winLow, Z(at + 0.08f)), new Vector3(x + side * 0.01f, winLow, Z(at + 1.52f)),
                        new Vector3(x + side * 0.01f, winHigh, Z(at + 1.52f)), new Vector3(x + side * 0.01f, winHigh, Z(at + 0.08f)),
                    }, Glass);
            }
            dk.Along(SaloonFrom, SaloonTo, h0, h1, 0.12f, x);
        }
        // the front wall with its door in the middle, the aft wall with a door to each side deck
        EndWall(m, dk, SaloonFrom, h0, h1, new[] { (-0.6f, 0.6f) });
        EndWall(m, dk, SaloonTo - 0.1f, h0, h1, new[] { (1.8f, 2.9f), (-2.9f, -1.8f) });
        m.Box(new Vector3(0, DeckY + 0.01f, Z((SaloonFrom + SaloonTo) * 0.5f)), new Vector3(SaloonHalf * 2f - 0.12f, 0.02f, SaloonTo - SaloonFrom - 0.2f), Carpet);

        // the seats: a pair each side of the aisle, in rows facing forward
        for (float at = SaloonFrom + 2.5f; at < SaloonTo - 2f; at += 1.5f)
            foreach (float x in new[] { 1.45f, 1.0f, -1.0f, -1.45f })
                Chair(m, dk, seats, new Vector3(x, DeckY + 0.5f, Z(at)), DeckY, Velvet, upholstered: true);
        // tables along the windows
        foreach (float side in new[] { 1f, -1f })
            for (float at = SaloonFrom + 2.6f; at < SaloonTo - 2f; at += 3f)
            {
                var top = new Vector3(side * (SaloonHalf - 0.45f), DeckY + 0.72f, Z(at));
                m.Box(top, new Vector3(0.7f, 0.05f, 1.0f), White);
                m.Box(top with { Y = DeckY + 0.36f }, new Vector3(0.1f, 0.7f, 0.1f), Varnish);
                dk.Box(top with { Y = DeckY + 0.375f }, new Vector3(0.7f, 0.75f, 1.0f));
            }
    }

    /// <summary>An end wall across the ship at a station, 0.1 m deep, with door openings (authored x ranges) 2.1 m high.</summary>
    private static void EndWall(MeshScratch m, DeckBuilder dk, float at, float y0, float y1, (float From, float To)[] doors)
    {
        float x = -SaloonHalf;
        var cuts = doors.OrderBy(d => d.From).ToList();
        foreach (var (from, to) in cuts.Append((SaloonHalf, SaloonHalf)))
        {
            if (from > x)
            {
                m.Box(new Vector3((x + from) * 0.5f, (y0 + y1) * 0.5f, Z(at + 0.05f)), new Vector3(from - x, y1 - y0, 0.1f), White);
                dk.Box(new Vector3((x + from) * 0.5f, (y0 + y1) * 0.5f, Z(at + 0.05f)), new Vector3(from - x, y1 - y0, 0.1f));
            }
            if (to > from)
            {
                // over the doorway
                m.Box(new Vector3((from + to) * 0.5f, (y0 + 2.1f + y1) * 0.5f, Z(at + 0.05f)), new Vector3(to - from, y1 - y0 - 2.1f, 0.1f), White);
                dk.Box(new Vector3((from + to) * 0.5f, (y0 + 2.1f + y1) * 0.5f, Z(at + 0.05f)), new Vector3(to - from, y1 - y0 - 2.1f, 0.1f));
                m.Box(new Vector3((from + to) * 0.5f, y0 + 2.1f, Z(at + 0.05f)), new Vector3(to - from + 0.1f, 0.08f, 0.14f), Varnish);
            }
            x = Mathf.Max(x, to);
        }
    }

    /// <summary>
    /// A seat facing forward with its hip at <paramref name="hip"/> (authored): velvet in the saloon,
    /// a varnished slatted chair on deck; a block to the walk from its foot to the top of its back.
    /// </summary>
    private static void Chair(MeshScratch m, DeckBuilder dk, List<SeatAnchor> seats, Vector3 hip, float floor, Color colour, bool upholstered)
    {
        float recline = upholstered ? HeavyCabin.PassengerSeat(m, hip, floor, colour, coach: false) : Bench(m, hip, floor);
        seats.Add(new SeatAnchor(0, CarMeshBuilder.Turned(hip), recline, floor));
        float top = hip.Y + 0.6f;
        dk.Box(new Vector3(hip.X, (floor + top) * 0.5f, hip.Z + 0.05f), new Vector3(0.44f, top - floor, 0.62f));
    }

    /// <summary>A deck chair of varnished slats: the seat, the back leaning 0.15 rad, four legs. Its recline.</summary>
    private static float Bench(MeshScratch m, Vector3 hip, float floor)
    {
        const float recline = 0.15f;
        float seat = hip.Y - 0.09f;
        var back = new Vector3(0, Mathf.Cos(recline), -Mathf.Sin(recline));
        var ahead = new Vector3(0, Mathf.Sin(recline), Mathf.Cos(recline));
        m.Box(new Vector3(hip.X, seat - 0.03f, hip.Z + 0.13f), new Vector3(0.44f, 0.05f, 0.42f), Varnish);
        m.Box(hip + back * 0.32f - ahead * 0.14f, new Vector3(0.44f, 0.45f, 0.05f), Varnish, new Basis(Vector3.Right, -recline));
        foreach (float lx in new[] { -0.18f, 0.18f })
            foreach (float lz in new[] { -0.05f, 0.3f })
                m.Box(new Vector3(hip.X + lx, (floor + seat) * 0.5f, hip.Z + lz), new Vector3(0.04f, seat - floor, 0.04f), Dark);
        return recline;
    }

    // ---- amidships ------------------------------------------------------------------------------

    /// <summary>The engine's casing between the paddle boxes, its skylights showing the cranks' glint; solid to the walk.</summary>
    private static void Casing(MeshScratch m, DeckBuilder dk)
    {
        float zc = Z((CasingFrom + CasingTo) * 0.5f), len = CasingTo - CasingFrom;
        m.Box(new Vector3(0, (DeckY + UnderUpper) * 0.5f, zc), new Vector3(CasingHalf * 2f, UnderUpper - DeckY, len), White);
        foreach (float side in new[] { 1f, -1f })
        {
            float x = side * (CasingHalf + 0.01f);
            for (float at = CasingFrom + 1f; at + 1.4f < CasingTo; at += 2f)
                m.Pane(new[]
                {
                    new Vector3(x, DeckY + 1.2f, Z(at)), new Vector3(x, DeckY + 1.2f, Z(at + 1.4f)),
                    new Vector3(x, DeckY + 2.1f, Z(at + 1.4f)), new Vector3(x, DeckY + 2.1f, Z(at)),
                }, Glass with { A = 0.6f });
            m.Box(new Vector3(side * CasingHalf, DeckY + 0.5f, zc), new Vector3(0.12f, 0.08f, len), Varnish);
        }
        // a glimpse of the engine through the skylights: brass cylinders and cranks
        foreach (float x in new[] { -0.6f, 0.6f })
            m.Tube(new Vector3(x, DeckY + 0.6f, Z(CasingFrom + 4f)), new Vector3(x, DeckY + 1.9f, Z(CasingFrom + 7f)), 0.42f, Brass, 8);
        dk.Along(CasingFrom, CasingTo, DeckY, UnderUpper, CasingHalf * 2f);
    }

    // ---- the upper deck --------------------------------------------------------------------

    /// <summary>
    /// The upper deck from over the saloon to over the aft deck: teak on white, posts down to the
    /// main deck's rail, a rail round it (open at the stairs' heads), the awning aft on its posts,
    /// deck chairs in rows under it.
    /// </summary>
    private static void UpperDeck(MeshScratch m, DeckBuilder dk, List<SeatAnchor> seats)
    {
        float zc = Z((UpperFrom + UpperTo) * 0.5f), len = UpperTo - UpperFrom;
        m.Box(new Vector3(0, (UnderUpper + UpperY - 0.02f) * 0.5f, zc), new Vector3(UpperHalf * 2f, UpperY - 0.02f - UnderUpper, len), White);
        m.Box(new Vector3(0, UpperY - 0.01f, zc), new Vector3(UpperHalf * 2f - 0.1f, 0.02f, len - 0.1f), Teak);
        dk.Along(UpperFrom, UpperTo, UnderUpper, UpperY, UpperHalf * 2f);
        // posts under its edges where nothing else holds it up
        foreach (float side in new[] { 1f, -1f })
            for (float at = SaloonTo + 1f; at <= UpperTo + 0.01f; at += 3f)
            {
                if (at > BoxFrom - 0.3f && at < BoxTo + 0.3f) continue;
                if (at > GangFrom - 0.3f && at < GangTo + 0.3f) continue;
                var foot = new Vector3(side * (UpperHalf - 0.12f), DeckY, Z(at));
                m.Tube(foot, foot with { Y = UnderUpper }, 0.05f, White, 6);
                dk.Box(foot with { Y = (DeckY + UnderUpper) * 0.5f }, new Vector3(0.1f, UnderUpper - DeckY, 0.1f));
                dk.Hold(side * (UpperHalf - 0.12f), at);
            }

        // the rail round it: white posts, a varnished handrail, two thin rails; solid to the walk
        float railTop = UpperY + 1.0f;
        void Rail(float x0, float at0, float x1, float at1)
        {
            var a = new Vector3(x0, 0, Z(at0));
            var b = new Vector3(x1, 0, Z(at1));
            float length = (b - a).Length();
            int posts = Mathf.Max(1, Mathf.RoundToInt(length / 1.5f));
            for (int i = 0; i <= posts; i++)
            {
                var p = a.Lerp(b, (float)i / posts);
                m.Box(p with { Y = (UpperY + railTop) * 0.5f }, new Vector3(0.05f, railTop - UpperY, 0.05f), White);
                if (i < posts) dk.Hold(p.X, Bow - p.Z);
            }
            m.Tube(a with { Y = railTop }, b with { Y = railTop }, 0.04f, Varnish, 5);
            m.Tube(a with { Y = UpperY + 0.62f }, b with { Y = UpperY + 0.62f }, 0.015f, White, 4);
            m.Tube(a with { Y = UpperY + 0.3f }, b with { Y = UpperY + 0.3f }, 0.015f, White, 4);
            dk.Wall(x0, at0, x1, at1, UpperY, railTop, 0.08f);
        }
        float rx = UpperHalf - 0.05f;
        foreach (float side in new[] { 1f, -1f })
            Rail(side * rx, UpperFrom + 0.05f, side * rx, UpperTo - 0.05f);
        Rail(rx, UpperFrom + 0.05f, -rx, UpperFrom + 0.05f);
        // aft: open where the stairs come up
        float s0 = StairX - StairWidth * 0.5f, s1 = StairX + StairWidth * 0.5f;
        Rail(-s0, UpperTo - 0.05f, s0, UpperTo - 0.05f);
        Rail(s1, UpperTo - 0.05f, rx, UpperTo - 0.05f);
        Rail(-rx, UpperTo - 0.05f, -s1, UpperTo - 0.05f);

        // the awning: canvas on posts, its scalloped valance round the edge
        float az = Z((AwningFrom + UpperTo) * 0.5f), alen = UpperTo - AwningFrom;
        m.Box(new Vector3(0, AwningY, az), new Vector3(UpperHalf * 2f + 0.3f, 0.05f, alen + 0.3f), Canvas);
        dk.Along(AwningFrom - 0.15f, UpperTo + 0.15f, AwningY - 0.025f, AwningY + 0.025f, UpperHalf * 2f + 0.3f);
        foreach (float side in new[] { 1f, -1f })
        {
            for (float at = AwningFrom; at < UpperTo; at += 0.6f)
                m.Box(new Vector3(side * (UpperHalf + 0.15f), AwningY - 0.14f, Z(at + 0.3f)), new Vector3(0.02f, (int)((at - AwningFrom) / 0.6f) % 2 == 0 ? 0.24f : 0.16f, 0.6f), Canvas);
            for (float at = AwningFrom + 0.2f; at < UpperTo; at += 2.75f)
            {
                var foot = new Vector3(side * (UpperHalf - 0.15f), UpperY, Z(at));
                m.Tube(foot, foot with { Y = AwningY }, 0.04f, White, 6);
                dk.Box(foot with { Y = (UpperY + AwningY) * 0.5f }, new Vector3(0.08f, AwningY - UpperY, 0.08f));
                dk.Hold(side * (UpperHalf - 0.15f), at);
            }
        }

        // deck chairs in rows facing forward under the awning
        for (float at = AwningFrom + 5f; at < UpperTo - 2.5f; at += 2f)
            foreach (float x in new[] { 1.45f, 1.0f, -1.0f, -1.45f })
                Chair(m, dk, seats, new Vector3(x, UpperY + 0.48f, Z(at)), UpperY, Varnish, upholstered: false);
    }

    /// <summary>
    /// The wheelhouse on the bridge between the paddle boxes: varnished below, glass all round, a
    /// white roof; the big spoked wheel, the telegraph to port of it, the helmsman's stool; a door aft.
    /// </summary>
    private static void Wheelhouse(MeshScratch m, DeckBuilder dk, List<SeatAnchor> seats)
    {
        float z0 = Z(HouseFrom), z1 = Z(HouseTo), zc = (z0 + z1) * 0.5f, len = HouseTo - HouseFrom;
        float sill = UpperY + 1.0f, head = HouseTop - 0.25f;
        foreach (float side in new[] { 1f, -1f })
        {
            float x = side * HouseHalf;
            m.Box(new Vector3(x, (UpperY + sill) * 0.5f, zc), new Vector3(0.08f, sill - UpperY, len), Varnish);
            m.Box(new Vector3(x, (head + HouseTop) * 0.5f, zc), new Vector3(0.08f, HouseTop - head, len), White);
            for (int i = 0; i <= 2; i++)
                m.Box(new Vector3(x, (sill + head) * 0.5f, Mathf.Lerp(z0, z1, i / 2f)), new Vector3(0.09f, head - sill, 0.08f), Varnish);
            m.Pane(new[] { new Vector3(x, sill, z0), new Vector3(x, sill, z1), new Vector3(x, head, z1), new Vector3(x, head, z0) }, Glass);
            dk.Along(HouseFrom, HouseTo, UpperY, HouseTop, 0.1f, x);
        }
        // the front: glass over a varnished panel
        m.Box(new Vector3(0, (UpperY + sill) * 0.5f, z0), new Vector3(HouseHalf * 2f, sill - UpperY, 0.08f), Varnish);
        m.Box(new Vector3(0, (head + HouseTop) * 0.5f, z0), new Vector3(HouseHalf * 2f, HouseTop - head, 0.08f), White);
        for (int i = 0; i <= 3; i++)
            m.Box(new Vector3(-HouseHalf + i * HouseHalf * 2f / 3f, (sill + head) * 0.5f, z0), new Vector3(0.08f, head - sill, 0.09f), Varnish);
        m.Pane(new[] { new Vector3(HouseHalf, sill, z0), new Vector3(-HouseHalf, sill, z0), new Vector3(-HouseHalf, head, z0), new Vector3(HouseHalf, head, z0) }, Glass);
        dk.Along(HouseFrom, HouseFrom + 0.1f, UpperY, HouseTop, HouseHalf * 2f);
        // the back: a door in the middle
        foreach (float side in new[] { 1f, -1f })
        {
            m.Box(new Vector3(side * (HouseHalf + 0.5f) * 0.5f, (UpperY + HouseTop) * 0.5f, z1), new Vector3(HouseHalf - 0.5f, HouseTop - UpperY, 0.08f), White);
            dk.Box(new Vector3(side * (HouseHalf + 0.5f) * 0.5f, (UpperY + HouseTop) * 0.5f, z1), new Vector3(HouseHalf - 0.5f, HouseTop - UpperY, 0.1f));
        }
        m.Box(new Vector3(0, (UpperY + 2.1f + HouseTop) * 0.5f, z1), new Vector3(1f, HouseTop - UpperY - 2.1f, 0.08f), White);
        dk.Box(new Vector3(0, (UpperY + 2.1f + HouseTop) * 0.5f, z1), new Vector3(1f, HouseTop - UpperY - 2.1f, 0.1f));
        // the roof, overhanging, and its teak floor
        m.Box(new Vector3(0, HouseTop + 0.06f, zc), new Vector3(HouseHalf * 2f + 0.4f, 0.12f, len + 0.4f), White);
        dk.Along(HouseFrom - 0.2f, HouseTo + 0.2f, HouseTop, HouseTop + 0.12f, HouseHalf * 2f + 0.4f);
        m.Box(new Vector3(0, UpperY + 0.01f, zc), new Vector3(HouseHalf * 2f - 0.1f, 0.02f, len - 0.1f), Varnish.Lightened(0.15f));

        // the wheel on its pedestal, the stool behind it
        var hub = HelmWheel;
        m.Ring(hub, Vector3.Back, HelmRadius - 0.05f, HelmRadius, 0.06f, Varnish, 16);
        for (int i = 0; i < 8; i++)
        {
            float a = Mathf.Tau * i / 8f;
            var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
            m.Tube(hub + dir * 0.08f, hub + dir * (HelmRadius + 0.14f), 0.022f, Varnish, 4);
        }
        m.Tube(hub + Vector3.Forward * 0.04f, hub + Vector3.Back * 0.35f, 0.07f, Brass, 6);
        m.Box(new Vector3(0, (UpperY + hub.Y - 0.1f) * 0.5f, hub.Z + 0.35f), new Vector3(0.36f, hub.Y - 0.1f - UpperY, 0.36f), Varnish);
        dk.Box(new Vector3(0, (UpperY + hub.Y) * 0.5f, hub.Z + 0.3f), new Vector3(0.4f, hub.Y - UpperY, 0.45f));
        m.Tube(HelmHip with { Y = UpperY }, HelmHip with { Y = HelmHip.Y - 0.1f }, 0.05f, Dark, 6);
        m.Tube(HelmHip with { Y = HelmHip.Y - 0.12f }, HelmHip with { Y = HelmHip.Y - 0.08f }, 0.2f, Varnish, 10);
        m.Ring(HelmHip with { Y = UpperY + 0.3f }, Vector3.Up, 0.14f, 0.18f, 0.02f, Brass, 10);
        // the telegraph: a brass column and its dial, the handle its own node
        var tel = TelegraphAt;
        m.Tube(tel with { Y = UpperY }, tel with { Y = tel.Y - 0.1f }, 0.08f, Brass, 8);
        m.Ring(tel + new Vector3(0, 0.12f, 0), Vector3.Right, 0f, 0.24f, 0.12f, Brass, 14);
        m.Ring(tel + new Vector3(0.065f, 0.12f, 0), Vector3.Right, 0f, 0.2f, 0.01f, White, 14);
        m.Ring(tel + new Vector3(-0.065f, 0.12f, 0), Vector3.Right, 0f, 0.2f, 0.01f, White, 14);
        dk.Box(tel with { Y = (UpperY + tel.Y + 0.3f) * 0.5f }, new Vector3(0.3f, tel.Y + 0.3f - UpperY, 0.3f));

        // the helm is seat 0: on the stool, hands on the wheel
        seats.Insert(0, new SeatAnchor(0, CarMeshBuilder.Turned(HelmHip), 0.05f, UpperY));
    }

    /// <summary>The telegraph's handle, authored round its pivot (its node turns it about X).</summary>
    private static ArrayMesh LeverMesh()
    {
        var m = new MeshScratch();
        m.Tube(Vector3.Zero, new Vector3(0, 0.26f, 0), 0.015f, Brass, 4);
        foreach (float x in new[] { 0.075f, -0.075f })
            m.Box(new Vector3(x, 0.26f, 0), new Vector3(0.04f, 0.04f, 0.04f), Dark);
        m.Tube(new Vector3(0.1f, 0.26f, 0), new Vector3(-0.1f, 0.26f, 0), 0.012f, Brass, 4);
        return m.Build();
    }

    /// <summary>The helmsman on the stool at the wheel, author space.</summary>
    public static ArrayMesh Helmsman(HumanPalette palette, float wheelTurn)
    {
        var m = new MeshScratch();
        float floor = UpperY + 0.32f;   // feet on the stool's ring
        var seat = new DriverSeat(HelmHip, 0.05f, HelmWheel, Vector3.Forward, HelmRadius - 0.05f,
            Throttle: new Vector3(HelmHip.X - 0.12f, floor, HelmHip.Z + 0.22f),
            Brake: new Vector3(HelmHip.X + 0.12f, floor, HelmHip.Z + 0.22f),
            Rest: new Vector3(HelmHip.X + 0.12f, floor, HelmHip.Z + 0.22f));
        HumanMeshBuilder.AppendDriver(m, palette, seat, wheelTurn, 0f, 0f);
        return m.Build();
    }

    // ---- funnel, stairs, fittings ----------------------------------------------------------------

    private static void Funnel(MeshScratch m, DeckBuilder dk)
    {
        float z = Z(FunnelAt);
        float black = FunnelTop - 1.15f;
        var foot = new Vector3(0, UnderUpper, z);
        var band = new Vector3(0, black, z - 0.3f);
        var top = new Vector3(0, FunnelTop, z - 0.42f);
        m.Tube(foot, band, FunnelRadius, FunnelRadius, Buff, 12);
        m.Tube(band, top, FunnelRadius, FunnelRadius, Boot, 12);
        m.Ring(top, Vector3.Up, FunnelRadius - 0.08f, FunnelRadius + 0.04f, 0.08f, Boot, 12);
        // the steam pipe up its front, the whistle on it
        var pipe = new Vector3(0, UpperY, z + FunnelRadius + 0.2f);
        m.Tube(pipe, WhistleAt with { Y = WhistleAt.Y - 0.2f }, 0.07f, Buff.Darkened(0.1f), 6);
        m.Tube(WhistleAt with { Y = WhistleAt.Y - 0.2f }, WhistleAt + new Vector3(0, 0.35f, 0), 0.09f, Brass, 8);
        dk.Box(new Vector3(0, (UpperY + FunnelTop) * 0.5f, z - 0.2f), new Vector3(FunnelRadius * 2f, FunnelTop - UpperY, FunnelRadius * 2f + 0.4f));
        // two cowl ventilators beside it
        foreach (float side in new[] { 1f, -1f })
        {
            var v = new Vector3(side * 2.3f, UpperY, Z(FunnelAt + 1.8f));
            m.Tube(v, v with { Y = UpperY + 1.3f }, 0.16f, White, 8);
            m.Tube(v with { Y = UpperY + 1.3f }, v + new Vector3(0, 1.55f, 0.25f), 0.28f, 0.3f, White, 8);
            m.Ring(v + new Vector3(0, 1.55f, 0.27f), Vector3.Forward, 0.12f, 0.28f, 0.03f, WheelRed, 8);
            dk.Box(v with { Y = UpperY + 0.8f }, new Vector3(0.6f, 1.6f, 0.6f));
        }
    }

    /// <summary>
    /// The stairs from the aft main deck up to the upper deck, either side: treads drawn, a ramp to
    /// the walk (it has no step-up) whose top edge is the upper deck's edge, flush; handrails to hold.
    /// </summary>
    private static void Stairs(MeshScratch m, DeckBuilder dk)
    {
        const int steps = 12;
        foreach (float side in new[] { 1f, -1f })
        {
            float x = side * StairX;
            for (int k = 0; k < steps; k++)
            {
                float f = (k + 0.5f) / steps;
                float at = Mathf.Lerp(StairFoot, UpperTo, f);
                float y = Mathf.Lerp(DeckY, UpperY, (k + 1f) / steps);
                m.Box(new Vector3(x, y - 0.03f, Z(at)), new Vector3(StairWidth, 0.06f, (StairFoot - UpperTo) / steps + 0.06f), Teak);
            }
            foreach (float e in new[] { -1f, 1f })
            {
                float sx = x + e * (StairWidth * 0.5f + 0.03f);
                m.Box(new Vector3(sx, (DeckY + UpperY) * 0.5f - 0.15f, Z((StairFoot + UpperTo) * 0.5f)), new Vector3(0.05f, 0.3f, StairFoot - UpperTo + 0.2f),
                    White, new Basis(Vector3.Right, -Mathf.Atan2(UpperY - DeckY, StairFoot - UpperTo)));
                m.Tube(new Vector3(sx, DeckY + 0.95f, Z(StairFoot)), new Vector3(sx, UpperY + 0.95f, Z(UpperTo)), 0.03f, Varnish, 5);
                m.Tube(new Vector3(sx, DeckY, Z(StairFoot)), new Vector3(sx, DeckY + 0.95f, Z(StairFoot)), 0.03f, Varnish, 5);
                dk.Hold(sx, StairFoot - 1.5f);
                dk.Hold(sx, UpperTo + 1.5f);
            }
            dk.RampAlong(StairFoot, DeckY, UpperTo, UpperY, StairWidth, x);
        }
    }

    /// <summary>The windlass and bitts on the open bow, the flagstaffs, the lifebuoys on the rails.</summary>
    private static void Fittings(MeshScratch m, DeckBuilder dk)
    {
        var windlass = new Vector3(0, DeckY + 0.4f, Z(4.5f));
        m.Box(windlass, new Vector3(1.2f, 0.8f, 0.7f), Dark);
        m.Tube(windlass + new Vector3(-0.8f, 0.1f, 0), windlass + new Vector3(0.8f, 0.1f, 0), 0.22f, Brass, 8);
        dk.Box(windlass, new Vector3(1.8f, 0.8f, 0.8f));
        foreach (float at in new[] { 7.5f, 70.5f })
            foreach (float side in new[] { 1f, -1f })
            {
                var bitt = new Vector3(side * (HalfAt(at) - 0.6f), DeckY, Z(at));
                m.Tube(bitt, bitt with { Y = DeckY + 0.5f }, 0.13f, Dark, 8);
                dk.Box(bitt with { Y = DeckY + 0.25f }, new Vector3(0.3f, 0.5f, 0.3f));
            }
        // the jack staff at the bow, the ensign staff at the stern with a Swiss flag (no logos)
        var jack = new Vector3(0, DeckY, Z(0.9f));
        m.Tube(jack, jack with { Y = DeckY + 4.5f }, 0.05f, Varnish, 5);
        var staff = new Vector3(0, DeckY, Z(75.2f));
        m.Tube(staff, staff + new Vector3(0, 3.2f, -0.6f), 0.05f, Varnish, 5);
        var red = new Color(0.85f, 0.1f, 0.1f);
        var at2 = staff + new Vector3(0, 2.6f, -1.1f);
        m.Box(at2, new Vector3(0.03f, 0.9f, 0.9f), red);
        m.Box(at2, new Vector3(0.04f, 0.56f, 0.16f), Colors.White);
        m.Box(at2, new Vector3(0.04f, 0.16f, 0.56f), Colors.White);
        // lifebuoys hung on the upper deck's rail
        foreach (float at in new[] { 20f, 52f })
            foreach (float side in new[] { 1f, -1f })
                m.Ring(new Vector3(side * (UpperHalf + 0.02f), UpperY + 0.55f, Z(at)), Vector3.Right, 0.18f, 0.32f, 0.1f, new Color(0.9f, 0.3f, 0.15f), 10);
    }
}

/// <summary>
/// The steamer as drawn (#303): the hull, the turning wheels, the helmsman (hidden while nobody holds
/// the wheel), the telegraph's handle, the gangway gates and planks, the wake and the paddles' foam,
/// the funnel's smoke, and its sounds (the paddles' churn, the whistle). Built for the driver's own
/// peer, remote copies and parked steamers alike; particles and sounds only where something renders.
/// </summary>
public partial class SteamerRig : Node3D
{
    private MeshInstance3D? _helmsman;
    private readonly MeshInstance3D[] _wheels = new MeshInstance3D[2];
    private readonly MeshInstance3D[] _gates = new MeshInstance3D[2], _planks = new MeshInstance3D[2];
    private MeshInstance3D _lever = null!;
    private GpuParticles3D? _wake, _bowWave, _smoke;
    private readonly GpuParticles3D?[] _churn = new GpuParticles3D?[2];
    private AudioStreamPlayer3D? _paddles, _whistle;
    private float _wheelAngle, _shownLever = float.NaN;
    private byte _shownGates = 255;

    /// <summary>Someone is at the wheel.</summary>
    public bool DriverShown
    {
        get => _helmsman?.Visible ?? false;
        set { if (_helmsman != null && _helmsman.Visible != value) _helmsman.Visible = value; }
    }

    public static SteamerRig Create(HumanPalette? helmsman)
    {
        var parts = SteamerMeshBuilder.Parts();
        var rig = new SteamerRig { Name = "Steamer" };
        var body = HumanMeshBuilder.FigureMaterial();
        var hull = new MeshInstance3D { Name = "Hull", Mesh = parts.Hull };
        rig.AddChild(hull);
        MeshScratch.Paint(hull, body, CarRig.GlassMaterial());
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? 1f : -1f;
            rig._wheels[i] = new MeshInstance3D
            {
                Name = i == 0 ? "WheelPort" : "WheelStarboard",
                Mesh = parts.Wheel,
                MaterialOverride = body,
                Position = BoatMeshBuilder.Flip(new Vector3(side * SteamerLines.WheelX, SteamerLines.WheelY, SteamerLines.WheelZ)),
            };
            rig.AddChild(rig._wheels[i]);
            rig._gates[i] = new MeshInstance3D { Name = $"Gate{i}", Mesh = parts.Gates[i], MaterialOverride = body };
            rig._planks[i] = new MeshInstance3D { Name = $"Plank{i}", Mesh = parts.Planks[i], MaterialOverride = body, Visible = false };
            rig.AddChild(rig._gates[i]);
            rig.AddChild(rig._planks[i]);
        }
        rig._lever = new MeshInstance3D { Name = "Telegraph", Mesh = parts.Lever, MaterialOverride = body, Position = BoatMeshBuilder.Flip(SteamerMeshBuilder.TelegraphAt + new Vector3(0, 0.12f, 0)) };
        rig.AddChild(rig._lever);
        if (helmsman != null)
        {
            rig._helmsman = new MeshInstance3D { Name = "Driver", Mesh = SteamerMeshBuilder.Helmsman(helmsman, 0f), MaterialOverride = body };
            rig.AddChild(rig._helmsman);
        }
        if (DisplayServer.GetName() != "headless")
        {
            rig.AddWater();
            rig.AddSound();
        }
        return rig;
    }

    // ---- water and smoke ---------------------------------------------------------------------

    private static readonly Color Foam = new(0.93f, 0.96f, 1f);
    private Vector3 _wakeAnchor, _bowAnchor;
    private readonly Vector3[] _churnAnchor = new Vector3[2];

    private void AddWater()
    {
        // node space: +Z aft, +X starboard
        _wake = Emitter("Wake", 500, 7f, 2.2f, flat: true, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(2.2f, 0.02f, 1f),
            Direction = new Vector3(0, 0, 1), Spread = 18f,
            InitialVelocityMin = 0.4f, InitialVelocityMax = 1.4f,
            Gravity = Vector3.Zero, DampingMin = 0.2f, DampingMax = 0.5f,
            ScaleMin = 0.8f, ScaleMax = 1.6f, ScaleCurve = Grow(),
            Color = Foam, ColorRamp = Fade(0.7f),
        });
        _wakeAnchor = new Vector3(0, 0, SteamerLines.WaterlineHalf - 1f);
        _bowWave = Emitter("BowWave", 200, 2.5f, 1.2f, flat: true, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.6f, 0.02f, 0.6f),
            Direction = new Vector3(0, 0, 1), Spread = 50f,
            InitialVelocityMin = 1.5f, InitialVelocityMax = 3f,
            Gravity = Vector3.Zero, DampingMin = 0.5f, DampingMax = 1f,
            ScaleMin = 0.6f, ScaleMax = 1.3f, ScaleCurve = Grow(),
            Color = Foam, ColorRamp = Fade(0.8f),
        });
        _bowAnchor = new Vector3(0, 0, -SteamerLines.WaterlineHalf + 0.5f);
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            _churn[i] = Emitter(i == 0 ? "ChurnPort" : "ChurnStarboard", 260, 3.5f, 1.4f, flat: true, new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(0.9f, 0.02f, 1.2f),
                Direction = new Vector3(side * 0.3f, 0, 1), Spread = 25f,
                InitialVelocityMin = 1.5f, InitialVelocityMax = 4f,
                Gravity = Vector3.Zero, DampingMin = 0.6f, DampingMax = 1.2f,
                ScaleMin = 0.8f, ScaleMax = 1.5f, ScaleCurve = Grow(),
                Color = Foam, ColorRamp = Fade(0.85f),
            });
            _churnAnchor[i] = new Vector3(side * SteamerLines.WheelX, 0, -SteamerLines.WheelZ + SteamerLines.WheelRadius + 0.5f);
        }
        _smoke = Emitter("Smoke", 90, 6f, 1.8f, flat: false, new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 1, 0.3f), Spread = 12f,
            InitialVelocityMin = 1.5f, InitialVelocityMax = 2.5f,
            Gravity = new Vector3(0, 0.25f, 0), DampingMin = 0.3f, DampingMax = 0.6f,
            ScaleMin = 0.8f, ScaleMax = 1.4f, ScaleCurve = Grow(),
            Color = new Color(0.55f, 0.55f, 0.57f), ColorRamp = Fade(0.45f),
        });
        _smoke.Position = BoatMeshBuilder.Flip(new Vector3(0, SteamerMeshBuilder.FunnelTop + 0.2f, SteamerMeshBuilder.Z(SteamerMeshBuilder.FunnelAt) - 0.42f));
    }

    private GpuParticles3D Emitter(string name, int amount, float life, float size, bool flat, ParticleProcessMaterial mat)
    {
        var p = new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = life,
            Emitting = false,
            ProcessMaterial = mat,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(size, size),
                Orientation = flat ? PlaneMesh.OrientationEnum.Y : PlaneMesh.OrientationEnum.Z,
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BillboardMode = flat ? BaseMaterial3D.BillboardModeEnum.Disabled : BaseMaterial3D.BillboardModeEnum.Particles,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
            },
            LocalCoords = false,
            VisibilityAabb = new Aabb(new Vector3(-60, -10, -60), new Vector3(120, 30, 120)),
        };
        AddChild(p);
        return p;
    }

    private static CurveTexture Grow()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.5f));
        c.AddPoint(new Vector2(1, 2f));
        return new CurveTexture { Curve = c };
    }

    private static GradientTexture1D Fade(float alpha)
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, alpha));
        g.SetColor(1, new Color(1, 1, 1, 0));
        return new GradientTexture1D { Gradient = g };
    }

    private void AddSound()
    {
        var mid = BoatMeshBuilder.Flip(new Vector3(0, SteamerLines.WheelY, SteamerLines.WheelZ));
        _paddles = new AudioStreamPlayer3D
        {
            Name = "Paddles", Stream = Audio.SfxSynth.Paddles, Position = mid, UnitSize = 18f, MaxDistance = 600f,
            VolumeDb = -80f, Bus = Audio.SfxBus.Name,
        };
        AddChild(_paddles);
        _whistle = new AudioStreamPlayer3D
        {
            Name = "Whistle", Stream = Audio.SfxSynth.Whistle, Position = BoatMeshBuilder.Flip(SteamerMeshBuilder.WhistleAt),
            UnitSize = 60f, MaxDistance = 4000f, VolumeDb = 2f, Bus = Audio.SfxBus.Name,
        };
        AddChild(_whistle);
    }

    private float _shownChurn = -1f, _shownChurn2 = -1f, _shownWake = -1f, _shownBow = -1f, _shownSmoke = -1f, _soundLevel = -1f, _soundPitch = -1f;

    /// <summary>
    /// Per frame: the wheels turn with the shaft (−1 full astern .. 1 full ahead, 46 rpm at full),
    /// foam where they bite and astern by the way on, a bow wave, smoke by the engine's work, the
    /// telegraph's handle at <paramref name="order"/> (−4..4), the gangways, the sounds. Only
    /// changes are written.
    /// </summary>
    public void Animate(float shaft, float speed, bool afloat, bool whistle, float order, byte gates, float dt)
    {
        _wheelAngle = Mathf.Wrap(_wheelAngle - shaft * (BoatCatalog.Steamer.MaxRpm / 60f) * Mathf.Tau * dt, -Mathf.Pi, Mathf.Pi);
        var turn = new Basis(Vector3.Right, _wheelAngle);
        foreach (var w in _wheels) w.Basis = turn;
        float lever = Mathf.Round(order * 4f) / 4f;
        if (lever != _shownLever)
        {
            _shownLever = lever;
            _lever.Basis = new Basis(Vector3.Right, -lever / Telegraph.Max * 1.1f);
        }
        if (gates != _shownGates)
        {
            _shownGates = gates;
            for (int i = 0; i < 2; i++)
            {
                bool open = (gates >> i & 1) != 0;
                _gates[i].Visible = !open;
                _planks[i].Visible = open;
            }
        }
        if (_wake == null) return;
        float work = Mathf.Abs(shaft);
        Set(_churn[0]!, afloat ? work : 0f, ref _shownChurn);
        Set(_churn[1]!, afloat ? work : 0f, ref _shownChurn2);
        Set(_wake, afloat ? Mathf.Clamp((speed - 0.5f) / 6f, 0f, 1f) : 0f, ref _shownWake);
        Set(_bowWave!, afloat ? Mathf.Clamp((speed - 2f) / 6f, 0f, 1f) : 0f, ref _shownBow);
        Set(_smoke!, 0.15f + 0.85f * work, ref _shownSmoke);
        if (_shownWake > 0f) OnSurface(_wake, _wakeAnchor);
        if (_shownBow > 0f) OnSurface(_bowWave!, _bowAnchor);
        if (_shownChurn > 0f) for (int i = 0; i < 2; i++) OnSurface(_churn[i]!, _churnAnchor[i]);

        if (_paddles != null)
        {
            float level = Mathf.Round(work * 20f) / 20f, pitch = Mathf.Round((0.35f + 0.85f * work) * 20f) / 20f;
            if (level != _soundLevel)
            {
                _soundLevel = level;
                _paddles.VolumeDb = level > 0.01f ? Mathf.LinearToDb(0.25f + 0.75f * level) - 4f : -80f;
                if (level > 0.01f && !_paddles.Playing) _paddles.Play();
                else if (level <= 0.01f && _paddles.Playing) _paddles.Stop();
            }
            if (pitch != _soundPitch) { _soundPitch = pitch; _paddles.PitchScale = pitch; }
        }
        if (_whistle != null && whistle != _whistle.Playing)
        {
            if (whistle) _whistle.Play(); else _whistle.Stop();
        }
    }

    /// <summary>Puts an emitter at its anchor on the hull (rig space) on the water's surface there.</summary>
    private void OnSurface(GpuParticles3D p, Vector3 anchor)
    {
        var at = GlobalTransform * anchor;
        if (World.WaterField.TryLevelAt(at, out float level)) p.GlobalPosition = at with { Y = level + 0.04f };
    }

    private static void Set(GpuParticles3D p, float amount, ref float shown)
    {
        float q = Mathf.Round(amount * 10f) / 10f;
        if (q == shown) return;
        shown = q;
        p.Emitting = q > 0f;
        if (q > 0f) p.AmountRatio = q;
    }
}
