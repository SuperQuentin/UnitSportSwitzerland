using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A block of flats whose outline is an L, a U or a ring round a courtyard (#577), planned as the
/// wings <see cref="PlanOutline"/> finds in it instead of one box.
///
/// <para>
/// Each wing is planned on its own by <see cref="TryBlock"/>, turned so that its front faces the
/// way its street door does, then turned back into the building's frame and merged. A wing with
/// no street door faces the wing it joins instead: that wing runs a corridor to the point where
/// they meet (off its back landing to its end wall, or a spine to its back wall), the doorless
/// wing's stairwell lobby is there, and the two open into each other through an arch. Every wing
/// has the same floors, and the same basement count. A window on a wall another wing stands
/// against is dropped; a wall onto the courtyard is a facade and keeps its windows.
/// </para>
///
/// <para>
/// An outline at other angles (a trapezoid, a bent or skewed block, #598) peels into one wing, its
/// largest rectangle, planned alone: every street door goes to the wing wall facing its way.
/// Anything that does not come out right (a wing too small for a stairwell, a link that cannot
/// meet) falls back to the whole box, as before #577; <see cref="WingFailure"/> says why.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>The slot a link between two wings borrows as a door while they are planned; never a real door's.</summary>
    private const int LinkSlot = 60;

    private sealed class WingPlan
    {
        public RectPlan R = null!;
        public int Index;
        /// <summary>Its front, as a side of the building's frame, and the quarter turns from its own frame to that.</summary>
        public Side Front;
        public int Turns;
        public readonly List<DoorSpot> Doors = new();
        public DoorSpot? Main;
        /// <summary>Wings joining it off a side other than its front: (side in the building's frame, where along it).</summary>
        public readonly List<(Side Side, float At, int Link)> Links = new();
        public InteriorLayout Sub = null!;
        public float CX => (R.X0 + R.X1) / 2;
        public float CZ => (R.Z0 + R.Z1) / 2;
        public float W => Turns % 2 == 0 ? R.X1 - R.X0 : R.Z1 - R.Z0;
        public float D => Turns % 2 == 0 ? R.Z1 - R.Z0 : R.X1 - R.X0;
    }

    private sealed record Link(int A, int B, Side ASide, float At);

    /// <summary>The planner's door: the building's real outline if it has wings (#577), else the box.</summary>
    internal static bool TryApartments(InteriorLayout l, Footprint fp, Building b, BuildingKind kind, int above, BuildingType type, Random rng)
    {
        var wings = PlanOutline.Wings(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);
        if (wings is { Count: >= 1 })
        {
            var (t, below, x, width) = (l.Type, l.Below, l.EntryX, l.EntryWidth);
            if (TryWings(l, fp, kind, above, type, wings)) return true;
            // what a wing plan that came apart left behind, gone before the box is planned
            (l.Type, l.Below, l.EntryX, l.EntryWidth) = (t, below, x, width);
            l.Floors.Clear();
            l.Lifts.Clear();
            l.InnerDoors.Clear();
            l.Entrances.Clear();
        }
        return TryBlock(l, fp, kind, above, type, rng, new AptOptions());
    }

    // ---- frames ------------------------------------------------------------------------------

    /// <summary>A quarter turn k times: (x, z) to (-z, x), which takes a wing's front (-Z) to the right (+X).</summary>
    private static (float X, float Z) Turn(int k, float x, float z) => (k & 3) switch
    {
        0 => (x, z),
        1 => (-z, x),
        2 => (-x, -z),
        _ => (z, -x),
    };

    /// <summary>A side turned k quarter turns: the sides are numbered round, Front, Right, Back, Left.</summary>
    private static Side TurnSide(Side s, int k) => (Side)(((int)s + k + 4) % 4);

    private static (float X, float Z) Dir(Side s) => s switch
    {
        Side.Front => (0, -1),
        Side.Right => (1, 0),
        Side.Back => (0, 1),
        _ => (-1, 0),
    };

    /// <summary>A wing's own point in the building's frame.</summary>
    private static (float X, float Z) ToBox(WingPlan w, float x, float z)
    {
        var (rx, rz) = Turn(w.Turns, x, z);
        return (rx + w.CX, rz + w.CZ);
    }

    /// <summary>A point of the building's frame in a wing's own.</summary>
    private static (float X, float Z) ToWing(WingPlan w, float x, float z) => Turn(4 - w.Turns, x - w.CX, z - w.CZ);

    private static RectPlan ToBox(WingPlan w, float x0, float z0, float x1, float z1)
    {
        var a = ToBox(w, x0, z0);
        var b = ToBox(w, x1, z1);
        return new RectPlan(Math.Min(a.X, b.X), Math.Min(a.Z, b.Z), Math.Max(a.X, b.X), Math.Max(a.Z, b.Z));
    }

    /// <summary>The point of a room's wall an opening's centre names.</summary>
    private static (float X, float Z) WallPoint(RoomPlan r, Side s, float center) => s switch
    {
        Side.Front => (center, r.Z0),
        Side.Back => (center, r.Z1),
        Side.Left => (r.X0, center),
        _ => (r.X1, center),
    };

    /// <summary>An opening's centre once its room is turned: the wall point taken across, read along the new wall.</summary>
    private static float ToBoxCenter(WingPlan w, RoomPlan subRoom, Side s, float center)
    {
        var (px, pz) = WallPoint(subRoom, s, center);
        var (bx, bz) = ToBox(w, px, pz);
        return AlongX(TurnSide(s, w.Turns)) ? bx : bz;
    }

    /// <summary>Where two wings meet: the side of <paramref name="a"/> that <paramref name="b"/> stands against, and the stretch.</summary>
    private static (Side Side, float Lo, float Hi)? Shared(RectPlan a, RectPlan b)
    {
        const float eps = 0.05f;
        if (Math.Abs(a.Z1 - b.Z0) < eps) return (Side.Back, Math.Max(a.X0, b.X0), Math.Min(a.X1, b.X1));
        if (Math.Abs(a.Z0 - b.Z1) < eps) return (Side.Front, Math.Max(a.X0, b.X0), Math.Min(a.X1, b.X1));
        if (Math.Abs(a.X1 - b.X0) < eps) return (Side.Right, Math.Max(a.Z0, b.Z0), Math.Min(a.Z1, b.Z1));
        if (Math.Abs(a.X0 - b.X1) < eps) return (Side.Left, Math.Max(a.Z0, b.Z0), Math.Min(a.Z1, b.Z1));
        return null;
    }

    // ---- planning --------------------------------------------------------------------------------

    /// <summary>
    /// Why the last wing plan on this thread fell back to the box, or null if it did not (for
    /// checks: <c>--shapedcheck</c> prints it).
    /// </summary>
    [ThreadStatic] internal static string? WingFailure;

    private static bool Fail(string why)
    {
        WingFailure = why;
        return false;
    }

    private static bool TryWings(InteriorLayout l, Footprint fp, BuildingKind kind, int above, BuildingType type, List<RectPlan> rects)
    {
        WingFailure = null;
        var axisU = fp.AxisU;
        var axisV = fp.AxisV;
        (float X, float Z) Local(Vector3 p)
        {
            var q = new Vector2(p.X - fp.Center.X, p.Z - fp.Center.Y);
            return (q.Dot(axisU), q.Dot(axisV));
        }
        Vector3 World(float x, float z, float y)
        {
            var q = fp.Center + axisU * x + axisV * z;
            return new Vector3(q.X, y, q.Y);
        }
        Vector3 WorldDir(Side s)
        {
            var (dx, dz) = Dir(s);
            var q = axisU * dx + axisV * dz;
            return new Vector3(q.X, 0, q.Y);
        }
        Side Facing(Vector3 outward)
        {
            var o = new Vector2(outward.X, outward.Z);
            float ox = o.Dot(axisU), oz = o.Dot(axisV);
            return Math.Abs(ox) > Math.Abs(oz) ? (ox > 0 ? Side.Right : Side.Left) : (oz > 0 ? Side.Back : Side.Front);
        }

        var wings = rects.Select((r, i) => new WingPlan { R = r, Index = i }).ToList();

        // ---- each street door to the wing whose wall it is on -------------------------------------
        foreach (var d in fp.Doors)
        {
            var (x, z) = Local(d.Position);
            var side = Facing(d.Outward);
            WingPlan? best = null;
            // a lone wing (an outline at other angles, #598) takes every door, wherever on its
            // slanted walls it stands: the wing's own wall facing the same way is its doorway
            bool lone = wings.Count == 1;
            float bestD = lone ? float.MaxValue : 2.0f;
            foreach (var w in wings)
            {
                var R = w.R;
                float edge = side switch { Side.Front => R.Z0, Side.Back => R.Z1, Side.Left => R.X0, _ => R.X1 };
                float across = AlongX(side) ? z : x, along = AlongX(side) ? x : z;
                float lo = AlongX(side) ? R.X0 : R.Z0, hi = AlongX(side) ? R.X1 : R.Z1;
                if (!lone && (along < lo - 0.5f || along > hi + 0.5f)) continue;
                float dist = Math.Abs(edge - across);
                if (dist < bestD) { bestD = dist; best = w; }
            }
            if (best == null) continue;
            best.Doors.Add(d);
            if (best.Main == null || d.Slot < best.Main.Value.Slot) { best.Main = d; best.Front = side; }
        }
        if (!wings.Any(w => w.Main is { Slot: 0 })) return Fail("no street door on a wing");

        // ---- a wing with no door of its own is reached through the next one ------------------------
        var reached = wings.Where(w => w.Main != null).ToHashSet();
        var links = new List<Link>();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var bw in wings.Where(w => !reached.Contains(w)).ToList())
            {
                (WingPlan A, Side S, float Lo, float Hi)? join = null;
                foreach (var aw in reached)
                    if (Shared(aw.R, bw.R) is { } sh && sh.Hi - sh.Lo >= 3.5f && (join == null || sh.Hi - sh.Lo > join.Value.Hi - join.Value.Lo))
                        join = (aw, sh.Side, sh.Lo, sh.Hi);
                if (join is not { } j) continue;
                links.Add(new Link(j.A.Index, bw.Index, j.S, (j.Lo + j.Hi) / 2));
                bw.Front = TurnSide(j.S, 2);
                reached.Add(bw);
                grew = true;
            }
        }
        // a wing nothing reaches is left out: the inside is a little smaller than the outline
        wings.RemoveAll(w => !reached.Contains(w));
        if (wings.Count == 0) return Fail("no wing reached");
        foreach (var w in wings) w.Turns = (int)w.Front;

        // where each link meets: a door of the wing it joins if that is its front, the corridor off
        // its back landing if it is an end of it (that corridor's height in the wing is fixed), else
        // the middle of the shared wall
        bool mixed = type == BuildingType.MixedUse;
        float area = wings.Sum(w => (w.R.X1 - w.R.X0) * (w.R.Z1 - w.R.Z0));
        // a garage (#694) has the basement it leads to
        int below = fp.Doors.Any(d => d.Vehicle && d.Width > 0 && d.Link.Any) ? 1 : AptBasement(l.Key, mixed, above, area);
        int floors = above + below;
        for (int i = 0; i < links.Count; i++)
        {
            var k = links[i];
            var aw = wings.First(w => w.Index == k.A);
            var bw = wings.First(w => w.Index == k.B);
            var sh = Shared(aw.R, bw.R)!.Value;
            float at = k.At;
            var subSide = TurnSide(k.ASide, -aw.Turns);
            if (subSide is Side.Left or Side.Right && BackLandingOf(aw.W, aw.D, l.StoreyHeight, floors) is { } rows)
            {
                float zc = (rows.ZM + rows.ZB1) / 2;
                var (bx, bz) = ToBox(aw, subSide == Side.Left ? -aw.W / 2 : aw.W / 2, zc);
                float along = AlongX(k.ASide) ? bx : bz;
                if (along >= sh.Lo + 1.2f && along <= sh.Hi - 1.2f) at = along;
            }
            k = links[i] = k with { At = at };
            // the joining wing's front door is the link: where the joined wing's doorway for it
            // comes out, once that wing is planned
            var (lx, lz) = k.ASide switch
            {
                Side.Front => (at, aw.R.Z0),
                Side.Back => (at, aw.R.Z1),
                Side.Left => (aw.R.X0, at),
                _ => (aw.R.X1, at),
            };
            var main = fp.Door;
            aw.Links.Add((k.ASide, at, i));
            if (subSide == Side.Front)
                aw.Doors.Add(new DoorSpot(main.Index, World(lx, lz, main.Position.Y), WorldDir(k.ASide), 1.2f, 2.1f)
                {
                    Slot = LinkSlot + i, Hang = DoorHang.Inward, Vehicle = false, Kind = main.Kind,
                });
        }

        // ---- each wing planned as a block of its own, a joined wing before the one joining it -------
        var order = wings.Where(w => w.Main != null).Concat(links.Select(k => wings.First(w => w.Index == k.B))).Distinct().ToList();
        foreach (var w in order)
        {
            if (w.Main == null)
            {
                int li = links.FindIndex(k => k.B == w.Index);
                var aw = wings.First(x => x.Index == links[li].A);
                string key = new DoorKey(fp.Key, LinkSlot + li).ToString();
                if (aw.Sub.AllEntrances().FirstOrDefault(e => e.Door == key) is not { } e) return Fail($"link {li}: no doorway in wing {aw.Index}");
                var (lx, lz) = ToBox(aw, e.X, e.Z);
                w.Main = new DoorSpot(fp.Door.Index, World(lx, lz, fp.Door.Position.Y), WorldDir(w.Front), 1.2f, 2.1f)
                {
                    Slot = LinkSlot + li, Hang = DoorHang.Inward, Vehicle = false, Kind = fp.Door.Kind,
                };
            }
            var c = World(w.CX, w.CZ, 0);
            var (ux, uz) = Turn(w.Turns, 1, 0);
            var u = axisU * ux + axisV * uz;
            var main = w.Main!.Value;
            var extra = w.Doors.Where(d => d.Slot != main.Slot).ToList();
            // a link off a side other than the front is a door there too: the corridor's end gets its doorway
            foreach (var (side, at, li) in w.Links)
            {
                if (TurnSide(side, -w.Turns) == Side.Front) continue;
                var (lx, lz) = side switch
                {
                    Side.Front => (at, w.R.Z0),
                    Side.Back => (at, w.R.Z1),
                    Side.Left => (w.R.X0, at),
                    _ => (w.R.X1, at),
                };
                extra.Add(new DoorSpot(main.Index, World(lx, lz, main.Position.Y), WorldDir(side), 1.2f, 2.1f)
                {
                    Slot = LinkSlot + li, Hang = DoorHang.Inward, Vehicle = false, Kind = main.Kind,
                });
            }
            var sub = new Footprint(fp.Key, fp.Kind, new Vector2(c.X, c.Z), u, w.W, w.D, main) { Extra = extra };
            var sl = new InteriorLayout
            {
                Key = l.Key, Kind = l.Kind, Width = w.W, Depth = w.D, StoreyHeight = l.StoreyHeight,
                EntryX = sub.EntryX, EntryWidth = Math.Min(main.Width, 1.8f), DoorHeight = main.Height,
            };

            // what the wing is told: the basement, where corridors must run, what is no facade
            // a wing entered from the next one keeps its stairwell where that one's corridor meets it
            var opts = new AptOptions { Below = below, Pinned = main.Slot >= LinkSlot };
            foreach (var (side, at, _) in w.Links)
            {
                var s = TurnSide(side, -w.Turns);
                if (s == Side.Front) continue;
                var (lx, lz) = side switch
                {
                    Side.Front => (at, w.R.Z0),
                    Side.Back => (at, w.R.Z1),
                    Side.Left => (w.R.X0, at),
                    _ => (w.R.X1, at),
                };
                var (sx, sz) = ToWing(w, lx, lz);
                opts.Links.Add((s, AlongX(s) ? sx : sz));
            }
            var blocked = new List<(Side Side, float Lo, float Hi)>();
            foreach (var o in wings)
            {
                if (o == w || Shared(w.R, o.R) is not { } sh || sh.Hi <= sh.Lo) continue;
                var (ax0, az0) = sh.Side switch
                {
                    Side.Front => (sh.Lo, w.R.Z0), Side.Back => (sh.Lo, w.R.Z1),
                    Side.Left => (w.R.X0, sh.Lo), _ => (w.R.X1, sh.Lo),
                };
                var (ax1, az1) = sh.Side switch
                {
                    Side.Front => (sh.Hi, w.R.Z0), Side.Back => (sh.Hi, w.R.Z1),
                    Side.Left => (w.R.X0, sh.Hi), _ => (w.R.X1, sh.Hi),
                };
                var s = TurnSide(sh.Side, -w.Turns);
                var p0 = ToWing(w, ax0, az0);
                var p1 = ToWing(w, ax1, az1);
                float a0 = AlongX(s) ? p0.X : p0.Z, a1 = AlongX(s) ? p1.X : p1.Z;
                blocked.Add((s, Math.Min(a0, a1), Math.Max(a0, a1)));
            }
            // the wall that faces out is what the next wing does not stand against
            opts.Free = (s, lo, hi) =>
                hi - lo - blocked.Where(x => x.Side == s).Sum(x => Math.Max(0, Math.Min(hi, x.Hi) - Math.Max(lo, x.Lo)));
            if (!TryBlock(sl, sub, kind, above, type, new Random(StableHash(l.Key + "|wing" + w.Index)), opts)) return Fail($"wing {w.Index} ({w.W:F1} x {w.D:F1}, front {w.Front}) does not plan");
            w.Sub = sl;
        }
        if (wings.Select(w => w.Sub.Floors.Count).Distinct().Count() != 1) return Fail("wings of different floor counts");

        // ---- merged into the building's frame ----------------------------------------------------
        l.Type = type;
        l.Below = below;
        l.Floors.Clear();
        l.Lifts.Clear();
        l.InnerDoors.Clear();
        l.Entrances.Clear();
        int count = wings[0].Sub.Floors.Count;
        for (int f = 0; f < count; f++) l.Floors.Add(new FloorPlan());
        foreach (var w in wings) Merge(l, w);

        // ---- each link's two doorways become one arch -------------------------------------------
        var ground = l.GroundFloor;
        for (int i = 0; i < links.Count; i++)
        {
            string key = new DoorKey(fp.Key, LinkSlot + i).ToString();
            var ends = l.Entrances.Where(e => e.Door == key).ToList();
            if (ends.Count != 2) return Fail($"link {i}: {ends.Count} doorways found");
            if (!Join(ground, ends[0], ends[1], Math.Min(2.2f, l.StoreyHeight - Slab - 0.2f))) return false;
        }
        l.Entrances.RemoveAll(e => DoorKey.TryParse(e.Door, out var dk) && dk.Slot >= LinkSlot);
        if (l.Entrances.Count == 0) return Fail("no street doorway left");
        var front = l.Entrances[0];
        l.EntryX = front.X;
        l.EntryWidth = front.Width;

        // ---- no window onto the next wing --------------------------------------------------------
        // a window whose wall the next wing stands against slides to the part that faces out, or goes
        bool Against(RoomPlan r, Side side, float c, float half)
        {
            var (dx, dz) = Dir(side);
            foreach (float t in new[] { -half, 0, half })
            {
                var (px, pz) = WallPoint(r, side, c + t);
                float qx = px + dx * 0.3f, qz = pz + dz * 0.3f;
                if (wings.Any(w => qx > w.R.X0 && qx < w.R.X1 && qz > w.R.Z0 && qz < w.R.Z1)) return true;
            }
            return false;
        }
        foreach (var floor in l.Floors)
            foreach (var r in floor.Rooms)
                for (int k = r.Openings.Count - 1; k >= 0; k--)
                {
                    var o = r.Openings[k];
                    if (o.Kind != OpeningKind.Window || !Against(r, o.Side, o.Center, o.Width / 2)) continue;
                    float lo = (AlongX(o.Side) ? r.X0 : r.Z0) + o.Width / 2 + 0.2f, hi = (AlongX(o.Side) ? r.X1 : r.Z1) - o.Width / 2 - 0.2f;
                    float? moved = null;
                    for (float step = 0.25f; moved == null && step < hi - lo + 0.25f; step += 0.25f)
                        foreach (float c in new[] { o.Center - step, o.Center + step })
                            if (c >= lo && c <= hi && !Against(r, o.Side, c, o.Width / 2)
                                && !r.Openings.Any(q => q != o && q.Side == o.Side && Math.Abs(q.Center - c) < (q.Width + o.Width) / 2 + 0.3f))
                            { moved = c; break; }
                    if (moved is { } m) o.Center = m;
                    else r.Openings.RemoveAt(k);
                }

        // a plan that does not hold together is dropped: the caller plans the box instead
        var errs = InteriorValidator.Validate(l);
        return errs.Count == 0 || Fail("invalid: " + string.Join("; ", errs.Take(3)));
    }

    /// <summary>One wing's plan, turned and moved into the building's frame, its rooms after those already there.</summary>
    private static void Merge(InteriorLayout l, WingPlan w)
    {
        var s = w.Sub;
        var offsets = new int[s.Floors.Count];
        for (int f = 0; f < s.Floors.Count; f++)
        {
            var into = l.Floors[f];
            var from = s.Floors[f];
            int off = offsets[f] = into.Rooms.Count;
            foreach (var r in from.Rooms)
            {
                var m = ToBox(w, r.X0, r.Z0, r.X1, r.Z1);
                var room = new RoomPlan
                {
                    X0 = m.X0, Z0 = m.Z0, X1 = m.X1, Z1 = m.Z1, Type = r.Type, Unit = r.Unit < 0 ? -1 : r.Unit + 1000 * w.Index,
                    Span = r.Span, Clear = r.Clear,
                };
                foreach (var o in r.Openings)
                    room.Openings.Add(new OpeningPlan
                    {
                        Side = TurnSide(o.Side, w.Turns), Center = ToBoxCenter(w, r, o.Side, o.Center), Width = o.Width,
                        Bottom = o.Bottom, Top = o.Top, Kind = o.Kind, Other = o.Other < 0 ? -1 : o.Other + off,
                    });
                into.Rooms.Add(room);
            }
            foreach (var h in from.Holes) into.Holes.Add(ToBox(w, h.X0, h.Z0, h.X1, h.Z1));
            foreach (var g in from.Landings)
            {
                var m = ToBox(w, g.X0, g.Z0, g.X1, g.Z1);
                into.Landings.Add(new LandingPlan { X0 = m.X0, Z0 = m.Z0, X1 = m.X1, Z1 = m.Z1, Level = g.Level });
            }
            foreach (var rail in from.Rails)
            {
                var a = ToBox(w, rail.X0, rail.Z0);
                var b = ToBox(w, rail.X1, rail.Z1);
                into.Rails.Add(new RectPlan(a.X, a.Z, b.X, b.Z));
            }
            foreach (var fl in from.AllFlights())
            {
                var t = TurnFlight(w, fl);
                if (into.Flight == null) into.Flight = t; else into.Flights.Add(t);
            }
        }
        foreach (var lift in s.Lifts)
        {
            var m = ToBox(w, lift.X0, lift.Z0, lift.X1, lift.Z1);
            var cab = new RoomPlan { X0 = lift.X0, Z0 = lift.Z0, X1 = lift.X1, Z1 = lift.Z1 };
            l.Lifts.Add(new LiftPlan
            {
                X0 = m.X0, Z0 = m.Z0, X1 = m.X1, Z1 = m.Z1, Bottom = lift.Bottom, Top = lift.Top,
                DoorSide = TurnSide(lift.DoorSide, w.Turns), DoorCenter = ToBoxCenter(w, cab, lift.DoorSide, lift.DoorCenter),
                DoorWidth = lift.DoorWidth, DoorTop = lift.DoorTop,
            });
        }
        foreach (var d in s.InnerDoors)
        {
            var room = s.Floors[d.Floor].Rooms[d.Room];
            l.InnerDoors.Add(new InnerDoorPlan
            {
                Floor = d.Floor, Room = d.Room + offsets[d.Floor], Side = TurnSide(d.Side, w.Turns),
                Center = ToBoxCenter(w, room, d.Side, d.Center), Width = d.Width, Top = d.Top,
                Unit = d.Unit + 1000 * w.Index, Locked = d.Locked,
            });
        }
        foreach (var e in s.AllEntrances())
        {
            var (x, z) = ToBox(w, e.X, e.Z);
            var (ix, iz) = Turn(w.Turns, e.InX, e.InZ);
            l.Entrances.Add(new EntrancePlan
            {
                Door = e.Door, X = x, Z = z, InX = ix, InZ = iz, Width = e.Width,
                DoorX = e.DoorX, DoorY = e.DoorY, DoorZ = e.DoorZ, DoorOutX = e.DoorOutX, DoorOutZ = e.DoorOutZ,
                DoorWidth = e.DoorWidth, DoorHeight = e.DoorHeight, Hang = e.Hang, Vehicle = e.Vehicle,
            });
        }
    }

    /// <summary>A flight turned with its wing: a quarter turn swaps its run between Z and X.</summary>
    private static FlightPlan TurnFlight(WingPlan w, FlightPlan fl)
    {
        bool alongX = fl.AlongX ^ (w.Turns % 2 == 1);
        var p0 = fl.Point(fl.X0, fl.ZBottom);
        var p1 = fl.Point(fl.X1, fl.ZBottom);
        var b0 = ToBox(w, p0.X, p0.Z);
        var b1 = ToBox(w, p1.X, p1.Z);
        var bm = ToBox(w, fl.Bottom.X, fl.Bottom.Z);
        var tm = ToBox(w, fl.TopEnd.X, fl.TopEnd.Z);
        float a0 = alongX ? b0.Z : b0.X, a1 = alongX ? b1.Z : b1.X;
        return new FlightPlan
        {
            X0 = Math.Min(a0, a1), X1 = Math.Max(a0, a1),
            ZBottom = alongX ? bm.X : bm.Z, ZTop = alongX ? tm.X : tm.Z,
            From = fl.From, To = fl.To, AlongX = alongX,
            // the parapet keeps to the same edge, which a turn may have swapped
            Parapet = fl.Parapet == 0 ? 0 : a1 > a0 ? fl.Parapet : -fl.Parapet,
        };
    }

    /// <summary>
    /// The two doorways a link cut, one in each wing's room either side of where they meet, made
    /// one arch: lined up on the stretch both walls share. False if they do not meet.
    /// </summary>
    private static bool Join(FloorPlan ground, EntrancePlan e1, EntrancePlan e2, float top)
    {
        // the two doorways are at one point, either side of the wall: each its own room
        int Find(EntrancePlan e, int not, out int opening)
        {
            opening = -1;
            for (int i = 0; i < ground.Rooms.Count; i++)
            {
                if (i == not) continue;
                var r = ground.Rooms[i];
                for (int k = 0; k < r.Openings.Count; k++)
                {
                    var o = r.Openings[k];
                    if (o.Kind != OpeningKind.Entry) continue;
                    var (px, pz) = WallPoint(r, o.Side, o.Center);
                    if (Math.Abs(px - e.X) < 0.05f && Math.Abs(pz - e.Z) < 0.05f) { opening = k; return i; }
                }
            }
            return -1;
        }
        int ra = Find(e1, -1, out int oa), rb = Find(e2, ra, out int ob);
        if (ra < 0 || rb < 0) return Fail($"join: no room at a doorway ({ra}, {rb}) at {e1.X:F1},{e1.Z:F1} / {e2.X:F1},{e2.Z:F1}");
        var A = ground.Rooms[ra];
        var B = ground.Rooms[rb];
        var sa = A.Openings[oa];
        var sb = B.Openings[ob];
        if (TurnSide(sa.Side, 2) != sb.Side) return Fail($"join: doorways on sides {sa.Side} / {sb.Side} of {A.Type} [{A.X0:F1},{A.Z0:F1}..{A.X1:F1},{A.Z1:F1}] / {B.Type} [{B.X0:F1},{B.Z0:F1}..{B.X1:F1},{B.Z1:F1}]");
        // the stretch both walls share, and the doorway on it
        bool along = AlongX(sa.Side);
        float lo = Math.Max(along ? A.X0 : A.Z0, along ? B.X0 : B.Z0), hi = Math.Min(along ? A.X1 : A.Z1, along ? B.X1 : B.Z1);
        float width = Math.Min(sa.Width, sb.Width);
        if (hi - lo < width + 0.3f) return Fail($"join: {hi - lo:F2} m shared for a {width:F2} m arch between {A.Type} [{A.X0:F1},{A.Z0:F1}..{A.X1:F1},{A.Z1:F1}] and {B.Type} [{B.X0:F1},{B.Z0:F1}..{B.X1:F1},{B.Z1:F1}], doorways at {e1.X:F1},{e1.Z:F1} / {e2.X:F1},{e2.Z:F1}");
        float c = Fit((sa.Center + sb.Center) / 2, lo + width / 2 + 0.15f, hi - width / 2 - 0.15f);
        A.Openings[oa] = new OpeningPlan { Side = sa.Side, Center = c, Width = width, Top = Math.Min(top, Math.Min(sa.Top, sb.Top)), Kind = OpeningKind.Arch, Other = rb };
        B.Openings[ob] = new OpeningPlan { Side = sb.Side, Center = c, Width = width, Top = Math.Min(top, Math.Min(sa.Top, sb.Top)), Kind = OpeningKind.Arch, Other = ra };
        return true;
    }
}
