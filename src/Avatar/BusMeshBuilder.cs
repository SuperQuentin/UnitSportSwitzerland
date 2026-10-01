using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// Low-poly buses (#70): a city bus, the two halves of an articulated one with the bellows between
/// them, a high-deck coach. The walls are built as walls, so the door leaves (on the right, Swiss
/// side) swing out of real holes, and the windows are glass panes between pillars you see through
/// (#157): into a saloon with its floor, wheel-arch podiums, passenger seats in the real layout,
/// grab poles or luggage racks and ceiling lights, and the driver's place up front with its
/// cockpit. The destination display is a Label3D the rig sets. Authored facing +Z, origin on the
/// ground under the section's centre of mass; operator colours from <see cref="HeavyLook"/>.
/// </summary>
public static class BusMeshBuilder
{
    private static readonly Color Underfloor = new(0.1f, 0.1f, 0.11f);
    private static readonly Color Bellows = new(0.13f, 0.13f, 0.14f);
    private static readonly Color Pole = new(0.95f, 0.78f, 0.15f);
    private static readonly Color CoachSeat = new(0.2f, 0.22f, 0.3f);
    private static readonly Color LightStrip = new(0.95f, 0.95f, 0.88f);
    private static readonly Color DoorButton = new(0.95f, 0.78f, 0.15f);
    private static readonly Color DoorButtonLamp = new(0.2f, 0.75f, 0.3f);
    private const float Wall = 0.08f;
    /// <summary>Window pillars: their width, and the glass between them a bus is built with.</summary>
    private const float Pillar = 0.1f, WindowLength = 1.35f;

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
        var glow = new MeshScratch();
        float front = cg, rear = cg - s.Length;
        float inner = hw - Wall;

        // heights: floor (at the doors), the saloon's (a coach's high deck), top of the skirt, the
        // window band, the roof
        float floor = coach ? 0.38f : 0.3f;
        float deck = coach ? 1.25f : floor;
        float belt = coach ? 1.5f : 0.95f;
        float winLow = coach ? 1.6f : 1.05f;
        float winHigh = coach ? 3.3f : 2.6f;
        float roof = s.Height - (coach ? 0.1f : 0.17f);

        // the articulated rear half starts with its bellows: rings of folds you walk through (#162)
        float from = !first && s.Pivot == Coupling.BusJoint ? 0.9f : 0f;
        if (from > 0f)
            for (int i = 0; i < 6; i++)
            {
                float w = s.Width - (i % 2 == 0 ? 0.08f : 0.2f);
                float a0 = i * 0.15f, a1 = a0 + 0.12f;
                foreach (int side in new[] { 1, -1 })
                    Along(m, cg, a0, a1, floor + 0.05f, roof - 0.05f, 0.1f, Bellows, side * (w * 0.5f - 0.05f));
                Along(m, cg, a0, a1, roof - 0.15f, roof - 0.05f, w, Bellows);
                Along(m, cg, a0, a1, floor, floor + 0.05f, w, Bellows);
            }

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

        // ---- the shell ----
        Along(m, cg, from, s.Length, roof, s.Height, s.Width - 0.04f, look.Paint);
        Along(m, cg, from + Wall, s.Length - Wall, roof - 0.02f, roof, inner * 2f, HeavyCabin.Lining);
        // roof pods: air conditioning (and the engine's cooling on the rear half)
        if (!coach) Along(m, cg, Mathf.Max(from, s.Length * 0.3f), Mathf.Max(from, s.Length * 0.3f) + 2.6f, s.Height - 0.02f, s.Height + 0.14f, 1.9f, look.Paint);

