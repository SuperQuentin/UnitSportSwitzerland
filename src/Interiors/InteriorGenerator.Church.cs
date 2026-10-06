using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Church rules: one interior for every solid of a <see cref="BuildingType.Church"/> group.
///
/// <para>
/// Frame: Z along the nave, the end nearest the street at −Z, the altar at +Z. The nave is one
/// room rising through several storeys; each tower's base is a porch opening into it through an
/// arch. The tallest tower that can hold a stair gets one: two switchback lanes against a side
/// wall, one flight a storey, up to a bell chamber. Every member's door is an entrance on the
/// wall of its own room that faces the way the real door does. Storeys are the tower's (a
/// uniform <see cref="InteriorLayout.StoreyHeight"/>), which keeps loot and the probes, which
/// count floors, working unchanged.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Bumped when these rules change, so stored churches are planned again.</summary>
    public const int ChurchRules = 1;

    private const float TowerStorey = 3.0f, TowerStoreyTight = 2.7f;
    private const float StairLanding = 0.9f;
    private const float MaxStairSlopeDeg = 47f;
    private const float StairWalkway = 0.9f;
    private const int MaxTowerFloors = 9;
    private const float CornerMargin = 0.35f;

    /// <summary>Plans any building: a church from all its solids, anything else from its own footprint.</summary>
    public static InteriorLayout? Generate(BuildingTile tile, int index, RoadTile? roads, ChunkGrid? grid)
    {
        // a building site has no interior: its half-built shell stands in the world (#608)
        if (tile.Buildings[index].Kind == BuildingKind.UnderConstruction) return null;
        var group = BuildingTypes.For(tile).GroupOf(index);
        var layout = group?.Type == BuildingType.Church ? Church(tile, group, roads, grid) : null;
        if (layout == null)
        {
            var fp = BuildingFootprint.Compute(tile, index, roads, grid);
            if (fp == null) return null;
            layout = Generate(fp, tile.Buildings[index], Loot.ShopTables.IsRural(tile.Buildings.Count),
                group?.Type ?? BuildingType.None);
        }
        layout.Group = GroupPrint(tile, index);
        return layout;
    }

    /// <summary>What a stored plan for this building must have been made from ("" for a lone solid).</summary>
    public static string GroupPrint(BuildingTile tile, int index) =>
        BuildingTypes.For(tile).GroupOf(index) switch
        {
            { Type: BuildingType.Church } g => g.Fingerprint(tile, ChurchRules),
            { Type: BuildingType.Ikea } g => g.Fingerprint(tile, IkeaRules),
            _ => "",
        };

    private sealed class Part
    {
        public int Index;
        public BuildingPart Role;
        public PlanBox Box;
        public RectPlan Rect = null!;
        public DoorSpot Door;
        /// <summary>This solid's other facade doors (#498): a side door on the nave, slot 1 and up.</summary>
        public IReadOnlyList<DoorSpot> Extra = Array.Empty<DoorSpot>();
        /// <summary>For a tower: which side of the nave it stands on.</summary>
        public Side Toward;
        public int Room = -1;
    }

    private static Side Opposite(Side s) => (Side)(((int)s + 2) % 4);

    private static Vector2 Outward(Side s) => s switch
    {
        Side.Front => new Vector2(0, -1),
        Side.Back => new Vector2(0, 1),
        Side.Left => new Vector2(-1, 0),
        _ => new Vector2(1, 0),
    };

    private static InteriorLayout? Church(BuildingTile tile, BuildingGroup group, RoadTile? roads, ChunkGrid? grid)
    {
        var map = BuildingTypes.For(tile);
        var hallB = tile.Buildings[group.Primary];
        if (map.Boxes[group.Primary] is not { } hall) return null;
        var key = new BuildingKey(tile.Id.E, tile.Id.N, group.Primary);

        var parts = new List<Part>();
        for (int m = 0; m < group.Members.Count; m++)
        {
            int i = group.Members[m];
            if (map.Boxes[i] is not { } box) continue;
            var fp = BuildingFootprint.Compute(tile, i, roads, grid);
            parts.Add(new Part
            {
                Index = i, Role = group.Parts[m], Box = box,
                Door = fp?.Door ?? default, Extra = fp?.Extra ?? Array.Empty<DoorSpot>(),
            });
        }
        if (parts.Count == 0 || parts[0].Role != BuildingPart.Nave) return null;

        // ---- frame: the street end of the nave is the front ------------------------------------
        var axis = hall.LongAxis;
        var toward = BuildingFootprint.StreetNear(roads, hall.Center)
            ?? (parts.Count > 1 ? parts[1].Box.Center
                : parts[0].Door.Width > 0 ? new Vector2(parts[0].Door.Position.X, parts[0].Door.Position.Z)
                : hall.Center - axis);
        var V = (toward - hall.Center).Dot(axis) > 0 ? -axis : axis;
        var U = new Vector2(V.Y, -V.X); // so that the layout's AxisV is V, as Footprint defines it

        foreach (var p in parts)
        {
            var (x0, x1) = p.Box.Along(U, hall.Center);
            var (z0, z1) = p.Box.Along(V, hall.Center);
            p.Rect = new RectPlan(x0, z0, x1, z1);
        }
        var nave = parts[0].Rect;

        var towers = new List<Part>();
        foreach (var t in parts.Skip(1))
            if (Attach(nave, t) && !towers.Any(o => o.Rect.Overlaps(t.Rect)))
                towers.Add(t);

        // recentre on everything, so Width x Depth is the whole church
        var rects = new List<RectPlan> { nave };
        rects.AddRange(towers.Select(t => t.Rect));
        float bx0 = rects.Min(r => r.X0), bx1 = rects.Max(r => r.X1);
        float bz0 = rects.Min(r => r.Z0), bz1 = rects.Max(r => r.Z1);
        float ox = (bx0 + bx1) / 2, oz = (bz0 + bz1) / 2;
        foreach (var r in rects) { r.X0 -= ox; r.X1 -= ox; r.Z0 -= oz; r.Z1 -= oz; }
        var center = hall.Center + U * ox + V * oz;
        Vector2 ToLocal(Vector3 p) { var d = new Vector2(p.X, p.Z) - center; return new Vector2(d.Dot(U), d.Dot(V)); }

        // ---- storeys: the tower's, and the nave rises through several ---------------------------
        Part? climb = null;
        float h = TowerStorey;
        foreach (var t in towers.OrderByDescending(t => tile.Buildings[t.Index].MaxY).ThenBy(t => t.Index))
        {
            foreach (float hs in new[] { TowerStorey, TowerStoreyTight })
                if (StairFits(t.Rect, hs)) { climb = t; h = hs; break; }
            if (climb != null) break;
        }
        float naveHeight = hall.Eave - hallB.MinY - 0.8f;
        int span = Math.Clamp((int)MathF.Round(naveHeight / h), 2, 6);
        int floors = 1;
        if (climb != null)
        {
            // the bell chamber on top is two storeys tall, so the bell hangs clear over heads
            float towerHeight = climb.Box.Eave - tile.Buildings[climb.Index].MinY - 0.8f;
            floors = Math.Clamp((int)(towerHeight / h) - 1, 2, MaxTowerFloors);
        }

        var layout = new InteriorLayout
        {
            Key = key.ToString(),
            Kind = BuildingKind.Sacral,
            Type = BuildingType.Church,
            TriangleCount = hallB.TriangleCount,
            MinY = hallB.MinY,
            MaxY = hallB.MaxY,
            CenterX = center.X,
            CenterZ = center.Y,
            Yaw = Mathf.Atan2(-U.Y, U.X),
            Width = bx1 - bx0,
            Depth = bz1 - bz0,
            StoreyHeight = h,
        };
        float clear = h - Slab;

        // ---- ground floor: nave and porches ----------------------------------------------------
        var ground = new FloorPlan();
        var naveRoom = new RoomPlan { X0 = nave.X0, Z0 = nave.Z0, X1 = nave.X1, Z1 = nave.Z1, Type = RoomType.Nave, Span = span };
        ground.Rooms.Add(naveRoom);
        foreach (var t in towers)
        {
            t.Room = ground.Rooms.Count;
            ground.Rooms.Add(new RoomPlan { X0 = t.Rect.X0, Z0 = t.Rect.Z0, X1 = t.Rect.X1, Z1 = t.Rect.Z1, Type = RoomType.Porch });
        }
        layout.Floors.Add(ground);

        var spans = new WallSpans();
        for (int ri = 0; ri < ground.Rooms.Count; ri++)
            spans.InitRoom(ri, ground.Rooms[ri]);

        // the stair: two lanes against whichever side wall the porch can spare
        float laneA0 = 0, laneA1 = 0, laneB0 = 0, laneB1 = 0, runZ0 = 0, runZ1 = 0;
        bool lanesLeft = true;
        if (climb != null)
        {
            var r = climb.Rect;
            // never against the wall shared with the nave: a tower on the nave's right meets it
            // with its own left wall
            lanesLeft = climb.Toward != Side.Right;
            if (lanesLeft) { laneA0 = r.X0; laneA1 = r.X0 + LaneWidth; laneB0 = laneA1; laneB1 = laneA1 + LaneWidth; }
            else { laneA1 = r.X1; laneA0 = r.X1 - LaneWidth; laneB1 = laneA0; laneB0 = laneA0 - LaneWidth; }
            int steps = (int)MathF.Ceiling(h / 0.19f);
            float tread = Math.Min(0.27f, (r.Z1 - r.Z0 - 2 * StairLanding) / steps);
            runZ0 = r.Z0 + StairLanding;
            runZ1 = runZ0 + tread * steps;

            int ri = climb.Room;
            spans.Remove(ri, lanesLeft ? Side.Left : Side.Right);
            float w0 = Math.Min(laneA0, laneB0) - 0.1f, w1 = Math.Max(laneA1, laneB1) + 0.15f;
            spans.Cut(ri, Side.Front, w0, w1);
            spans.Cut(ri, Side.Back, w0, w1);
        }

        // arches from each porch into the nave
        foreach (var t in towers)
        {
            var porch = ground.Rooms[t.Room];
            var wall = Opposite(t.Toward); // the porch's wall against the nave
            bool acrossX = wall is Side.Front or Side.Back;
            float s0 = acrossX ? Math.Max(porch.X0, nave.X0) : Math.Max(porch.Z0, nave.Z0);
            float s1 = acrossX ? Math.Min(porch.X1, nave.X1) : Math.Min(porch.Z1, nave.Z1);
            // keep it off the stair lanes
            if (t == climb && acrossX)
            {
                if (lanesLeft) s0 = Math.Max(s0, laneB1 + 0.15f);
                else s1 = Math.Min(s1, laneB0 - 0.15f);
            }
            float len = s1 - s0 - 2 * 0.25f;
            if (len < 1.0f) return null; // a porch we cannot walk into: plan the nave alone instead
            float width = Math.Min(2.2f, len);
            float c = (s0 + s1) / 2;
            float top = Math.Min(2.6f, clear - 0.2f);
            porch.Openings.Add(new OpeningPlan { Side = wall, Center = c, Width = width, Top = top, Kind = OpeningKind.Arch, Other = 0 });
            naveRoom.Openings.Add(new OpeningPlan { Side = t.Toward, Center = c, Width = width, Top = top, Kind = OpeningKind.Arch, Other = t.Room });
            spans.Remove(t.Room, wall);
            // the nave wall behind a porch is not outside, for doors and windows alike
            spans.Cut(0, t.Toward, acrossX ? porch.X0 - 0.25f : porch.Z0 - 0.25f, acrossX ? porch.X1 + 0.25f : porch.Z1 + 0.25f);
        }

        // ---- entrances: every door of every member opens into its own room ----------------------
        // the main door of each solid first (a church of one gets its west door before its side
        // door, so the one nearest the street below still wins the main entrance), then the extras
        var mainDoors = new HashSet<string>();
        foreach (var p in parts.SelectMany(p => p.Extra.Prepend(p.Door).Select(d => (Part: p, Door: d)))
                     .OrderBy(x => x.Door.Slot))
        {
            var spot = p.Door;
            if (spot.Width <= 0) continue;
            int room = p.Part.Role == BuildingPart.Nave || p.Part.Room < 0 ? 0 : p.Part.Room;
            var r = ground.Rooms[room];
            var at = ToLocal(spot.Position);
            var d = new Vector2(spot.Outward.X, spot.Outward.Z);
            var outward = new Vector2(d.Dot(U), d.Dot(V));
            float width = Math.Clamp(spot.Width, 0.9f, 1.8f);
            foreach (var side in new[] { Side.Front, Side.Right, Side.Back, Side.Left }
                         .OrderByDescending(s => Outward(s).Dot(outward)).ThenBy(s => (int)s))
            {
                float want = side is Side.Front or Side.Back ? at.X : at.Y;
                if (spans.Fit(room, side, want, width) is not { } c) continue;
                spans.Cut(room, side, c - width / 2 - 0.3f, c + width / 2 + 0.3f);
                r.Openings.Add(new OpeningPlan
                {
                    Side = side, Center = c, Width = width, Bottom = 0,
                    Top = Math.Min(2.5f, layout.ClearOf(r) - 0.3f), Kind = OpeningKind.Entry,
                });
                var inward = -Outward(side);
                var line = side switch
                {
                    Side.Front => new Vector2(c, r.Z0),
                    Side.Back => new Vector2(c, r.Z1),
                    Side.Left => new Vector2(r.X0, c),
                    _ => new Vector2(r.X1, c),
                };
                string name = new DoorKey(tile.Id.E, tile.Id.N, p.Part.Index, spot.Slot).ToString();
                if (spot.Slot == 0) mainDoors.Add(name);
                layout.Entrances.Add(new EntrancePlan
                {
                    Door = name,
                    X = line.X, Z = line.Y, InX = inward.X, InZ = inward.Y, Width = width,
                    DoorX = spot.Position.X, DoorY = spot.Position.Y, DoorZ = spot.Position.Z,
                    DoorOutX = spot.Outward.X, DoorOutZ = spot.Outward.Z,
                    DoorWidth = spot.Width, DoorHeight = spot.Height, Hang = spot.Hang, Vehicle = spot.Vehicle,
                });
                break;
            }
        }
        if (layout.Entrances.Count == 0) return null;

        // the main entrance, first in the list: the one whose door is nearest the street
        // the main entrance is one of the solids' own front doors, never a side door
        var main = layout.Entrances.Where(e => mainDoors.Contains(e.Door)).DefaultIfEmpty(layout.Entrances[0])
            .MinBy(e => new Vector2(e.DoorX, e.DoorZ).DistanceTo(toward))!;
        layout.Entrances.Remove(main);
        layout.Entrances.Insert(0, main);
        layout.DoorX = main.DoorX; layout.DoorY = main.DoorY; layout.DoorZ = main.DoorZ;
        layout.DoorOutX = main.DoorOutX; layout.DoorOutZ = main.DoorOutZ;
        layout.DoorWidth = main.DoorWidth > 0 ? main.DoorWidth : main.Width;
        layout.DoorHeight = main.DoorHeight;
        layout.EntryX = main.X;
        layout.EntryWidth = main.Width;

        // ---- the tower above the porch ---------------------------------------------------------
        float ridge = hallB.MaxY - hallB.MinY - 0.8f;
        for (int f = 1; f < floors; f++)
        {
            var r = climb!.Rect;
            var floor = new FloorPlan();
            bool belfry = f == floors - 1;
            var room = new RoomPlan
            {
                X0 = r.X0, Z0 = r.Z0, X1 = r.X1, Z1 = r.Z1,
                Type = belfry ? RoomType.Belfry : RoomType.Landing, Span = belfry ? 2 : 1,
            };
            floor.Rooms.Add(room);
            bool holeA = (f - 1) % 2 == 0;
            floor.Holes.Add(holeA
                ? new RectPlan(Math.Min(laneA0, laneA1), runZ0, Math.Max(laneA0, laneA1), runZ1)
                : new RectPlan(Math.Min(laneB0, laneB1), runZ0, Math.Max(laneB0, laneB1), runZ1));
            // lane A lies against the wall; the edge it shares with lane B, and lane B's open edge
            float shared = lanesLeft ? laneA1 : laneA0, open = lanesLeft ? laneB1 : laneB0;
            floor.Rails.Add(new RectPlan(shared, runZ0, shared, runZ1));
            if (!holeA) floor.Rails.Add(new RectPlan(open, runZ0, open, runZ1));

            // openings: slits up the shaft, the bell chamber open on every side above the roofs
            float roomClear = layout.ClearOf(room);
            for (int s = 0; s < 4; s++)
            {
                var side = (Side)s;
                if (side == Opposite(climb.Toward) && f * h < ridge) continue; // against the nave
                float a = side is Side.Front or Side.Back ? r.X0 : r.Z0;
                float b = side is Side.Front or Side.Back ? r.X1 : r.Z1;
                float w = belfry ? Math.Min(1.3f, b - a - 1.4f) : 0.35f;
                if (w < 0.3f) continue;
                room.Openings.Add(new OpeningPlan
                {
                    Side = side, Center = (a + b) / 2, Width = w,
                    Bottom = belfry ? 0.9f : 1.2f, Top = belfry ? roomClear - 0.4f : Math.Min(2.2f, roomClear - 0.3f),
                    Kind = OpeningKind.Window,
                });
            }
            layout.Floors.Add(floor);
        }
        for (int f = 0; f < floors - 1; f++)
            layout.Floors[f].Flight = f % 2 == 0
                ? new FlightPlan { X0 = Math.Min(laneA0, laneA1), X1 = Math.Max(laneA0, laneA1), ZBottom = runZ0, ZTop = runZ1 }
                : new FlightPlan { X0 = Math.Min(laneB0, laneB1), X1 = Math.Max(laneB0, laneB1), ZBottom = runZ1, ZTop = runZ0 };

        // ---- windows ---------------------------------------------------------------------------
        float naveClear = layout.ClearOf(naveRoom);
        foreach (var side in new[] { Side.Left, Side.Right })
            foreach (var (a, b) in spans.Free(0, side))
            {
                const float w = 1.1f, spacing = 3.6f;
                int count = (int)((b - a) / spacing);
                if (count == 0 && b - a >= w + 0.4f) count = 1;
                for (int k = 0; k < count; k++)
                    naveRoom.Openings.Add(new OpeningPlan
                    {
                        Side = side, Center = a + (b - a) * (k + 0.5f) / count, Width = w,
                        Bottom = 2.4f, Top = Math.Max(4.0f, naveClear - 1.4f), Kind = OpeningKind.Window,
                    });
            }
        // a tall window over the door end, two lancets either side of the cross
        if (naveClear >= 6f && spans.Fit(0, Side.Front, (nave.X0 + nave.X1) / 2, 1.6f) is { } fc)
            naveRoom.Openings.Add(new OpeningPlan
            {
                Side = Side.Front, Center = fc, Width = 1.6f, Bottom = naveClear - 3.6f, Top = naveClear - 1.0f, Kind = OpeningKind.Window,
            });
        float mid = (nave.X0 + nave.X1) / 2;
        foreach (float dx in new[] { -1.5f, 1.5f })
            if (spans.Fit(0, Side.Back, mid + dx, 0.7f) is { } bc && Math.Abs(bc - (mid + dx)) < 0.05f)
                naveRoom.Openings.Add(new OpeningPlan
                {
                    Side = Side.Back, Center = bc, Width = 0.7f, Bottom = 2.8f, Top = Math.Max(4.2f, naveClear - 1.2f), Kind = OpeningKind.Window,
                });
        foreach (var t in towers)
        {
            var porch = ground.Rooms[t.Room];
            for (int s = 0; s < 4; s++)
            {
                var side = (Side)s;
                float a = side is Side.Front or Side.Back ? porch.X0 : porch.Z0;
                float b = side is Side.Front or Side.Back ? porch.X1 : porch.Z1;
                if (spans.Fit(t.Room, side, (a + b) / 2, 0.4f) is { } c)
                    porch.Openings.Add(new OpeningPlan
                    {
                        Side = side, Center = c, Width = 0.4f, Bottom = 1.3f, Top = Math.Min(2.3f, clear - 0.3f), Kind = OpeningKind.Window,
                    });
            }
        }

        FurnishChurch(layout, naveRoom, climb, lanesLeft ? laneB1 : laneB0, lanesLeft);
        return layout;
    }

    /// <summary>
    /// Snaps a tower's plan onto the nave wall it stands against — closing a survey gap, or
    /// trimming an overlap — and records which side that is. False when what is left is too
    /// small for a room or shares too little wall for an arch.
    /// </summary>
    private static bool Attach(RectPlan nave, Part t)
    {
        var r = t.Rect;
        float cx = (r.X0 + r.X1) / 2, cz = (r.Z0 + r.Z1) / 2;
        float nx = (nave.X0 + nave.X1) / 2, nz = (nave.Z0 + nave.Z1) / 2;
        float outX = Math.Abs(cx - nx) - (nave.X1 - nave.X0) / 2;
        float outZ = Math.Abs(cz - nz) - (nave.Z1 - nave.Z0) / 2;
        float shared;
        if (outZ >= outX)
        {
            if (cz < nz) { t.Toward = Side.Front; r.Z1 = nave.Z0; }
            else { t.Toward = Side.Back; r.Z0 = nave.Z1; }
            if (r.Z1 - r.Z0 < 2.5f) return false;
            shared = Math.Min(r.X1, nave.X1) - Math.Max(r.X0, nave.X0);
        }
        else
        {
            if (cx < nx) { t.Toward = Side.Left; r.X1 = nave.X0; }
            else { t.Toward = Side.Right; r.X0 = nave.X1; }
            if (r.X1 - r.X0 < 2.5f) return false;
            shared = Math.Min(r.Z1, nave.Z1) - Math.Max(r.Z0, nave.Z0);
        }
        return shared >= 1.5f;
    }

    /// <summary>Whether a switchback of storey <paramref name="h"/> fits the tower, no steeper than a player can climb.</summary>
    private static bool StairFits(RectPlan r, float h)
    {
        if (r.X1 - r.X0 < 2 * LaneWidth + StairWalkway) return false;
        float run = r.Z1 - r.Z0 - 2 * StairLanding;
        if (run <= 0) return false;
        int steps = (int)MathF.Ceiling(h / 0.19f);
        float tread = Math.Min(0.27f, run / steps);
        return Mathf.RadToDeg(MathF.Atan(h / (tread * (steps + 1)))) <= MaxStairSlopeDeg;
    }

    private static void FurnishChurch(InteriorLayout l, RoomPlan nave, Part? climb, float walkEdge, bool lanesLeft)
    {
        const float inset = WallInset + 0.02f;
        var placed = new List<RectPlan>();
        var blocked = nave.Openings.Where(o => o.Kind != OpeningKind.Window).Select(o => Clearance(nave, o)).ToList();
        // arriving through a door must not put anyone inside a pew
        foreach (var o in nave.Openings.Where(o => o.Kind is OpeningKind.Entry or OpeningKind.Arch))
            blocked.Add(Grow(Clearance(nave, o), 0.6f));

        float x0 = nave.X0 + inset, x1 = nave.X1 - inset, z1 = nave.Z1 - inset;
        float width = x1 - x0, cx = (x0 + x1) / 2;

        // the chancel: a step across the altar end, as deep as the nave allows
        float daisH = 0.3f;
        float chancel = Math.Clamp((nave.Z1 - nave.Z0) * 0.22f, 2.4f, 5.0f);
        float daisW = Math.Min(width - 1.2f, 9f);
        float daisZ0 = z1;
        for (; chancel >= 2.0f; chancel -= 0.4f)
        {
            var rect = new RectPlan(cx - daisW / 2, z1 - chancel, cx + daisW / 2, z1);
            if (daisW < 2.4f || !Free(nave, rect, placed, blocked, 0)) continue;
            Add(l, 0, new Piece(FurnitureType.Dais, daisW, chancel, daisH, false), rect, 0, placed);
            daisZ0 = rect.Z0;
            break;
        }
        bool hasDais = daisZ0 < z1;
        float lift = hasDais ? daisH : 0;

        // altar a pace from the east wall, the cross on it, the lectern at the step
        float altarW = Math.Min(2.2f, Math.Max(1.2f, daisW * 0.3f));
        float az = z1 - 1.0f - 0.45f;
        var altar = new RectPlan(cx - altarW / 2, az - 0.45f, cx + altarW / 2, az + 0.45f);
        if (Free(nave, altar, placed.Where(p => !hasDais || p != placed[0]).ToList(), blocked, 0))
            AddLifted(l, 0, new Piece(FurnitureType.Altar, altarW, 0.9f, 1.0f, false), altar, 2, placed, lift);
        float crossLift = Math.Min(l.ClearOf(nave) - 3.0f, 2.8f);
        if (crossLift > 1.8f)
            AddLifted(l, 0, new Piece(FurnitureType.Cross, 1.2f, 0.12f, 2.4f, false),
                new RectPlan(cx - 0.6f, z1 - 0.12f, cx + 0.6f, z1), 2, new List<RectPlan>(), crossLift);
        if (hasDais)
        {
            float lx = cx - Math.Min(daisW / 2 - 0.6f, width * 0.28f);
            var lectern = new RectPlan(lx - 0.3f, daisZ0 - 0.95f, lx + 0.3f, daisZ0 - 0.45f);
            if (Free(nave, lectern, placed, blocked, 0.05f))
                Add(l, 0, new Piece(FurnitureType.Lectern, 0.6f, 0.5f, 1.15f, false), lectern, 2, placed);
        }

        // the pastor rat (#241), blessing the congregation: beside the altar, else at the step
        var off = placed.Where(p => !hasDais || p != placed[0]).ToList();
        float rw = RatPiece.W, rd = RatPiece.D;
        var spots = new List<(float X, float Z, bool Up)>();
        foreach (float side in new[] { 1f, -1f })
            spots.Add((cx + side * (altarW / 2 + 0.3f + rw / 2), az, hasDais));
        if (hasDais) spots.Add((cx + Math.Min(daisW / 2 - 0.6f, width * 0.28f), daisZ0 - 0.4f - rd / 2, false));
        spots.Add((cx, az - 1.2f, hasDais));
        foreach (var (rx, rz, up) in spots)
        {
            var rat = new RectPlan(rx - rw / 2, rz - rd / 2, rx + rw / 2, rz + rd / 2);
            // on the step or clear of it, never half on it
            var dais = hasDais ? placed[0] : null;
            if (dais != null && (up ? !Inside(rat, dais) : rat.Overlaps(dais))) continue;
            if (!Free(nave, rat, off, blocked, 0.05f)) continue;
            AddLifted(l, 0, RatPiece, rat, 2, placed, up ? lift : 0);
            break;
        }
        RadioByRat(l, 0, nave, placed, off, blocked, hasDais ? placed[0] : null);

        // pews in two blocks either side of the aisle, facing the altar
        const float aisle = 1.6f, sideAisle = 0.9f, pewD = 0.55f, pitch = 1.05f;
        float pewW = Math.Min(4.5f, (width - aisle - 2 * sideAisle) / 2);
        var columns = new List<float>();
        if (pewW >= 1.2f) columns.AddRange(new[] { cx - aisle / 2 - pewW / 2, cx + aisle / 2 + pewW / 2 });
        else if (width - aisle - sideAisle >= 1.2f)
        {
            pewW = Math.Min(4.5f, width - aisle - sideAisle);
            columns.Add(x0 + sideAisle + pewW / 2);
        }
        float zStart = nave.Z0 + inset + 2.4f, zEnd = (hasDais ? daisZ0 : z1 - 2.5f) - 1.6f;
        for (float z = zStart; z + pewD <= zEnd; z += pitch)
            foreach (float x in columns)
            {
                var rect = new RectPlan(x - pewW / 2, z, x + pewW / 2, z + pewD);
                if (Free(nave, rect, placed, blocked, 0.05f))
                    Add(l, 0, new Piece(FurnitureType.Pew, pewW, pewD, 0.9f, false), rect, 0, placed);
            }
        FillFrontPews(l, 0);

        // the bell, hung over the stair-free half of the chamber
        if (climb != null && l.Floors.Count > 1)
        {
            int top = l.Floors.Count - 1;
            var room = l.Floors[top].Rooms[0];
            float wx0 = lanesLeft ? walkEdge : room.X0, wx1 = lanesLeft ? room.X1 : walkEdge;
            float size = Math.Min(1.1f, wx1 - wx0 - 0.3f);
            if (size >= 0.6f)
            {
                float bx = (wx0 + wx1) / 2, bz = (room.Z0 + room.Z1) / 2;
                AddLifted(l, top, new Piece(FurnitureType.Bell, size, size, size, false),
                    new RectPlan(bx - size / 2, bz - size / 2, bx + size / 2, bz + size / 2), 0, new List<RectPlan>(),
                    Math.Max(2.1f, l.ClearOf(room) - size - 0.4f));
            }
        }
    }

    private static bool Inside(RectPlan a, RectPlan b) => a.X0 >= b.X0 && a.X1 <= b.X1 && a.Z0 >= b.Z0 && a.Z1 <= b.Z1;

    private static RectPlan Grow(RectPlan r, float by) => new(r.X0 - by, r.Z0 - by, r.X1 + by, r.Z1 + by);

    private static void AddLifted(InteriorLayout l, int f, Piece p, RectPlan rect, int turns, List<RectPlan> placed, float lift)
    {
        Add(l, f, p, rect, turns, placed);
        l.Furniture[^1].Lift = lift;
    }

    /// <summary>
    /// Free stretches of each ground-floor wall, for doors and windows to go in: the whole wall
    /// less its corners, less what stands against it (a porch, a stair) and what is already cut.
    /// Along-wall coordinates are X for front/back walls, Z for side walls.
    /// </summary>
    private sealed class WallSpans
    {
        private readonly Dictionary<(int, Side), List<(float A, float B)>> _free = new();

        public void InitRoom(int room, RoomPlan r)
        {
            for (int s = 0; s < 4; s++)
            {
                var side = (Side)s;
                float a = (side is Side.Front or Side.Back ? r.X0 : r.Z0) + CornerMargin;
                float b = (side is Side.Front or Side.Back ? r.X1 : r.Z1) - CornerMargin;
                _free[(room, side)] = b > a ? new List<(float, float)> { (a, b) } : new List<(float, float)>();
            }
        }

        public void Remove(int room, Side side) => _free[(room, side)] = new List<(float, float)>();

        public void Cut(int room, Side side, float a, float b)
        {
            if (!_free.TryGetValue((room, side), out var list)) return;
            var next = new List<(float, float)>();
            foreach (var (x0, x1) in list)
            {
                if (b <= x0 || a >= x1) { next.Add((x0, x1)); continue; }
                if (a > x0) next.Add((x0, a));
                if (b < x1) next.Add((b, x1));
            }
            _free[(room, side)] = next;
        }

        public List<(float A, float B)> Free(int room, Side side) =>
            _free.TryGetValue((room, side), out var list) ? list : new List<(float, float)>();

        /// <summary>Centre nearest <paramref name="want"/> for an opening <paramref name="width"/> wide, if any stretch takes it.</summary>
        public float? Fit(int room, Side side, float want, float width)
        {
            float? best = null;
            foreach (var (a, b) in Free(room, side))
            {
                if (b - a < width) continue;
                float c = Math.Clamp(want, a + width / 2, b - width / 2);
                if (best == null || Math.Abs(c - want) < Math.Abs(best.Value - want)) best = c;
            }
            return best;
        }
    }
}
