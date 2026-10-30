using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Restaurants, cafés, bars and clubs (#1242, part 5 of #1232): every dining venue a solid holds takes
/// a ground-floor room the building's own planner made (a shop unit under flats, a house's living room,
/// a commercial ground floor) and makes it its guest room, by its kind; the kitchen or store off it
/// becomes its kitchen, a house's dining room a second guest room (the Säli). Numbers are in
/// <c>docs/notes/terrain/venue-dining.md</c>, the rules in <c>docs/notes/terrain/dining.md</c>.
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>
    /// Set while a block of flats holding a dining venue is planned: its ground floor is shop units, as
    /// a mixed block's, for the venues to take. Per thread: tiles are planned on several.
    /// </summary>
    [ThreadStatic] internal static bool DiningShops;

    /// <summary>A dining venue's guest room (not its kitchen).</summary>
    internal static bool DiningRoom(RoomType t) => t is RoomType.Gaststube or RoomType.Pizzeria or RoomType.CafeRoom
        or RoomType.FastFoodRoom or RoomType.KebabRoom or RoomType.BarRoom or RoomType.PubRoom or RoomType.ClubRoom;

    /// <summary>The guest room a venue makes, from its kind and its cuisine.</summary>
    internal static RoomType DiningRoomOf(VenueRecord v)
    {
        // the cuisine, else the name: many a pizzeria is tagged only by what it is called
        string c = (v.Cuisine + " " + v.Name).ToLowerInvariant().Replace("döner", "kebab").Replace("doner", "kebab").Replace("pizz", "pizza");
        return v.Type switch
        {
            VenueType.Restaurant when c.Contains("pizza") || c.Contains("italian") => RoomType.Pizzeria,
            VenueType.Restaurant or VenueType.FastFood when c.Contains("kebab") => RoomType.KebabRoom,
            VenueType.Restaurant when c.Contains("burger") => RoomType.FastFoodRoom,
            VenueType.Restaurant => RoomType.Gaststube,
            VenueType.Cafe => RoomType.CafeRoom,
            VenueType.FastFood => RoomType.FastFoodRoom,
            VenueType.Bar => RoomType.BarRoom,
            VenueType.Nightclub => RoomType.ClubRoom,
            _ => RoomType.PubRoom,   // pub, beer garden
        };
    }

    /// <summary>A guest room smaller than this is no venue's: a kebab fits in less than a Beiz.</summary>
    private static float DiningMinArea(RoomType t) => t is RoomType.KebabRoom or RoomType.FastFoodRoom or RoomType.CafeRoom ? 9f : 14f;

    /// <summary>
    /// How much a room is wanted as a guest room, lower first: a shop, then a living room, a dining room,
    /// an office, a classroom; a hall last (it is the house's stair hall, where few tables fit).
    /// </summary>
    private static int GuestRank(RoomType t) => t switch
    {
        RoomType.Shop => 0,
        RoomType.Living => 1,
        RoomType.Dining => 2,
        RoomType.Office => 3,
        RoomType.Classroom => 4,
        _ => 5,
    };

    /// <summary>
    /// Each dining venue of the solid takes a ground-floor room, in list order, one room each: the best
    /// kind (<see cref="GuestRank"/>), then the nearest its point, then the biggest. Shop units only in a
    /// block of flats (its living rooms are flats'), any public or living room elsewhere. A venue with no
    /// room left, or none big enough, is not drawn.
    /// </summary>
    private static void DiningUnits(InteriorLayout l, Footprint fp, IReadOnlyList<VenueRecord>? dining)
    {
        if (dining is not { Count: > 0 } || l.Floors.Count == 0) return;
        if (l.Type is not (BuildingType.None or BuildingType.Apartments or BuildingType.MixedUse)) return;
        bool apt = l.Type is BuildingType.Apartments or BuildingType.MixedUse;
        var rooms = l.GroundFloor.Rooms;
        var tile = new TileId(fp.Key.TileE, fp.Key.TileN);
        var taken = new HashSet<int>();
        foreach (var v in dining)
        {
            // the venue's point in the plan's frame: +X along AxisU, +Z along AxisV, from the centre
            var d = new Vector2((float)(v.E - tile.MinE), (float)(tile.MaxN - v.N)) - fp.Center;
            float px = d.Dot(fp.AxisU), pz = d.Dot(fp.AxisV);
            var guest = DiningRoomOf(v);
            int best = -1;
            // its own street door (#1292): the room that door opens into is its guest room
            EntrancePlan? own = null;
            foreach (var door in fp.Doors)
                if (door.Venue > 0 && Venues.At(door.Venue - 1) == v && l.EntranceOf(new DoorKey(fp.Key, door.Slot).ToString()) is { } e && e.Level == 0)
                    own = e;
            if (own != null && EntryRoom(rooms, own) is int gi && !taken.Contains(gi) && rooms[gi].Shape == null
                && rooms[gi].Area >= DiningMinArea(guest) && (apt ? rooms[gi].Type == RoomType.Shop : GuestCapable(rooms[gi].Type)))
                best = gi;
            bool behind = best >= 0;
            // its own door with no doorway yet (a wing's plan had no room on that wall for it, #1304): the room that will
            // stand behind it is the venue's, whatever it was, so the doorway is fitted into a guest room once the rooms
            // reach the facade (Vestibules); the plan follows the door
            bool doorless = !behind && !apt && own == null
                && fp.Doors.Any(x => x.Unit && x.Venue > 0 && Venues.At(x.Venue - 1) == v);
            if (doorless && fp.Doors.First(x => x.Unit && x.Venue > 0 && Venues.At(x.Venue - 1) == v) is var vd)
            {
                var dd = new Vector2(vd.Position.X, vd.Position.Z) - fp.Center;
                (px, pz) = (dd.Dot(fp.AxisU), dd.Dot(fp.AxisV));
            }
            (int Rank, float Dist) bestD = (int.MaxValue, float.MaxValue);
            for (int i = 0; i < rooms.Count && best < 0; i++)
            {
                var r = rooms[i];
                // a shaped room (#1197, a drawn one) is not carved: its kitchen would stand in its shape
                if (taken.Contains(i) || r.Area < DiningMinArea(guest) || r.Shape != null) continue;
                bool fits = apt ? r.Type == RoomType.Shop
                    : r.Type is RoomType.Shop or RoomType.Living or RoomType.Dining or RoomType.Office
                        or RoomType.Classroom or RoomType.Hall
                      // behind its door, a private room too: a bedroom or a study becomes the venue's
                      || doorless && r.Type is RoomType.Bedroom or RoomType.Study or RoomType.GuestRoom or RoomType.Storage;
                if (!fits) continue;
                float dx = Math.Max(0, Math.Max(r.X0 - px, px - r.X1)), dz = Math.Max(0, Math.Max(r.Z0 - pz, pz - r.Z1));
                // behind its door the nearest room wins, whatever its kind; else the best kind, then the nearest its point
                var score = (doorless ? 0 : GuestRank(r.Type), dx * dx + dz * dz - r.Area * 1e-4f);
                if (score.CompareTo(bestD) < 0) (best, bestD) = (i, score);
            }
            if (best < 0) continue;
            var was = rooms[best].Type;
            rooms[best].Type = guest;
            taken.Add(best);
            // the room walled off to its front's span, what is left of it keeping its use (#1292, the owner's call)
            var parts = new List<int>();
            if (behind && !apt && fp.Doors.FirstOrDefault(x => x.Venue > 0 && Venues.At(x.Venue - 1) == v) is { Span: > 0 } front)
                foreach (int part in WallToSpan(l, rooms, best, own, front.Span, was)) { taken.Remove(part); parts.Add(part); }
            // through its doors: the first kitchen, store or pantry becomes its kitchen, a dining room its Säli
            bool kitchen = false;
            int store = -1;
            foreach (var o in rooms[best].Openings.ToList())
            {
                if (o.Kind != OpeningKind.Door || o.Other < 0 || o.Other >= rooms.Count || taken.Contains(o.Other)) continue;
                var next = rooms[o.Other];
                // a bar or a club, which cooks nothing, may have its toilets there instead (#1285): kept for BackOfHouse
                if (store < 0 && !Cooks(guest) && next.Type is RoomType.Storage or RoomType.Pantry && next.Area >= 5f)
                {
                    store = o.Other;
                    continue;
                }
                if (!kitchen && next.Type is RoomType.Kitchen or RoomType.Storage or RoomType.Pantry && next.Area >= 5f)
                {
                    next.Type = RoomType.GastroKitchen;
                    taken.Add(o.Other);
                    kitchen = true;
                }
                else if (!apt && next.Type == RoomType.Dining && next.Area >= DiningMinArea(guest))
                {
                    next.Type = guest;
                    taken.Add(o.Other);
                }
            }
            // a house's kitchen across its hall is the restaurant's all the same
            if (!kitchen && !apt)
            {
                var g = rooms[best];
                int k = Enumerable.Range(0, rooms.Count).Where(i => !taken.Contains(i) && rooms[i].Type == RoomType.Kitchen)
                    .OrderBy(i => Math.Abs((rooms[i].X0 + rooms[i].X1 - g.X0 - g.X1) / 2) + Math.Abs((rooms[i].Z0 + rooms[i].Z1 - g.Z0 - g.Z1) / 2))
                    .DefaultIfEmpty(-1).First();
                if (k >= 0)
                {
                    rooms[k].Type = RoomType.GastroKitchen;
                    taken.Add(k);
                    kitchen = true;
                }
            }
            // a place that serves food and still has none: its kitchen, and a WC beside it, walled off the
            // back of its own room; an oversized room gives a second room through an arch (#1250)
            bool toilets = BackOfHouse(l, rooms, best, guest, kitchen, store, taken, was);
            // none of its own: a WC the building has on its ground floor is the venue's (a Beiz's in the house, #1285)
            if (!toilets)
            {
                int f = l.Floors.FindIndex(fl => fl.Rooms == rooms);
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (rooms[i].Type != RoomType.WC || rooms[i].Openings.Any(o => o.Kind == OpeningKind.Entry)) continue;
                    // in by the doorway from the biggest room it opens onto: the hall, not a pantry behind it
                    var door = rooms[i].Openings.Where(o => o.Kind == OpeningKind.Door && o.Other >= 0 && o.Other < rooms.Count)
                        .OrderByDescending(o => rooms[o.Other].Area).FirstOrDefault();
                    if (door == null) continue;
                    AdoptWC(l, f, i, door);
                    break;
                }
            }
            // entered by its own street door, it keeps no door into the building's rooms (a school's classroom, #1292
            // playtest): each walled up where that room is still reached some other way
            if (behind && !apt) SealOff(l, rooms, best, r => taken.Contains(r) || parts.Contains(r) || ToiletRoom(rooms[r].Type) || rooms[r].Type == RoomType.WC);
        }
        // the shop counter a shop building is promised goes to its shop, never into a restaurant
        if (taken.Count > 0 && l.Shop != Loot.ShopType.None && !l.Floors.Any(fl => fl.Rooms.Any(r => r.Type == RoomType.Shop)))
            l.Shop = Loot.ShopType.None;
    }

    /// <summary>A room a venue may make its guest room outside a block of flats: a public or a living room, a hall last.</summary>
    internal static bool GuestCapable(RoomType t) => t is RoomType.Shop or RoomType.Living or RoomType.Dining or RoomType.Office
        or RoomType.Classroom or RoomType.Hall;

    /// <summary>The ground-floor room a street doorway is cut in, or null.</summary>
    internal static int? EntryRoom(List<RoomPlan> rooms, EntrancePlan e)
    {
        // on a rectangle's wall or, once the outline fit moved it (#1290), on a shaped room's angled edge
        for (int i = 0; i < rooms.Count; i++)
            foreach (var o in rooms[i].Openings)
            {
                if (o.Kind != OpeningKind.Entry) continue;
                var at = rooms[i].WallFrame(o).At;
                if (Math.Abs(at.X - e.X) < 0.05f && Math.Abs(at.Y - e.Z) < 0.05f) return i;
            }
        return null;
    }

    /// <summary>
    /// Walls up guest room <paramref name="gi"/>'s doors and arches into rooms not its venue's (<paramref name="ours"/>
    /// false), both sides and the leaf, where that room is still reached without the guest room.
    /// </summary>
    private static void SealOff(InteriorLayout l, List<RoomPlan> rooms, int gi, Func<int, bool> ours)
    {
        int f = l.Floors.FindIndex(fl => fl.Rooms == rooms);
        foreach (var o in rooms[gi].Openings.ToList())
        {
            if (o.Kind is not (OpeningKind.Door or OpeningKind.Arch) || o.Other < 0 || o.Other >= rooms.Count || ours(o.Other)) continue;
            int other = o.Other;
            var back = rooms[other].Openings.FirstOrDefault(b => b.Other == gi && b.Side == Opposite(o.Side) && Math.Abs(b.Center - o.Center) < 0.01f);
            rooms[gi].Openings.Remove(o);
            if (back != null) rooms[other].Openings.Remove(back);
            if (ReachedWithout(rooms, other, gi))
            {
                l.InnerDoors.RemoveAll(d => d.Floor == f && Math.Abs(d.Center - o.Center) < 0.01f
                    && (d.Room == gi && d.Side == o.Side || d.Room == other && d.Side == Opposite(o.Side)));
                continue;
            }
            // the only way to it: the door stays
            rooms[gi].Openings.Add(o);
            if (back != null) rooms[other].Openings.Add(back);
        }
    }

    /// <summary>A part walled off a guest room must be at least this wide to be a room of its own, m; less stays the venue's.</summary>
    private const float SpanRest = 2.2f;

    /// <summary>Whether room <paramref name="target"/> is reached from a street doorway without passing through room <paramref name="avoid"/>.</summary>
    private static bool ReachedWithout(List<RoomPlan> rooms, int target, int avoid)
    {
        var seen = new HashSet<int>();
        var todo = new Stack<int>();
        for (int i = 0; i < rooms.Count; i++)
            if (i != avoid && rooms[i].Openings.Any(o => o.Kind == OpeningKind.Entry)) { seen.Add(i); todo.Push(i); }
        while (todo.Count > 0)
        {
            int i = todo.Pop();
            if (i == target) return true;
            foreach (var o in rooms[i].Openings)
                if (o.Kind is OpeningKind.Door or OpeningKind.Arch && o.Other >= 0 && o.Other < rooms.Count && o.Other != avoid && seen.Add(o.Other))
                    todo.Push(o.Other);
        }
        return false;
    }

    /// <summary>A guest room keeps this much across past a strip of gendered toilets off its own wall, m: less, and a smaller set.</summary>
    private const float GenderedKeep = 6f;

    /// <summary>A guest room is never walled off smaller than this to its front, m².</summary>
    private const float SpanKeep = 30f;

    /// <summary>
    /// Walls guest room <paramref name="gi"/> off to its glazed front (#1292): <paramref name="span"/> m along the wall its
    /// street doorway is in, centred on the doorway. What is left at either end, when wide enough, is a room of what the
    /// room was, behind a door from the guest room. Returns the parts walled off.
    /// </summary>
    private static List<int> WallToSpan(InteriorLayout l, List<RoomPlan> rooms, int gi, EntrancePlan e, float span, RoomType was)
    {
        var parts = new List<int>();
        var r = rooms[gi];
        var o = r.Openings.FirstOrDefault(x => x.Kind == OpeningKind.Entry && WallPoint(r, x.Side, x.Center) is var (x0, z0)
            && Math.Abs(x0 - e.X) < 0.05f && Math.Abs(z0 - e.Z) < 0.05f);
        if (o == null) return parts;
        bool alongX = o.Side is Side.Front or Side.Back;
        float top = Math.Min(2.05f, l.ClearOf(r) - 0.2f);
        // each end of the wall, the far one first so the doorway's centre is measured on the room as it was
        foreach (var end in alongX ? new[] { Side.Right, Side.Left } : new[] { Side.Back, Side.Front })
        {
            float lo = alongX ? r.X0 : r.Z0, hi = alongX ? r.X1 : r.Z1;
            float rest = end is Side.Right or Side.Back ? hi - (o.Center + span / 2) : o.Center - span / 2 - lo;
            // and the guest room keeps enough of itself for its tables: else it keeps that end, its glass on part of it
            float across = alongX ? r.Z1 - r.Z0 : r.X1 - r.X0;
            if (rest < SpanRest || (hi - lo - rest) * across < SpanKeep) continue;
            // a door in the wall the cut stands across moves the cut along, never into the front
            int k = -1;
            for (float slide = 0; slide <= 1.5f && k < 0; slide += 0.5f)
                if (rest - slide >= SpanRest) k = Carve(l, rooms, gi, end, rest - slide, was);
            if (k < 0) continue;
            parts.Add(k);
            // reached some other way than through the guest room (a door to the lobby it kept): its wall stays whole,
            // for the venue's toilets; else a door from the guest room, at the back of the wall between them
            if (ReachedWithout(rooms, k, gi)) continue;
            float a0 = alongX ? r.Z0 : r.X0, a1 = alongX ? r.Z1 : r.X1;
            float c = o.Side is Side.Front or Side.Left ? Math.Max((a0 + a1) / 2, a1 - 1.2f) : Math.Min((a0 + a1) / 2, a0 + 1.2f);
            Opening(rooms, gi, end, k, c, 0.9f, top, OpeningKind.Door);
        }
        return parts;
    }

    /// <summary>Whether a guest room's venue cooks: everything but a bar and a club [S: a pub's kitchen is the norm in CH].</summary>
    private static bool Cooks(RoomType t) => t is not (RoomType.BarRoom or RoomType.ClubRoom);

    /// <summary>The share of a guest room's floor its kitchen takes [S: a kitchen 30-40 % of the dining area; a café's or a kebab's less].</summary>
    private static float KitchenShare(RoomType t) => t switch
    {
        RoomType.CafeRoom or RoomType.KebabRoom or RoomType.FastFoodRoom => 0.18f,
        RoomType.PubRoom => 0.2f,
        _ => 0.28f,
    };

    /// <summary>A guest room smaller than this, m2, cooks behind its counter, as a kiosk or a snack bar does: no kitchen of its own.</summary>
    private const float KitchenFrom = 24f;

    /// <summary>A guest room bigger than this, m2, is two rooms: a Beiz and its Säli, a café's front and back room.</summary>
    private const float GuestMaxArea = 85f;

    /// <summary>
    /// The back of house of guest room <paramref name="gi"/> (#1250): a cooking venue with no kitchen walls
    /// one off the back of its room (the wall away from the way in), sized by <see cref="KitchenShare"/>,
    /// the swing door in the middle, and from a big enough room a WC at the strip's end with its door onto
    /// the guest room; a room still over <see cref="GuestMaxArea"/> is cut across, the far part a second
    /// guest room through a wide arch. Each cut is given up where it would cross a door.
    /// </summary>
    /// <param name="store">A store off a venue that cooks nothing: its toilets when none fit in its room, else its glass-wash (#1285).</param>
    /// <returns>Whether the venue has its public toilets (#1285).</returns>
    private static bool BackOfHouse(InteriorLayout l, List<RoomPlan> rooms, int gi, RoomType guest, bool kitchen, int store, HashSet<int> taken, RoomType was)
    {
        bool toilets = false;
        var g = rooms[gi];
        var entry = g.Openings.Where(o => o.Kind is OpeningKind.Entry or OpeningKind.Door).OrderByDescending(o => o.Kind == OpeningKind.Entry).FirstOrDefault();
        var inSide = entry?.Side ?? Side.Front;
        var back = Opposite(inSide);
        float top = Math.Min(2.05f, l.ClearOf(g) - 0.2f);
        if (!kitchen && Cooks(guest) && g.Area >= KitchenFrom)
        {
            // the back wall first, then the side walls; a cut that would cross a door is tried deeper or shallower
            int ki = -1;
            Side cutSide = back;
            foreach (var side in new[] { back, Turn(back), Opposite(Turn(back)) })
            {
                float wall = side is Side.Front or Side.Back ? g.Width : g.Depth, across = side is Side.Front or Side.Back ? g.Depth : g.Width;
                float want = Math.Clamp(g.Area * KitchenShare(guest) / wall, 2.4f, 5f);
                if (wall < 2.4f) continue;
                foreach (float deep in new[] { want, want + 0.8f, want - 0.6f, want + 1.6f })
                {
                    if (deep < 2.0f || across - deep < 3.2f || wall * (across - deep) < DiningMinArea(guest)) continue;
                    ki = Carve(l, rooms, gi, side, deep, RoomType.GastroKitchen);
                    if (ki >= 0) break;
                }
                if (ki >= 0) { cutSide = side; break; }
            }
            back = cutSide;
            {
                float wall = back is Side.Front or Side.Back ? g.Width : g.Depth;
                if (ki >= 0)
                {
                    taken.Add(ki);
                    var k = rooms[ki];
                    // the public toilets at one end of the strip (#1285), from a room that seats enough to need them, their
                    // doors onto the guest room: what the strip holds past a kitchen of 2.4 m, by gender in a big restaurant
                    // the strip's end with no doorway in it (an extra street door is often there, #498)
                    var end = k.Openings.Any(o => o.Side == Turn(back) && o.Kind != OpeningKind.Window)
                        && !k.Openings.Any(o => o.Side == Opposite(Turn(back)) && o.Kind != OpeningKind.Window) ? Opposite(Turn(back)) : Turn(back);
                    int seats = (int)(g.Area / 1.4f);
                    var loo = !toilets && g.Area >= 18f ? ToiletSizing.Choose(wall - 2.4f, back is Side.Front or Side.Back ? k.Depth : k.Width, seats)
                        : ToiletSizing.Plan.None;
                    // a doorway in the strip's end wall stays with the WC it always was
                    bool endDoor = k.Openings.Any(o => o.Side == end && o.Kind != OpeningKind.Window);
                    int wi = loo.Set != ToiletSet.None ? Carve(l, rooms, ki, end, endDoor ? 1.9f : loo.Length, RoomType.WC) : -1;
                    if (wi >= 0 && !endDoor && wi == rooms.Count - 1 && rooms[wi].Openings.All(o => o.Kind == OpeningKind.Window))
                    {
                        var piece = rooms[wi];
                        rooms.RemoveAt(wi);
                        var at = end switch
                        {
                            Side.Front => ((piece.X0 + piece.X1) / 2, piece.Z0),
                            Side.Back => ((piece.X0 + piece.X1) / 2, piece.Z1),
                            Side.Left => (piece.X0, (piece.Z0 + piece.Z1) / 2),
                            _ => (piece.X1, (piece.Z0 + piece.Z1) / 2),
                        };
                        PublicToilets(l, l.Floors.First(fl => fl.Rooms == rooms), gi, new RectPlan(piece.X0, piece.Z0, piece.X1, piece.Z1), Opposite(back), at, seats);
                        for (int i = wi; i < rooms.Count; i++) taken.Add(i);
                        toilets = true;
                    }
                    else if (wi >= 0)
                    {
                        // a strip end that carried a doorway on: the one WC it always was, the public one
                        taken.Add(wi);
                        Opening(rooms, gi, back, wi, Mid(rooms[wi], back), InnerDoor, top, OpeningKind.Door);
                        AdoptWC(l, l.Floors.FindIndex(fl => fl.Rooms == rooms), wi, rooms[wi].Openings[^1]);
                        toilets = true;
                    }
                    Opening(rooms, gi, back, ki, Mid(k, back), InnerDoor, top, OpeningKind.Door);
                }
            }
        }
        // a venue the kitchen strip gave no toilets (a bar, a club, a café with no kitchen of its own): a strip of them
        // walled off a wall with no doorway in it (#1285)
        if (!toilets && g.Area >= 18f) toilets = ToiletsOff(l, rooms, gi, back, taken, top);
        if (store >= 0)
        {
            if (!toilets) toilets = StoreToilets(l, rooms, gi, store, taken);
            if (!taken.Contains(store))
            {
                rooms[store].Type = RoomType.GastroKitchen;
                taken.Add(store);
            }
        }
        // a hall of a room: cut across its length, the far part a second room of the same kind through an
        // arch (the Säli, no bar of its own); what is still over GuestMaxArea after that is cut again, the far
        // parts left the kind of room they were before the venue took the room (a shop, a living room, an
        // office), behind a door (Café du Vieil Ouchy's room was 590 m2: one venue, not a warren of bars, nor stores)
        if (guest is RoomType.ClubRoom) return toilets;
        bool sali = false;
        var todo = new Queue<int>();
        todo.Enqueue(gi);
        for (int guard = 0; todo.Count > 0 && guard < 16; guard++)
        {
            int ri = todo.Dequeue();
            var r = rooms[ri];
            if (r.Area <= GuestMaxArea) continue;
            var way = r.Openings.Where(o => o.Kind is OpeningKind.Entry or OpeningKind.Door or OpeningKind.Arch)
                .OrderByDescending(o => o.Kind == OpeningKind.Entry).FirstOrDefault();
            int si = -1;
            Side far = Side.Back;
            foreach (bool alongX in new[] { r.Width >= r.Depth, r.Width < r.Depth })
            {
                float len = alongX ? r.Width : r.Depth;
                if (len < 7f) continue;
                // the end away from the way in: across from it when it is in an end wall, else the end it is not near
                float mid = alongX ? (r.X0 + r.X1) / 2 : (r.Z0 + r.Z1) / 2;
                bool inLong = way != null && (way.Side is Side.Front or Side.Back) == alongX;
                far = way != null && !inLong ? Opposite(way.Side)
                    : alongX ? ((way?.Center ?? mid) <= mid ? Side.Right : Side.Left) : ((way?.Center ?? mid) <= mid ? Side.Back : Side.Front);
                // the Säli up to the size of a room; a store whatever is left past that
                // rooms kept near square: a side of at least sqrt(GuestMaxArea), the other cut next time round
                float keep = Math.Max(MathF.Sqrt(GuestMaxArea), GuestMaxArea / (alongX ? r.Depth : r.Width));
                float want = !sali ? Math.Min(len * 0.45f, keep) : len - keep;
                foreach (float cut in new[] { want, want * 0.8f, want * 1.2f })
                {
                    if (cut < 2.4f || len - cut < 3f) continue;
                    si = Carve(l, rooms, ri, far, cut, sali ? was : guest);
                    if (si >= 0) break;
                }
                if (si >= 0) break;
            }
            if (si < 0) continue;
            taken.Add(si);
            var s = rooms[si];
            if (s.Type != guest)
            {
                // the building's own room again: not the venue's, so out of its count
                taken.Remove(si);
                Opening(rooms, ri, far, si, Mid(s, far), InnerDoor, top, OpeningKind.Door);
            }
            else
            {
                float span = far is Side.Front or Side.Back ? s.Width : s.Depth;
                Opening(rooms, ri, far, si, Mid(s, far), Math.Min(2.4f, span - 1.2f), top, OpeningKind.Arch);
                todo.Enqueue(si);
            }
            sali = true;
            todo.Enqueue(ri);
        }
        return toilets;
    }

    /// <summary>
    /// A store off guest room <paramref name="gi"/> made the venue's public toilets (#1285), when its one doorway is
    /// onto the room, the room runs the length of that wall and a set fits: a bar's or a club's, which cook nothing.
    /// The set's first room takes the store's slot; what is left of it is a store again.
    /// </summary>
    private static bool StoreToilets(InteriorLayout l, List<RoomPlan> rooms, int gi, int si, HashSet<int> taken)
    {
        var s = rooms[si];
        var g = rooms[gi];
        var doors = s.Openings.Where(o => o.Kind != OpeningKind.Window).ToList();
        if (doors.Count != 1 || doors[0].Kind != OpeningKind.Door || doors[0].Other != gi) return false;
        var side = doors[0].Side;
        bool alongX = side is Side.Front or Side.Back;
        if (alongX ? g.X0 > s.X0 + 0.01f || g.X1 < s.X1 - 0.01f : g.Z0 > s.Z0 + 0.01f || g.Z1 < s.Z1 - 0.01f) return false;
        float len = alongX ? s.Width : s.Depth, deep = alongX ? s.Depth : s.Width;
        int seats = (int)(g.Area / 1.4f);
        if (ToiletSizing.Choose(len, deep, seats).Set == ToiletSet.None) return false;
        float c = doors[0].Center, mid = alongX ? (s.X0 + s.X1) / 2 : (s.Z0 + s.Z1) / 2;
        g.Openings.RemoveAll(o => o.Kind == OpeningKind.Door && o.Other == si && Math.Abs(o.Center - c) < 0.01f);
        var rect = new RectPlan(s.X0, s.Z0, s.X1, s.Z1);
        var at = alongX ? (c > mid ? s.X0 : s.X1, (s.Z0 + s.Z1) / 2) : ((s.X0 + s.X1) / 2, c > mid ? s.Z0 : s.Z1);
        int before = rooms.Count;
        var (_, left) = PublicToilets(l, l.Floors.First(fl => fl.Rooms == rooms), gi, rect, side, at, seats, reuse: si);
        if (left != null)
        {
            var store = new RoomPlan { Type = RoomType.Storage };
            SetRect(store, left);
            rooms.Add(store);
            Opening(rooms, rooms.Count - 1, side, gi, Mid(store, side), InnerDoor, Math.Min(2.05f, l.ClearOf(store) - 0.2f), OpeningKind.Door);
        }
        taken.Add(si);
        for (int i = before; i < rooms.Count; i++) taken.Add(i);
        return true;
    }

    /// <summary>
    /// Public toilets (#1285) in a strip walled off guest room <paramref name="gi"/>: off its back wall, else a side
    /// wall, one with no doorway in or near it, as deep as the set the wall's length holds needs (a gendered set
    /// 3.4 m, the shared anteroom 2.9, a WC 2.0), while the room keeps its size; what the set leaves of the strip
    /// is a store off the room. False when no wall takes them.
    /// </summary>
    private static bool ToiletsOff(InteriorLayout l, List<RoomPlan> rooms, int gi, Side back, HashSet<int> taken, float top)
    {
        var g = rooms[gi];
        int seats = (int)(g.Area / 1.4f);
        foreach (var side in new[] { back, Turn(back), Opposite(Turn(back)) })
        {
            float wall = side is Side.Front or Side.Back ? g.Width : g.Depth, across = side is Side.Front or Side.Back ? g.Depth : g.Width;
            float line = side switch { Side.Front => g.Z0, Side.Back => g.Z1, Side.Left => g.X0, _ => g.X1 };
            foreach (float deep in new[] { 3.4f, ToiletSizing.SharedD, ToiletSizing.SingleD + 0.1f })
            {
                var plan = ToiletSizing.Choose(wall, deep, seats);
                float need = plan.Set switch
                {
                    ToiletSet.Gendered => 3.4f, ToiletSet.Shared => ToiletSizing.SharedD,
                    ToiletSet.Pair or ToiletSet.Single => ToiletSizing.SingleD + 0.1f, _ => 0f,
                };
                // the shallowest strip that holds the set: a deeper one only wastes the room
                if (plan.Set == ToiletSet.None || need < deep - 0.01f) continue;
                // the room keeps 4 m across and most of its floor for its tables (a kebab's or a café's counter needs less);
                // the big gendered set only where the room keeps 6 m, else the vestibule or the pair (#1292, the owner's call)
                if (across - deep < (plan.Set == ToiletSet.Gendered ? GenderedKeep : 4f) || wall * (across - deep) < Math.Max((DiningMinArea(g.Type) < 10f ? 1.5f : 2f) * DiningMinArea(g.Type), Math.Min(0.65f * g.Area, 40f))) continue;
                // no doorway on that wall, nor on a side wall within the strip: the cut takes those with it
                bool lateral(OpeningPlan o) => o.Side != side && o.Side != Opposite(side);
                if (g.Openings.Any(o => o.Kind != OpeningKind.Window
                        && (o.Side == side || lateral(o) && Math.Abs(o.Center - line) < deep + o.Width / 2 + 0.2f)))
                    continue;
                int si = Carve(l, rooms, gi, side, deep, RoomType.Storage);
                if (si < 0) continue;
                var strip = rooms[si];
                if (si != rooms.Count - 1 || strip.Openings.Any(o => o.Kind != OpeningKind.Window))
                {
                    // never meant to happen: keep it a store off the room, as a strip always could be
                    taken.Add(si);
                    Opening(rooms, gi, side, si, Mid(strip, side), InnerDoor, top, OpeningKind.Door);
                    return false;
                }
                rooms.RemoveAt(si);
                var rect = new RectPlan(strip.X0, strip.Z0, strip.X1, strip.Z1);
                // the toilets from the end away from the way in
                var entry = g.Openings.FirstOrDefault(o => o.Kind is OpeningKind.Entry or OpeningKind.Door);
                float mid = side is Side.Front or Side.Back ? (rect.X0 + rect.X1) / 2 : (rect.Z0 + rect.Z1) / 2;
                bool lowEnd = entry != null && lateral(entry) ? false : (entry?.Center ?? mid) > mid;
                var at = side is Side.Front or Side.Back ? (lowEnd ? rect.X0 : rect.X1, (rect.Z0 + rect.Z1) / 2)
                    : ((rect.X0 + rect.X1) / 2, lowEnd ? rect.Z0 : rect.Z1);
                var (_, left) = PublicToilets(l, l.Floors.First(fl => fl.Rooms == rooms), gi, rect, Opposite(side), at, seats);
                if (left != null)
                {
                    var store = new RoomPlan { Type = RoomType.Storage };
                    SetRect(store, left);
                    rooms.Add(store);
                    Opening(rooms, rooms.Count - 1, Opposite(side), gi, Mid(store, side), InnerDoor, top, OpeningKind.Door);
                }
                for (int i = si; i < rooms.Count; i++) taken.Add(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>The middle of room <paramref name="r"/> along its wall <paramref name="side"/> (or the one across from it).</summary>
    private static float Mid(RoomPlan r, Side side) => side is Side.Front or Side.Back ? (r.X0 + r.X1) / 2 : (r.Z0 + r.Z1) / 2;

    /// <summary>
    /// Walls a strip <paramref name="deep"/> m off room <paramref name="ri"/>'s wall <paramref name="side"/> into
    /// a new room of <paramref name="type"/>: the openings on that wall and on the side walls past the cut
    /// go with it (and what they open onto points at it), windows the cut crosses go. Returns the new
    /// room's index, or −1 (and nothing changed) when the cut would cross a door. The two are not joined:
    /// the caller opens the doors it wants.
    /// </summary>
    private static int Carve(InteriorLayout l, List<RoomPlan> rooms, int ri, Side side, float deep, RoomType type)
    {
        int floor = l.Floors.FindIndex(fl => fl.Rooms == rooms);
        var r = rooms[ri];
        float cut = side switch
        {
            Side.Back => r.Z1 - deep,
            Side.Front => r.Z0 + deep,
            Side.Right => r.X1 - deep,
            _ => r.X0 + deep,
        };
        // past the cut: the strip's side of it
        bool Beyond(float c) => side is Side.Back or Side.Right ? c > cut : c < cut;
        foreach (var o in r.Openings)
        {
            if (o.Side == side || o.Side == Opposite(side)) continue;
            if (o.Center - o.Width / 2 - 0.15f < cut && o.Center + o.Width / 2 + 0.15f > cut && o.Kind != OpeningKind.Window) return -1;
        }
        var k = new RoomPlan { Type = type, Unit = r.Unit, Span = r.Span, Clear = r.Clear };
        (k.X0, k.Z0, k.X1, k.Z1) = (r.X0, r.Z0, r.X1, r.Z1);
        switch (side)
        {
            case Side.Back: k.Z0 = cut; r.Z1 = cut; break;
            case Side.Front: k.Z1 = cut; r.Z0 = cut; break;
            case Side.Right: k.X0 = cut; r.X1 = cut; break;
            default: k.X1 = cut; r.X0 = cut; break;
        }
        int ki = rooms.Count;
        rooms.Add(k);
        foreach (var o in r.Openings.ToList())
        {
            bool lateral = o.Side != side && o.Side != Opposite(side);
            bool crosses = lateral && o.Center - o.Width / 2 - 0.15f < cut && o.Center + o.Width / 2 + 0.15f > cut;
            if (crosses) { r.Openings.Remove(o); continue; } // a window: the new wall stands in it
            if (o.Side != side && !(lateral && Beyond(o.Center))) continue;
            r.Openings.Remove(o);
            k.Openings.Add(o);
            // a door's leaf hangs in it on the new room's side now
            foreach (var leaf in l.InnerDoors)
                if (leaf.Floor == floor && leaf.Room == ri && leaf.Side == o.Side && Math.Abs(leaf.Center - o.Center) < 0.01f) leaf.Room = ki;
            if (o.Other < 0 || o.Other >= rooms.Count) continue;
            foreach (var back in rooms[o.Other].Openings)
                if (back.Other == ri && back.Side == Opposite(o.Side) && Math.Abs(back.Center - o.Center) < 0.01f) back.Other = ki;
        }
        return ki;
    }

    /// <summary>
    /// A guest room by its kind: what stands at its walls (the bar or the buffet, the pizza oven, the
    /// pastry case, the order counter, the DJ booth, banquettes), the games, then tables in a grid over
    /// what is left. A kitchen: the cook line under its hood, prep tables, fridges, the sink.
    /// </summary>
    private static void DiningLayout(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var rooms = l.Floors[f].Rooms;
        string key = $"{l.Key}|{f}|{rooms.IndexOf(r)}";
        double Odds(string what) => Core.Fnv.Unit(key + "|" + what);
        // the way in stays clear 2 m into the room from the street door, 1.2 m from an inner one
        foreach (var o in r.Openings)
            if (o.Kind is OpeningKind.Entry or OpeningKind.Door) blocked.Add(Walk(r, o, o.Kind == OpeningKind.Entry ? 2.0f : 1.2f));

        if (r.Type == RoomType.GastroKitchen)
        {
            var guest = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door && o.Other >= 0 && o.Other < rooms.Count && DiningRoom(rooms[o.Other].Type));
            var back = guest != null ? Opposite(guest.Side) : Side.Back;
            var line = new[] { back, Turn(back), Opposite(Turn(back)), guest?.Side ?? Side.Front };
            // the line under its hood keeps out of the windows: shorter, on an inner wall, when the long one finds none
            foreach (float len in new[] { Math.Clamp(r.Width * 0.6f, 1.6f, 4.5f), 1.6f, 1.2f })
            {
                int before = l.Furniture.Count;
                TryPlace(l, f, r, new Piece(FurnitureType.CookLine, len, 0.9f, 2.3f, true), placed, blocked, rng, line);
                if (l.Furniture.Count > before) break;
            }
            TryPlace(l, f, r, new Piece(FurnitureType.Sink, 1.2f, 0.65f, 0.9f, true), placed, blocked, rng, line);
            TryPlace(l, f, r, new Piece(FurnitureType.Fridge, 0.7f, 0.75f, 2.0f, true), placed, blocked, rng, line);
            TryPlace(l, f, r, new Piece(FurnitureType.Counter, 1.6f, 0.7f, 0.9f, true), placed, blocked, rng, line);
            if (r.Area >= 12f) TryPlace(l, f, r, new Piece(FurnitureType.Fridge, 0.7f, 0.75f, 2.0f, true), placed, blocked, rng, line);
            if (r.Area >= 16f) TryPlace(l, f, r, new Piece(FurnitureType.Counter, 1.6f, 0.7f, 0.9f, false), placed, blocked, rng);
            TryPlace(l, f, r, new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true), placed, blocked, rng, line);
            return;
        }

        var entry = r.Openings.Where(o => o.Kind is OpeningKind.Entry or OpeningKind.Door)
            .OrderByDescending(o => o.Kind == OpeningKind.Entry).FirstOrDefault();
        var toKitchen = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door && o.Other >= 0 && o.Other < rooms.Count
            && rooms[o.Other].Type == RoomType.GastroKitchen);
        var inSide = entry?.Side ?? Side.Front;
        // the bar stands by the kitchen's door (the Buffet's swing door), else facing the way in
        var barSide = toKitchen?.Side ?? Opposite(inSide);
        float barAt = toKitchen?.Center ?? float.NaN;
        var far = new[] { Opposite(inSide), Turn(inSide), Opposite(Turn(inSide)) };
        var walls = new[] { Opposite(inSide), Turn(inSide), Opposite(Turn(inSide)), inSide };
        var barSides = new[] { barSide }.Concat(walls.Where(s => s != barSide)).ToArray();

        // a Säli: the second room through an arch from the venue's room (#1250), served from that one's bar
        bool second = r.Openings.Any(o => o.Kind == OpeningKind.Arch && o.Other >= 0 && o.Other < rooms.IndexOf(r)
            && rooms[o.Other].Type == r.Type);
        void Bar(float len, bool stools)
        {
            if (second) return;
            int before = l.Furniture.Count;
            foreach (float n in new[] { len, Math.Min(len, 2.4f), 1.8f })
            {
                TryPlace(l, f, r, new Piece(FurnitureType.BarCounter, n, 2.2f, 2.2f, true), placed, blocked, rng, barSides, barAt);
                if (l.Furniture.Count > before) break;
            }
            if (stools && l.Furniture.Count > before) Stools(l, f, r, l.Furniture[^1], placed, blocked);
        }
        void Along(Piece p, int max, Side[] sides)
        {
            for (int i = 0; i < max; i++)
            {
                int before = l.Furniture.Count;
                TryPlace(l, f, r, p, placed, blocked, rng, sides);
                if (l.Furniture.Count == before) break;
            }
        }
        // the games by the research's odds: what hangs or stands at a wall before the tables, what
        // stands free (table football, pool) in the floor the tables leave
        void WallGames(double pinball, double darts, double jukebox)
        {
            if (Odds("darts") < darts) TryPlace(l, f, r, new Piece(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true), placed, blocked, rng, far);
            if (Odds("pinball") < pinball) TryPlace(l, f, r, new Piece(FurnitureType.Pinball, 0.75f, 1.4f, 1.93f, true), placed, blocked, rng, walls);
            if (Odds("jukebox") < jukebox) TryPlace(l, f, r, new Piece(FurnitureType.Jukebox, 0.8f, 0.6f, 1.5f, true), placed, blocked, rng, walls);
        }
        void FloorGames(double foosball, double pool)
        {
            if (Odds("foosball") < foosball) TryPlace(l, f, r, new Piece(FurnitureType.TableFootball, 1.5f, 0.8f, 0.92f, false), placed, blocked, rng);
            if (Odds("pool") < pool && Math.Min(r.Width, r.Depth) >= 4.0f && r.Area >= 36f)
                TryPlace(l, f, r, new Piece(FurnitureType.PoolTable, 1.4f, 2.7f, 0.8f, false), placed, blocked, rng);
        }

        switch (r.Type)
        {
            case RoomType.Gaststube:
                // the Buffet, the Stammtisch near it, a tiled stove sometimes, benches round the walls
                Bar(Math.Clamp(r.Width * 0.3f, 2.0f, 4.0f), stools: r.Area >= 40f);
                if (Odds("stove") < 0.35) TryPlace(l, f, r, new Piece(FurnitureType.Kachelofen, 1.2f, 0.9f, 1.7f, true), placed, blocked, rng, far);
                if (r.Area >= 30f) TryPlace(l, f, r, new Piece(FurnitureType.TableSet, 1.9f, 1.9f, 0.76f, false), placed, blocked, rng);
                // the service station the waiters work from, a few plants between the tables (#1250)
                if (r.Area >= 25f) TryPlace(l, f, r, new Piece(FurnitureType.Counter, 1.4f, 0.55f, 0.95f, true), placed, blocked, rng, far);
                WallGames(0.03, 0.10, 0.02);
                FloorGames(0.15, 0.03);
                Along(new Piece(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.7f, 1.7f, 2.5f, 2.5f);
                break;
            case RoomType.Pizzeria:
                if (!second) TryPlace(l, f, r, new Piece(FurnitureType.PizzaOven, 1.8f, 1.6f, 2.4f, true), placed, blocked, rng, barSides, barAt);
                Bar(2.4f, stools: false);
                TryPlace(l, f, r, new Piece(FurnitureType.WineRack, 1.2f, 0.4f, 1.9f, true), placed, blocked, rng, walls);
                if (r.Area >= 40f) TryPlace(l, f, r, new Piece(FurnitureType.WineRack, 1.2f, 0.4f, 1.9f, true), placed, blocked, rng, far);
                Along(new Piece(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.7f, 1.7f, 2.5f, 2.5f);
                break;
            case RoomType.CafeRoom:
                // the pastry case and the coffee bar by the door, little tables for two
                // counter-high in the plan, so a shopfront's windows do not keep it off the wall
                if (!second)
                    TryPlace(l, f, r, new Piece(FurnitureType.PastryCase, Math.Clamp(r.Width * 0.4f, 2.2f, 4.0f), 1.8f, 1.0f, true),
                        placed, blocked, rng, new[] { Turn(inSide), Opposite(Turn(inSide)), Opposite(inSide) });
                Along(new Piece(FurnitureType.BanquetteSet, 1.2f, 1.45f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.0f, 1.5f, 1.8f, 2.2f);
                TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
                break;
            case RoomType.FastFoodRoom:
            case RoomType.KebabRoom:
                // the order counter facing the door, fixed booths round the walls, tables for two
                var counter = r.Type == RoomType.KebabRoom ? FurnitureType.KebabCounter : FurnitureType.OrderCounter;
                // counter-high in the plan (its menu boards hang over the back line at their own height), so
                // a shopfront's windows do not keep it off the wall
                if (!second) TryPlace(l, f, r, new Piece(counter, Math.Clamp(r.Width * 0.5f, 2.2f, 5.0f), 2.0f, 1.05f, true), placed, blocked, rng, walls);
                Along(new Piece(FurnitureType.BanquetteSet, 1.2f, 1.45f, 1.0f, true), 6, far);
                Tables(l, f, r, placed, blocked, 1.0f, 1.5f, 1.9f, 2.2f);
                break;
            case RoomType.BarRoom:
            case RoomType.PubRoom:
            {
                bool pub = r.Type == RoomType.PubRoom;
                Bar(Math.Clamp(r.Width * 0.45f, 3.0f, 8.0f), stools: true);
                // a small stage on the wall facing the bar, in a room big enough for a crowd before it
                if (r.Area >= 60f && Odds("stage") < (pub ? 0.25 : 0.4))
                    TryPlace(l, f, r, new Piece(FurnitureType.Stage, Math.Clamp(r.Width * 0.4f, 3.0f, 6.0f), 2.5f, 0.4f, true),
                        placed, blocked, rng, new[] { Opposite(barSide), Turn(barSide), Opposite(Turn(barSide)) });
                WallGames(pub ? 0.20 : 0.40, pub ? 0.55 : 0.35, pub ? 0.20 : 0.15);
                if (pub) Along(new Piece(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true), 4, far);
                Tables(l, f, r, placed, blocked, 1.4f, 1.4f, 2.2f, 2.2f, FurnitureType.HighTable, 1.05f);
                FloorGames(pub ? 0.40 : 0.55, pub ? 0.45 : 0.35);
                break;
            }
            case RoomType.ClubRoom:
            {
                // the dance floor in the middle, the DJ at one end of it, the bar along a side, sofas round
                float fw = Math.Max(2.4f, r.Width * 0.45f), fd = Math.Max(2.4f, r.Depth * 0.45f);
                var floor = new RectPlan((r.X0 + r.X1 - fw) / 2, (r.Z0 + r.Z1 - fd) / 2, (r.X0 + r.X1 + fw) / 2, (r.Z0 + r.Z1 + fd) / 2);
                if (Free(r, floor, placed, blocked, 0f))
                {
                    Put(l, f, FurnitureType.DanceFloor, (r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2, 0, fw, fd, 0.02f, placed);
                    blocked.Add(floor);
                }
                Bar(Math.Clamp(r.Width * 0.4f, 3.0f, 10.0f), stools: true);
                // facing the bar across the floor, else beside it on the bar's own wall when the others have doors (#1292)
                TryPlace(l, f, r, new Piece(FurnitureType.DjBooth, 2.4f, 1.3f, 1.3f, true), placed, blocked, rng,
                    new[] { Opposite(barSide), Turn(barSide), Opposite(Turn(barSide)), barSide });
                Along(new Piece(FurnitureType.Settee, 2.0f, 0.9f, 1.0f, true), 6, walls);
                break;
            }
        }
        for (int i = 0; i < Math.Clamp((int)(r.Area / 35f), 1, 4); i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
    }

    /// <summary>The next side round: front, right, back, left.</summary>
    private static Side Turn(Side s) => (Side)(((int)s + 1) % 4);

    /// <summary>The strip in front of an opening, <paramref name="deep"/> m into the room, a hand wider than it.</summary>
    private static RectPlan Walk(RoomPlan r, OpeningPlan o, float deep)
    {
        float a = o.Center - o.Width / 2 - 0.2f, b = o.Center + o.Width / 2 + 0.2f;
        return (o.Side switch
        {
            Side.Front => new RectPlan(a, r.Z0, b, r.Z0 + deep),
            Side.Back => new RectPlan(a, r.Z1 - deep, b, r.Z1),
            Side.Left => new RectPlan(r.X0, a, r.X0 + deep, b),
            _ => new RectPlan(r.X1 - deep, a, r.X1, b),
        }).Clip(r);
    }

    /// <summary>
    /// Tables (with their chairs or stools) in a grid over the room, <paramref name="pitchX"/> by
    /// <paramref name="pitchZ"/> apart, each where it is free and leaves a way through. The grid is tried
    /// centred and shifted half a pitch each way, and laid where it seats the most: a centred row can fall
    /// right on a bar's stools. Tables with chairs vary from spot to spot (#1250): a table for four, one for
    /// two turned either way, a long one for six where the pitch has room, each a hand off the grid.
    /// </summary>
    private static void Tables(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked,
        float w, float d, float pitchX, float pitchZ, FurnitureType type = FurnitureType.TableSet, float h = 0.76f)
    {
        const float margin = 0.5f;
        var variants = new List<(float W, float D, int Turns)> { (w, d, 0) };
        if (type == FurnitureType.TableSet)
        {
            if (w >= 1.6f)
            {
                // a long table for six where a row has room for it, a table for two either way round
                if (pitchX >= 3.1f) variants.Add((2.4f, d, 0));
                variants.Add((1.0f, 1.5f, 0));
                variants.Add((1.0f, 1.5f, 1));
            }
            else
            {
                variants.Add((w, d, 1));
                if (pitchX >= 2.5f && pitchZ >= 2.5f) variants.Add((1.7f, 1.7f, 0));
            }
        }
        string key = $"{l.Key}|{f}|{l.Floors[f].Rooms.IndexOf(r)}|tables";
        List<(float X, float Z, float W, float D, int T)> Lay(float sx, float sz, List<RectPlan> taken)
        {
            var spots = new List<(float, float, float, float, int)>();
            int nx = (int)((r.Width - 2 * margin - w - Math.Abs(sx)) / pitchX) + 1, nz = (int)((r.Depth - 2 * margin - d - Math.Abs(sz)) / pitchZ) + 1;
            if (nx < 1 || nz < 1) return spots;
            float x0 = (r.X0 + r.X1) / 2 + sx / 2 - (nx - 1) * pitchX / 2, z0 = (r.Z0 + r.Z1) / 2 + sz / 2 - (nz - 1) * pitchZ / 2;
            for (int i = 0; i < nx; i++)
                for (int k = 0; k < nz; k++)
                {
                    double roll = Core.Fnv.Unit($"{key}|{i}|{k}");
                    // the variant this spot rolls first, then the plain one, then the others
                    int first = variants.Count == 1 ? 0 : roll < 0.5 ? 0 : 1 + (int)((roll - 0.5) * 2 * (variants.Count - 1)) % (variants.Count - 1);
                    float jx = variants.Count == 1 ? 0 : (float)(Core.Fnv.Unit($"{key}|{i}|{k}|x") - 0.5) * 0.25f;
                    float jz = variants.Count == 1 ? 0 : (float)(Core.Fnv.Unit($"{key}|{i}|{k}|z") - 0.5) * 0.25f;
                    foreach (int vi in new[] { first, 0 }.Concat(Enumerable.Range(0, variants.Count)).Distinct())
                    {
                        var (vw, vd, vt) = variants[vi];
                        float ew = vt % 2 == 0 ? vw : vd, ed = vt % 2 == 0 ? vd : vw;
                        float x = x0 + i * pitchX + jx, z = z0 + k * pitchZ + jz;
                        var rect = new RectPlan(x - ew / 2, z - ed / 2, x + ew / 2, z + ed / 2);
                        if (!Free(r, rect, taken, blocked, 0.3f) || !KeepsWay(r, taken, rect)) continue;
                        taken.Add(rect);
                        spots.Add((x, z, vw, vd, vt));
                        break;
                    }
                }
            return spots;
        }
        var best = new List<(float X, float Z, float W, float D, int T)>();
        foreach (var (sx, sz) in new[] { (0f, 0f), (pitchX / 2, 0f), (0f, pitchZ / 2), (pitchX / 2, pitchZ / 2), (-pitchX / 2, 0f), (0f, -pitchZ / 2) })
        {
            var spots = Lay(sx, sz, new List<RectPlan>(placed));
            if (spots.Count > best.Count) best = spots;
        }
        foreach (var (x, z, tw, td, t) in best) Put(l, f, type, x, z, t, tw, td, h, placed);
    }

    /// <summary>The dining pieces at the sizes this file places them, for the model viewer.</summary>
    private static readonly Piece[] DiningPieces =
    {
        new(FurnitureType.TableSet, 1.7f, 1.7f, 0.76f, false),
        new(FurnitureType.BanquetteSet, 1.5f, 1.55f, 1.0f, true),
        new(FurnitureType.HighTable, 1.4f, 1.4f, 1.05f, false),
        new(FurnitureType.Kachelofen, 1.2f, 0.9f, 1.7f, true),
        new(FurnitureType.PizzaOven, 1.8f, 1.6f, 2.4f, true),
        new(FurnitureType.PastryCase, 3.0f, 1.8f, 1.0f, true),
        new(FurnitureType.OrderCounter, 3.6f, 2.0f, 1.05f, true),
        new(FurnitureType.KebabCounter, 3.0f, 2.0f, 1.05f, true),
        new(FurnitureType.TableFootball, 1.5f, 0.8f, 0.92f, false),
        new(FurnitureType.Jukebox, 0.8f, 0.6f, 1.5f, true),
        new(FurnitureType.Stage, 4.0f, 2.5f, 0.4f, true),
        new(FurnitureType.DjBooth, 2.4f, 1.3f, 1.3f, true),
        new(FurnitureType.DanceFloor, 4.0f, 4.0f, 0.02f, false),
        new(FurnitureType.CookLine, 3.0f, 0.9f, 2.3f, true),
    };

    /// <summary>Whole venues in the model viewer: a hand-made building holding one of each kind.</summary>
    [Core.Showcase("Interiors")]
    private static IEnumerable<(string, Func<Godot.Node3D>)> ShowcaseDining()
    {
        foreach (var (name, type, cuisine) in new[]
        {
            ("Gaststube", VenueType.Restaurant, "regional"), ("Pizzeria", VenueType.Restaurant, "pizza"),
            ("Café", VenueType.Cafe, ""), ("Kebab", VenueType.FastFood, "kebab"), ("Bar", VenueType.Bar, ""),
            ("Pub", VenueType.Pub, ""), ("Club", VenueType.Nightclub, ""),
        })
            yield return ($"Dining - {name}", () =>
            {
                var id = new TileId(2600, 1200);
                var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, 12f, 16f, 4f, BuildingKind.Commercial) } };
                Venues.Override = new List<VenueRecord> { new("n1", type, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, cuisine, name) };
                InteriorLayout layout;
                try { layout = Generate(tile, 0, null, null)!; }
                finally { Venues.Override = null; }
                var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Godot.Transform3D.Identity);
                foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
                return node;
            });
    }
}