        // the walls: the left whole but for the arches, the right cut at every door too; the
        // window band is pillars with glass between them, none in a city bus's last metre (the engine)
        float glassTo = last && !coach ? s.Length - 1.2f : s.Length - 0.3f;
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * (hw - Wall * 0.5f);
            var lowCuts = side < 0 ? archCuts.Concat(doorCuts).ToList() : archCuts;
            var highCuts = side < 0 ? doorCuts : new List<(float, float)>();
            Cut(m, cg, from, s.Length, floor, archTop, x, lowCuts, look.Lower);
            Cut(m, cg, from, s.Length, archTop, belt, x, highCuts, coach ? look.Paint : look.Lower);
            Cut(m, cg, from, s.Length, belt, winLow, x, highCuts, look.Paint);
            Cut(m, cg, from, s.Length, winHigh, roof, x, highCuts, look.Accent);
            // the lining inside, below the windows
            Cut(m, cg, from + Wall, s.Length - Wall, deck, winLow, side * (inner - 0.005f), highCuts, HeavyCabin.Lining, 0.01f);
            Windows(m, cg, from, glassTo, winLow, winHigh, x, side * hw, highCuts, look.Paint);
            if (glassTo < s.Length) Cut(m, cg, glassTo, s.Length, winLow, winHigh, x, highCuts, look.Paint);
            if (coach)
                // the luggage bays' doors: a dark seam top and bottom
                Cut(m, cg, from + 2.5f, s.Length - 3f, belt - 0.06f, belt, side * (hw + 0.005f), archCuts, Trim, 0.02f);
        }

        // ---- the deck you walk on (#162): the walls with their door holes, floor and roof ----
        var dk = new DeckBuilder(cg);
        dk.Along(0f, s.Length, floor - 0.12f, floor, s.Width);
        dk.Along(0f, s.Length, roof, roof + 0.12f, s.Width);
        foreach (int side in new[] { 1, -1 })
            DeckWall(dk, from, s.Length, floor - 0.12f, roof, side * (hw - Wall * 0.5f), side < 0 ? doorCuts : new List<(float, float)>());
        if (from > 0f)
            // along the bellows: their folds, inside the wall line
            foreach (int side in new[] { 1, -1 })
                dk.Along(0f, from, floor, roof, 0.12f, side * (hw - 0.1f));
        foreach (var d in doors)
        {
            // a button by each door, inside and out, anyone presses to open or shut it: just ahead
            // of the opening, at hand height
            float buttonAt = d.At - d.Width * 0.5f - 0.14f;
            foreach (bool outside in new[] { false, true })
            {
                float x = outside ? -(hw + 0.012f) : -(inner + 0.012f);
                var normal = new Vector3(outside ? -1f : 1f, 0, 0);
                var at = new Vector3(x, (coach && !outside ? deck : floor) + 0.95f, cg - buttonAt);
                m.Box(at, new Vector3(0.024f, 0.12f, 0.08f), DoorButton);
                m.Box(at + normal * 0.012f + new Vector3(0, 0.02f, 0), new Vector3(0.004f, 0.04f, 0.04f), DoorButtonLamp);
                dk.Button(d.Index, at, normal);
            }
            // a shut door is solid; an open one has a step down to the road outside it (the walk has no
            // step-up), meeting the floor flush at its edge: a lip of even a centimetre stops the walk on it
            dk.Along(d.At - d.Width * 0.5f, d.At + d.Width * 0.5f, floor, roof, Wall, -(hw - Wall * 0.5f), DeckPart.DoorShut, d.Index);
            dk.RampAcross(d.At, -(hw + 0.8f), -0.05f, -hw, floor, d.Width - 0.06f, DeckPart.DoorStep, d.Index);
        }
        bool jointAhead = !first && s.Pivot == Coupling.BusJoint, jointBehind = !last && s.Hitch == Coupling.BusJoint;
        if (first) EndWall(dk, 0f, 0.08f, floor, roof, s.Width, inner, open: false);
        else if (jointAhead) EndWall(dk, from, 0.06f, floor, roof, s.Width, inner, open: true);
        EndWall(dk, s.Length - 0.08f, 0.08f, floor, roof, s.Width, inner, open: jointBehind);

        // ---- the saloon: floor, podiums over the wheels, a coach's high deck and its stairwells ----
        float saloonFrom = from + Wall, saloonTo = s.Length - Wall;
        Along(m, cg, saloonFrom, saloonTo, floor, floor + 0.02f, inner * 2f, HeavyCabin.FloorColour);
        foreach (var (a0, a1) in archCuts)
            foreach (int side in new[] { 1, -1 })
            {
                Along(m, cg, a0, a1, floor, archTop, 0.95f, HeavyCabin.Dash, side * (inner - 0.475f));
                dk.Along(a0, a1, floor, archTop, 0.95f, side * (inner - 0.475f));
            }
        // a city bus's last section: the back raised over the engine, a ramp up the aisle to it
        float podiumFrom = last && !coach ? s.Length - 2.6f : float.MaxValue;
        if (podiumFrom < s.Length)
        {
            Along(m, cg, podiumFrom, saloonTo, floor, floor + 0.3f, inner * 2f, HeavyCabin.Dash);
            dk.Along(podiumFrom, saloonTo, floor, floor + 0.3f, inner * 2f);
            dk.RampAlong(podiumFrom - 0.7f, floor, podiumFrom, floor + 0.3f, 1.1f);
        }
        float deckFrom = first ? 2.2f : saloonFrom;
        if (coach)
        {
            // the deck over the luggage bays, cut where each door's stairwell comes up on the right
            Along(m, cg, deckFrom, saloonTo, floor, deck, inner, Underfloor, inner * 0.5f);
            dk.Along(deckFrom, saloonTo, floor, deck, inner, inner * 0.5f);
            float at = deckFrom;
            foreach (var d in doors.Where(d => d.At > deckFrom).OrderBy(d => d.At))
            {
                Along(m, cg, at, d.At - d.Width * 0.5f - 0.1f, floor, deck, inner, Underfloor, -inner * 0.5f);
                dk.Along(at, d.At - d.Width * 0.5f - 0.1f, floor, deck, inner, -inner * 0.5f);
                // the stairwell, a slope to the walk: from the door up to the deck by the aisle
                dk.RampAcross(d.At, -(inner - 0.02f), floor, -0.02f, deck, d.Width + 0.2f);
                // three steps down toward the door
                for (int k = 0; k < 3; k++)
                    Along(m, cg, d.At - d.Width * 0.5f - 0.1f, d.At + d.Width * 0.5f + 0.1f, floor, deck - (k + 1) * (deck - floor) / 4f,
                        0.3f, HeavyCabin.Dash, -0.15f - k * 0.3f);
                at = d.At + d.Width * 0.5f + 0.1f;
            }
            Along(m, cg, at, saloonTo, floor, deck, inner, Underfloor, -inner * 0.5f);
            dk.Along(at, saloonTo, floor, deck, inner, -inner * 0.5f);
            if (first)
            {
                // the steps up from the front door, beside the driver
                for (int k = 0; k < 3; k++)
                    Along(m, cg, deckFrom - (k + 1) * 0.25f, deckFrom, floor, deck - (k + 1) * (deck - floor) / 4f, inner, HeavyCabin.Dash, -inner * 0.5f);
                dk.RampAlong(deckFrom - 1f, floor, deckFrom, deck, inner, -inner * 0.5f);
            }
        }

        // ---- passenger seats in rows, 2 + 2 across the aisle ----
        var seats = new List<SeatAnchor>();
        HeavyCockpit? cockpit = null;
        float seatsFrom = first ? deckFrom + (coach ? 0.2f : -0.2f) : saloonFrom + 0.3f;
        if (first)
        {
            cockpit = DriverPlace(m, dk, spec, s, cg, hw, inner, floor, roof, coach);
            seats.Add(new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, cockpit.Frame.Floor));
            seatsFrom = Mathf.Max(seatsFrom, cg - cockpit.Seat.Hip.Z + 0.6f);
        }
        float pitch = coach ? 0.86f : 0.78f;
        var seatColour = coach ? CoachSeat : look.Accent.Darkened(0.3f);
        // a city bus keeps a pram and wheelchair space opposite its second door
        float pram = !coach && look.Doors.Length > 1 ? look.Doors[1].At - offset : float.NaN;
        float aisle = inner - 0.24f - 0.45f - 0.22f;
        int row = 0;
        for (float at = seatsFrom; at + 0.45f < saloonTo; at += pitch, row++)
        {
            foreach (int side in new[] { 1, -1 })
            {
                if (side < 0 && doors.Any(d => Mathf.Abs(at - d.At) < d.Width * 0.5f + 0.45f)) continue;
                if (side > 0 && Mathf.Abs(at - pram) < 1.1f) continue;
                // the floor under it: the deck, a podium over a wheel, the raised back
                float under = coach ? deck : floor;
                if (!coach && archCuts.Any(c => at > c.Item1 - 0.25f && at < c.Item2 + 0.1f)) under = archTop;
                if (at > podiumFrom - 0.2f) under = Mathf.Max(under, floor + 0.3f);
                foreach (float col in new[] { 0.24f, 0.69f })
                {
                    var hip = new Vector3(side * (inner - col), under + 0.5f, cg - at);
                    float recline = HeavyCabin.PassengerSeat(m, hip, under, seatColour, coach);
                    seats.Add(new SeatAnchor(section, CarMeshBuilder.Turned(hip), recline, under));
                    // the seat as a block from its foot to the top of its back
                    float top = hip.Y + (coach ? 0.85f : 0.6f);
                    dk.Box(new Vector3(hip.X, (under + top) * 0.5f, hip.Z + 0.05f), new Vector3(0.44f, top - under, 0.62f));
                }
                if (!coach && row % 2 == 0)
                {
                    // a pole at the aisle end of every other row
                    m.Tube(new Vector3(side * aisle, under, cg - at + 0.32f), new Vector3(side * aisle, roof - 0.02f, cg - at + 0.32f), 0.018f, Pole, 5);
                    dk.Box(new Vector3(side * aisle, (under + roof) * 0.5f, cg - at + 0.32f), new Vector3(0.05f, roof - under, 0.05f));
                    dk.Hold(side * aisle, at - 0.32f);
                }
            }
        }
        if (!coach)
        {
            // rails under the ceiling along the aisle, and a pole each side of every door
            foreach (int side in new[] { 1, -1 })
            {
                m.Tube(new Vector3(side * aisle, roof - 0.28f, cg - seatsFrom), new Vector3(side * aisle, roof - 0.28f, cg - saloonTo + 0.3f), 0.016f, Pole, 5);
            }
            foreach (var d in doors)
                foreach (float e in new[] { -1f, 1f })
                {
                    float z = cg - d.At - e * (d.Width * 0.5f + 0.06f);
                    m.Tube(new Vector3(-(inner - 0.12f), floor, z), new Vector3(-(inner - 0.12f), roof - 0.02f, z), 0.018f, Pole, 5);
                    dk.Box(new Vector3(-(inner - 0.12f), (floor + roof) * 0.5f, z), new Vector3(0.05f, roof - floor, 0.05f));
                    dk.Hold(-(inner - 0.12f), cg - z);
                }
        }
        else
            // luggage racks over the windows
            foreach (int side in new[] { 1, -1 })
                Along(m, cg, deckFrom, saloonTo - 0.3f, winHigh - 0.06f, winHigh + 0.1f, 0.36f, HeavyCabin.Dash, side * (inner - 0.18f));
        // light strips along the ceiling, lit with the headlights
        foreach (int side in new[] { 1, -1 })
            Along(glow, cg, saloonFrom + 0.3f, saloonTo - 0.3f, roof - 0.035f, roof - 0.02f, 0.1f, LightStrip, side * (inner - 0.5f));

        if (first)
        {
            // the face: wall up to the windscreen and above it, pillars at its sides, the display above
            float wsLow = coach ? 0.95f : 0.85f;
            float wsHigh = s.Height - 0.42f;
            Along(m, cg, 0f, 0.06f, floor, wsLow, s.Width - 0.04f, look.Paint);
            Along(m, cg, 0f, 0.06f, wsHigh, s.Height, s.Width - 0.04f, look.Paint);
            foreach (float sx in new[] { -1f, 1f })
                Along(m, cg, 0f, 0.06f, wsLow, wsHigh, 0.12f, look.Paint, sx * (hw - 0.08f));
            FrontPane(m, front - 0.03f, hw - 0.14f, wsLow, wsHigh);
            float dispY = wsHigh + 0.16f;
            m.Box(new Vector3(0, dispY, front + 0.02f), new Vector3(s.Width - 0.5f, 0.24f, 0.04f), Trim);
            m.Box(new Vector3(0, (floor + wsLow) * 0.5f, front + 0.02f), new Vector3(s.Width - 0.1f, wsLow - floor, 0.04f), look.Lower);
            foreach (float sx in new[] { -1f, 1f })
            {
                Lamp(head, sx * (hw - 0.32f), floor + 0.35f, front + 0.05f, 0.4f, 0.16f, 0.04f, HeadLamp);
                Lamp(m, sx * (hw - 0.07f), floor + 0.35f, front + 0.05f, 0.1f, 0.14f, 0.04f, Amber);
            }
        }
        else EndWall(m, cg, from, from + 0.06f, floor, s.Height, s.Width - 0.04f, look.Paint, inner);

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
        else EndWall(m, cg, s.Length - 0.06f, s.Length, floor, s.Height, s.Width - 0.04f, look.Paint, inner);

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
                // the frame round the glass, and the glass a pane you see through
                float y0 = floor + 0.35f, y1 = doorTop - 0.12f, half = leafW * 0.5f - 0.08f;
                float cz = cg - centreAt;
                leaf.Box(new Vector3(lx, (floor + y0) * 0.5f, cz), new Vector3(0.05f, y0 - floor, leafW - 0.03f), look.Paint);
                leaf.Box(new Vector3(lx, (y1 + doorTop) * 0.5f, cz), new Vector3(0.05f, doorTop - y1, leafW - 0.03f), look.Paint);
                foreach (float e in new[] { -1f, 1f })
                    leaf.Box(new Vector3(lx, (y0 + y1) * 0.5f, cz + e * (half + 0.035f)), new Vector3(0.05f, y1 - y0, 0.07f), look.Paint);
                SidePane(leaf, lx, cz - half, cz + half, y0, y1);
                var pivot = new Vector3(lx, 0f, cg - hingeAt);
                leaves.Add(new HeavyDoorLeaf(index, leaf.Build(pivot), new Vector3(-pivot.X, 0f, -pivot.Z), i == 0 ? 1.35f : -1.35f));
            }
        }

        var parts = new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), leaves.ToArray())
        {
            Cockpit = cockpit,
            Seats = seats.ToArray(),
            Glow = glow.Build(),
            // aboard: anywhere inside the walls, floor to roof, from end to end of the section
            Deck = dk.Build(section, new Aabb(new Vector3(-inner, floor - 0.3f, cg - s.Length + 0.06f),
                new Vector3(inner * 2f, roof - floor + 0.3f, s.Length - 0.12f))),
        };
        if (first && look.Destinations.Length > 0)
        {
            float dispY = s.Height - 0.42f + 0.16f;
            parts = parts with { Display = (new Vector3(0f, dispY, -front - 0.05f), s.Width - 0.6f) };
        }
        return parts;
    }

    /// <summary>
    /// The driver's place at the front left: a platform raised over the saloon floor, the cockpit
    /// on it (<see cref="HeavyCabin"/>), a city bus's partition behind the seat, the mirrors out on
    /// their long stalks.
    /// </summary>
    private static HeavyCockpit DriverPlace(MeshScratch m, DeckBuilder dk, HeavySpec spec, SectionSpec s, float cg, float hw, float inner,
        float floor, float roof, bool coach)
    {
        float front = cg;
        float platform = floor + (coach ? 0.6f : 0.4f);
        float driverX = hw - 0.62f;
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (float sx in new[] { -1f, 1f })
        {
            var root = new Vector3(sx * (hw - 0.05f), s.Height - 0.35f, front - 0.02f);
            var elbow = new Vector3(sx * (hw + 0.2f), s.Height - 0.5f, front + 0.32f);
            m.Tube(root, elbow, 0.025f, Trim, 4);
            m.Tube(elbow, elbow - new Vector3(0, 0.15f, 0), 0.02f, Trim, 4);
            mirrors.Add((sx > 0 ? "MirrorLeft" : "MirrorRight", new Vector3(sx * (hw + 0.2f), s.Height - 0.88f, front + 0.28f), new Vector2(0.2f, 0.42f)));
        }
        var cab = new CabFrame
        {
            Front = front, Floor = platform, Ceiling = roof - 0.02f,
            WsBase = coach ? 0.95f : 0.85f, WsTop = s.Height - 0.42f, DashTop = platform + 0.72f, InnerHalf = inner,
            Nose = coach ? 0.4f : 0.35f, DriverX = driverX, HipRise = 0.48f, Recline = 0.2f, ColumnTilt = 1.05f,
            WheelRadius = 0.24f, DashToX = driverX - 0.6f, Clutch = spec.Box == Transmission.Amt,
        };
        var cockpit = HeavyCabin.Build(m, cab, HeavyCabin.GaugesFor(spec.LimiterKmh, spec.Redline),
            HeavyDriveline.AirLow, HeavyDriveline.AirMax, mirrors);

        // the platform under the seat and pedals, from the face to behind the seat
        float behind = cg - cockpit.Seat.Hip.Z + 0.45f;
        float x1 = driverX - 0.55f;
        m.Box(new Vector3((inner + x1) * 0.5f, (floor + platform) * 0.5f, cg - behind * 0.5f), new Vector3(inner - x1, platform - floor, behind - 0.06f), HeavyCabin.Dash);
        if (!coach)
            // the cab's partition behind the driver: solid to the shoulder, nothing above
            m.Box(new Vector3((inner + x1) * 0.5f, platform + 0.55f, cg - behind), new Vector3(inner - x1, 1.1f, 0.04f), HeavyCabin.Dash);
        // the driver's corner, to the walk: one block from the face to behind the seat, beside the door
        float top = platform + 1.25f;
        dk.Box(new Vector3((inner + x1) * 0.5f, (floor + top) * 0.5f, cg - behind * 0.5f), new Vector3(inner - x1, top - floor, behind));
        return cockpit;
    }

    /// <summary>
    /// The window band of a wall at <paramref name="x"/>: a pillar at each end of every run between
    /// <paramref name="cuts"/> and evenly between, glass panes (in the wall's outer plane
    /// <paramref name="paneX"/>) in the gaps.
    /// </summary>
    private static void Windows(MeshScratch m, float cg, float fromAt, float toAt, float y0, float y1, float x, float paneX,
        List<(float, float)> cuts, Color colour)
    {
        var runs = new List<(float, float)>();
        float at = fromAt;
        foreach (var (c0, c1) in cuts.OrderBy(c => c.Item1))
        {
            if (c1 <= at) continue;
            if (c0 > at) runs.Add((at, Mathf.Min(c0, toAt)));
            at = Mathf.Max(at, c1);
            if (at >= toAt) break;
        }
        if (at < toAt) runs.Add((at, toAt));
        foreach (var (r0, r1) in runs)
        {
            float span = r1 - r0;
            if (span < Pillar * 2f + 0.2f) { Along(m, cg, r0, r1, y0, y1, Wall, colour, x); continue; }
            int panes = Mathf.Max(1, Mathf.RoundToInt((span - Pillar) / (WindowLength + Pillar)));
            float glass = (span - Pillar * (panes + 1)) / panes;
            for (int i = 0; i <= panes; i++)
            {
                float p0 = r0 + i * (glass + Pillar);
                Along(m, cg, p0, p0 + Pillar, y0, y1, Wall, colour, x);
                if (i < panes) SidePane(m, paneX, cg - (p0 + Pillar), cg - (p0 + Pillar + glass), y0 + 0.03f, y1 - 0.03f);
            }
        }
    }

    /// <summary>A side wall of the deck between two stations, with holes where the doors are (<see cref="Cut"/>, for walking).</summary>
    private static void DeckWall(DeckBuilder dk, float fromAt, float toAt, float y0, float y1, float x, List<(float, float)> cuts)
    {
        float at = fromAt;
        foreach (var (c0, c1) in cuts.OrderBy(c => c.Item1))
        {
            if (c1 <= at) continue;
            if (c0 > at) dk.Along(at, Mathf.Min(c0, toAt), y0, y1, Wall, x);
            at = Mathf.Max(at, c1);
            if (at >= toAt) return;
        }
        if (at < toAt) dk.Along(at, toAt, y0, y1, Wall, x);
    }

    /// <summary>The width of the way through an articulated bus's joint, and how high it is.</summary>
    private const float PassageWidth = 1.3f, PassageHeight = 2.1f;

    /// <summary>An end of a section: shut, or at an articulated bus's joint, open for walking through.</summary>
    private static void EndWall(MeshScratch m, float cg, float fromAt, float toAt, float floor, float height, float width, Color colour, float inner)
    {
        float side = (width - PassageWidth) * 0.5f;
        foreach (int sx in new[] { 1, -1 })
            Along(m, cg, fromAt, toAt, floor, height, side, colour, sx * (PassageWidth * 0.5f + side * 0.5f));
        Along(m, cg, fromAt, toAt, floor + PassageHeight, height, PassageWidth, colour);
    }

    /// <summary>The deck's end wall at a station, <paramref name="depth"/> deep: shut, or with the passage of a joint.</summary>
    private static void EndWall(DeckBuilder dk, float at, float depth, float floor, float roof, float width, float inner, bool open)
    {
        if (!open) { dk.Along(at, at + depth, floor, roof, width); return; }
        float side = (width - PassageWidth) * 0.5f;
        foreach (int sx in new[] { 1, -1 })
            dk.Along(at, at + depth, floor, roof, side, sx * (PassageWidth * 0.5f + side * 0.5f));
        dk.Along(at, at + depth, floor + PassageHeight, roof, PassageWidth);
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
