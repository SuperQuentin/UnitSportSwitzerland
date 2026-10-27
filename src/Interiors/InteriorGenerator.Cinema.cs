using System.Runtime.CompilerServices;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Cinemas (#1238, part 3 of #1232): what is inside the solids <see cref="Venues"/> names
/// <c>amenity=cinema</c>. Numbers are in <c>docs/notes/terrain/venue-cinema.md</c> (UNIC handbook,
/// ProCinema).
///
/// <para>
/// Planned in the bowling centre's frame (<see cref="BowlFrame"/>: <c>u</c> across, <c>v</c> from the
/// entrance end). Front to back: the lobby (box office by the door, concession stand, posters,
/// standees, queue stanchions, benches) with a column of WCs and a store at its end away from the
/// door; then the auditoriums side by side, as many as the width holds at 7-16 m each (a multiplex),
/// with a staff corridor along the door's side; then the projection room across the back of them all,
/// one storey up when there are two (its stair at the corridor's far end), at floor level when not.
/// </para>
/// <para>
/// An auditorium's screen is on its lobby wall and its seats rise away from it, so the way in is a
/// door beside the screen at floor level and up the aisle steps, as in many real halls; the booth is
/// behind the top rows. A porthole is a piece, not a cut: a real opening between the two would be glass
/// the shader draws as sky. The cat on the screen and the house lights are the booth's
/// (<see cref="CinemaBooths"/>), not the plan's.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Bumped when these rules change, so stored cinemas are planned again.</summary>
    public const int CinemaRules = 8;

    private const float BoothDepth = 2.6f, LobbyMin = 4.6f, LobbyMax = 12f, AudMinLength = 8.5f, AudMaxLength = 32f;
    private const float AudMinWidth = 7f, AudTargetWidth = 13f, AudMaxWidth = 18f, StaffWidth = 1.6f;
    private const int MaxHalls = 10;
    /// <summary>The screen's depth from its wall: masking, the speakers behind it [S: 1.0-1.2 m].</summary>
    private const float ScreenDepth = 1.1f;

    /// <summary>
    /// The holes through a hall's back wall into the booth (#1250), across a porthole's <see cref="PortCasingW"/> × 0.45 m casing
    /// (x from its middle, y over its foot): the projection port in the middle (±<see cref="PortHalf"/>), a
    /// darkened viewing window from <see cref="ViewIn"/> to <see cref="ViewOut"/> either side, all from
    /// <see cref="PortFoot"/> to <see cref="PortHead"/>. The casing's mesh frames exactly these.
    /// </summary>
    /// <summary>How far the projector's lens stands over the back row's tread, at least, m: a standing adult's head under the beam.</summary>
    private const float ProjectorClear = 2.0f;

    /// <summary>How far a projector's lens sits under the top of its piece, m (the mesh's pedestal makes up the rest).</summary>
    internal const float ProjectorLensDrop = 0.43f;

    internal const float PortHalf = 0.18f, ViewIn = 0.5f, ViewOut = 1.05f, PortFoot = 0.075f, PortHead = 0.375f;

    /// <summary>The casing's width, m: the viewing windows clear of the projector's 0.8 m body, so the projectionist sees past it.</summary>
    internal const float PortCasingW = 2 * ViewOut + 0.1f;
    /// <summary>Row pitch [S: 1.0-1.2 m]; the rise of each row [S: 0.10-0.45 m, here a steady one].</summary>
    public const float RowPitch = 1.1f, RowRise = 0.32f;
    private const float ExitClear = 0.7f;

    private sealed class CinemaPlan
    {
        public BowlFrame Frame;
        public float LobbyV, HallV1, DoorU, DoorV;
        public int Halls;
        public bool BoothUpstairs;
        /// <summary>The ports' casing's foot over the booth's floor, m (over the hall's: plus a storey when the booth is upstairs).</summary>
        public float PortY;
        public RectPlan? Flight;
    }

    private static readonly ConditionalWeakTable<InteriorLayout, CinemaPlan> CinemaPlans = new();

    /// <summary>Plans a cinema into <paramref name="l"/>; false gives the layout back as it was.</summary>
    internal static bool TryCinema(InteriorLayout l, Footprint fp, Building b, float doorHeight, Random rng)
    {
        bool whole = Venues.WholeBuilding(b);
        float wall = b.MaxY - b.MinY;
        float h;
        int levels;
        if (whole)
        {
            levels = wall >= 7.0f ? 2 : 1;
            h = levels == 2 ? Math.Clamp(wall * 0.88f / 2, 3.2f, 4.4f) : Math.Clamp(wall * 0.85f, 3.0f, 5.5f);
        }
        else
        {
            var (sh, n) = Storeys(b);
            h = sh;
            levels = n >= 2 && sh >= 2.8f ? 2 : 1;
        }

        var (storey, entryX, entryW, type, shop) = (l.StoreyHeight, l.EntryX, l.EntryWidth, l.Type, l.Shop);
        for (int lv = levels; lv >= 1; lv--)
        {
            l.StoreyHeight = lv == levels ? h : whole ? Math.Clamp(wall * 0.85f, 3.0f, 5.5f) : h;
            (l.EntryX, l.EntryWidth) = (entryX, entryW);
            if (PlanCinema(l, lv, doorHeight, whole) && InteriorValidator.Validate(l).Count == 0) return true;
        }
        (l.StoreyHeight, l.EntryX, l.EntryWidth, l.Type, l.Shop) = (storey, entryX, entryW, type, shop);
        l.Floors.Clear();
        l.Below = 0;
        CinemaPlans.Remove(l);
        return false;
    }

    private static bool PlanCinema(InteriorLayout l, int levels, float doorHeight, bool whole)
    {
        float W = l.Width, D = l.Depth;
        var frame = D >= W ? new BowlFrame(true, 1, W, D) : new BowlFrame(false, l.EntryX <= 0 ? 1 : -1, W, D);
        // a door far along a long wall would stretch the lobby to it (CinéVersoix's was 36 x 57 m, #1250): the
        // halls run across the building instead, the lobby along the door's wall, where the depth allows them
        if (!frame.AlongZ && frame.V(l.EntryX, -D / 2) + 2.5f > LobbyMax && D - LobbyMin - BoothDepth >= AudMinLength)
            frame = new BowlFrame(true, 1, W, D);
        float along = frame.Along, across = frame.Across;
        float doorU = frame.U(l.EntryX, -D / 2), doorV = frame.V(l.EntryX, -D / 2);

        // ---- along: the lobby, the halls, the booth; a plain hall past them in a long solid ----------
        float lobby = Math.Clamp(along * 0.25f, LobbyMin, LobbyMax);
        // a door in a long wall past the lobby's depth: the lobby reaches it, while the halls keep their length
        if (!frame.AlongZ) lobby = Math.Max(lobby, Math.Min(doorV + 2.5f, along - BoothDepth - AudMinLength));
        float hallLen = Math.Min(along - lobby - BoothDepth, AudMaxLength);
        if (hallLen < AudMinLength)
        {
            lobby = LobbyMin;
            hallLen = Math.Min(along - lobby - BoothDepth, AudMaxLength);
            if (hallLen < AudMinLength) return false;
        }
        float hallV1 = lobby + hallLen, progV = hallV1 + BoothDepth;
        float rest = along - progV;
        if (rest < 3f) { hallLen += rest; hallV1 += rest; progV = along; rest = 0; }

        // ---- across: the staff corridor on the door's side, the halls, the WC column across the lobby ------
        int halls = Math.Clamp((int)MathF.Round((across - StaffWidth) / AudTargetWidth), 1, MaxHalls);
        while (halls > 1 && (across - StaffWidth) / halls < AudMinWidth) halls--;
        float hallW = Math.Min((across - StaffWidth) / halls, AudMaxWidth);
        if (hallW < AudMinWidth - 0.01f) return false;
        float progW = Math.Min(across, StaffWidth + halls * hallW);
        if (across - progW < 3f) { progW = across; hallW = (progW - StaffWidth) / halls; }
        float uL = frame.AlongZ ? Fit(doorU - progW / 2, -across / 2, across / 2 - progW) : -across / 2;
        float uR = uL + progW;
        bool staffAtL = !frame.AlongZ || doorU <= (uL + uR) / 2;
        float sU0 = staffAtL ? uL : uR - StaffWidth, sU1 = sU0 + StaffWidth;
        float hU0 = staffAtL ? sU1 : uL;

        l.Type = BuildingType.Cinema;
        l.Shop = Loot.ShopType.None;
        l.Below = 0;
        l.Floors.Clear();
        l.Lifts.Clear();
        var ground = new FloorPlan();
        l.Floors.Add(ground);
        float clear = l.StoreyHeight - Slab, top = Math.Min(2.05f, clear - 0.2f);

        // the WC column at the lobby's end away from the door (and the staff corridor)
        const float svcW = 2.6f;
        bool svc = progW >= svcW + 7f && lobby >= 2.4f;
        float cU0 = svc && !staffAtL ? uL + svcW : uL, cU1 = svc && staffAtL ? uR - svcW : uR;
        var hall = new RoomPlan { Type = RoomType.CinemaLobby };
        SetRect(hall, frame.Rect(cU0, 0, cU1, lobby));
        ground.Rooms.Add(hall);
        const int iLobby = 0;
        if (svc)
        {
            float su0 = staffAtL ? uR - svcW : uL;
            var program = new List<RoomType> { RoomType.WC };
            if (lobby >= 5.4f) program.Add(RoomType.WC);
            if (lobby - 2.6f * program.Count >= 1.8f) program.Add(RoomType.Storage);
            float step = lobby / program.Count;
            for (int i = 0; i < program.Count; i++)
            {
                var room = new RoomPlan { Type = program[i] };
                SetRect(room, frame.Rect(su0, step * i, su0 + svcW, i == program.Count - 1 ? lobby : step * (i + 1)));
                ground.Rooms.Add(room);
                int ri = ground.Rooms.Count - 1;
                float c = frame.OnUWall(step * (i + 0.5f));
                var side = staffAtL ? frame.UMinus : frame.UPlus;
                room.Openings.Add(new OpeningPlan { Side = side, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = iLobby });
                hall.Openings.Add(new OpeningPlan { Side = Opposite(side), Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = ri });
            }
        }

        // ---- the auditoriums, each with a door beside its screen onto the lobby ---------------------------
        var auds = new List<int>();
        for (int i = 0; i < halls; i++)
        {
            float a = hU0 + i * hallW, bb = a + hallW;
            var aud = new RoomPlan { Type = RoomType.Auditorium, Span = levels };
            SetRect(aud, frame.Rect(a, lobby, bb, hallV1));
            ground.Rooms.Add(aud);
            int ai = ground.Rooms.Count - 1;
            auds.Add(ai);
            // the door at the hall's end nearer the lobby's middle, inside both
            float c0 = Math.Max(a, cU0) + 0.9f, c1 = Math.Min(bb, cU1) - 0.9f;
            if (c1 < c0) return false;
            float c = (a + bb) / 2 < (cU0 + cU1) / 2 ? c1 : c0;
            c = Math.Clamp(c, a + 0.9f, bb - 0.9f);
            aud.Openings.Add(new OpeningPlan { Side = frame.VMinus, Center = c, Width = 1.2f, Top = top, Kind = OpeningKind.Door, Other = iLobby });
            hall.Openings.Add(new OpeningPlan { Side = frame.VPlus, Center = c, Width = 1.2f, Top = top, Kind = OpeningKind.Door, Other = ai });
        }

        // ---- the staff corridor to the booth -------------------------------------------------------------
        var staff = new RoomPlan { Type = RoomType.StaffCorridor };
        SetRect(staff, frame.Rect(sU0, lobby, sU1, progV));
        ground.Rooms.Add(staff);
        int iStaff = ground.Rooms.Count - 1;
        float sc = (sU0 + sU1) / 2;
        staff.Openings.Add(new OpeningPlan { Side = frame.VMinus, Center = sc, Width = 1.0f, Top = top, Kind = OpeningKind.Door, Other = iLobby });
        hall.Openings.Add(new OpeningPlan { Side = frame.VPlus, Center = sc, Width = 1.0f, Top = top, Kind = OpeningKind.Door, Other = iStaff });

        var plan = new CinemaPlan { Frame = frame, LobbyV = lobby, HallV1 = hallV1, Halls = halls, DoorU = doorU, DoorV = doorV };

        // ---- the booth: upstairs behind the top rows, or at floor level -----------------------------------
        float bU0 = staffAtL ? sU1 : uL, bU1 = staffAtL ? uR : sU0;
        var boothSide = staffAtL ? frame.UMinus : frame.UPlus;
        float boothDoorAt = frame.OnUWall((hallV1 + progV) / 2);
        if (levels == 2)
        {
            int steps = (int)MathF.Ceiling(l.StoreyHeight / GarageRule.StairRiser);
            float run = steps * 0.28f;
            float v1 = hallV1 - 0.2f, v0 = v1 - run;
            if (v0 < lobby + 1.4f) return false;
            // the store under the booth, off the corridor
            var under = new RoomPlan { Type = RoomType.Storage };
            SetRect(under, frame.Rect(bU0, hallV1, bU1, progV));
            ground.Rooms.Add(under);
            int iu = ground.Rooms.Count - 1;
            under.Openings.Add(new OpeningPlan { Side = boothSide, Center = boothDoorAt, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = iStaff });
            staff.Openings.Add(new OpeningPlan { Side = Opposite(boothSide), Center = boothDoorAt, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = iu });

            // the stair up the corridor, along v, landing at the booth's level by its door
            float laneU0 = sU0 + 0.25f, laneU1 = sU1 - 0.25f;
            var lane = frame.Rect(laneU0, v0, laneU1, v1);
            var flight = frame.AlongZ
                ? new FlightPlan { X0 = lane.X0, X1 = lane.X1, ZBottom = frame.Point(0, v0).Z, ZTop = frame.Point(0, v1).Z }
                : new FlightPlan { AlongX = true, X0 = lane.Z0, X1 = lane.Z1, ZBottom = frame.Point(0, v0).X, ZTop = frame.Point(0, v1).X };
            ground.Flight = flight;
            plan.Flight = flight.Area();

            var upper = new FloorPlan();
            l.Floors.Add(upper);
            var landing = new RoomPlan { Type = RoomType.StaffCorridor };
            SetRect(landing, frame.Rect(sU0, v0 - 0.3f, sU1, progV));
            upper.Rooms.Add(landing);
            var booth = new RoomPlan { Type = RoomType.ProjectionRoom };
            SetRect(booth, frame.Rect(bU0, hallV1, bU1, progV));
            upper.Rooms.Add(booth);
            upper.Holes.Add(flight.Area());
            float utop = Math.Min(2.05f, l.ClearOf(booth) - 0.2f);
            booth.Openings.Add(new OpeningPlan { Side = boothSide, Center = boothDoorAt, Width = InnerDoor, Top = utop, Kind = OpeningKind.Door, Other = 0 });
            landing.Openings.Add(new OpeningPlan { Side = Opposite(boothSide), Center = boothDoorAt, Width = InnerDoor, Top = utop, Kind = OpeningKind.Door, Other = 1 });
            plan.BoothUpstairs = true;
        }
        else
        {
            var booth = new RoomPlan { Type = RoomType.ProjectionRoom };
            SetRect(booth, frame.Rect(bU0, hallV1, bU1, progV));
            ground.Rooms.Add(booth);
            int ib = ground.Rooms.Count - 1;
            booth.Openings.Add(new OpeningPlan { Side = boothSide, Center = boothDoorAt, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = iStaff });
            staff.Openings.Add(new OpeningPlan { Side = Opposite(boothSide), Center = boothDoorAt, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = ib });
        }

        // ---- a plain hall past the programme in a mall --------------------------------------------------
        if (rest >= 3f)
        {
            var back = new RoomPlan { Type = RoomType.Hall };
            SetRect(back, frame.Rect(uL, progV, uR, along));
            ground.Rooms.Add(back);
            int bi = ground.Rooms.Count - 1;
            float c = (sU0 + sU1) / 2;
            staff.Openings.Add(new OpeningPlan { Side = frame.VPlus, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = bi });
            back.Openings.Add(new OpeningPlan { Side = frame.VMinus, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = iStaff });
        }
        if (progW < across)
        {
            bool right = uL <= -across / 2 + 0.01f;
            var side = new RoomPlan { Type = RoomType.Hall };
            SetRect(side, right ? frame.Rect(uR, 0, across / 2, along) : frame.Rect(-across / 2, 0, uL, along));
            ground.Rooms.Add(side);
            int si = ground.Rooms.Count - 1;
            var wallSide = right ? frame.UPlus : frame.UMinus;
            // through the lobby when it reaches that side, else the corridor
            int from = right ? (cU1 >= uR - 0.01f ? iLobby : -1) : (cU0 <= uL + 0.01f ? iLobby : -1);
            if (from < 0) from = (right ? sU1 >= uR - 0.01f : sU0 <= uL + 0.01f) ? iStaff : -1;
            if (from < 0) return false;
            float c = from == iLobby ? frame.OnUWall(lobby / 2) : frame.OnUWall((lobby + progV) / 2);
            ground.Rooms[from].Openings.Add(new OpeningPlan { Side = wallSide, Center = c, Width = 1.6f, Top = top, Kind = OpeningKind.Door, Other = si });
            side.Openings.Add(new OpeningPlan { Side = Opposite(wallSide), Center = c, Width = 1.6f, Top = top, Kind = OpeningKind.Door, Other = from });
        }

        // ---- the way in --------------------------------------------------------------------------------
        l.EntryWidth = Math.Min(Math.Max(l.DoorWidth, 1.8f), W - 1.2f);
        l.EntryX = Fit(l.EntryX, -W / 2 + l.EntryWidth / 2 + 0.3f, W / 2 - l.EntryWidth / 2 - 0.3f);
        int entryRoom = RoomAt(ground, l.EntryX, -D / 2 + 0.1f);
        if (entryRoom != iLobby) return false;
        hall.Openings.Add(new OpeningPlan
        {
            Side = Side.Front, Center = l.EntryX, Width = l.EntryWidth, Bottom = 0,
            Top = Math.Min(Math.Max(doorHeight, 2.1f), l.ClearOf(hall) - 0.3f), Kind = OpeningKind.Entry,
        });

        // the lobby and its WCs see the street when the cinema is not its own blind box
        if (!whole) AddWindows(l, ground, 0);
        foreach (var r in ground.Rooms.Where(r => r.Type is RoomType.Auditorium or RoomType.ProjectionRoom or RoomType.StaffCorridor))
            r.Openings.RemoveAll(o => o.Kind == OpeningKind.Window);

        // ---- the ports through each hall's back wall into the booth (#1250): real holes, the projection port
        // open in the middle of the porthole's casing, a darkened viewing window either side of it -----------
        // upstairs the windows at a standing projectionist's eyes (1.4-1.7 m), the projector's lens at the port
        plan.PortY = plan.BoothUpstairs ? 1.3f : Math.Min(clear - 0.7f, 2.4f);
        var boothFloor = l.Floors[plan.BoothUpstairs ? 1 : 0];
        int iBooth = boothFloor.Rooms.FindIndex(r => r.Type == RoomType.ProjectionRoom);
        if (iBooth < 0) return false;
        float hallLift = plan.BoothUpstairs ? l.StoreyHeight : 0;
        float viewW = ViewOut - ViewIn, viewAt = (ViewIn + ViewOut) / 2;
        for (int i = 0; i < halls; i++)
        {
            float u = hU0 + hallW * (i + 0.5f);
            foreach (var (c, w, open) in new[] { (u, 2 * PortHalf, true), (u - viewAt, viewW, false), (u + viewAt, viewW, false) })
            {
                ground.Rooms[auds[i]].Openings.Add(new OpeningPlan
                {
                    Side = frame.VPlus, Center = c, Width = w, Bottom = hallLift + plan.PortY + PortFoot,
                    Top = hallLift + plan.PortY + PortHead, Kind = OpeningKind.Window, Other = iBooth, Open = open,
                });
                boothFloor.Rooms[iBooth].Openings.Add(new OpeningPlan
                {
                    Side = frame.VMinus, Center = c, Width = w, Bottom = plan.PortY + PortFoot,
                    Top = plan.PortY + PortHead, Kind = OpeningKind.Window, Other = auds[i], Open = open,
                });
            }
        }

        CinemaPlans.AddOrUpdate(l, plan);
        return true;
    }

    // ---- furnishing (HallLayout) -------------------------------------------------------------------

    private static bool CinemaRoom(RoomType t) => t is RoomType.CinemaLobby or RoomType.Auditorium or RoomType.ProjectionRoom;

    private static void CinemaLayout(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        if (!CinemaPlans.TryGetValue(l, out var plan)) return;
        switch (r.Type)
        {
            case RoomType.CinemaLobby: CinemaLobby(l, f, r, plan, placed, blocked, rng); break;
            case RoomType.Auditorium: Auditorium(l, f, r, plan, placed, blocked); break;
            case RoomType.ProjectionRoom: Booth(l, f, r, plan, placed, blocked, rng); break;
        }
    }

    /// <summary>
    /// An auditorium: the screen across its lobby wall, clear of the door's corner; the seating bank
    /// from the front row's distance to the back wall; exit signs over the door; the porthole high in
    /// the back wall.
    /// </summary>
    private static void Auditorium(InteriorLayout l, int f, RoomPlan r, CinemaPlan p, List<RectPlan> placed, List<RectPlan> blocked)
    {
        var fr = p.Frame;
        float clear = l.ClearOf(r);
        float u0 = Math.Min(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1)), u1 = Math.Max(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1));
        float width = u1 - u0, uMid = (u0 + u1) / 2;
        // the screen with its curtains: the width less the door's corner each side, under the ceiling
        float screenW = Math.Clamp(width - 3.6f, 3f, 21f);
        var (sx, sz) = fr.Point(uMid, p.LobbyV + 0.15f + ScreenDepth / 2);
        Put(l, f, FurnitureType.MovieScreen, sx, sz, fr.FaceVPlus, screenW, ScreenDepth, clear, placed);
        // the seating: the front row 0.6 screen widths from the screen [S], at least 3 m
        float front = p.LobbyV + 0.15f + ScreenDepth + Math.Max(3f, 0.6f * screenW * 0.7f);
        // 0.7 m off the back and side walls: past the 0.6 m a fire exit's doorway keeps clear (#498 puts
        // extra doors round long buildings, and in a hall they are its exits)
        float back = p.HallV1 - ExitClear, depth = back - front;
        if (depth >= RowPitch * 2)
        {
            var (bx, bz) = fr.Point(uMid, (front + back) / 2);
            int rows = (int)(depth / RowPitch);
            float rise = Math.Min(RowRise, Math.Max(0.12f, (clear - 2.8f) / Math.Max(1f, depth / RowPitch)));
            // the back row under the projector's beam (#1250): its tread at least ProjectorClear under the lens, so
            // the light passes over the heads and the seat backs, even of someone standing up there
            float lens = (p.BoothUpstairs ? l.StoreyHeight : 0) + p.PortY + (PortFoot + PortHead) / 2;
            rise = Math.Min(rise, Math.Max(0f, lens - ProjectorClear) / Math.Max(1, rows));
            Put(l, f, FurnitureType.SeatingBank, bx, bz, fr.FaceVMinus, width - 2 * ExitClear, depth, rows * rise, placed);
        }
        // the screen channels in tall stacks either side of the screen, the surrounds and wall lights along the
        // side walls in turn, clear of their doors (#1250)
        foreach (int side in new[] { -1, 1 })
        {
            var (lx, lz) = fr.Point(uMid + side * (screenW / 2 + 0.55f), p.LobbyV + 0.15f + 0.35f);
            var stack = new RectPlan(lx - 0.4f, lz - 0.4f, lx + 0.4f, lz + 0.4f);
            if (Free(r, stack, placed, blocked, 0.02f)) Put(l, f, FurnitureType.Loudspeaker, lx, lz, fr.FaceVPlus, 0.7f, 0.6f, Math.Min(2.4f, clear - 0.5f), placed);
            var wallSide = side < 0 ? fr.UMinus : fr.UPlus;
            float wu = side < 0 ? u0 + 0.18f : u1 - 0.18f;
            int turns = side < 0 ? fr.FaceUPlus : fr.FaceUMinus;
            int k = 0;
            for (float v = p.LobbyV + ScreenDepth + 2.5f; v < p.HallV1 - 1.0f; v += 1.8f, k++)
            {
                float at = fr.OnUWall(v);
                if (r.Openings.Any(o => o.Side == wallSide && Math.Abs(o.Center - at) < o.Width / 2 + 0.7f)) continue;
                var (x, z) = fr.Point(wu, v);
                if (k % 2 == 0)
                    Put(l, f, FurnitureType.Loudspeaker, x, z, turns, 0.45f, 0.32f, 0.65f, placed, lift: Math.Min(3.2f, clear - 0.9f));
                else
                    Put(l, f, FurnitureType.WallSconce, x, z, turns, 0.36f, 0.16f, 0.5f, placed, lift: Math.Min(2.1f, clear - 1.6f));
            }
        }
        // exit signs over the doors, green running men [S: CH/EU]
        foreach (var o in r.Openings.Where(o => o.Kind == OpeningKind.Door))
        {
            var (ex, ez) = fr.Point(o.Center, p.LobbyV + 0.1f);
            Put(l, f, FurnitureType.ExitSign, ex, ez, fr.FaceVPlus, 0.45f, 0.08f, 0.22f, placed, lift: Math.Min(2.3f, clear - 0.4f));
        }
        // the porthole the projector shines through, high in the back wall
        var (px, pz) = fr.Point(uMid, p.HallV1 - 0.1f);
        Put(l, f, FurnitureType.Porthole, px, pz, fr.FaceVMinus, PortCasingW, 0.1f, 0.45f, placed, lift: (p.BoothUpstairs ? l.StoreyHeight : 0) + p.PortY);
    }

    /// <summary>The projection room: a projector to each hall's porthole, the rack, the old platter, the control panel.</summary>
    private static void Booth(InteriorLayout l, int f, RoomPlan r, CinemaPlan p, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var fr = p.Frame;
        float u0 = Math.Min(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1)), u1 = Math.Max(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1));
        float hallW = (u1 - u0) / p.Halls;
        for (int i = 0; i < p.Halls; i++)
        {
            float u = u0 + hallW * (i + 0.5f);
            var (x, z) = fr.Point(u, p.HallV1 + 0.8f);
            var rect = new RectPlan(x - 0.6f, z - 0.6f, x + 0.6f, z + 0.6f);
            if (!Free(r, rect, placed, blocked, 0.05f)) continue;
            // as tall as puts its lens in the middle of the port (#1250)
            float lens = p.PortY + (PortFoot + PortHead) / 2;
            Put(l, f, FurnitureType.Projector, x, z, fr.FaceVMinus, 0.8f, 1.2f, lens + ProjectorLensDrop, placed);
            // the porthole it shines through, from the booth's side (#1250): the hall's is on the other face of
            // the wall, at the same height over the hall's floor
            var (wx, wz) = fr.Point(u, p.HallV1 + 0.1f);
            Put(l, f, FurnitureType.Porthole, wx, wz, fr.FaceVPlus, PortCasingW, 0.1f, 0.45f, placed, lift: p.PortY);
        }
        TryPlace(l, f, r, new Piece(FurnitureType.BoothPanel, 1.2f, 0.3f, 1.7f, true), placed, blocked, rng, new[] { fr.VPlus });
        TryPlace(l, f, r, new Piece(FurnitureType.ServerRack, 0.6f, 0.9f, 2.0f, true), placed, blocked, rng, new[] { fr.VPlus });
        TryPlace(l, f, r, new Piece(FurnitureType.FilmPlatter, 1.2f, 1.2f, 1.5f, false), placed, blocked, rng);
        TryPlace(l, f, r, new Piece(FurnitureType.Chair, 0.45f, 0.45f, 0.9f, false), placed, blocked, rng);
    }

    /// <summary>The lobby: carpet, box office by the door, the concession stand, posters, standees, stanchions, benches.</summary>
    private static void CinemaLobby(InteriorLayout l, int f, RoomPlan r, CinemaPlan p, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var fr = p.Frame;
        Carpet(l, f, r, placed);
        var (dx, dz) = fr.Point(p.DoorU, p.DoorV);
        blocked.Add(new RectPlan(dx - 1.6f, dz - 1.6f, dx + 1.6f, dz + 1.6f).Clip(r));
        // the doors to the halls and the corridor keep their fronts clear (the opening clearances do the rest)
        blocked.Add(Strip(r, fr.VPlus, 1.0f));
        var near = Sides(fr, l, r);
        TryPlace(l, f, r, new Piece(FurnitureType.BoxOffice, Math.Min(3.2f, Math.Max(2.2f, r.Width * 0.25f)), 2.2f, 2.3f, true),
            placed, blocked, rng, near.Door, near.DoorAt);
        float stand = Math.Clamp(1.2f * Math.Max(1, p.Halls) + 2.4f, 3.2f, 8f);
        int before = l.Furniture.Count;
        foreach (float len in new[] { stand, 3.2f, 2.4f })
        {
            TryPlace(l, f, r, new Piece(FurnitureType.ConcessionStand, len, 2.4f, 2.6f, true), placed, blocked, rng, near.Far);
            if (l.Furniture.Count > before) break;
        }
        // the rest scales with the floor: a multiplex's foyer is a big room, and an empty one reads as a hangar
        float area = r.Area;
        int posters = Math.Clamp((int)((r.Width + r.Depth) / 2.2f), 2, 16);
        for (int i = 0; i < posters; i++)
            TryPlace(l, f, r, new Piece(FurnitureType.PosterFrame, 0.9f, 0.1f, 2.1f, true), placed, blocked, rng);
        for (int i = 0; i < Math.Clamp((int)(area / 120f), 1, 3); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Standee, 0.8f, 0.4f, 1.9f, false), placed, blocked, rng);
        for (int i = 0; i < Math.Clamp((int)(area / 150f), 1, 3); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Stanchions, 3.0f, 0.3f, 1.0f, false), placed, blocked, rng);
        for (int i = 0; i < Math.Clamp((int)(area / 50f), 2, 8); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Bench, 1.6f, 0.5f, 0.45f, true), placed, blocked, rng);
        // a big foyer has a few arcade machines for the wait, as multiplexes do
        if (area >= 200f)
            for (int i = 0; i < Math.Clamp((int)(area / 150f), 2, 5); i++)
                TryPlace(l, f, r, new Piece(FurnitureType.ArcadeCabinet, 0.75f, 0.9f, 1.85f, true), placed, blocked, rng, near.Far);
        for (int i = 0; i < Math.Clamp((int)(area / 80f), 1, 6); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
        // the middle of a big foyer (#1250): high tables by the bar's side, sofas, standees of the coming
        // films, a table or two [S: Cineplexx, Kino Svetozor, CinemaxX foyers]
        // a wide foyer's roof stands on columns, a row of them down its middle every 8 m [S: multiplex foyers]
        if (Math.Min(r.Width, r.Depth) >= 10f)
        {
            bool alongX = r.Width >= r.Depth;
            float len = alongX ? r.Width : r.Depth, mid = alongX ? (r.Z0 + r.Z1) / 2 : (r.X0 + r.X1) / 2;
            int n = (int)(len / 8f);
            for (int i = 1; i < n; i++)
            {
                float a = (alongX ? r.X0 : r.Z0) + len * i / n;
                var (x, z) = alongX ? (a, mid) : (mid, a);
                var rect = new RectPlan(x - 0.25f, z - 0.25f, x + 0.25f, z + 0.25f);
                if (Free(r, rect, placed, blocked, 0.1f)) Put(l, f, FurnitureType.Pillar, x, z, 0, 0.5f, 0.5f, l.ClearOf(r), placed);
            }
        }
        // groups on every other cell, so the foyer keeps its ways across: a few high tables by the bar, sofas,
        // two pairs of standees, a table or two, a plant
        int standees = 0;
        FillFloor(l, f, r, placed, blocked, 3.6f, 3.6f, 1.4f, (i, k) => (i + k) % 2 == 1 ? null : ((i / 2 * 3 + k) % 6) switch
        {
            0 => HighTables,
            1 => LoungeGroup,
            2 when standees++ < 2 => Standees,
            3 => TableForFour,
            4 => HighTables,
            _ => null,
        });
    }

    /// <summary>Whole cinemas in the model viewer: a town cinema on two levels, and a small one on one.</summary>
    [Core.Showcase("Interiors", "Cinema")]
    private static IEnumerable<(string Name, Func<Godot.Node3D> Make)> ShowcaseCinema()
    {
        foreach (var (name, w, d, h) in new[] { ("Town", 18f, 34f, 9f), ("Small", 14f, 26f, 5f) })
            yield return (name, () =>
            {
                var id = new TileId(2600, 1200);
                var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, w, d, h, BuildingKind.Commercial) } };
                Venues.Override = new List<VenueRecord>
                {
                    new("n1", VenueType.Cinema, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", name),
                };
                var layout = Generate(tile, 0, null, null)!;
                Venues.Override = null;
                var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout),
                    Styles.StyleKit.Material(Styles.MaterialRole.Interior), Godot.Transform3D.Identity);
                foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
                // the show on, as the booth would have it: lights dimmed, the cat on the screen
                InteriorNode.ShowSlide(0);
                bool real = InteriorNode.RealProjection;
                node.ShowCinema(CinemaBooths.Level(CinemaBooths.Lights.Dim), screen: true, real);
                var screen = layout.Furniture.First(f => f.Type == FurnitureType.MovieScreen);
                // at High detail the first hall's projector throws the picture, as for a player standing in it
                if (real)
                {
                    var facing = new Godot.Basis(Godot.Vector3.Up, screen.Turns * Godot.Mathf.Pi / 2);
                    var front = new Godot.Vector3(screen.X, layout.FloorY(screen.Floor), screen.Z) + facing * new Godot.Vector3(0, 0, 1.2f);
                    node.ProjectFor(front + new Godot.Vector3(0, 1.5f, 0));
                    // a mannequin walking across the beam, wearing the picture and shadowing the screen
                    var lane = new Godot.Node3D { Name = "WalkerLane", Transform = new Godot.Transform3D(facing, front) };
                    node.AddChild(lane);
                    lane.AddChild(new ProjectionWalker(new Godot.StandardMaterial3D { AlbedoColor = new Godot.Color(0.85f, 0.85f, 0.85f) }) { Name = "Walker" });
                }
                Godot.GD.Print($"[cinema] {name}: screen at {screen.X:F1}, {screen.Z:F1}, {screen.W:F1} m wide, turns {screen.Turns}");
                return node;
            });
    }

    /// <summary>The cinema pieces at the sizes this file places them, for the model viewer.</summary>
    private static readonly Piece[] CinemaPieces =
    {
        new(FurnitureType.SeatingBank, 12f, 9f, 3f, false),
        new(FurnitureType.MovieScreen, 9f, ScreenDepth, 5f, true),
        new(FurnitureType.ExitSign, 0.45f, 0.08f, 0.22f, true),
        new(FurnitureType.Porthole, PortCasingW, 0.1f, 0.45f, true),
        new(FurnitureType.BoxOffice, 3.0f, 2.2f, 2.3f, true),
        new(FurnitureType.ConcessionStand, 5.0f, 2.4f, 2.6f, true),
        new(FurnitureType.PosterFrame, 0.9f, 0.1f, 2.1f, true),
        new(FurnitureType.Standee, 0.8f, 0.4f, 1.9f, false),
        new(FurnitureType.Stanchions, 3.0f, 0.3f, 1.0f, false),
        new(FurnitureType.Projector, 0.8f, 1.2f, 1.6f, false),
        new(FurnitureType.ServerRack, 0.6f, 0.9f, 2.0f, true),
        new(FurnitureType.FilmPlatter, 1.2f, 1.2f, 1.5f, false),
        new(FurnitureType.BoothPanel, 1.2f, 0.3f, 1.7f, true),
        new(FurnitureType.Loudspeaker, 0.45f, 0.32f, 0.65f, true),
        new(FurnitureType.WallSconce, 0.36f, 0.16f, 0.5f, true),
    };
}
